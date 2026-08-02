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
using MediaBrowser.Controller.Lyrics;
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
        private readonly ILyricManager _lyrics;
        private readonly IQuoteStore _store;
        private readonly QuoteIndexProvider _provider;
        private readonly ILogger<SubtitleIndexer> _logger;

        public SubtitleIndexer(
            ILibraryScanner scanner,
            IMediaSourceManager mediaSources,
            ISubtitleEncoder subtitles,
            ILyricManager lyrics,
            IQuoteStore store,
            QuoteIndexProvider provider,
            ILogger<SubtitleIndexer> logger)
        {
            _scanner = scanner;
            _mediaSources = mediaSources;
            _subtitles = subtitles;
            _lyrics = lyrics;
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

            if (config.EnableLyricSearch)
            {
                var lyrics = await IndexLyricsAsync(coverage, cancellationToken).ConfigureAwait(false);
                extracted += lyrics.Extracted;
                skipped += lyrics.Skipped;
                unavailable += lyrics.Unavailable;
                cues += lyrics.Cues;
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

        /// <summary>
        /// Indexes song lyrics alongside film dialogue.
        /// </summary>
        /// <remarks>
        /// Far cheaper than subtitles and a different shape of job. Jellyfin already
        /// holds lyrics as parsed, time-stamped lines — fetched by whatever lyric
        /// provider is configured — so there is no ffmpeg, no extraction and no
        /// format to parse. It is a read.
        /// <para>
        /// They land in the same phrase index as dialogue, which is the point: a
        /// remembered line is a remembered line, and the searcher should not have to
        /// know whether it was spoken or sung. A matched lyric deep-links to the
        /// second it is sung, exactly as a quoted line does.
        /// </para>
        /// </remarks>
        private async Task<(int Extracted, int Skipped, int Unavailable, int Cues)> IndexLyricsAsync(
            List<QuoteCoverage> coverage,
            CancellationToken cancellationToken)
        {
            var songs = _scanner.ScanAudio();
            var extracted = 0;
            var skipped = 0;
            var unavailable = 0;
            var cues = 0;

            foreach (var item in songs)
            {
                cancellationToken.ThrowIfCancellationRequested();

                // The lyric API is typed to Audio rather than BaseItem, which is a
                // useful narrowing: anything else in a music library — a folder, a
                // playlist — simply has no lyrics to ask for.
                if (item is not MediaBrowser.Controller.Entities.Audio.Audio song)
                {
                    continue;
                }

                QuoteCoverage Cover(bool indexed, string reason, int count = 0)
                    => new(song.Id, song.Name ?? string.Empty, song.ProductionYear, indexed, reason, count);

                long size = 0;
                var modified = DateTime.MinValue;
                try
                {
                    if (!string.IsNullOrEmpty(song.Path) && File.Exists(song.Path))
                    {
                        var info = new FileInfo(song.Path);
                        size = info.Length;
                        modified = info.LastWriteTimeUtc;
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "Concierge: could not stat {Path}", song.Path);
                }

                var existing = await _store.LoadAsync(song.Id, cancellationToken).ConfigureAwait(false);
                if (existing is not null && existing.IsFresh(-1, song.Path, size, modified))
                {
                    skipped++;
                    coverage.Add(Cover(true, "lyrics", existing.Cues.Count));
                    continue;
                }

                try
                {
                    var lyrics = await _lyrics.GetLyricsAsync(song, cancellationToken).ConfigureAwait(false);
                    var lines = lyrics?.Lyrics;

                    if (lines is null || lines.Count == 0)
                    {
                        unavailable++;
                        coverage.Add(Cover(false, "no lyrics available"));
                        continue;
                    }

                    // Cleaned with the same rules as subtitles, which strips the
                    // "[Chorus]" and "(x2)" markers lyric files carry and would
                    // otherwise match on.
                    var cleaned = new List<Core.Subtitles.CleanCue>(lines.Count);
                    foreach (var line in lines)
                    {
                        var text = CueCleaner.CleanLine(line.Text);
                        if (text.Length == 0)
                        {
                            continue;
                        }

                        // An unsynced lyric file has no timings at all. Those are still
                        // worth indexing — the song is findable, it just cannot be
                        // seeked to — so a missing start becomes zero rather than a skip.
                        var start = TimeSpan.FromTicks(line.Start ?? 0);
                        cleaned.Add(new Core.Subtitles.CleanCue(start, start, text, line.Text ?? text));
                    }

                    if (cleaned.Count == 0)
                    {
                        unavailable++;
                        coverage.Add(Cover(false, "lyrics held no words once cleaned"));
                        continue;
                    }

                    // -1 as the stream index marks this as lyrics rather than a
                    // subtitle track, so the staleness check cannot confuse the two.
                    await _store.SaveAsync(
                            new QuoteTrack(
                                song.Id,
                                song.Name ?? string.Empty,
                                -1,
                                song.Path ?? string.Empty,
                                size,
                                modified,
                                DateTime.UtcNow,
                                cleaned
                                    .Select(c => new StoredCue(c.Start.Ticks, c.End.Ticks, c.Text, c.Raw))
                                    .ToList()),
                            cancellationToken)
                        .ConfigureAwait(false);

                    extracted++;
                    cues += cleaned.Count;
                    coverage.Add(Cover(true, "lyrics", cleaned.Count));
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "Concierge: could not read lyrics for {Song}", song.Name);
                    unavailable++;
                    coverage.Add(Cover(false, "lyrics could not be read"));
                }
            }

            _logger.LogInformation(
                "Concierge: lyrics — {Extracted} song(s) indexed, {Skipped} unchanged, {Without} without lyrics",
                extracted,
                skipped,
                unavailable);

            return (extracted, skipped, unavailable, cues);
        }

        /// <summary>
        /// Lists an item's subtitle tracks, and says which one is stored.
        /// </summary>
        /// <param name="item">The item.</param>
        /// <returns>Its tracks, best-first by the same rules extraction would use.</returns>
        public IReadOnlyList<SubtitleTrackOption> Tracks(BaseItem item)
        {
            ArgumentNullException.ThrowIfNull(item);

            IReadOnlyList<MediaBrowser.Model.Entities.MediaStream> streams;
            try
            {
                streams = _mediaSources.GetMediaStreams(item.Id);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Concierge: could not read streams for {Item}", item.Name);
                return [];
            }

            return streams
                .Where(s => s.Type == MediaBrowser.Model.Entities.MediaStreamType.Subtitle)
                .Select(s => new SubtitleTrackOption(
                    s.Index,
                    s.Language ?? string.Empty,
                    s.DisplayTitle ?? s.Title ?? string.Empty,
                    s.Codec ?? string.Empty,
                    s.IsForced,
                    s.IsDefault,
                    s.IsExternal,

                    // Image subtitles hold pictures of words. Extraction cannot read
                    // them and the list should say so rather than let somebody pick
                    // one and wonder why nothing came out.
                    !s.IsTextSubtitleStream))
                .ToList();
        }

        /// <summary>
        /// Extracts one item's dialogue from a nominated track, now.
        /// </summary>
        /// <param name="item">The item.</param>
        /// <param name="streamIndex">The track to read.</param>
        /// <param name="config">The configuration.</param>
        /// <param name="cancellationToken">Cancellation token.</param>
        /// <returns>What happened, and how many lines came out.</returns>
        /// <remarks>
        /// Bypasses the freshness check on purpose. That check exists so a rebuild is
        /// free, and it is precisely what stops a wrong-language track being redone —
        /// the media file has not changed and never will.
        /// </remarks>
        public async Task<QuoteCoverage> ExtractAsync(
            BaseItem item,
            int streamIndex,
            PluginConfiguration config,
            CancellationToken cancellationToken)
        {
            var (_, coverage) = await ProcessAsync(item, config, cancellationToken, streamIndex)
                .ConfigureAwait(false);

            _provider.Invalidate();

            return coverage;
        }

        private async Task<(Status Status, QuoteCoverage Coverage)> ProcessAsync(
            BaseItem item,
            PluginConfiguration config,
            CancellationToken cancellationToken,
            int? forcedStreamIndex = null)
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

            // A forced index is somebody looking at the list and picking. The
            // selector's rules — preferred language, not forced, text over image —
            // are good defaults and exactly the thing being overruled, so they do not
            // get a second say.
            var choice = forcedStreamIndex is { } forced
                ? new TrackChoice(
                    streams.FirstOrDefault(s => s.Index == forced),
                    "chosen by hand")
                : TrackSelector.Choose(streams, config.SubtitleLanguage);

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
            if (forcedStreamIndex is null
                && existing is not null
                && existing.IsFresh(stream.Index, item.Path, size, modified))
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
