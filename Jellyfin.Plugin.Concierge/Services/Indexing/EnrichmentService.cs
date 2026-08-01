using System;
using System.Collections.Generic;
using System.Diagnostics;
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
    /// <param name="Calls">One record per model call, for the cost line.</param>
    /// <param name="Failed">How many items were sent but came back with nothing usable.</param>
    public sealed record EnrichmentRunResult(
        IReadOnlyList<StoredEnrichment> Enrichment,
        IReadOnlyList<QueryCallRecord> Calls,
        int Failed);

    /// <summary>
    /// The one paid pass that runs at index time.
    /// </summary>
    /// <remarks>
    /// Overviews describe the premise; people remember moments. This pass asks a
    /// model that has seen these films how someone half-remembering one would
    /// describe it, and those phrasings are indexed pointing back at the item. That
    /// turns "the one where they kill the guy's dog" from an impossible match into
    /// an easy one, because the comparison is now fuzzy-sentence against
    /// fuzzy-sentence rather than fuzzy-sentence against marketing copy.
    /// <para>
    /// It is also the pass where spending up is obviously right: it runs once, its
    /// output is cached forever, and its quality is the ceiling on what any future
    /// query can retrieve.
    /// </para>
    /// </remarks>
    public sealed class EnrichmentService
    {
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
        /// <param name="documents">The documents to enrich. Callers pass only the stale ones.</param>
        /// <param name="config">The plugin configuration.</param>
        /// <param name="progress">Reports fraction complete, or null.</param>
        /// <param name="cancellationToken">Cancellation token.</param>
        /// <returns>What was generated.</returns>
        public async Task<EnrichmentRunResult> EnrichAsync(
            IReadOnlyList<ItemDocument> documents,
            PluginConfiguration config,
            IProgress<double>? progress,
            CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(documents);
            ArgumentNullException.ThrowIfNull(config);

            var results = new List<StoredEnrichment>();
            var calls = new List<QueryCallRecord>();
            var failed = 0;

            if (documents.Count == 0)
            {
                return new EnrichmentRunResult(results, calls, failed);
            }

            // Hard rule 12: one Normalize for the whole run, and every pass resolves
            // from it. Normalizing per batch would mint a fresh id for a repaired
            // profile each time and report one run as many models.
            var normalized = ModelProfiles.Normalize(config);
            var profile = ModelProfiles.Resolve(normalized, config.EnrichmentModelProfileId);
            var provider = _providerFactory.Create(profile, config.EnableThinking);

            var batchSize = Math.Max(1, config.EnrichmentBatchSize);
            var batches = documents.Chunk(batchSize).ToList();

            _logger.LogInformation(
                "Concierge: enriching {Items} item(s) in {Batches} batch(es) with {Model}",
                documents.Count,
                batches.Count,
                profile.Model);

            for (var i = 0; i < batches.Count; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var batch = batches[i];
                var (batchResults, call, batchFailed) =
                    await EnrichBatchAsync(batch, provider, profile, config, cancellationToken).ConfigureAwait(false);

                results.AddRange(batchResults);
                failed += batchFailed;
                if (call is not null)
                {
                    calls.Add(call);
                }

                progress?.Report((i + 1) / (double)batches.Count);
            }

            _logger.LogInformation(
                "Concierge: enrichment finished — {Enriched} enriched, {Empty} the model did not know, "
                + "{Failed} failed, ${Cost}",
                results.Count(r => !r.Enrichment.IsEmpty),
                results.Count(r => r.Enrichment.IsEmpty),
                failed,
                calls.Sum(c => c.EstimatedCostUsd).ToString("F4", System.Globalization.CultureInfo.InvariantCulture));

            return new EnrichmentRunResult(results, calls, failed);
        }

        private async Task<(List<StoredEnrichment> Results, QueryCallRecord? Call, int Failed)> EnrichBatchAsync(
            ItemDocument[] batch,
            ILlmProvider provider,
            ModelProfile profile,
            PluginConfiguration config,
            CancellationToken cancellationToken)
        {
            var results = new List<StoredEnrichment>();

            // Everything goes in the cacheable prefix and the suffix is left empty,
            // which means no cache marker is written. That is deliberate: each batch
            // sends a different item list, so there is nothing a later call could read
            // back, and marking it anyway would pay the cache-write premium on every
            // batch for a hit that can never happen.
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
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // One failed batch must not sink an index build that has already paid
                // for everything before it. These items keep their old hash and are
                // retried on the next run.
                _logger.LogWarning(ex, "Concierge: an enrichment batch failed; its items stay unenriched");
                return (results, null, batch.Length);
            }

            stopwatch.Stop();

            var call = new QueryCallRecord(
                QueryPass.Enrichment,
                profile.Provider.ToString(),
                provider.ModelId,
                result.InputTokens,
                result.OutputTokens,
                result.CacheReadTokens,
                result.CacheWriteTokens,
                result.ThinkingTokens,
                CallCost.ForChat(
                    profile,
                    result.InputTokens,
                    result.OutputTokens,
                    result.CacheReadTokens,
                    result.CacheWriteTokens),
                (int)stopwatch.ElapsedMilliseconds,
                result.Truncated);

            if (result.Truncated)
            {
                _logger.LogWarning(
                    "Concierge: an enrichment batch hit the output cap. Lower EnrichmentBatchSize or raise "
                    + "MaxOutputTokens — thinking counts against the same cap as the answer.");
            }

            IReadOnlyDictionary<int, Enrichment> parsed;
            try
            {
                parsed = EnrichmentParser.Parse(result.Text, batch.Length);
            }
            catch (FormatException ex)
            {
                _logger.LogWarning(ex, "Concierge: an enrichment batch returned no usable JSON");
                return (results, call, batch.Length);
            }

            var missing = 0;
            for (var i = 0; i < batch.Length; i++)
            {
                if (!parsed.TryGetValue(i, out var enrichment))
                {
                    // Silently omitted by the model. Left unstored so the next run
                    // asks again, rather than recording an empty answer it never gave.
                    missing++;
                    continue;
                }

                results.Add(new StoredEnrichment(
                    batch[i].ItemId,
                    DocumentHash.Of(batch[i]),
                    enrichment,
                    DateTime.UtcNow));
            }

            return (results, call, missing);
        }
    }
}
