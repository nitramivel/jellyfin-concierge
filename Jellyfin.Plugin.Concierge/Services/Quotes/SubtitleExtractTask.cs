using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Concierge.Services.Quotes
{
    /// <summary>
    /// Reads dialogue out of the library so quotes become searchable.
    /// </summary>
    /// <remarks>
    /// Weekly rather than daily: unlike the item index, this only has work to do when
    /// files are added or re-encoded, and a pass over an unchanged library is a few
    /// seconds of stat calls.
    /// <para>
    /// It is not triggered automatically on install. The first run over embedded
    /// tracks is real CPU work, and starting hours of ffmpeg without being asked is
    /// not a reasonable thing for a plugin to do.
    /// </para>
    /// </remarks>
    public sealed class SubtitleExtractTask : IScheduledTask
    {
        private readonly SubtitleIndexer _indexer;
        private readonly ILogger<SubtitleExtractTask> _logger;

        public SubtitleExtractTask(SubtitleIndexer indexer, ILogger<SubtitleExtractTask> logger)
        {
            _indexer = indexer;
            _logger = logger;
        }

        /// <inheritdoc />
        public string Name => "Read dialogue for Concierge quote search";

        /// <inheritdoc />
        public string Key => "ConciergeSubtitleExtract";

        /// <inheritdoc />
        public string Description =>
            "Extracts subtitles and indexes the dialogue, so searching for a line of a film finds it and the "
            + "moment it is said. Costs no money — only CPU, and only for files it has not read before.";

        /// <inheritdoc />
        public string Category => "Concierge";

        /// <inheritdoc />
        public async Task ExecuteAsync(IProgress<double> progress, CancellationToken cancellationToken)
        {
            var config = Plugin.Instance?.Configuration;
            if (config is null)
            {
                _logger.LogWarning("Concierge: the plugin is not loaded; skipping dialogue extraction");
                return;
            }

            if (!config.EnableQuoteSearch)
            {
                _logger.LogInformation("Concierge: quote search is switched off; nothing to extract");
                return;
            }

            try
            {
                await _indexer.RunAsync(config, progress, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // Every item is written as it is read, so a stop keeps everything done
                // so far and the next run resumes rather than restarting.
                _logger.LogInformation(
                    "Concierge: dialogue extraction stopped. Everything read so far is saved; "
                    + "running it again picks up where this left off.");
                throw;
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
                    IntervalTicks = TimeSpan.FromDays(7).Ticks,
                },
            ];
        }
    }
}
