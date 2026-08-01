using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using Jellyfin.Plugin.Concierge.Core.Documents;

namespace Jellyfin.Plugin.Concierge.Core.Ranking
{
    /// <summary>
    /// Builds the re-rank pass: one call that puts the shortlist in order and says
    /// why each item is on it.
    /// </summary>
    /// <remarks>
    /// Retrieval's job is recall — get the right item into the shortlist. This is
    /// precision, and the model already knows these films. On the owner's library,
    /// "michael scott" put Scott Pilgrim vs. the World above The Office because a
    /// title match outweighs a character name nobody indexed; a model looking at both
    /// has no difficulty at all.
    /// </remarks>
    public static class RerankPromptBuilder
    {
        /// <summary>
        /// The system prompt.
        /// </summary>
        /// <remarks>
        /// Two rules here are load-bearing.
        /// <para>
        /// <b>It orders, it does not select.</b> The parser treats the answer as a
        /// preference over the shortlist, so a dropped index changes nothing — but a
        /// model told to "pick the best" will return three items and think it has
        /// done well, and every item it silently left out is a correct answer somebody
        /// searched for.
        /// </para>
        /// <para>
        /// <b>The reasons must not spoil.</b> The shortlist deliberately carries
        /// spoilers, because "the one where he was dead the whole time" has to work.
        /// What must never happen is that twist appearing under a poster of a film
        /// the reader has not seen.
        /// </para>
        /// </remarks>
        public const string SystemPrompt =
            """
            You are ordering search results for someone looking through their own
            media library. They typed a description; below is a shortlist of things
            they own, each with a number.

            Put the shortlist in the order you would show it, best match first, and
            give each entry one short line saying why it matched.

            Rules:

            - Order the WHOLE list. Do not select, do not filter, do not drop anything
              you think is a poor match — put it lower instead. Something you leave out
              is something the searcher cannot find.
            - Use each number exactly once. Never invent a number that is not listed.
            - "why" is one clause, under twelve words, naming what actually connects it
              to the search: "amnesia, tattoos, told backwards" or "the polite bear
              one". Not a review, not a plot summary, not "matches your search".
            - NEVER put a twist, an ending, or a death in "why". Some entries below
              include spoilers so you can rank them correctly; they exist for your
              judgement only and the searcher will read what you write. If the only
              honest reason is a spoiler, write something vaguer instead.
            - Judge by what the searcher meant, not by word overlap. A character name,
              a quoted line, or a half-remembered image should rank its own title
              first, above anything that merely shares a word with the query.
            """;

        /// <summary>
        /// Renders the shortlist.
        /// </summary>
        /// <remarks>
        /// Items are addressed by <b>batch-local integer index</b> and never by
        /// Jellyfin id (hard rule 1). The parser discards anything outside
        /// <c>0..n-1</c>, which is what makes it structurally impossible for this pass
        /// to return something the searcher does not own.
        /// </remarks>
        /// <param name="documents">The shortlist, in fused order.</param>
        /// <returns>The candidate list.</returns>
        public static string BuildCandidates(IReadOnlyList<ItemDocument> documents)
        {
            ArgumentNullException.ThrowIfNull(documents);

            var text = new StringBuilder();

            for (var i = 0; i < documents.Count; i++)
            {
                var d = documents[i];
                text.Append(i.ToString(CultureInfo.InvariantCulture)).Append(". ").Append(d.Title);

                if (d.Year is { } year)
                {
                    text.Append(" (").Append(year.ToString(CultureInfo.InvariantCulture)).Append(')');
                }

                text.Append(" [").Append(d.Kind).Append(']');

                if (d.Genres.Count > 0)
                {
                    text.Append(" — ").Append(string.Join(", ", d.Genres));
                }

                text.Append('\n');

                // The enrichment is what makes ranking possible at all: the premise
                // says what actually happens and the themes say how it feels, neither
                // of which the overview reliably does.
                if (d.Enrichment is { IsEmpty: false } e)
                {
                    if (!string.IsNullOrWhiteSpace(e.Premise))
                    {
                        text.Append("   ").Append(Collapse(e.Premise, 260)).Append('\n');
                    }

                    if (e.Themes.Count > 0)
                    {
                        text.Append("   themes: ").Append(string.Join(", ", e.Themes.Take(8))).Append('\n');
                    }

                    if (e.Moments.Count > 0)
                    {
                        text.Append("   moments: ").Append(Collapse(string.Join("; ", e.Moments.Take(4)), 200));

                        // Flagged so the model knows which lines it must not repeat
                        // back, rather than having to guess what counts as a spoiler.
                        text.Append(e.Spoiler ? "   [SPOILERS — do not repeat]\n" : "\n");
                    }
                }
                else if (!string.IsNullOrWhiteSpace(d.Overview))
                {
                    text.Append("   ").Append(Collapse(d.Overview, 220)).Append('\n');
                }
            }

            return text.ToString();
        }

        /// <summary>
        /// The instruction that follows the shortlist.
        /// </summary>
        /// <param name="query">What the searcher typed.</param>
        /// <param name="count">How many candidates are listed.</param>
        /// <returns>The trailing instruction.</returns>
        public static string BuildInstruction(string query, int count)
        {
            return string.Create(
                CultureInfo.InvariantCulture,
                $"""

                They searched for: {query}

                Return all {count} numbers in your preferred order.
                """) + "\n" + ResponseTemplate;
        }

        /// <summary>
        /// The shape asked for in prose, matching what the schemas declare.
        /// </summary>
        public const string ResponseTemplate =
            """
            Respond with JSON only:
            {"order":[{"i":0,"why":"..."}]}
            """;

        private static string Collapse(string text, int max)
        {
            var collapsed = text.Replace('\r', ' ').Replace('\n', ' ').Trim();
            return collapsed.Length <= max ? collapsed : collapsed[..max];
        }
    }
}
