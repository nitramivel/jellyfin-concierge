using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Concierge.Configuration;
using Jellyfin.Plugin.Concierge.Core.Documents;
using Jellyfin.Plugin.Concierge.Core.Retrieval;
using MediaBrowser.Common.Configuration;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Concierge.Services.Indexing
{
    /// <summary>
    /// Reads and writes the index under the plugin's data directory.
    /// </summary>
    public interface IIndexStore
    {
        /// <summary>
        /// Loads the index, or null when there is none or it cannot be used with the
        /// given embedding profile.
        /// </summary>
        /// <param name="profile">The embedding profile in force now.</param>
        /// <param name="cancellationToken">Cancellation token.</param>
        /// <returns>The index, or null.</returns>
        Task<ConciergeIndex?> LoadAsync(EmbeddingProfile profile, CancellationToken cancellationToken);

        /// <summary>Writes a freshly built index.</summary>
        /// <param name="state">The index identity.</param>
        /// <param name="documents">The documents, without enrichment attached.</param>
        /// <param name="rows">What each vector row points at.</param>
        /// <param name="vectors">The vectors, parallel to <paramref name="rows"/>.</param>
        /// <param name="cancellationToken">Cancellation token.</param>
        /// <returns>A task.</returns>
        Task SaveAsync(
            IndexState state,
            IReadOnlyList<ItemDocument> documents,
            IReadOnlyList<VectorRowSource> rows,
            IReadOnlyList<float[]> vectors,
            CancellationToken cancellationToken);

        /// <summary>Reads stored enrichment, keyed by item.</summary>
        /// <param name="cancellationToken">Cancellation token.</param>
        /// <returns>The stored enrichment.</returns>
        Task<IReadOnlyDictionary<Guid, StoredEnrichment>> LoadEnrichmentAsync(CancellationToken cancellationToken);

        /// <summary>Writes stored enrichment.</summary>
        /// <param name="enrichment">Everything to keep.</param>
        /// <param name="cancellationToken">Cancellation token.</param>
        /// <returns>A task.</returns>
        Task SaveEnrichmentAsync(
            IReadOnlyCollection<StoredEnrichment> enrichment,
            CancellationToken cancellationToken);

        /// <summary>Reads the stored state without loading vectors.</summary>
        /// <param name="cancellationToken">Cancellation token.</param>
        /// <returns>The state, or null when nothing is stored.</returns>
        Task<IndexState?> LoadStateAsync(CancellationToken cancellationToken);

        /// <summary>
        /// Deletes the whole index.
        /// </summary>
        /// <remarks>
        /// Safe by construction: the index is a cache and the library is read-only
        /// (hard rule 6), so this restores exactly the behaviour the server had
        /// before Concierge was installed. Enrichment goes too — it is the expensive
        /// part, so anything offering this must say so plainly.
        /// </remarks>
        /// <param name="cancellationToken">Cancellation token.</param>
        /// <returns>A task.</returns>
        Task DeleteAsync(CancellationToken cancellationToken);
    }

    /// <summary>
    /// Default <see cref="IIndexStore"/>: plain files under <c>data/concierge</c>.
    /// </summary>
    /// <remarks>
    /// The lexical postings are <em>not</em> persisted, unlike the sketch in the
    /// plan. Rebuilding BM25 from the stored documents takes milliseconds at any
    /// library size this plugin targets, and a derived file that can fall out of step
    /// with its source is a bug waiting to happen. Vectors are persisted because
    /// regenerating those costs money.
    /// </remarks>
    public sealed class IndexStore : IIndexStore
    {
        private const string StateFile = "state.json";
        private const string DocumentsFile = "docs.json";
        private const string RowsFile = "rows.json";
        private const string VectorsFile = "vectors.bin";
        private const string EnrichmentFile = "enrichment.json";

        private static readonly JsonSerializerOptions SerializerOptions = new() { WriteIndented = false };

        private readonly string _directory;
        private readonly ILogger<IndexStore> _logger;
        private readonly SemaphoreSlim _gate = new(1, 1);

        public IndexStore(IApplicationPaths applicationPaths, ILogger<IndexStore> logger)
        {
            ArgumentNullException.ThrowIfNull(applicationPaths);

            _logger = logger;
            _directory = Path.Combine(applicationPaths.DataPath, "concierge");
        }

        /// <inheritdoc />
        public async Task<IndexState?> LoadStateAsync(CancellationToken cancellationToken)
        {
            var path = Path.Combine(_directory, StateFile);
            if (!File.Exists(path))
            {
                return null;
            }

            try
            {
                await using var stream = File.OpenRead(path);
                return await JsonSerializer
                    .DeserializeAsync<IndexState>(stream, SerializerOptions, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning(ex, "Concierge: the index state could not be read");
                return null;
            }
        }

        /// <inheritdoc />
        public async Task<ConciergeIndex?> LoadAsync(EmbeddingProfile profile, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(profile);

            var state = await LoadStateAsync(cancellationToken).ConfigureAwait(false);
            if (state is null)
            {
                return null;
            }

            if (!state.IsUsableWith(profile))
            {
                // Refuse rather than degrade. Serving searches from vectors written by
                // a different model produces plausible-looking nonsense and reports no
                // error anywhere.
                _logger.LogWarning(
                    "Concierge: the stored index cannot be used — {Reason}. Rebuild it from the plugin settings.",
                    state.ExplainMismatch(profile));
                return null;
            }

            try
            {
                var documents = await ReadJsonAsync<List<ItemDocument>>(DocumentsFile, cancellationToken)
                    .ConfigureAwait(false);
                var rows = await ReadJsonAsync<List<VectorRowSource>>(RowsFile, cancellationToken)
                    .ConfigureAwait(false);

                if (documents is null || rows is null)
                {
                    return null;
                }

                var vectors = await ReadVectorsAsync(rows.Count, state.Dimensions, cancellationToken)
                    .ConfigureAwait(false);
                if (vectors is null)
                {
                    return null;
                }

                var enrichment = await LoadEnrichmentAsync(cancellationToken).ConfigureAwait(false);
                var enriched = Attach(documents, enrichment);

                return new ConciergeIndex(
                    state,
                    enriched,
                    Bm25Index.Build(enriched),
                    VectorIndex.Build(rows, vectors));
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning(ex, "Concierge: the index could not be loaded and will be rebuilt");
                return null;
            }
        }

        /// <inheritdoc />
        public async Task SaveAsync(
            IndexState state,
            IReadOnlyList<ItemDocument> documents,
            IReadOnlyList<VectorRowSource> rows,
            IReadOnlyList<float[]> vectors,
            CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(state);
            ArgumentNullException.ThrowIfNull(documents);
            ArgumentNullException.ThrowIfNull(rows);
            ArgumentNullException.ThrowIfNull(vectors);

            await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                Directory.CreateDirectory(_directory);

                // Documents are stored without enrichment: enrichment.json owns it,
                // and one copy cannot disagree with itself.
                var stripped = documents.Select(d => d with { Enrichment = null }).ToList();

                await WriteJsonAsync(DocumentsFile, stripped, cancellationToken).ConfigureAwait(false);
                await WriteJsonAsync(RowsFile, rows, cancellationToken).ConfigureAwait(false);
                await WriteVectorsAsync(vectors, cancellationToken).ConfigureAwait(false);

                // State last. It names the generation everything else belongs to, so
                // a crash midway leaves the previous state pointing at the previous
                // files rather than a half-written set.
                await WriteJsonAsync(StateFile, state, cancellationToken).ConfigureAwait(false);

                _logger.LogInformation(
                    "Concierge: index generation {Generation} written — {Items} item(s), {Rows} vector row(s), "
                    + "{Enriched} enriched, model {Model} at {Dimensions} dimensions",
                    state.Generation,
                    state.ItemCount,
                    state.RowCount,
                    state.EnrichedCount,
                    state.EmbeddingModel,
                    state.Dimensions);
            }
            finally
            {
                _gate.Release();
            }
        }

        /// <inheritdoc />
        public async Task<IReadOnlyDictionary<Guid, StoredEnrichment>> LoadEnrichmentAsync(
            CancellationToken cancellationToken)
        {
            var stored = await ReadJsonAsync<List<StoredEnrichment>>(EnrichmentFile, cancellationToken)
                .ConfigureAwait(false);

            var map = new Dictionary<Guid, StoredEnrichment>();
            foreach (var entry in stored ?? [])
            {
                map[entry.ItemId] = entry;
            }

            return map;
        }

        /// <inheritdoc />
        public async Task SaveEnrichmentAsync(
            IReadOnlyCollection<StoredEnrichment> enrichment,
            CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(enrichment);

            await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                Directory.CreateDirectory(_directory);

                // Merged by item, never replaced wholesale. What arrives here is one
                // run's results, and a run covers whatever subset it was asked for —
                // so writing it directly discards every item the run did not touch.
                //
                // That is not hypothetical. A regeneration over a 5,272-item library
                // was stopped 14 batches in and this method had already reduced a
                // 322-entry store to 131, losing 191 answers that had been paid for
                // across earlier runs. Nothing else on disk held them: docs.json
                // stores documents stripped of enrichment, and only the flattened
                // row text in rows.json happened to preserve the themes and asks.
                //
                // Upserting also gives a regeneration exactly the semantics it wants
                // — a fresh answer replaces the old one for that item — without
                // touching anything it did not re-ask.
                var existing = await ReadJsonAsync<List<StoredEnrichment>>(EnrichmentFile, cancellationToken)
                    .ConfigureAwait(false);

                var merged = new Dictionary<Guid, StoredEnrichment>();
                foreach (var entry in existing ?? [])
                {
                    merged[entry.ItemId] = entry;
                }

                foreach (var entry in enrichment)
                {
                    merged[entry.ItemId] = entry;
                }

                // Entries for items no longer in the library are kept. They are about
                // 450 bytes each, they cost real money to produce, and an item that
                // vanished from a scan is far more often a mount that did not come up
                // than a deletion. DeleteAsync is the deliberate way to clear this.
                await WriteJsonAsync(
                        EnrichmentFile,
                        merged.Values.ToList(),
                        cancellationToken)
                    .ConfigureAwait(false);
            }
            finally
            {
                _gate.Release();
            }
        }

        /// <inheritdoc />
        public async Task DeleteAsync(CancellationToken cancellationToken)
        {
            await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                foreach (var name in new[] { StateFile, DocumentsFile, RowsFile, VectorsFile, EnrichmentFile })
                {
                    var path = Path.Combine(_directory, name);
                    if (File.Exists(path))
                    {
                        File.Delete(path);
                    }
                }

                _logger.LogInformation("Concierge: index deleted. Search falls back to Jellyfin's own until it is rebuilt.");
            }
            finally
            {
                _gate.Release();
            }
        }

        /// <summary>
        /// Reattaches stored enrichment to the documents it was generated from.
        /// </summary>
        /// <remarks>
        /// Enrichment whose source hash no longer matches the document is dropped
        /// rather than used. That is §5.3's trap made concrete: a metadata refresh
        /// that rewrote an item leaves enrichment describing what it used to be, and
        /// nothing about it would look wrong.
        /// </remarks>
        private static List<ItemDocument> Attach(
            IReadOnlyList<ItemDocument> documents,
            IReadOnlyDictionary<Guid, StoredEnrichment> enrichment)
        {
            var attached = new List<ItemDocument>(documents.Count);

            foreach (var document in documents)
            {
                if (enrichment.TryGetValue(document.ItemId, out var stored)
                    && string.Equals(stored.SourceHash, DocumentHash.Of(document), StringComparison.Ordinal))
                {
                    attached.Add(document with { Enrichment = stored.Enrichment });
                }
                else
                {
                    attached.Add(document);
                }
            }

            return attached;
        }

        private async Task<T?> ReadJsonAsync<T>(string name, CancellationToken cancellationToken)
            where T : class
        {
            var path = Path.Combine(_directory, name);
            if (!File.Exists(path))
            {
                return null;
            }

            await using var stream = File.OpenRead(path);
            return await JsonSerializer.DeserializeAsync<T>(stream, SerializerOptions, cancellationToken)
                .ConfigureAwait(false);
        }

        /// <summary>
        /// Writes to a sibling and renames into place, so a crash mid-write cannot
        /// leave a truncated file that fails to parse.
        /// </summary>
        private async Task WriteJsonAsync<T>(string name, T value, CancellationToken cancellationToken)
        {
            var path = Path.Combine(_directory, name);
            var temporary = path + ".tmp";

            await using (var stream = File.Create(temporary))
            {
                await JsonSerializer.SerializeAsync(stream, value, SerializerOptions, cancellationToken)
                    .ConfigureAwait(false);
            }

            File.Move(temporary, path, overwrite: true);
        }

        private async Task WriteVectorsAsync(IReadOnlyList<float[]> vectors, CancellationToken cancellationToken)
        {
            var path = Path.Combine(_directory, VectorsFile);
            var temporary = path + ".tmp";

            await using (var stream = File.Create(temporary))
            {
                foreach (var vector in vectors)
                {
                    var bytes = new byte[vector.Length * sizeof(float)];
                    Buffer.BlockCopy(vector, 0, bytes, 0, bytes.Length);
                    await stream.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
                }
            }

            File.Move(temporary, path, overwrite: true);
        }

        private async Task<List<float[]>?> ReadVectorsAsync(
            int rowCount,
            int dimensions,
            CancellationToken cancellationToken)
        {
            var path = Path.Combine(_directory, VectorsFile);
            if (!File.Exists(path) || dimensions <= 0)
            {
                return null;
            }

            var expected = (long)rowCount * dimensions * sizeof(float);
            var actual = new FileInfo(path).Length;
            if (actual != expected)
            {
                _logger.LogWarning(
                    "Concierge: vectors.bin is {Actual} bytes and the manifest expects {Expected}; rebuilding.",
                    actual,
                    expected);
                return null;
            }

            var vectors = new List<float[]>(rowCount);
            var buffer = new byte[dimensions * sizeof(float)];

            await using var stream = File.OpenRead(path);
            for (var row = 0; row < rowCount; row++)
            {
                await stream.ReadExactlyAsync(buffer, cancellationToken).ConfigureAwait(false);
                var vector = new float[dimensions];
                Buffer.BlockCopy(buffer, 0, vector, 0, buffer.Length);
                vectors.Add(vector);
            }

            return vectors;
        }
    }
}
