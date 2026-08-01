using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Concierge.Configuration;
using Jellyfin.Plugin.Concierge.Core.Subtitles;
using Jellyfin.Plugin.Concierge.Services.Library;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.MediaEncoding;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Concierge.Services.Quotes
{
    /// <summary>What one extraction run did.</summary>
    /// <param name="Considered">Items looked at.</param>
    /// <param name="Extracted">Items whose dialogue was read this run.</param>
    /// <param name="Skipped">Items already extracted and unchanged.</param>
    /// <param name="Unavailable">Items with no usable text track.</param>
    /// <param name="Failed">Items whose extraction errored.</param>
    /// <param name="Cues">Lines of dialogue indexed this run.</param>
    public sealed record SubtitleRunResult(
        int Considered,
        int Extracted,
        int Skipped,
        int Unavailable,
        int Failed,
        int Cues);

    /// <summary>
    /// Reads dialogue out of the library, one item at a time.
    /// </summary>
    /// <remarks>
    /// <b>Extraction is the expensive part of quote search and is treated as such.</b>
    /// On an embedded stream <c>GetSubtitles</c> shells out to ffmpeg internally and
    /// takes seconds to a minute per file, so this is a throttled background job and
    /// never any part of a query or an index build.
    /// <para>
    /// It is resumable because it <em>will</em> be interrupted — installing any plugin
    /// tears the host down mid-task. Each item is written the moment it is read, and a
    /// restart skips whatever is already on disk, so progress is never lost.
    /// </para>
    /// <para>
    /// Films first, always. 140 items finishes in minutes and makes the feature
    /// demonstrable; several thousand episodes is an overnight job nobody asked for.
    /// </para>
    /// </remarks>
    public sealed class SubtitleIndexer
    {
        /// <summary>A pause between items, so a long run leaves the server usable.</summary>
        private static readonly TimeSpan Breather = TimeSpan.FromMilliseconds(250);

        private readonly ILibraryScanner _scanner;
        private readonly IMediaSourceManager _mediaSources;
        private readonly ISubtitleEncoder _subtitles;
        private readonly IQuoteStore _store;
        private readonly QuoteIndexProvider _provider;
        private readonly ILogger<SubtitleIndexer> _logger;

        public SubtitleIndexer(
            ILibraryScanner scanner,
            IMediaSourceManager mediaSources,
            ISubtitleEncoder subtitles,
            IQuoteStore store,
            QuoteIndexProvider provider,
            ILogger<SubtitleIndexer> logger)
        {
            _scanner = scanner;
            _mediaSources = mediaSources;
            _subtitles = subtitles;
            _store = store;
            _provider = provider;
            _logger = logger;
        }

        /// <summary>
        /// Extracts dialogue for everything that needs it.
        /// </summary>
        /// <param name="config">The plugin configuration.</param>
        /// <param name="progress">Reports 0-100, or null.</param>
        /// <param name="cancellationToken">Cancellation token.</param>
        /// <returns>What the run did.</returns>
        public async Task<SubtitleRunResult> RunAsync(
            PluginConfiguration config,
            IProgress<double>? progress,
            CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(config);

            // Films first. Episodes are opt-in, and the plan's own position is that
            // nobody needs a sitcom's every line to find one of them.
            var items = _scanner.Scan(config.QuoteIncludeEpisodes)
                .OrderBy(i => i.GetBaseItemKind() == Jellyfin.Data.Enums.BaseItemKind.Episode ? 1 : 0)
                .ToList();

            var coverage = new List<QuoteCoverage>(items.Count);
            var extracted = 0;
            var skipped = 0;
            var unavailable = 0;
            var failed = 0;
            var cues = 0;

            _logger.LogInformation(
                "Concierge: reading dialogue for {Count} item(s), episodes {Episodes}",
                items.Count,
                config.QuoteIncludeEpisodes ? "included" : "excluded");

            for (var i = 0; i < items.Count; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                progress?.Report(i / (double)items.Count * 100);

                var item = items[i];
                var outcome = await ProcessAsync(item, config, cancellationToken).ConfigureAwait(false);

                coverage.Add(outcome.Coverage);
                cues += outcome.Coverage.CueCount;

                switch (outcome.Status)
                {
                    case Status.Extracted:
                        extracted++;
                        break;
                    case Status.Skipped:
                        skipped++;
                        break;
                    case Status.Unavailable:
                        unavailable++;
                        break;
                    default:
                        failed++;
                        break;
                }

                if (outcome.Status == Status.Extracted)
                {
                    // Only pause after real work. Skipping an unchanged item should
                    // cost nothing at all, so a no-op run finishes in seconds.
                    await Task.Delay(Breather, cancellationToken).ConfigureAwait(false);

                    if (extracted % 10 == 0)
                    {
                        _logger.LogInformation(
                            "Concierge: dialogue {Done}/{Total} — {Extracted} read, {Skipped} unchanged, "
                            + "{Unavailable} without usable subtitles",
                            i + 1,
                            items.Count,
                            extracted,
                            skipped,
                            unavailable);
                    }
                }
            }

            await _store.SaveCoverageAsync(coverage, cancellationToken).ConfigureAwait(false);
            _provider.Invalidate();
            progress?.Report(100);

            _logger.LogInformation(
                "Concierge: dialogue finished — {Extracted} read, {Skipped} unchanged, {Unavailable} "
                + "without usable subtitles, {Failed} failed, {Cues} line(s) indexed",
                extracted,
                skipped,
                unavailable,
                failed,
                cues);

            return new SubtitleRunResult(items.Count, extracted, skipped, unavailable, failed, cues);
        }

        private async Task<(Status Status, QuoteCoverage Coverage)> ProcessAsync(
            BaseItem item,
            PluginConfiguration config,
            CancellationToken cancellationToken)
        {
            QuoteCoverage Cover(bool indexed, string reason, int count = 0)
                => new(item.Id, item.Name ?? string.Empty, item.ProductionYear, indexed, reason, count);

            IReadOnlyList<MediaBrowser.Model.Entities.MediaStream> streams;
            try
            {
                streams = _mediaSources.GetMediaStreams(item.Id);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Concierge: could not read streams for {Item}", item.Name);
                return (Status.Failed, Cover(false, "could not read media streams"));
            }

            var choice = TrackSelector.Choose(streams, config.SubtitleLanguage);
            if (!choice.Found)
            {
                return (Status.Unavailable, Cover(false, choice.Reason));
            }

            var stream = choice.Stream!;

            // The staleness key: stream, path, size and modified time together. A
            // re-index is then free, and a re-encoded file re-extracts.
            long size = 0;
            var modified = DateTime.MinValue;
            try
            {
                if (!string.IsNullOrEmpty(item.Path) && File.Exists(item.Path))
                {
                    var info = new FileInfo(item.Path);
                    size = info.Length;
                    modified = info.LastWriteTimeUtc;
                }
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Concierge: could not stat {Path}", item.Path);
            }

            var existing = await _store.LoadAsync(item.Id, cancellationToken).ConfigureAwait(false);
            if (existing is not null && existing.IsFresh(stream.Index, item.Path, size, modified))
            {
                return (Status.Skipped, Cover(true, choice.Reason, existing.Cues.Count));
            }

            try
            {
                // Always "srt". Jellyfin converts ASS, mov_text, WebVTT and external
                // files into it, so exactly one format is ever parsed.
                await using var subtitle = await _subtitles.GetSubtitles(
                        item,
                        item.Id.ToString("N", System.Globalization.CultureInfo.InvariantCulture),
                        stream.Index,
                        "srt",
                        0,
                        0,
                        false,
                        cancellationToken)
                    .ConfigureAwait(false);

                using var reader = new StreamReader(subtitle);
                var content = await reader.ReadToEndAsync(cancellationToken).ConfigureAwait(false);

                var cleaned = CueCleaner.Clean(SrtParser.Parse(content));
                if (cleaned.Count == 0)
                {
                    return (Status.Unavailable, Cover(false, "the track held no dialogue once cleaned"));
                }

                await _store.SaveAsync(
                        new QuoteTrack(
                            item.Id,
                            item.Name ?? string.Empty,
                            stream.Index,
                            item.Path ?? string.Empty,
                            size,
                            modified,
                            DateTime.UtcNow,
                            cleaned
                                .Select(c => new StoredCue(c.Start.Ticks, c.End.Ticks, c.Text, c.Raw))
                                .ToList()),
                        cancellationToken)
                    .ConfigureAwait(false);

                return (Status.Extracted, Cover(true, choice.Reason, cleaned.Count));
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                // One film failing to extract must not end a run that has already
                // spent an hour of CPU on the others.
                _logger.LogWarning(ex, "Concierge: could not extract dialogue for {Item}", item.Name);
                return (Status.Failed, Cover(false, "extraction failed: " + ex.Message));
            }
        }

        private enum Status
        {
            Extracted,
            Skipped,
            Unavailable,
            Failed,
        }
    }
}
