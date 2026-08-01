using System;
using System.Collections.Generic;
using System.Linq;
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

                // Genres ride along with the themes because "romance" and "horror" are
                // part of how people describe a mood, not just a shelf to file it on.
                if (enrichment.Themes.Count > 0)
                {
                    Add(
                        document.ItemId,
                        VectorRowKind.Vibe,
                        string.Join(", ", document.Genres.Concat(enrichment.Themes)));
                }

                foreach (var ask in enrichment.Asks.Take(Math.Max(0, maxAsks)))
                {
                    Add(document.ItemId, VectorRowKind.Ask, ask);
                }
            }

            return (rows, texts);
        }
    }
}
