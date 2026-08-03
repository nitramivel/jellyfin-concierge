using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Concierge.Services.Indexing
{
    /// <summary>
    /// Pays for enrichment without publishing an index.
    /// </summary>
    /// <remarks>
    /// The expensive half of a build, on its own schedule. Enrichment over this
    /// library's episodes was measured at eight hours and 45% unknown-to-model, and
    /// while it was welded to the index build it also pinned the search index for that
    /// whole time — a working day in which no metadata change could reach search.
    /// <para>
    /// This task runs the same scan and the same enrichment pass, writes the answers
    /// to the enrichment store, and stops. <c>state.json</c>, <c>docs.json</c>,
    /// <c>rows.json</c> and <c>vectors.bin</c> are untouched and the generation counter
    /// does not move, so search carries on serving exactly what it was serving before.
    /// The next ordinary build picks the answers up by source hash and pays nothing for
    /// them.
    /// </para>
    /// <para>
    /// <b>It has no default trigger.</b> Everything else in this plugin is cheap enough
    /// to run nightly without asking; this is not, and the repository's standing rule is
    /// that the larger model workload is opt-in. The owner schedules it, or runs it by
    /// hand from Dashboard → Scheduled Tasks.
    /// </para>
    /// </remarks>
    public sealed class EnrichmentBankTask : IScheduledTask
    {
        private readonly ItemIndexer _indexer;
        private readonly ILogger<EnrichmentBankTask> _logger;

        public EnrichmentBankTask(ItemIndexer indexer, ILogger<EnrichmentBankTask> logger)
        {
            _indexer = indexer;
            _logger = logger;
        }

        /// <summary>The stable key Jellyfin uses for this scheduled task.</summary>
        public const string TaskKey = "ConciergeEnrichmentBank";

        /// <inheritdoc />
        public string Name => "Bank Concierge enrichment (leaves the index alone)";

        /// <inheritdoc />
        public string Key => TaskKey;

        /// <inheritdoc />
        public string Description =>
            "Asks the enrichment model about anything new and saves the answers, without rebuilding or "
            + "replacing the search index. Use this for a long enrichment pass — episodes especially — "
            + "so searching keeps working normally while it runs. The next index build picks the answers "
            + "up for free. This costs money.";

        /// <inheritdoc />
        public string Category => "Concierge";

        /// <inheritdoc />
        public async Task ExecuteAsync(IProgress<double> progress, CancellationToken cancellationToken)
        {
            var config = Plugin.Instance?.Configuration;
            if (config is null)
            {
                _logger.LogWarning("Concierge: the plugin is not loaded; skipping the enrichment pass");
                return;
            }

            if (!config.EnableEnrichment)
            {
                // Nothing to do, and saying so is better than a run that reports
                // success having called nothing and spent nothing.
                _logger.LogInformation(
                    "Concierge: enrichment is switched off, so there is nothing for this task to bank. "
                    + "Turn it on in Dashboard → Plugins → Concierge first.");
                return;
            }

            try
            {
                var result = await _indexer
                    .BuildAsync(
                        config,
                        "enrichment-only",
                        progress,
                        cancellationToken,
                        regenerate: false,
                        enrichOnly: true)
                    .ConfigureAwait(false);

                if (result.Skipped)
                {
                    return;
                }

                // Deliberately no _search.Invalidate() here. The search path caches the
                // index, and this task did not write one — dropping that cache would
                // cost a reload of every vector to serve byte-identical results.
                _logger.LogInformation(
                    "Concierge: banked enrichment for {Enriched} item(s) across {Items} scanned, ${Cost:F4}. "
                    + "The search index is unchanged. Run {RunId}",
                    result.Enriched,
                    result.Items,
                    result.CostUsd,
                    result.RunId);
            }
            catch (OperationCanceledException)
            {
                // Checkpointed throughout, and the store merges rather than replaces,
                // so a stop keeps every answer already paid for.
                _logger.LogInformation(
                    "Concierge: the enrichment pass was cancelled. Everything paid for before the stop is "
                    + "saved, and the next run asks only for what is still missing.");
                throw;
            }
            catch (InvalidOperationException ex)
            {
                _logger.LogError(
                    "Concierge: cannot enrich — {Reason} Open Dashboard → Plugins → Concierge and set a "
                    + "model profile, then run this task again.",
                    ex.Message);
                throw;
            }
            catch (Exception ex)
            {
                // Nothing was published, so a failure here cannot damage search.
                _logger.LogError(
                    ex, "Concierge: the enrichment pass failed; the search index is untouched and still in use");
                throw;
            }
        }

        /// <inheritdoc />
        /// <remarks>
        /// None on purpose. See the note on the class: this one spends real money on a
        /// large library, so it runs when the owner says so and not before.
        /// </remarks>
        public IEnumerable<TaskTriggerInfo> GetDefaultTriggers() => [];
    }
}
