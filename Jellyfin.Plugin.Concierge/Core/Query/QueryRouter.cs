using System;
using System.Collections.Generic;
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
    public sealed record RouteDecision(QueryRoute Route, string Reason);

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
                return new RouteDecision(QueryRoute.Concierge, "quoted — dialogue search");
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
                return new RouteDecision(QueryRoute.Native, "too short to be a description");
            }

            if (tokens.Count >= 4)
            {
                return new RouteDecision(QueryRoute.Concierge, "long enough to be a description");
            }

            if (hasConstraint)
            {
                return new RouteDecision(QueryRoute.Concierge, "carries a time or length constraint");
            }

            if (hasFunctionWord)
            {
                return new RouteDecision(QueryRoute.Concierge, "reads as a sentence");
            }

            // Three content words that name nothing in the library. Keyword search has
            // nothing to grip, so this is a description of a mood or a subject —
            // "dark twisted thriller" — and the semantic half is the one that can
            // answer it.
            if (names is not null && !names.LooksLikeKnownName(trimmed))
            {
                return new RouteDecision(QueryRoute.Concierge, "names nothing in the library");
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
