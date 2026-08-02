using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Concierge.Services.Indexing
{
    /// <summary>
    /// Rebuilds the search index on a schedule.
    /// </summary>
    /// <remarks>
    /// Daily by default and cheap to run: everything is keyed by a hash of the
    /// item's source text, so a night where nothing in the library changed embeds
    /// nothing and calls no model. Only genuinely new or edited items cost anything.
    /// </remarks>
    public sealed class IndexBuildTask : IScheduledTask
    {
        private readonly ItemIndexer _indexer;
        private readonly IndexBuildRequest _request;
        private readonly SearchService _search;
        private readonly ILogger<IndexBuildTask> _logger;

        public IndexBuildTask(
            ItemIndexer indexer,
            IndexBuildRequest request,
            SearchService search,
            ILogger<IndexBuildTask> logger)
        {
            _indexer = indexer;
            _request = request;
            _search = search;
            _logger = logger;
        }

        /// <summary>The stable key Jellyfin uses for this scheduled task.</summary>
        public const string TaskKey = "ConciergeIndexBuild";

        /// <inheritdoc />
        public string Name => "Build the Concierge search index";

        /// <inheritdoc />
        public string Key => TaskKey;

        /// <inheritdoc />
        public string Description =>
            "Scans the library, asks the enrichment model about anything new, and rebuilds the "
            + "keyword and semantic indexes. Items that have not changed cost nothing.";

        /// <inheritdoc />
        public string Category => "Concierge";

        /// <inheritdoc />
        public async Task ExecuteAsync(IProgress<double> progress, CancellationToken cancellationToken)
        {
            var config = Plugin.Instance?.Configuration;
            if (config is null)
            {
                _logger.LogWarning("Concierge: the plugin is not loaded; skipping the index build");
                return;
            }

            var regenerate = false;
            try
            {
                regenerate = _request.ConsumeFullRegeneration();
                var result = await _indexer
                    .BuildAsync(
                        config,
                        regenerate ? "manual-regeneration" : "scheduled",
                        progress,
                        cancellationToken,
                        regenerate)
                    .ConfigureAwait(false);

                // The new index is on disk; drop the copy the search path is holding
                // so the next query picks it up rather than serving the old one until
                // the server restarts.
                _search.Invalidate();

                _logger.LogInformation(
                    "Concierge: index built — {Items} item(s), {Rows} row(s), {Embedded} embedded, "
                    + "{Reused} reused, {Enriched} enriched, ${Cost:F4}. Run {RunId}",
                    result.Items,
                    result.Rows,
                    result.Embedded,
                    result.Reused,
                    result.Enriched,
                    result.CostUsd,
                    result.RunId);
            }
            catch (OperationCanceledException)
            {
                // Enrichment checkpoints as it goes, so a cancel keeps everything it
                // has already paid for and the next run resumes rather than restarts.
                _logger.LogInformation(
                    "Concierge: index build cancelled. Enrichment completed before the stop was saved; "
                    + "the next run picks up where this one left off.");
                throw;
            }
            catch (InvalidOperationException ex)
            {
                // Nothing is configured yet. That is the state every fresh install
                // starts in, so it gets a sentence the owner can act on rather than a
                // stack trace suggesting the plugin is broken.
                _logger.LogError(
                    "Concierge: cannot build the index — {Reason} Open Dashboard → Plugins → Concierge and set "
                    + "an embedding profile, then run this task again.",
                    ex.Message);
                throw;
            }
            catch (Exception ex)
            {
                // A failed build leaves the previous index in place and searchable.
                _logger.LogError(ex, "Concierge: the index build failed; the previous index is still in use");
                throw;
            }
            finally
            {
                if (regenerate)
                {
                    _request.CompleteFullRegeneration();
                }
            }
        }

        /// <inheritdoc />
        public IEnumerable<TaskTriggerInfo> GetDefaultTriggers()
        {
            return
            [
                new TaskTriggerInfo
                {
                    Type = TaskTriggerInfoType.IntervalTrigger,
                    IntervalTicks = TimeSpan.FromHours(24).Ticks,
                },
            ];
        }
    }
}
