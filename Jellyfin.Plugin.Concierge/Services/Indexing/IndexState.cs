using System;
using System.Collections.Generic;
using Jellyfin.Plugin.Concierge.Configuration;
using Jellyfin.Plugin.Concierge.Core.Documents;
using Jellyfin.Plugin.Concierge.Core.Retrieval;

namespace Jellyfin.Plugin.Concierge.Services.Indexing
{
    /// <summary>
    /// What the stored index was built with.
    /// </summary>
    /// <remarks>
    /// <b>This is the index's identity, and it is load-bearing</b> (hard rule 9).
    /// Vectors written by one embedding model are not comparable with vectors
    /// written by another, and mixing them does not error — it just ranks garbage.
    /// The prefixes are part of the identity for the same reason: a document
    /// embedded under <c>passage: </c> and a query embedded under nothing are two
    /// different spaces, and the only symptom is worse results.
    /// </remarks>
    /// <param name="Generation">
    /// Bumped on every write. The query cache is keyed on it, so a rebuild
    /// invalidates every cached answer without a sweep.
    /// </param>
    /// <param name="EmbeddingModel">The model every vector was written by.</param>
    /// <param name="Dimensions">The vector width.</param>
    /// <param name="QueryPrefix">The marker queries were embedded under.</param>
    /// <param name="DocumentPrefix">The marker documents were embedded under.</param>
    /// <param name="BuiltUtc">When the index was last written.</param>
    /// <param name="ItemCount">How many items it holds.</param>
    /// <param name="RowCount">How many vector rows it holds.</param>
    /// <param name="EnrichedCount">How many items carry non-empty enrichment.</param>
    public sealed record IndexState(
        long Generation,
        string EmbeddingModel,
        int Dimensions,
        string QueryPrefix,
        string DocumentPrefix,
        DateTime BuiltUtc,
        int ItemCount,
        int RowCount,
        int EnrichedCount)
    {
        /// <summary>
        /// Whether an index written under this state can still be used with a given
        /// embedding profile.
        /// </summary>
        /// <remarks>
        /// Refuse, do not degrade. Returning false means the index is discarded and
        /// rebuilt, which costs money and time; using it anyway costs nothing and
        /// silently ruins every search, which is worse in every way that matters.
        /// </remarks>
        /// <param name="profile">The profile that would be used now.</param>
        /// <returns>True when the stored vectors are still valid.</returns>
        public bool IsUsableWith(EmbeddingProfile profile)
        {
            ArgumentNullException.ThrowIfNull(profile);

            return string.Equals(EmbeddingModel, profile.Model, StringComparison.OrdinalIgnoreCase)
                && (profile.Dimensions == 0 || Dimensions == profile.Dimensions)
                && string.Equals(QueryPrefix, profile.QueryPrefix, StringComparison.Ordinal)
                && string.Equals(DocumentPrefix, profile.DocumentPrefix, StringComparison.Ordinal);
        }

        /// <summary>
        /// Says, in one line an owner can act on, why a stored index was rejected.
        /// </summary>
        /// <param name="profile">The profile that would be used now.</param>
        /// <returns>The reason, or empty when the index is usable.</returns>
        public string ExplainMismatch(EmbeddingProfile profile)
        {
            ArgumentNullException.ThrowIfNull(profile);

            if (!string.Equals(EmbeddingModel, profile.Model, StringComparison.OrdinalIgnoreCase))
            {
                return $"the index was built with '{EmbeddingModel}' and the profile now names '{profile.Model}'";
            }

            if (profile.Dimensions != 0 && Dimensions != profile.Dimensions)
            {
                return $"the index holds {Dimensions}-dimension vectors and the profile now asks for {profile.Dimensions}";
            }

            if (!string.Equals(QueryPrefix, profile.QueryPrefix, StringComparison.Ordinal)
                || !string.Equals(DocumentPrefix, profile.DocumentPrefix, StringComparison.Ordinal))
            {
                return "the query/document prefixes changed, so stored vectors sit in a different space";
            }

            return string.Empty;
        }
    }

    /// <summary>
    /// A loaded index: the documents, the two retrievers built over them, and the
    /// state that says what they were built with.
    /// </summary>
    /// <param name="State">The index identity.</param>
    /// <param name="Documents">Every indexed document, enrichment attached.</param>
    /// <param name="Lexical">The BM25 index.</param>
    /// <param name="Vectors">The vector index.</param>
    public sealed record ConciergeIndex(
        IndexState State,
        IReadOnlyList<ItemDocument> Documents,
        Bm25Index Lexical,
        VectorIndex Vectors);
}
