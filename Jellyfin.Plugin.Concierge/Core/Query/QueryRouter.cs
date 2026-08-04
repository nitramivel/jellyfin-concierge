using System;
using System.Collections.Generic;
using System.Linq;
using Jellyfin.Plugin.Concierge.Core.Retrieval;

namespace Jellyfin.Plugin.Concierge.Core.Query
{
    /// <summary>Where a query should be answered.</summary>
    public enum QueryRoute
    {
        /// <summary>Jellyfin's own search, unchanged. Free, instant, no model.</summary>
        Native = 0,

        /// <summary>
        /// Both, concurrently. Native renders on its own timeline and Concierge
        /// merges in when it arrives — hard rule 2 made concrete.
        /// </summary>
        Both = 1,

        /// <summary>The full Concierge pipeline.</summary>
        Concierge = 2,
    }

    /// <summary>What the router decided and why.</summary>
    /// <param name="Route">The chosen route.</param>
    /// <param name="Reason">
    /// A short phrase naming the rule that fired. Recorded on every query, because
    /// the router is the thing most likely to need arguing with later.
    /// </param>
    /// <param name="MayCarryConstraints">
    /// Whether the query is worth sending to the plan pass at all.
    /// </param>
    /// <remarks>
    /// The plan pass is skippable, and skipping it saves a model call and about
    /// 400ms on the most common Concierge query. "dark and twisted" has no year, no
    /// runtime and no watch state hiding in it — there is nothing for a plan to
    /// extract, so paying one to look is waste. A query with an explicit constraint
    /// word, or one long enough to be hiding one, is worth the call.
    /// </remarks>
    public sealed record RouteDecision(QueryRoute Route, string Reason, bool MayCarryConstraints = false);

    /// <summary>
    /// What the router needs to know about the library's vocabulary. Implemented by
    /// <see cref="Bm25Index"/>; an interface so the router stays testable on its own.
    /// </summary>
    public interface INameDictionary
    {
        /// <summary>Whether every token begins a title, person or studio we hold.</summary>
        /// <param name="text">The query text.</param>
        /// <returns>True when this looks like a name in the library.</returns>
        bool LooksLikeKnownName(string text);
    }

    /// <summary>
    /// Decides, for free and without calling anything, whether a query needs
    /// Concierge at all.
    /// </summary>
    /// <remarks>
    /// <b>The single most important cost decision in the plugin.</b> Most searches
    /// are not natural language — they are somebody typing four letters of a title
    /// they already know, and every one of those handed to a model is money burnt to
    /// produce a worse answer than substring matching gives for free.
    /// <para>
    /// Wrong toward "always Concierge" and the plugin is expensive and sluggish.
    /// Wrong toward "rarely Concierge" and it appears not to work at all. It is pure,
    /// so it is pinned by a table of real queries and can be argued with safely.
    /// </para>
    /// </remarks>
    public static class QueryRouter
    {
        /// <summary>
        /// Words that mean a sentence is being written rather than a title recalled.
        /// </summary>
        private static readonly HashSet<string> FunctionWords = new(StringComparer.Ordinal)
        {
            "who", "where", "what", "when", "why", "which", "how",
            "about", "with", "like", "that", "from", "under", "over", "without",
            "something", "anything", "some", "any", "one", "movie", "movies",
            "film", "films", "show", "shows", "watch", "seen", "haven", "hasn",
            "for", "but", "not", "and", "the", "where's", "whose",
        };

        /// <summary>
        /// Words that carry a constraint the plan pass would turn into a filter.
        /// </summary>
        private static readonly HashSet<string> ConstraintWords = new(StringComparer.Ordinal)
        {
            "under", "over", "less", "more", "than", "minutes", "minute", "hours", "hour",
            "before", "after", "since", "recent", "recently", "new", "newer", "newest",
            "old", "older", "oldest", "latest", "classic", "unwatched", "unseen",
            "short", "shorter", "long", "longer",
        };

        /// <summary>
        /// The decade words <see cref="Documents.EraTokens"/> writes into documents.
        /// Their presence in a query is a temporal constraint.
        /// </summary>
        private static readonly HashSet<string> EraWords = new(StringComparer.Ordinal)
        {
            "20s", "30s", "40s", "50s", "60s", "70s", "80s", "90s", "00s", "10s",
            "1920s", "1930s", "1940s", "1950s", "1960s", "1970s", "1980s", "1990s",
            "2000s", "2010s", "2020s",
            "twenties", "thirties", "forties", "fifties", "sixties", "seventies",
            "eighties", "nineties", "noughties", "aughts",
        };

        /// <summary>
        /// How much clearer the top keyword hit must be than the runner-up for a
        /// Native route to stand.
        /// </summary>
        /// <remarks>
        /// §4.2's third native rule: "lexical retrieval already returns a hit with a
        /// dominant score — a clear winner, not a flat distribution." This is that
        /// threshold, and it is what "michael scott" needed. Scott Pilgrim scored
        /// 5.93 against The Office's 5.55 — a 7% edge, which is a coin toss dressed
        /// as an answer, and the router was treating it as certainty.
        /// </remarks>
        public const double DominanceRatio = 1.35;

        /// <summary>
        /// Whether the keyword results have a clear winner.
        /// </summary>
        /// <remarks>
        /// Checked <em>after</em> retrieval, because it is the only native rule that
        /// needs to see the answer before it can judge. A dominant top hit means
        /// somebody typed a title and got it; a flat distribution means the words
        /// appear all over the library and something more than keywords is needed.
        /// </remarks>
        /// <param name="scores">The lexical scores, best first.</param>
        /// <returns>True when the top hit clearly wins.</returns>
        /// <summary>
        /// Whether a Native query is substantial enough that a flat keyword result is
        /// worth paying a model to disentangle.
        /// </summary>
        /// <param name="query">The raw query text.</param>
        /// <returns>True when the query has real words to be ambiguous about.</returns>
        /// <remarks>
        /// <b>A flat score distribution has two completely different causes.</b> Either
        /// several real titles tie — <c>michael scott</c> scoring Scott Pilgrim 5.93
        /// against The Office 5.55, which is exactly what the upgrade exists to rescue —
        /// or the query is so thin that everything matches it weakly and nothing matches
        /// it well. The second is not ambiguity, it is noise, and no model can resolve
        /// it into a title the person never typed.
        /// <para>
        /// Measured: of eight deliberately title-shaped evaluation queries, seven left
        /// the free native route under the paid path, against two on the free run.
        /// <c>s</c>, <c>bla</c>, <c>the of</c> and <c>blade</c> were each sent to a
        /// model — seconds of latency and real money for a query the native list
        /// already answers, against hard rule 2 and hard rule 11.
        /// </para>
        /// <para>
        /// So the upgrade needs two words that are actually words: at least
        /// <c>MinimumUpgradeTokens</c> tokens of <c>MinimumUpgradeTokenLength</c>
        /// characters or more. It deliberately does not try to judge whether those
        /// words name anything — that is what the scores are for.
        /// </para>
        /// </remarks>
        /// <summary>How many real words a query needs before a model may be paid to read it.</summary>
        private const int MinimumUpgradeTokens = 2;

        /// <summary>How long a token must be to count as a real word rather than a fragment.</summary>
        private const int MinimumUpgradeTokenLength = 3;

        public static bool IsWorthUpgrading(string? query)
        {
            var tokens = Tokenizer.Tokenize(query);
            var substantial = tokens.Count(t => t.Length >= MinimumUpgradeTokenLength);

            return substantial >= MinimumUpgradeTokens;
        }

        public static bool HasDominantWinner(IReadOnlyList<double> scores)
        {
            ArgumentNullException.ThrowIfNull(scores);

            if (scores.Count == 0)
            {
                return false;
            }

            // A single hit is dominant by definition: nothing else matched at all.
            if (scores.Count == 1)
            {
                return true;
            }

            return scores[1] <= 0 || scores[0] / scores[1] >= DominanceRatio;
        }

        /// <summary>
        /// Routes a query.
        /// </summary>
        /// <param name="query">The raw query text.</param>
        /// <param name="names">The library's name vocabulary, or null when no index exists yet.</param>
        /// <returns>The decision and the rule that produced it.</returns>
        public static RouteDecision Decide(string? query, INameDictionary? names = null)
        {
            if (string.IsNullOrWhiteSpace(query))
            {
                return new RouteDecision(QueryRoute.Native, "empty query");
            }

            var trimmed = query.Trim();

            // A quoted string means the user is reciting dialogue. That is quote
            // search, and it is never a title lookup.
            if (IsQuoted(trimmed))
            {
                return new RouteDecision(QueryRoute.Concierge, "quoted — dialogue search", true);
            }

            var tokens = Tokenizer.Tokenize(trimmed);
            if (tokens.Count == 0)
            {
                return new RouteDecision(QueryRoute.Native, "no searchable tokens");
            }

            var hasFunctionWord = false;
            var hasConstraint = false;
            foreach (var token in tokens)
            {
                if (FunctionWords.Contains(token))
                {
                    hasFunctionWord = true;
                }

                if (ConstraintWords.Contains(token) || EraWords.Contains(token) || IsYear(token))
                {
                    hasConstraint = true;
                }
            }

            // Checked before length, so "the lord of the rings" is recognised as the
            // title it is rather than treated as a sentence for its function words.
            if (tokens.Count <= 6 && names?.LooksLikeKnownName(trimmed) == true)
            {
                return new RouteDecision(QueryRoute.Native, "matches names in the library");
            }

            if (tokens.Count <= 2 && !hasFunctionWord && !hasConstraint)
            {
                // Short, and the check above already established it names nothing in
                // the library — so it is not somebody typing a title they know. It is
                // a two-word description, and "dark comedy" is as real a search as
                // "something dark and funny" is.
                //
                // Measured on the owner's library before this was fixed: "dark
                // comedy", "weed comedy" and "comedy" all fell through to native and
                // returned nothing, because the rule assumed short meant title.
                // Both is the honest answer — native still renders instantly and for
                // free, and Concierge adds semantic results for one embedding call
                // costing a rounding error.
                //
                // With no dictionary there is no index to search either, so the free
                // path is the only path.
                return names is null
                    ? new RouteDecision(QueryRoute.Native, "too short to be a description")
                    : new RouteDecision(QueryRoute.Both, "short, and names nothing in the library");
            }

            // Worth a plan pass when a constraint is stated outright, or when the
            // query is long enough that one may be buried in the prose.
            var worthPlanning = hasConstraint || tokens.Count >= 5;

            if (tokens.Count >= 4)
            {
                return new RouteDecision(
                    QueryRoute.Concierge, "long enough to be a description", worthPlanning);
            }

            if (hasConstraint)
            {
                return new RouteDecision(QueryRoute.Concierge, "carries a time or length constraint", true);
            }

            if (hasFunctionWord)
            {
                return new RouteDecision(QueryRoute.Concierge, "reads as a sentence", worthPlanning);
            }

            // Three content words that name nothing in the library. Keyword search has
            // nothing to grip, so this is a description of a mood or a subject —
            // "dark twisted thriller" — and the semantic half is the one that can
            // answer it.
            if (names is not null && !names.LooksLikeKnownName(trimmed))
            {
                return new RouteDecision(QueryRoute.Concierge, "names nothing in the library", worthPlanning);
            }

            return new RouteDecision(QueryRoute.Both, "ambiguous — run both");
        }

        private static bool IsQuoted(string text)
        {
            if (text.Length < 3)
            {
                return false;
            }

            return (text[0] == '"' && text[^1] == '"')
                || (text[0] == '“' && text[^1] == '”')
                || (text[0] == '\'' && text[^1] == '\'');
        }

        /// <summary>A bare four-digit year, e.g. "1994".</summary>
        private static bool IsYear(string token)
        {
            if (token.Length != 4)
            {
                return false;
            }

            foreach (var c in token)
            {
                if (!char.IsDigit(c))
                {
                    return false;
                }
            }

            return token[0] == '1' || token[0] == '2';
        }
    }
}
