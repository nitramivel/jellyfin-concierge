using System;
using System.Collections.Generic;
using System.Linq;

namespace Jellyfin.Plugin.Concierge.Core.Retrieval
{
    /// <summary>
    /// What a single embedded row points back at.
    /// </summary>
    /// <param name="ItemId">The item this row belongs to.</param>
    /// <param name="Kind">Whether this is the item's own document or one generated phrasing.</param>
    /// <param name="Text">The text that was embedded, kept for the query log.</param>
    public sealed record VectorRowSource(Guid ItemId, VectorRowKind Kind, string Text);

    /// <summary>Which sort of text a vector row holds.</summary>
    public enum VectorRowKind
    {
        /// <summary>The item's own rendered document.</summary>
        Document = 0,

        /// <summary>One generated "how someone would describe it" phrasing.</summary>
        Ask = 1,

        /// <summary>
        /// The item's themes alone — what it is about and what watching it feels
        /// like — as a short vector of their own.
        /// </summary>
        /// <remarks>
        /// Exists because a mood query has nowhere else to land. The themes are also
        /// inside the document row, but that row averages a title, genres, cast, a
        /// full overview and a premise, so seven words of tone are diluted to
        /// nothing. Measured on the owner's library: "erotic" ranked Fifty Shades of
        /// Grey first on keywords alone, "sexy" did not return it at all, and the
        /// semantic half could not bridge the two because the only vector carrying
        /// "erotic" was mostly about a college newspaper and a helicopter.
        /// <para>
        /// Costs one extra row per enriched item and no extra model call — the themes
        /// were already generated.
        /// </para>
        /// </remarks>
        Vibe = 2,
    }

    /// <summary>
    /// Cosine similarity over a packed float array. Brute force, and deliberately so.
    /// </summary>
    /// <remarks>
    /// No ANN index. HNSW is a dependency, a build step and a class of correctness
    /// bug bought to solve a problem this plugin does not have: a full scan of 90,000
    /// rows is about 30ms, and the owner's library is 1,900. <b>Memory is the
    /// constraint here, never speed</b> — which is why the vector width and the
    /// number of phrasings per item are both settings rather than constants.
    /// <para>
    /// Vectors are L2-normalized once at build, so the per-query work is a plain dot
    /// product with no divisions in the inner loop.
    /// </para>
    /// </remarks>
    public sealed class VectorIndex
    {
        private readonly float[] _packed;
        private readonly VectorRowSource[] _rows;

        private VectorIndex(float[] packed, VectorRowSource[] rows, int dimensions)
        {
            _packed = packed;
            _rows = rows;
            Dimensions = dimensions;
        }

        /// <summary>Gets the vector width every row shares.</summary>
        public int Dimensions { get; }

        /// <summary>Gets how many rows are indexed.</summary>
        public int RowCount => _rows.Length;

        /// <summary>
        /// Builds the index from rows and their vectors.
        /// </summary>
        /// <param name="sources">What each row points at.</param>
        /// <param name="vectors">The vectors, one per source, all the same width.</param>
        /// <returns>The index.</returns>
        /// <exception cref="ArgumentException">The inputs disagree in count or width.</exception>
        public static VectorIndex Build(
            IReadOnlyList<VectorRowSource> sources,
            IReadOnlyList<float[]> vectors)
        {
            ArgumentNullException.ThrowIfNull(sources);
            ArgumentNullException.ThrowIfNull(vectors);

            if (sources.Count != vectors.Count)
            {
                throw new ArgumentException(
                    $"Concierge: {sources.Count} row sources against {vectors.Count} vectors.",
                    nameof(vectors));
            }

            if (sources.Count == 0)
            {
                return new VectorIndex([], [], 0);
            }

            var dimensions = vectors[0].Length;
            var packed = new float[sources.Count * dimensions];

            for (var row = 0; row < vectors.Count; row++)
            {
                var vector = vectors[row];
                if (vector.Length != dimensions)
                {
                    // Mixed widths mean two embedding models got into one index.
                    // Refuse, do not degrade: mixed vectors rank garbage and nothing
                    // else in the system would ever report a problem.
                    throw new ArgumentException(
                        $"Concierge: row {row} has {vector.Length} dimensions, expected {dimensions}. "
                        + "The index holds vectors from more than one embedding model and must be rebuilt.",
                        nameof(vectors));
                }

                Normalize(vector, packed, row * dimensions);
            }

            return new VectorIndex(packed, [.. sources], dimensions);
        }

        /// <summary>
        /// Finds the items whose best row is closest to the query vector.
        /// </summary>
        /// <remarks>
        /// <b>Collapses rows to items before returning</b> (§4.4). An item owns its
        /// document row plus one per generated phrasing, so the raw scan returns rows;
        /// handing those to fusion unchanged would let a single thoroughly-enriched
        /// film occupy eight of the top ten slots. Each item is represented once, by
        /// its best-scoring row.
        /// </remarks>
        /// <param name="query">The query vector. Need not be normalized.</param>
        /// <param name="limit">How many items to return at most.</param>
        /// <returns>Items, best first.</returns>
        public IReadOnlyList<ScoredItem> Search(float[] query, int limit)
        {
            ArgumentNullException.ThrowIfNull(query);

            if (RowCount == 0 || limit <= 0)
            {
                return [];
            }

            if (query.Length != Dimensions)
            {
                throw new ArgumentException(
                    $"Concierge: the query vector has {query.Length} dimensions and the index has {Dimensions}. "
                    + "The embedding model changed under the index; rebuild it.",
                    nameof(query));
            }

            var normalized = new float[Dimensions];
            Normalize(query, normalized, 0);

            var best = new Dictionary<Guid, double>();

            for (var row = 0; row < _rows.Length; row++)
            {
                var offset = row * Dimensions;
                double dot = 0;
                for (var d = 0; d < Dimensions; d++)
                {
                    dot += _packed[offset + d] * normalized[d];
                }

                var itemId = _rows[row].ItemId;
                if (!best.TryGetValue(itemId, out var current) || dot > current)
                {
                    best[itemId] = dot;
                }
            }

            return best
                .OrderByDescending(pair => pair.Value)
                .ThenBy(pair => pair.Key)
                .Take(limit)
                .Select(pair => new ScoredItem(pair.Key, pair.Value))
                .ToList();
        }

        /// <summary>
        /// Enumerates every row with its stored vector.
        /// </summary>
        /// <remarks>
        /// For the indexer's reuse pass: a row whose text has not changed does not
        /// need re-embedding, and that is what makes a nightly rebuild of an unchanged
        /// library cost nothing at all.
        /// <para>
        /// The vectors handed back are the normalized copies rather than whatever was
        /// originally passed in. That is harmless — cosine similarity is invariant to
        /// scale, so a normalized vector ranks identically to the vector it came from,
        /// and normalizing it again is a no-op.
        /// </para>
        /// </remarks>
        /// <returns>Each row source paired with its vector.</returns>
        public IEnumerable<(VectorRowSource Source, float[] Vector)> EnumerateRows()
        {
            for (var row = 0; row < _rows.Length; row++)
            {
                var vector = new float[Dimensions];
                Array.Copy(_packed, row * Dimensions, vector, 0, Dimensions);
                yield return (_rows[row], vector);
            }
        }

        /// <summary>
        /// Writes an L2-normalized copy of <paramref name="source"/> into
        /// <paramref name="destination"/> at <paramref name="offset"/>.
        /// </summary>
        /// <remarks>
        /// A zero vector is left as zeros rather than producing NaN. It scores 0
        /// against everything, which is the right answer for a row that carries no
        /// information.
        /// </remarks>
        private static void Normalize(float[] source, float[] destination, int offset)
        {
            double sum = 0;
            foreach (var value in source)
            {
                sum += value * value;
            }

            if (sum <= 0)
            {
                return;
            }

            var scale = 1.0 / Math.Sqrt(sum);
            for (var i = 0; i < source.Length; i++)
            {
                destination[offset + i] = (float)(source[i] * scale);
            }
        }
    }
}
