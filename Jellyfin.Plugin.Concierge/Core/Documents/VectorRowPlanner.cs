using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Jellyfin.Plugin.Concierge.Core.Retrieval;

namespace Jellyfin.Plugin.Concierge.Core.Documents
{
    /// <summary>
    /// Decides which vectors an item owns.
    /// </summary>
    /// <remarks>
    /// Pure, because it decides what the index can ever match and is therefore worth
    /// pinning. Each item gets:
    /// <list type="bullet">
    /// <item><description>
    /// its <b>document</b> row — everything, for general similarity;
    /// </description></item>
    /// <item><description>
    /// a <b>vibe</b> row of genres and themes alone, so a mood query matches something
    /// short instead of being averaged against a plot summary;
    /// </description></item>
    /// <item><description>
    /// one <b>ask</b> row per generated phrasing, so a half-remembered sentence is
    /// compared against other half-remembered sentences.
    /// </description></item>
    /// </list>
    /// Retrieval collapses an item's rows back to its best one before fusion, so more
    /// rows buy recall without letting one well-enriched item flood the results.
    /// </remarks>
    public static class VectorRowPlanner
    {
        /// <summary>
        /// Lays out the rows for a set of documents.
        /// </summary>
        /// <param name="documents">The documents, enrichment attached where it exists.</param>
        /// <param name="maxAsks">How many generated phrasings to keep per item.</param>
        /// <returns>The row sources and the parallel texts to embed.</returns>
        public static (List<VectorRowSource> Rows, List<string> Texts) Plan(
            IReadOnlyList<ItemDocument> documents,
            int maxAsks)
        {
            ArgumentNullException.ThrowIfNull(documents);

            var rows = new List<VectorRowSource>(documents.Count * 4);
            var texts = new List<string>(documents.Count * 4);

            void Add(Guid itemId, VectorRowKind kind, string text)
            {
                if (string.IsNullOrWhiteSpace(text))
                {
                    return;
                }

                rows.Add(new VectorRowSource(itemId, kind, text));
                texts.Add(text);
            }

            foreach (var document in documents)
            {
                Add(document.ItemId, VectorRowKind.Document, document.RenderEmbeddingText());

                if (document.Enrichment is not { } enrichment)
                {
                    continue;
                }

                if (enrichment.Themes.Count > 0)
                {
                    Add(document.ItemId, VectorRowKind.Vibe, Vibe(document, enrichment));
                }

                foreach (var ask in enrichment.Asks.Take(Math.Max(0, maxAsks)))
                {
                    Add(document.ItemId, VectorRowKind.Ask, ask);
                }
            }

            return (rows, texts);
        }

        /// <summary>
        /// The mood row: what an item is about and what watching it feels like.
        /// </summary>
        /// <remarks>
        /// <b>Written as a sentence, not a list.</b> The other side of this comparison
        /// is a person typing "something dark and twisted from the nineties" — a
        /// phrase. A comma-separated dump of genres and themes is not a phrase, and
        /// embedding models put prose nearer prose. The words are identical; the
        /// shape is what changes.
        /// <para>
        /// The era is in because mood queries carry one so often — "nostalgic 90s
        /// classics" is two thirds mood and one third decade, and without it the
        /// decade has nowhere on this row to land.
        /// </para>
        /// <para>
        /// Everything else is deliberately still out. This row's whole advantage is
        /// that it is short: the same themes sit inside the document row too, where a
        /// title, cast, studios and a full overview dilute seven words of tone to
        /// nothing. Adding plot here would recreate exactly that.
        /// </para>
        /// </remarks>
        /// <param name="document">The item.</param>
        /// <param name="enrichment">Its enrichment.</param>
        /// <returns>The text to embed.</returns>
        public static string Vibe(ItemDocument document, Enrichment enrichment)
        {
            ArgumentNullException.ThrowIfNull(document);
            ArgumentNullException.ThrowIfNull(enrichment);

            // A theme that merely repeats a genre spends one of the few words this row
            // has on saying "horror" twice.
            var seen = new HashSet<string>(document.Genres, StringComparer.OrdinalIgnoreCase);
            var themes = enrichment.Themes.Where(theme => seen.Add(theme)).ToList();

            var kind = string.Equals(document.Kind, "Series", StringComparison.OrdinalIgnoreCase)
                || string.Equals(document.Kind, "Episode", StringComparison.OrdinalIgnoreCase)
                    ? "television"
                    : "a film";

            var decade = EraTokens.Decade(document.Year);
            var text = new StringBuilder();

            text.Append(kind);

            if (document.Genres.Count > 0)
            {
                text.Append(' ').Append(string.Join(", ", document.Genres).ToLowerInvariant());
            }

            if (decade.Length > 0)
            {
                text.Append(" from the ").Append(decade);
            }

            if (themes.Count > 0)
            {
                text.Append(". It is about ").Append(string.Join(", ", themes)).Append('.');
            }

            // Tried and rejected: repeating the tone words alone at the end, to pull
            // the row's centre further towards feeling. It works, and it doubles the
            // length — and this row's entire advantage over the document row is that
            // it is short. A test holds it under two hundred characters for that
            // reason, and being right about the trade is worth more than being
            // slightly better at one kind of query.

            return text.ToString();
        }
    }
}
