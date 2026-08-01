using System;
using System.Collections.Generic;

namespace Jellyfin.Plugin.Concierge.Core.Retrieval
{
    /// <summary>
    /// One item and what a single retriever thought of it.
    /// </summary>
    /// <remarks>
    /// Always one entry per <em>item</em>, never per vector row. The vector index
    /// collapses an item's rows to its best one before returning, because an item
    /// owns its document row plus a row for every generated phrasing — and without
    /// the collapse one thoroughly-enriched film takes eight of the top ten slots
    /// (§4.4).
    /// </remarks>
    /// <param name="ItemId">The item.</param>
    /// <param name="Score">The retriever's raw score. Not comparable across retrievers.</param>
    public sealed record ScoredItem(Guid ItemId, double Score);

    /// <summary>
    /// One item's position after fusion.
    /// </summary>
    /// <param name="ItemId">The item.</param>
    /// <param name="Score">The fused score.</param>
    /// <param name="LexicalRank">Its 1-based lexical rank, or null if lexical did not return it.</param>
    /// <param name="VectorRank">Its 1-based vector rank, or null if the vector search did not return it.</param>
    public sealed record FusedResult(Guid ItemId, double Score, int? LexicalRank, int? VectorRank)
    {
        /// <summary>
        /// Gets whether both retrievers found this item.
        /// </summary>
        /// <remarks>
        /// Worth surfacing in the query log: agreement between a keyword match and a
        /// semantic one is the strongest free signal available, and a results page
        /// where nothing agrees usually means the index is stale rather than that the
        /// library lacks the film.
        /// </remarks>
        public bool FoundByBoth => LexicalRank is not null && VectorRank is not null;
    }

    /// <summary>
    /// Reciprocal rank fusion.
    /// </summary>
    /// <remarks>
    /// Chosen over weighted score blending because BM25 scores and cosine
    /// similarities are not on comparable scales — BM25 is unbounded and corpus
    /// dependent, cosine is [-1,1] — so any weight between them is a magic number
    /// that happens to suit one library. RRF reads only <em>ranks</em>, has a single
    /// parameter, and is hard to make badly wrong.
    /// </remarks>
    public static class RankFusion
    {
        /// <summary>
        /// The standard damping constant. Large enough that the top few ranks are
        /// not wildly dominant, small enough that rank still matters.
        /// </summary>
        public const int DefaultK = 60;

        /// <summary>
        /// Fuses a lexical and a vector ranking.
        /// </summary>
        /// <param name="lexical">Lexical results, best first.</param>
        /// <param name="vector">Vector results, best first.</param>
        /// <param name="k">The damping constant.</param>
        /// <returns>The fused ranking, best first.</returns>
        public static IReadOnlyList<FusedResult> Fuse(
            IReadOnlyList<ScoredItem> lexical,
            IReadOnlyList<ScoredItem> vector,
            int k = DefaultK)
        {
            ArgumentNullException.ThrowIfNull(lexical);
            ArgumentNullException.ThrowIfNull(vector);

            var scores = new Dictionary<Guid, double>();
            var lexicalRanks = new Dictionary<Guid, int>();
            var vectorRanks = new Dictionary<Guid, int>();

            Accumulate(lexical, k, scores, lexicalRanks);
            Accumulate(vector, k, scores, vectorRanks);

            var fused = new List<FusedResult>(scores.Count);
            foreach (var (itemId, score) in scores)
            {
                fused.Add(new FusedResult(
                    itemId,
                    score,
                    lexicalRanks.TryGetValue(itemId, out var lr) ? lr : null,
                    vectorRanks.TryGetValue(itemId, out var vr) ? vr : null));
            }

            // Ties broken by id so a given index always produces a given order.
            // Without it, two items on identical scores swap places between calls and
            // an evaluation run stops being reproducible.
            fused.Sort((a, b) =>
            {
                var byScore = b.Score.CompareTo(a.Score);
                return byScore != 0 ? byScore : a.ItemId.CompareTo(b.ItemId);
            });

            return fused;
        }

        private static void Accumulate(
            IReadOnlyList<ScoredItem> ranking,
            int k,
            Dictionary<Guid, double> scores,
            Dictionary<Guid, int> ranks)
        {
            for (var i = 0; i < ranking.Count; i++)
            {
                var rank = i + 1;
                var itemId = ranking[i].ItemId;

                // A retriever that somehow returns an item twice must not be paid
                // twice for it.
                if (!ranks.TryAdd(itemId, rank))
                {
                    continue;
                }

                scores.TryGetValue(itemId, out var existing);
                scores[itemId] = existing + (1.0 / (k + rank));
            }
        }
    }
}
