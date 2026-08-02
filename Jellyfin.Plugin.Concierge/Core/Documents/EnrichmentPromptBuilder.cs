using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace Jellyfin.Plugin.Concierge.Core.Documents
{
    /// <summary>
    /// Builds the index-time enrichment prompt: the one paid pass whose output is
    /// cached forever and which sets the recall ceiling for every future query.
    /// </summary>
    public static class EnrichmentPromptBuilder
    {
        /// <summary>How many phrasings to ask for per item.</summary>
        /// <remarks>
        /// The asks are the heavy lifter — each is embedded as its own row, so a vague
        /// sentence is compared against other vague sentences rather than against
        /// marketing copy. More of them is more surface for a half-remembered query to
        /// land on, at index-time cost only.
        /// </remarks>
        public const int DefaultAsksPerItem = 10;

        /// <summary>How many themes to ask for per item.</summary>
        public const int DefaultThemesPerItem = 8;

        /// <summary>How many moments to ask for per item.</summary>
        public const int DefaultMomentsPerItem = 6;

        /// <summary>
        /// The system prompt.
        /// </summary>
        /// <remarks>
        /// Two things here are load-bearing and neither is decoration.
        /// <para>
        /// <b>Permission to know nothing.</b> This pass works because the model has
        /// seen these films. For an obscure or brand-new title it has not, and a model
        /// with nothing to say invents — and an invented phrasing is a permanent wrong
        /// answer that costs nothing to create and stays invisible until somebody
        /// searches for it. The instruction to return <c>known: false</c> is stated
        /// early, twice, and framed as the correct answer rather than a failure,
        /// because a model asked for eight phrasings will produce eight phrasings.
        /// </para>
        /// <para>
        /// <b>Themes carry tone, not just subject.</b> Mood searches — "dark and
        /// twisted", "something warm", "nothing too heavy" — have no plot to match
        /// against. They land on themes or they land nowhere, so themes are asked for
        /// in both registers explicitly.
        /// </para>
        /// </remarks>
        public const string SystemPrompt =
            """
            You help a media library's search engine find films and shows from vague,
            half-remembered descriptions.

            For each item you are given, write what someone searching for it might
            actually type — not marketing copy. Overviews describe the premise; people
            remember moments, images, and how something felt to watch.

            IF YOU DO NOT GENUINELY KNOW THE TITLE, say so by returning "known": false
            with empty lists. That is a correct and useful answer. Do not guess from
            the title, do not extrapolate from the genre, and do not reconstruct a
            plausible plot. A confident invention is far worse here than an admission,
            because it becomes a permanent wrong search result that nobody will trace
            back to you.

            For each item you do know, return:

            - premise: one or two sentences saying what actually happens — what the
              overview should have said. Plain, concrete, no blurb language.
            - moments: specific images or scenes people remember, as many as the
              instruction below asks for. The dog. The
              chestburster. The bullet-time. Concrete nouns, not summary.
            - themes: short phrases, as many as the instruction below asks for,
              covering BOTH what it is about (identity,
              surveillance, grief) AND what watching it feels like (bleak, warm,
              tense, silly, nostalgic, dark, twisted, comforting). The second kind is
              what someone means when they ask for "something dark and twisted" or "a
              comfort watch", so do not skip it.
            - asks: ways a person might describe this to a friend when they have
              forgotten the title. The instruction below says how many; produce that
              many. Write them as real search phrases — "the one where
              they kill the guy's dog", "that movie with the spinning top". Vary them:
              some about plot, some about a single image, some about the feeling or
              the era. Never include the title itself.
            - spoiler: true if the premise or the moments give away a twist or ending.

            Be exact about films you know. Be silent about films you do not.
            """;

        /// <summary>
        /// Renders the item list for one batch.
        /// </summary>
        /// <remarks>
        /// Items are addressed by <b>batch-local integer index</b> and never by
        /// Jellyfin id (hard rule 1). The parser discards any index outside the batch,
        /// which is what makes it structurally impossible for this pass to attach
        /// enrichment to an item that was not in it.
        /// </remarks>
        /// <param name="documents">The batch, in order.</param>
        /// <returns>The user prompt body.</returns>
        public static string BuildItemList(IReadOnlyList<ItemDocument> documents)
        {
            ArgumentNullException.ThrowIfNull(documents);

            var text = new StringBuilder();
            for (var i = 0; i < documents.Count; i++)
            {
                var document = documents[i];
                text.Append(i.ToString(CultureInfo.InvariantCulture)).Append(". ");
                text.Append(document.FullTitle);

                if (document.Year is { } year)
                {
                    text.Append(" (").Append(year.ToString(CultureInfo.InvariantCulture)).Append(')');
                }

                text.Append(" [").Append(document.Kind).Append(']');

                if (document.Genres.Count > 0)
                {
                    text.Append(" — ").Append(string.Join(", ", document.Genres));
                }

                if (!string.IsNullOrWhiteSpace(document.Overview))
                {
                    text.Append('\n').Append("   ").Append(Collapse(document.Overview));
                }

                text.Append('\n');
            }

            return text.ToString();
        }

        /// <summary>
        /// The instruction that follows the item list.
        /// </summary>
        /// <param name="count">How many items are in the batch.</param>
        /// <param name="asksPerItem">How many phrasings to ask for.</param>
        /// <returns>The trailing instruction.</returns>
        public static string BuildInstruction(
            int count,
            int asksPerItem = DefaultAsksPerItem,
            int themesPerItem = DefaultThemesPerItem,
            int momentsPerItem = DefaultMomentsPerItem)
        {
            var body = string.Create(
                CultureInfo.InvariantCulture,
                $"""

                Return one object per item, using the integer index above as "i".
                Cover all {count} items, in order. For items you know, give
                {asksPerItem} entries in "asks", {themesPerItem} in "themes" and
                {momentsPerItem} in "moments" — those numbers, not fewer. For items you
                do not know, leave every list empty.

                Respond with JSON only:
                """);

            return body + "\n" + ResponseTemplate;
        }

        /// <summary>
        /// The shape asked for in prose, for providers with no structured output.
        /// </summary>
        /// <remarks>
        /// Kept as its own literal rather than interpolated into the instruction: a
        /// JSON example is mostly braces, and every one of them would need doubling
        /// inside an interpolated string. Curator's rule applies here too — this must
        /// describe exactly the fields the schema declares, in both provider
        /// dialects, or the model writes a missing field into the previous string.
        /// </remarks>
        public const string ResponseTemplate =
            """
            {"items":[{"i":0,"known":true,"premise":"...","moments":["..."],
            "themes":["..."],"asks":["..."],"spoiler":false}]}
            """;

        private static string Collapse(string text)
        {
            var collapsed = text.Replace('\r', ' ').Replace('\n', ' ').Trim();
            return collapsed.Length <= 600 ? collapsed : collapsed[..600];
        }
    }
}
