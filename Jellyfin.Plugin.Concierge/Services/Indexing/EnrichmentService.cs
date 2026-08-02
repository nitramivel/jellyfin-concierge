using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Concierge.Configuration;
using Jellyfin.Plugin.Concierge.Core.Documents;
using Jellyfin.Plugin.Concierge.Core.Llm;
using Jellyfin.Plugin.Concierge.Services.Llm;
using Jellyfin.Plugin.Concierge.Services.Runs;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Concierge.Services.Indexing
{
    /// <summary>What one enrichment run produced.</summary>
    /// <param name="Enrichment">Everything generated, ready to store.</param>
    /// <param name="CostUsd">What the pass cost, summed per call.</param>
    /// <param name="Known">Items the model knew and described.</param>
    /// <param name="Unknown">Items the model correctly declined to invent.</param>
    /// <param name="Failed">Items sent that came back with nothing usable.</param>
    public sealed record EnrichmentRunResult(
        IReadOnlyList<StoredEnrichment> Enrichment,
        decimal CostUsd,
        int Known,
        int Unknown,
        int Failed);

    /// <summary>
    /// The one paid pass that runs at index time.
    /// </summary>
    /// <remarks>
    /// Overviews describe the premise; people remember moments. This pass asks a
    /// model that has seen these films how someone half-remembering one would
    /// describe it, and those phrasings are indexed pointing back at the item. That
    /// turns "the one where they kill the guy's dog" from an impossible match into
    /// an easy one.
    /// <para>
    /// <b>It checkpoints.</b> Everything generated is handed back to the caller every
    /// few batches so it can be persisted. A pass over a large library runs for
    /// hours, and losing all of it to a cancel — or a crash in hour two — would mean
    /// losing the money as well as the time.
    /// </para>
    /// </remarks>
    public sealed class EnrichmentService
    {
        /// <summary>
        /// How many batches run between checkpoints.
        /// </summary>
        /// <remarks>
        /// The trade is disk churn against exposure: each checkpoint rewrites the
        /// whole enrichment file, and the window is what a cancel can cost. Five
        /// batches is about a minute of work on a hosted model.
        /// </remarks>
        public const int CheckpointEveryBatches = 5;

        /// <summary>How many batches run between heartbeat lines in the server log.</summary>
        public const int ProgressLogEveryBatches = 10;

        private readonly ILlmProviderFactory _providerFactory;
        private readonly ILogger<EnrichmentService> _logger;

        public EnrichmentService(ILlmProviderFactory providerFactory, ILogger<EnrichmentService> logger)
        {
            _providerFactory = providerFactory;
            _logger = logger;
        }

        /// <summary>
        /// Enriches the documents that need it.
        /// </summary>
        /// <param name="documents">The stale documents. Callers pass only what needs redoing.</param>
        /// <param name="config">The plugin configuration.</param>
        /// <param name="runLog">The run recorder.</param>
        /// <param name="checkpointAsync">
        /// Persists everything generated so far. Called every
        /// <see cref="CheckpointEveryBatches"/> batches, and once more on the way out
        /// of a cancellation so a deliberate stop keeps what it paid for.
        /// </param>
        /// <param name="progress">Reports fraction complete, or null.</param>
        /// <param name="cancellationToken">Cancellation token.</param>
        /// <returns>What was generated.</returns>
        public async Task<EnrichmentRunResult> EnrichAsync(
            IReadOnlyList<ItemDocument> documents,
            PluginConfiguration config,
            IIndexRunLog runLog,
            Func<IReadOnlyList<StoredEnrichment>, CancellationToken, Task> checkpointAsync,
            IProgress<double>? progress,
            CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(documents);
            ArgumentNullException.ThrowIfNull(config);
            ArgumentNullException.ThrowIfNull(runLog);
            ArgumentNullException.ThrowIfNull(checkpointAsync);

            var results = new List<StoredEnrichment>();
            decimal cost = 0;
            var known = 0;
            var unknown = 0;
            var failed = 0;

            if (documents.Count == 0)
            {
                return new EnrichmentRunResult(results, 0m, 0, 0, 0);
            }

            // Hard rule 12: one Normalize for the whole run, and every pass resolves
            // from it. Normalizing per batch would mint a fresh id for a repaired
            // profile each time and report one run as many models.
            var normalized = ModelProfiles.Normalize(config);
            var profile = ModelProfiles.Resolve(normalized, config.EnrichmentModelProfileId);
            var provider = _providerFactory.Create(
                profile,
                ThinkingPolicy.For(config, ThinkingPass.Enrichment, profile));
            var pricing = RunPricing.From(profile);

            var batchSize = Math.Max(1, config.EnrichmentBatchSize);
            var batches = documents.Chunk(batchSize).ToList();

            runLog.Step(
                "enrichment.started",
                $"Enriching {documents.Count} item(s) in {batches.Count} batch(es) with {profile.Model}",
                new Dictionary<string, object?>
                {
                    ["items"] = documents.Count,
                    ["batches"] = batches.Count,
                    ["batchSize"] = batchSize,
                    ["model"] = provider.ModelId,
                    ["provider"] = profile.Provider.ToString(),
                    ["inputCostPerMillion"] = pricing.InputCostPerMillion,
                    ["outputCostPerMillion"] = pricing.OutputCostPerMillion,
                    ["maxAsksPerItem"] = config.MaxAsksPerItem,
                    ["maxOutputTokens"] = config.MaxOutputTokens,
                });

            _logger.LogInformation(
                "Concierge: enriching {Items} item(s) in {Batches} batch(es) with {Model}",
                documents.Count,
                batches.Count,
                profile.Model);

            try
            {
                for (var i = 0; i < batches.Count; i++)
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    var outcome = await EnrichBatchAsync(
                            batches[i], i + 1, provider, profile, pricing, config, runLog, cancellationToken)
                        .ConfigureAwait(false);

                    results.AddRange(outcome.Results);
                    cost += outcome.Cost;
                    known += outcome.Known;
                    unknown += outcome.Unknown;
                    failed += outcome.Failed;

                    progress?.Report((i + 1) / (double)batches.Count);

                    // One step per batch, with the running total. A run that is killed
                    // after five minutes still shows exactly where the money went, and
                    // a run that is merely slow can be watched rather than guessed at.
                    runLog.Step(
                        "enrichment.batch",
                        $"Batch {i + 1} of {batches.Count} — {outcome.Known} enriched, "
                            + $"{outcome.Unknown} unknown, ${cost:F4} so far",
                        new Dictionary<string, object?>
                        {
                            ["batch"] = i + 1,
                            ["batches"] = batches.Count,
                            ["items"] = batches[i].Length,
                            ["known"] = outcome.Known,
                            ["unknown"] = outcome.Unknown,
                            ["failed"] = outcome.Failed,
                            ["batchCostUsd"] = decimal.Round(outcome.Cost, 6),
                            ["runningCostUsd"] = decimal.Round(cost, 6),
                        });

                    // A heartbeat in the server log. Without one, a pass over a large
                    // library is fifteen minutes of silence between "starting" and
                    // "finished", which is indistinguishable from a hang.
                    if ((i + 1) % ProgressLogEveryBatches == 0 || i + 1 == batches.Count)
                    {
                        _logger.LogInformation(
                            "Concierge: enrichment batch {Batch}/{Batches} — {Known} enriched, {Unknown} unknown, "
                            + "{Failed} failed, ${Cost:F4} so far",
                            i + 1,
                            batches.Count,
                            known,
                            unknown,
                            failed,
                            cost);
                    }

                    if ((i + 1) % CheckpointEveryBatches == 0)
                    {
                        await CheckpointAsync(checkpointAsync, results, runLog, i + 1, batches.Count, cancellationToken)
                            .ConfigureAwait(false);
                    }
                }
            }
            catch (OperationCanceledException)
            {
                // Keep what has been paid for. The next run resumes on the document
                // hash and re-asks only for what is genuinely still missing.
                runLog.Step(
                    "enrichment.cancelled",
                    $"Cancelled after {results.Count} item(s); checkpointing so nothing paid for is lost",
                    new Dictionary<string, object?> { ["enriched"] = results.Count, ["costUsd"] = cost });

                await CheckpointAsync(checkpointAsync, results, runLog, 0, batches.Count, CancellationToken.None)
                    .ConfigureAwait(false);
                throw;
            }

            await CheckpointAsync(checkpointAsync, results, runLog, batches.Count, batches.Count, cancellationToken)
                .ConfigureAwait(false);

            runLog.Step(
                "enrichment.finished",
                $"{known} enriched, {unknown} the model did not know, {failed} failed",
                new Dictionary<string, object?>
                {
                    ["known"] = known,
                    ["unknown"] = unknown,
                    ["failed"] = failed,
                    ["costUsd"] = cost,
                });

            _logger.LogInformation(
                "Concierge: enrichment finished — {Known} enriched, {Unknown} the model did not know, "
                + "{Failed} failed, ${Cost:F4}",
                known,
                unknown,
                failed,
                cost);

            return new EnrichmentRunResult(results, cost, known, unknown, failed);
        }

        private async Task CheckpointAsync(
            Func<IReadOnlyList<StoredEnrichment>, CancellationToken, Task> checkpointAsync,
            List<StoredEnrichment> results,
            IIndexRunLog runLog,
            int batch,
            int batches,
            CancellationToken cancellationToken)
        {
            if (results.Count == 0)
            {
                return;
            }

            try
            {
                await checkpointAsync(results, cancellationToken).ConfigureAwait(false);

                runLog.Step(
                    "enrichment.checkpoint",
                    $"Saved {results.Count} enriched item(s) after batch {batch} of {batches}",
                    new Dictionary<string, object?> { ["saved"] = results.Count, ["batch"] = batch });
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // A failed checkpoint is bad but not fatal — the pass carries on and
                // the next checkpoint tries again with strictly more to save.
                _logger.LogWarning(ex, "Concierge: an enrichment checkpoint could not be written");
            }
        }

        private async Task<BatchOutcome> EnrichBatchAsync(
            ItemDocument[] batch,
            int batchNumber,
            ILlmProvider provider,
            ModelProfile profile,
            RunPricing pricing,
            PluginConfiguration config,
            IIndexRunLog runLog,
            CancellationToken cancellationToken)
        {
            var results = new List<StoredEnrichment>();

            // Everything goes in the cacheable prefix and the suffix is left empty,
            // which means no cache marker is written. Each batch sends a different
            // item list, so there is nothing a later call could read back, and marking
            // it anyway would pay the cache-write premium for a hit that cannot happen.
            var prompt = EnrichmentPromptBuilder.BuildItemList(batch)
                + EnrichmentPromptBuilder.BuildInstruction(batch.Length, config.MaxAsksPerItem);

            var request = new LlmRequest(
                EnrichmentPromptBuilder.SystemPrompt,
                prompt,
                string.Empty,
                config.MaxOutputTokens,
                ResponseShape.Enrichment);

            var stopwatch = Stopwatch.StartNew();
            LlmResult result;
            try
            {
                result = await provider.CompleteAsync(request, cancellationToken).ConfigureAwait(false);
                stopwatch.Stop();
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                stopwatch.Stop();

                runLog.LlmCall(
                    "enrichment", batchNumber, batch.Length, stopwatch.Elapsed, request, null,
                    "error", ex.Message, provider.ModelId, profile.Provider.ToString(), pricing);

                foreach (var document in batch)
                {
                    runLog.ItemNotEnriched(document.Title, "batch-failed");
                    runLog.ItemEnriched(Record(document, batchNumber, "batch-failed", null, 0m));
                }

                // One failed batch must not sink a build that has already paid for
                // everything before it. These items keep their old hash and are
                // retried next run.
                _logger.LogWarning(ex, "Concierge: enrichment batch {Batch} failed; its items stay unenriched", batchNumber);
                return new BatchOutcome(results, 0m, 0, 0, batch.Length);
            }

            var cost = CallCost.ForChat(
                profile, result.InputTokens, result.OutputTokens, result.CacheReadTokens, result.CacheWriteTokens);

            IReadOnlyDictionary<int, Enrichment> parsed;
            try
            {
                parsed = EnrichmentParser.Parse(result.Text, batch.Length);
            }
            catch (FormatException ex)
            {
                runLog.LlmCall(
                    "enrichment", batchNumber, batch.Length, stopwatch.Elapsed, request, result,
                    result.Truncated ? "truncated" : "unparseable", ex.Message,
                    provider.ModelId, profile.Provider.ToString(), pricing);

                foreach (var document in batch)
                {
                    runLog.ItemNotEnriched(document.Title, result.Truncated ? "truncated" : "unparseable");
                    runLog.ItemEnriched(Record(
                        document,
                        batchNumber,
                        result.Truncated ? "truncated" : "unparseable",
                        null,
                        batch.Length > 0 ? cost / batch.Length : 0m));
                }

                _logger.LogWarning(
                    "Concierge: enrichment batch {Batch} returned no usable JSON ({Reason}). "
                    + "{Detail}",
                    batchNumber,
                    result.Truncated ? "hit the output cap" : "unparseable",
                    result.Truncated
                        ? "Lower EnrichmentBatchSize or raise MaxOutputTokens — thinking counts against the same cap."
                        : ex.Message);

                return new BatchOutcome(results, cost, 0, 0, batch.Length);
            }

            var known = 0;
            var unknown = 0;
            var missing = 0;

            // Items are billed as a batch, so a per-item cost can only ever be a
            // share. Recorded anyway: it is the number that makes two models
            // comparable, and "what did this title cost me" is the question a bill
            // cannot answer.
            var share = batch.Length > 0 ? cost / batch.Length : 0m;

            for (var i = 0; i < batch.Length; i++)
            {
                if (!parsed.TryGetValue(i, out var enrichment))
                {
                    // Silently omitted. Left unstored so the next run asks again,
                    // rather than recording an empty answer the model never gave.
                    missing++;
                    runLog.ItemNotEnriched(batch[i].Title, "omitted");
                    runLog.ItemEnriched(Record(batch[i], batchNumber, "omitted", null, share));
                    continue;
                }

                if (enrichment.IsEmpty)
                {
                    // The model said it did not know this one. That is the correct
                    // answer for an obscure title and it is stored, so the next run
                    // does not pay to ask again.
                    unknown++;
                    runLog.ItemNotEnriched(batch[i].Title, "unknown-to-model");
                    runLog.ItemEnriched(
                        Record(batch[i], batchNumber, "unknown-to-model", enrichment, share));
                }
                else
                {
                    known++;
                    runLog.ItemEnriched(
                        Record(batch[i], batchNumber, "enriched", enrichment, share));
                }

                results.Add(new StoredEnrichment(
                    batch[i].ItemId,
                    DocumentHash.Of(batch[i]),
                    enrichment,
                    DateTime.UtcNow,
                    runLog.RunId,
                    provider.ModelId,
                    decimal.Round(share, 6)));
            }

            runLog.LlmCall(
                "enrichment", batchNumber, batch.Length, stopwatch.Elapsed, request, result,
                result.Truncated ? "truncated" : "ok",
                result.Truncated ? "output cap reached; later items in the batch were lost" : null,
                provider.ModelId, profile.Provider.ToString(), pricing);

            return new BatchOutcome(results, cost, known, unknown, missing);
        }

        /// <summary>
        /// What one item got, in a shape the run log can store.
        /// </summary>
        /// <remarks>
        /// Counts and lengths rather than the text itself. The full answer is already
        /// in the enrichment store and repeating it here would turn a run log into a
        /// second copy of the index — but "premise: 0 characters, asks: 0" is exactly
        /// what you need to see to know a model was paid for nothing.
        /// </remarks>
        private static RunItemRecord Record(
            ItemDocument document,
            int batch,
            string outcome,
            Enrichment? enrichment,
            decimal share)
        {
            return new RunItemRecord(
                document.ItemId,
                document.Title,
                document.Year,
                batch,
                outcome,
                enrichment?.Premise.Length ?? 0,
                enrichment?.Moments.Count ?? 0,
                enrichment?.Themes.Count ?? 0,
                enrichment?.Asks.Count ?? 0,
                enrichment?.Spoiler ?? false,
                decimal.Round(share, 6));
        }

        private sealed record BatchOutcome(
            List<StoredEnrichment> Results,
            decimal Cost,
            int Known,
            int Unknown,
            int Failed);
    }
}
