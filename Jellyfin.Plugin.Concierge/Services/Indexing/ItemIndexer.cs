using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Concierge.Configuration;
using Jellyfin.Plugin.Concierge.Core.Documents;
using Jellyfin.Plugin.Concierge.Core.Llm;
using Jellyfin.Plugin.Concierge.Core.Retrieval;
using Jellyfin.Plugin.Concierge.Services.Embeddings;
using Jellyfin.Plugin.Concierge.Services.Library;
using Jellyfin.Plugin.Concierge.Services.Runs;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Concierge.Services.Indexing
{
    /// <summary>What one index build did.</summary>
    /// <param name="RunId">The run log this build wrote.</param>
    /// <param name="Items">How many items were indexed.</param>
    /// <param name="Rows">How many vector rows the index holds.</param>
    /// <param name="Embedded">How many rows had to be embedded this run.</param>
    /// <param name="Reused">How many rows reused a vector from the previous index.</param>
    /// <param name="Enriched">How many items carry non-empty enrichment.</param>
    /// <param name="CostUsd">What the run cost.</param>
    public sealed record IndexBuildResult(
        Guid RunId,
        int Items,
        int Rows,
        int Embedded,
        int Reused,
        int Enriched,
        decimal CostUsd,

        /* True when another build already held the single-flight gate and this call
         * did nothing at all. Distinct from a build that ran and found no work: that
         * one costs a scan and writes a generation, this one never started. */
        bool Skipped = false);

    /// <summary>
    /// Builds the index: scan, enrich what changed, embed what changed, write.
    /// </summary>
    public sealed class ItemIndexer
    {
        private readonly ILibraryScanner _scanner;
        private readonly ILibraryManager _libraryManager;

        /// <summary>Held for the whole of a build, so only one ever runs at a time.</summary>
        private readonly SemaphoreSlim _buildGate = new(1, 1);
        private readonly IIndexStore _store;
        private readonly EnrichmentService _enrichment;
        private readonly IEmbeddingProviderFactory _embeddingFactory;
        private readonly IIndexRunLogStore _runLogs;
        private readonly ILogger<ItemIndexer> _logger;

        public ItemIndexer(
            ILibraryScanner scanner,
            ILibraryManager libraryManager,
            IIndexStore store,
            EnrichmentService enrichment,
            IEmbeddingProviderFactory embeddingFactory,
            IIndexRunLogStore runLogs,
            ILogger<ItemIndexer> logger)
        {
            _scanner = scanner;
            _libraryManager = libraryManager;
            _store = store;
            _enrichment = enrichment;
            _embeddingFactory = embeddingFactory;
            _runLogs = runLogs;
            _logger = logger;
        }

        /// <summary>
        /// Builds or refreshes the index.
        /// </summary>
        /// <param name="config">The plugin configuration.</param>
        /// <param name="trigger">"scheduled" or "manual".</param>
        /// <param name="progress">Reports 0-100, or null.</param>
        /// <param name="cancellationToken">Cancellation token.</param>
        /// <param name="regenerate">
        /// Whether to ignore every cached enrichment and vector and generate a new
        /// index from source library metadata.
        /// </param>
        /// <param name="enrichOnly">
        /// Whether to stop after banking enrichment, leaving the live index exactly as
        /// it was. Nothing is embedded and no generation is written.
        /// </param>
        /// <returns>What the build did.</returns>
        public async Task<IndexBuildResult> BuildAsync(
            PluginConfiguration config,
            string trigger,
            IProgress<double>? progress,
            CancellationToken cancellationToken,
            bool regenerate = false,
            bool enrichOnly = false)
        {
            ArgumentNullException.ThrowIfNull(config);

            // Single-flight across every caller, because there are now two scheduled
            // tasks that can reach this. Both write the enrichment store, and the
            // ordinary build also rewrites the index and advances the generation
            // counter — two of those at once race on that write, and whichever
            // finished second would silently publish over the other.
            //
            // Refused rather than queued: these are long, scheduled, repeating jobs.
            // Waiting would stack up an hours-long backlog nobody asked for, and the
            // next scheduled run does the same work anyway.
            if (!await _buildGate.WaitAsync(0, cancellationToken).ConfigureAwait(false))
            {
                _logger.LogInformation(
                    "Concierge: a Concierge build is already running, so the {Trigger} run was skipped. "
                    + "It will run at its next scheduled time.",
                    trigger);

                return new IndexBuildResult(Guid.Empty, 0, 0, 0, 0, 0, 0m, Skipped: true);
            }

            try
            {
                return await BuildCoreAsync(config, trigger, progress, cancellationToken, regenerate, enrichOnly)
                    .ConfigureAwait(false);
            }
            finally
            {
                _buildGate.Release();
            }
        }

        private async Task<IndexBuildResult> BuildCoreAsync(
            PluginConfiguration config,
            string trigger,
            IProgress<double>? progress,
            CancellationToken cancellationToken,
            bool regenerate,
            bool enrichOnly)
        {
            var runLog = _runLogs.Begin(trigger, new Dictionary<string, object?>
            {
                ["includeEpisodes"] = config.IncludeEpisodes,
                ["enableEnrichment"] = config.EnableEnrichment,
                ["enrichmentBatchSize"] = config.EnrichmentBatchSize,
                ["maxAsksPerItem"] = config.MaxAsksPerItem,
                ["embeddingBatchSize"] = config.EmbeddingBatchSize,
                ["maxOutputTokens"] = config.MaxOutputTokens,
                ["enableThinking"] = config.EnableThinking,
                ["regenerate"] = regenerate,
                ["enrichOnly"] = enrichOnly,
                ["enrichmentConcurrency"] = config.EnrichmentConcurrency,
            });

            var report = new Progress<double>(p =>
            {
                runLog.Progress(p);
                progress?.Report(p);
            });

            try
            {
                var result = await RunAsync(config, runLog, report, cancellationToken, regenerate, enrichOnly)
                    .ConfigureAwait(false);
                runLog.Complete();
                return result;
            }
            catch (OperationCanceledException)
            {
                // Checkpointed enrichment survives; this is a deliberate stop, not a
                // defect, and the log records it as such.
                runLog.Cancel();
                throw;
            }
            catch (Exception ex)
            {
                runLog.Fail(ex.Message);
                throw;
            }
        }

        private async Task<IndexBuildResult> RunAsync(
            PluginConfiguration config,
            IIndexRunLog runLog,
            IProgress<double> progress,
            CancellationToken cancellationToken,
            bool regenerate,
            bool enrichOnly)
        {
            var embeddingProfile = EmbeddingProfiles.Resolve(config, config.EmbeddingProfileId);
            var embedder = _embeddingFactory.Create(embeddingProfile);

            // 1. Project the library.
            var scanned = Stopwatch.StartNew();
            var items = _scanner.Scan(config.IncludeEpisodes);
            var documents = items.Select(BuildDocument).ToList();
            scanned.Stop();

            runLog.Step(
                "library.scanned",
                $"{documents.Count} item(s) to index",
                new Dictionary<string, object?>
                {
                    ["items"] = documents.Count,
                    ["episodes"] = config.IncludeEpisodes,
                    ["durationMs"] = (int)scanned.ElapsedMilliseconds,
                });

            progress.Report(5);

            // 2. Work out what enrichment is still valid. Anything whose source text
            //    changed is stale by definition — the hash covers the library fields
            //    only, so a metadata refresh cannot leave enrichment describing what
            //    an item used to be.
            var stored = regenerate
                ? new Dictionary<Guid, StoredEnrichment>()
                : await _store.LoadEnrichmentAsync(cancellationToken).ConfigureAwait(false);
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

            runLog.Step(
                "enrichment.planned",
                $"{keep.Count} item(s) already enriched, {stale.Count} to do",
                new Dictionary<string, object?>
                {
                    ["cached"] = keep.Count,
                    ["stale"] = stale.Count,
                    ["enabled"] = config.EnableEnrichment,
                });

            decimal cost = 0;

            // 3. Enrich the stale ones, checkpointing as it goes.
            if (config.EnableEnrichment && stale.Count > 0)
            {
                var carried = keep.ToList();

                try
                {
                    var run = await _enrichment.EnrichAsync(
                            stale,
                            config,
                            runLog,
                            (generated, ct) => _store.SaveEnrichmentAsync(
                                [.. carried, .. generated], ct),
                            new Progress<double>(p => progress.Report(5 + (p * 55))),
                            cancellationToken)
                        .ConfigureAwait(false);

                    keep.AddRange(run.Enrichment);
                    cost += run.CostUsd;
                }
                catch (InvalidOperationException ex)
                {
                    // No chat profile configured. Degrade rather than fail: an index
                    // built from overviews alone is worse at plot and mood recall but
                    // it is a working search index.
                    runLog.Step("enrichment.skipped", ex.Message);
                    _logger.LogWarning(
                        "Concierge: enrichment was skipped — {Reason} The index will still build, but plot "
                        + "and mood searches will be markedly worse until it runs.",
                        ex.Message);
                }
            }
            else if (!config.EnableEnrichment)
            {
                runLog.Step("enrichment.disabled", "Enrichment is off; the index holds only library metadata");
                _logger.LogInformation(
                    "Concierge: enrichment is off, so the index holds only what the library already knew. "
                    + "Plot and mood searches will be markedly worse.");
            }

            progress.Report(60);

            // 3b. Enrichment-only runs stop here, with the index left exactly as it
            //     was. Everything below this point rewrites state.json, docs.json,
            //     rows.json and vectors.bin and advances the generation counter, and a
            //     long enrichment pass has no business doing any of that.
            //
            //     This is what makes episode enrichment safe to run on its own
            //     schedule. Episodes are the expensive, slow, low-yield half — eight
            //     hours and 45% unknown-to-model, measured — and pinning the live index
            //     behind them means a search index that cannot be refreshed for a
            //     working day. Banking the answers and letting the next ordinary build
            //     pick them up by source hash separates "paying the model" from
            //     "publishing an index", which were only ever one step by accident.
            if (enrichOnly)
            {
                await _store.SaveEnrichmentAsync(keep, cancellationToken).ConfigureAwait(false);

                var banked = keep.Count(e => e.Enrichment is { IsEmpty: false });

                runLog.Step(
                    "enrichment.banked",
                    $"{banked} enriched item(s) stored; the index was left untouched",
                    new Dictionary<string, object?>
                    {
                        ["enriched"] = banked,
                        ["stored"] = keep.Count,
                        ["costUsd"] = cost,
                        ["indexWritten"] = false,
                    });

                _logger.LogInformation(
                    "Concierge: enrichment-only run finished — {Banked} item(s) banked for ${Cost:F4}. "
                    + "The search index is unchanged; the next ordinary build will pick these up.",
                    banked,
                    cost);

                progress.Report(100);

                // No rows, nothing embedded, no generation: the honest report of a run
                // that deliberately produced no index.
                return new IndexBuildResult(runLog.RunId, documents.Count, 0, 0, 0, banked, cost);
            }

            // 4. Attach enrichment and lay out the vector rows.
            var byItem = keep.ToDictionary(e => e.ItemId);
            var enriched = documents
                .Select(d => byItem.TryGetValue(d.ItemId, out var e) ? d with { Enrichment = e.Enrichment } : d)
                .ToList();

            var (rows, texts) = VectorRowPlanner.Plan(enriched, config.MaxAsksPerItem);

            // 5. Reuse every vector whose text is unchanged. This is what makes a
            //    nightly rebuild cost approximately nothing.
            var reusable = regenerate
                ? new Dictionary<string, float[]>(StringComparer.Ordinal)
                : await LoadReusableVectorsAsync(embeddingProfile, cancellationToken).ConfigureAwait(false);
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

            runLog.Step(
                "embedding.planned",
                $"{toEmbed.Count} row(s) to embed, {texts.Count - toEmbed.Count} reused",
                new Dictionary<string, object?>
                {
                    ["rows"] = rows.Count,
                    ["embedded"] = toEmbed.Count,
                    ["reused"] = texts.Count - toEmbed.Count,
                    ["model"] = embedder.ModelId,
                });

            cost += await EmbedAsync(
                    embedder, embeddingProfile, texts, toEmbed, vectors, config, runLog, progress, cancellationToken)
                .ConfigureAwait(false);

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

            runLog.Step(
                "index.written",
                $"Generation {state.Generation}: {state.ItemCount} item(s), {state.RowCount} row(s), "
                + $"{state.EnrichedCount} enriched",
                new Dictionary<string, object?>
                {
                    ["generation"] = state.Generation,
                    ["items"] = state.ItemCount,
                    ["rows"] = state.RowCount,
                    ["enriched"] = state.EnrichedCount,
                    ["dimensions"] = state.Dimensions,
                    ["embeddingModel"] = state.EmbeddingModel,
                    ["costUsd"] = cost,
                });

            progress.Report(100);

            return new IndexBuildResult(
                runLog.RunId,
                enriched.Count,
                rows.Count,
                toEmbed.Count,
                texts.Count - toEmbed.Count,
                state.EnrichedCount,
                cost);
        }

        private async Task<decimal> EmbedAsync(
            IEmbeddingProvider embedder,
            EmbeddingProfile profile,
            IReadOnlyList<string> texts,
            IReadOnlyList<int> indexes,
            float[][] vectors,
            PluginConfiguration config,
            IIndexRunLog runLog,
            IProgress<double> progress,
            CancellationToken cancellationToken)
        {
            if (indexes.Count == 0)
            {
                _logger.LogInformation("Concierge: nothing changed, so no embedding was needed");
                return 0m;
            }

            var batchSize = Math.Max(1, config.EmbeddingBatchSize);
            var batches = indexes.Chunk(batchSize).ToList();
            decimal cost = 0;

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
                var stopwatch = Stopwatch.StartNew();

                var result = await embedder
                    .EmbedAsync(input, EmbeddingPurpose.Document, cancellationToken)
                    .ConfigureAwait(false);

                stopwatch.Stop();

                for (var i = 0; i < batch.Length; i++)
                {
                    vectors[batch[i]] = result.Vectors[i];
                }

                var batchCost = CallCost.ForEmbedding(profile, result.InputTokens);
                cost += batchCost;

                runLog.EmbeddingCall(
                    b + 1,
                    batch.Length,
                    stopwatch.Elapsed,
                    result.InputTokens,
                    batchCost,
                    embedder.ModelId,
                    profile.Provider.ToString());

                progress.Report(60 + ((b + 1) / (double)batches.Count * 35));
            }

            return cost;
        }

        /// <summary>
        /// The previous index's vectors, keyed by the text they were made from.
        /// </summary>
        private async Task<Dictionary<string, float[]>> LoadReusableVectorsAsync(
            EmbeddingProfile profile,
            CancellationToken cancellationToken)
        {
            var reusable = new Dictionary<string, float[]>(StringComparer.Ordinal);

            // LoadAsync refuses a stored index whose state disagrees with this
            // profile, so anything reaching past here was written by the model in
            // force now.
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
    }
}
