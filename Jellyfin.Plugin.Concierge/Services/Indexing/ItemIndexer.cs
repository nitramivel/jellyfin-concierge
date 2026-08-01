using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Concierge.Configuration;
using Jellyfin.Plugin.Concierge.Core.Documents;
using Jellyfin.Plugin.Concierge.Core.Llm;
using Jellyfin.Plugin.Concierge.Core.Retrieval;
using Jellyfin.Plugin.Concierge.Services.Embeddings;
using Jellyfin.Plugin.Concierge.Services.Library;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Concierge.Services.Indexing
{
    /// <summary>What one index build did.</summary>
    /// <param name="Items">How many items were indexed.</param>
    /// <param name="Rows">How many vector rows the index holds.</param>
    /// <param name="Embedded">How many rows had to be embedded this run.</param>
    /// <param name="Reused">How many rows reused a vector from the previous index.</param>
    /// <param name="Enriched">How many items carry non-empty enrichment.</param>
    /// <param name="CostUsd">What the run cost.</param>
    public sealed record IndexBuildResult(
        int Items,
        int Rows,
        int Embedded,
        int Reused,
        int Enriched,
        decimal CostUsd);

    /// <summary>
    /// Builds the index: scan, enrich what changed, embed what changed, write.
    /// </summary>
    public sealed class ItemIndexer
    {
        private readonly ILibraryScanner _scanner;
        private readonly ILibraryManager _libraryManager;
        private readonly IIndexStore _store;
        private readonly EnrichmentService _enrichment;
        private readonly IEmbeddingProviderFactory _embeddingFactory;
        private readonly ILogger<ItemIndexer> _logger;

        public ItemIndexer(
            ILibraryScanner scanner,
            ILibraryManager libraryManager,
            IIndexStore store,
            EnrichmentService enrichment,
            IEmbeddingProviderFactory embeddingFactory,
            ILogger<ItemIndexer> logger)
        {
            _scanner = scanner;
            _libraryManager = libraryManager;
            _store = store;
            _enrichment = enrichment;
            _embeddingFactory = embeddingFactory;
            _logger = logger;
        }

        /// <summary>
        /// Builds or refreshes the index.
        /// </summary>
        /// <param name="config">The plugin configuration.</param>
        /// <param name="progress">Reports 0-100, or null.</param>
        /// <param name="cancellationToken">Cancellation token.</param>
        /// <returns>What the build did.</returns>
        public async Task<IndexBuildResult> BuildAsync(
            PluginConfiguration config,
            IProgress<double>? progress,
            CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(config);

            var embeddingProfile = EmbeddingProfiles.Resolve(config, config.EmbeddingProfileId);
            var embedder = _embeddingFactory.Create(embeddingProfile);

            // 1. Project the library.
            var items = _scanner.Scan(config.IncludeEpisodes);
            var documents = items.Select(BuildDocument).ToList();
            progress?.Report(5);

            // 2. Work out what enrichment is still valid. Anything whose source text
            //    changed is stale by definition — see DocumentHash for why the hash
            //    covers the library fields only.
            var stored = await _store.LoadEnrichmentAsync(cancellationToken).ConfigureAwait(false);
            var keep = new List<StoredEnrichment>();
            var stale = new List<ItemDocument>();

            foreach (var document in documents)
            {
                var hash = DocumentHash.Of(document);
                if (stored.TryGetValue(document.ItemId, out var existing)
                    && string.Equals(existing.SourceHash, hash, StringComparison.Ordinal))
                {
                    keep.Add(existing);
                }
                else
                {
                    stale.Add(document);
                }
            }

            decimal cost = 0;

            // 3. Enrich the stale ones.
            if (config.EnableEnrichment && stale.Count > 0)
            {
                try
                {
                    var run = await _enrichment.EnrichAsync(
                            stale,
                            config,
                            new Progress<double>(p => progress?.Report(5 + (p * 55))),
                            cancellationToken)
                        .ConfigureAwait(false);

                    keep.AddRange(run.Enrichment);
                    cost += run.Calls.Sum(c => c.EstimatedCostUsd);
                }
                catch (InvalidOperationException ex)
                {
                    // No chat profile configured. Degrade rather than fail: an index
                    // built from overviews alone is worse at plot and mood recall but
                    // it is a working search index, and refusing to build one would
                    // mean a fresh install with only an embedding profile gets nothing
                    // at all.
                    _logger.LogWarning(
                        "Concierge: enrichment was skipped — {Reason} The index will still build, but plot "
                        + "and mood searches will be markedly worse until it runs.",
                        ex.Message);
                }
            }
            else if (!config.EnableEnrichment)
            {
                _logger.LogInformation(
                    "Concierge: enrichment is off, so the index holds only what the library already knew. "
                    + "Plot and mood searches will be markedly worse.");
            }

            progress?.Report(60);

            // 4. Attach enrichment and lay out the vector rows.
            var byItem = keep.ToDictionary(e => e.ItemId);
            var enriched = documents
                .Select(d => byItem.TryGetValue(d.ItemId, out var e) ? d with { Enrichment = e.Enrichment } : d)
                .ToList();

            var (rows, texts) = BuildRows(enriched, config.MaxAsksPerItem);

            // 5. Reuse every vector whose text is unchanged. This is what makes a
            //    nightly rebuild cost approximately nothing: a library where nothing
            //    changed embeds nothing at all.
            var reusable = await LoadReusableVectorsAsync(embeddingProfile, cancellationToken).ConfigureAwait(false);
            var vectors = new float[texts.Count][];
            var toEmbed = new List<int>();

            for (var i = 0; i < texts.Count; i++)
            {
                if (reusable.TryGetValue(texts[i], out var vector))
                {
                    vectors[i] = vector;
                }
                else
                {
                    toEmbed.Add(i);
                }
            }

            await EmbedAsync(embedder, texts, toEmbed, vectors, config, progress, cancellationToken)
                .ConfigureAwait(false);

            cost += CallCost.ForEmbedding(embeddingProfile, EstimateTokens(texts, toEmbed));

            // 6. Write it.
            var previous = await _store.LoadStateAsync(cancellationToken).ConfigureAwait(false);
            var state = new IndexState(
                (previous?.Generation ?? 0) + 1,
                embeddingProfile.Model,
                vectors.Length > 0 ? vectors[0].Length : 0,
                embeddingProfile.QueryPrefix,
                embeddingProfile.DocumentPrefix,
                DateTime.UtcNow,
                enriched.Count,
                rows.Count,
                enriched.Count(d => d.Enrichment is { IsEmpty: false }));

            await _store.SaveEnrichmentAsync(keep, cancellationToken).ConfigureAwait(false);
            await _store.SaveAsync(state, enriched, rows, vectors, cancellationToken).ConfigureAwait(false);

            progress?.Report(100);

            return new IndexBuildResult(
                enriched.Count,
                rows.Count,
                toEmbed.Count,
                texts.Count - toEmbed.Count,
                state.EnrichedCount,
                cost);
        }

        /// <summary>
        /// Lays out one vector row per item document plus one per generated phrasing.
        /// </summary>
        /// <remarks>
        /// The phrasings are the point: a user's fuzzy sentence gets compared against
        /// other fuzzy sentences about the same film rather than against the overview.
        /// Retrieval collapses an item's rows back to its best one before fusion.
        /// </remarks>
        private static (List<VectorRowSource> Rows, List<string> Texts) BuildRows(
            IReadOnlyList<ItemDocument> documents,
            int maxAsks)
        {
            var rows = new List<VectorRowSource>(documents.Count * 4);
            var texts = new List<string>(documents.Count * 4);

            foreach (var document in documents)
            {
                var text = document.RenderEmbeddingText();
                rows.Add(new VectorRowSource(document.ItemId, VectorRowKind.Document, text));
                texts.Add(text);

                if (document.Enrichment is not { } enrichment)
                {
                    continue;
                }

                foreach (var ask in enrichment.Asks.Take(Math.Max(0, maxAsks)))
                {
                    if (string.IsNullOrWhiteSpace(ask))
                    {
                        continue;
                    }

                    rows.Add(new VectorRowSource(document.ItemId, VectorRowKind.Ask, ask));
                    texts.Add(ask);
                }
            }

            return (rows, texts);
        }

        private async Task EmbedAsync(
            IEmbeddingProvider embedder,
            IReadOnlyList<string> texts,
            IReadOnlyList<int> indexes,
            float[][] vectors,
            PluginConfiguration config,
            IProgress<double>? progress,
            CancellationToken cancellationToken)
        {
            if (indexes.Count == 0)
            {
                _logger.LogInformation("Concierge: nothing changed, so no embedding was needed");
                return;
            }

            var batchSize = Math.Max(1, config.EmbeddingBatchSize);
            var batches = indexes.Chunk(batchSize).ToList();

            _logger.LogInformation(
                "Concierge: embedding {Count} new row(s) in {Batches} batch(es) with {Model}",
                indexes.Count,
                batches.Count,
                embedder.ModelId);

            for (var b = 0; b < batches.Count; b++)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var batch = batches[b];
                var input = batch.Select(i => texts[i]).ToList();

                var result = await embedder
                    .EmbedAsync(input, EmbeddingPurpose.Document, cancellationToken)
                    .ConfigureAwait(false);

                for (var i = 0; i < batch.Length; i++)
                {
                    vectors[batch[i]] = result.Vectors[i];
                }

                progress?.Report(60 + ((b + 1) / (double)batches.Count * 35));
            }
        }

        /// <summary>
        /// The previous index's vectors, keyed by the text they were made from.
        /// </summary>
        /// <remarks>
        /// Keyed by text rather than by item, so an unchanged phrasing survives its
        /// item being re-enriched and an unchanged item survives its phrasings
        /// changing. Only ever consulted when the stored index is still valid for the
        /// current embedding profile — reusing a vector across a model change is
        /// exactly the mixing hard rule 9 forbids.
        /// </remarks>
        private async Task<Dictionary<string, float[]>> LoadReusableVectorsAsync(
            EmbeddingProfile profile,
            CancellationToken cancellationToken)
        {
            var reusable = new Dictionary<string, float[]>(StringComparer.Ordinal);

            // LoadAsync refuses a stored index whose state disagrees with this
            // profile, so anything reaching past here was written by the model in
            // force now. That refusal is the only thing standing between a changed
            // embedding model and an index quietly holding two incomparable spaces.
            var existing = await _store.LoadAsync(profile, cancellationToken).ConfigureAwait(false);
            if (existing is null)
            {
                return reusable;
            }

            foreach (var (source, vector) in existing.Vectors.EnumerateRows())
            {
                reusable[source.Text] = vector;
            }

            _logger.LogInformation(
                "Concierge: {Rows} row(s) from generation {Generation} are eligible for reuse",
                reusable.Count,
                existing.State.Generation);

            return reusable;
        }

        private ItemDocument BuildDocument(BaseItem item)
            => ItemDocumentFactory.FromItem(item, ResolvePeople(item));

        /// <summary>
        /// Top cast plus directors and writers.
        /// </summary>
        /// <remarks>
        /// Never fatal. People are a ranking signal — "the one with Toni Collette" —
        /// and an item indexed without them is worse, not broken.
        /// </remarks>
        private IReadOnlyList<string> ResolvePeople(BaseItem item)
        {
            try
            {
                return _libraryManager.GetPeople(item)
                    .Where(p => !string.IsNullOrWhiteSpace(p.Name))
                    .Select(p => p.Name)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .Take(12)
                    .ToList();
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Concierge: could not read people for {Item}", item.Name);
                return [];
            }
        }

        /// <summary>
        /// A rough token count for the cost line, at the usual four-characters-a-token.
        /// </summary>
        /// <remarks>
        /// An estimate, and labelled as one: most embedding endpoints report usage and
        /// the provider prefers the reported figure, but the Google batch endpoint
        /// reports none at all.
        /// </remarks>
        private static long EstimateTokens(IReadOnlyList<string> texts, IReadOnlyList<int> indexes)
        {
            long characters = 0;
            foreach (var index in indexes)
            {
                characters += texts[index].Length;
            }

            return characters / 4;
        }
    }
}
