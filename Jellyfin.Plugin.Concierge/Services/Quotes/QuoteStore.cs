using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Common.Configuration;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Concierge.Services.Quotes
{
    /// <summary>One cleaned cue, as stored.</summary>
    /// <param name="StartTicks">When it appears.</param>
    /// <param name="EndTicks">When it goes away.</param>
    /// <param name="Text">The cleaned dialogue, for searching.</param>
    /// <param name="Raw">The line as it appeared, for display.</param>
    public sealed record StoredCue(long StartTicks, long EndTicks, string Text, string Raw);

    /// <summary>
    /// One item's extracted dialogue, and the fingerprint of the file it came from.
    /// </summary>
    /// <remarks>
    /// <paramref name="StreamIndex"/>, <paramref name="SourcePath"/>,
    /// <paramref name="FileSize"/> and <paramref name="FileModifiedUtc"/> together are
    /// the staleness key from §6.6. A re-index is then free, and a re-encoded file
    /// re-extracts — which matters because extraction is the expensive part and
    /// re-running it needlessly is hours of CPU.
    /// </remarks>
    /// <param name="ItemId">The film or episode.</param>
    /// <param name="Title">Its title, so the coverage report reads without the item index.</param>
    /// <param name="StreamIndex">Which subtitle stream was used.</param>
    /// <param name="SourcePath">The media file's path.</param>
    /// <param name="FileSize">Its size in bytes.</param>
    /// <param name="FileModifiedUtc">Its last write time.</param>
    /// <param name="ExtractedUtc">When Concierge read it.</param>
    /// <param name="Cues">The cleaned dialogue.</param>
    public sealed record QuoteTrack(
        Guid ItemId,
        string Title,
        int StreamIndex,
        string SourcePath,
        long FileSize,
        DateTime FileModifiedUtc,
        DateTime ExtractedUtc,
        IReadOnlyList<StoredCue> Cues)
    {
        /// <summary>
        /// Whether this extraction still describes the file on disk.
        /// </summary>
        /// <param name="streamIndex">The stream that would be chosen now.</param>
        /// <param name="path">The file's path now.</param>
        /// <param name="size">Its size now.</param>
        /// <param name="modifiedUtc">Its last write time now.</param>
        /// <returns>True when nothing needs re-extracting.</returns>
        public bool IsFresh(int streamIndex, string? path, long size, DateTime modifiedUtc)
            => StreamIndex == streamIndex
                && string.Equals(SourcePath, path ?? string.Empty, StringComparison.Ordinal)
                && FileSize == size
                && FileModifiedUtc == modifiedUtc;
    }

    /// <summary>Why an item is or is not searchable by dialogue.</summary>
    /// <param name="ItemId">The item.</param>
    /// <param name="Title">Its title.</param>
    /// <param name="Year">Its year, or null.</param>
    /// <param name="Indexed">Whether dialogue was extracted.</param>
    /// <param name="Reason">
    /// What happened, in words the owner can act on — "image-only subtitles (PGS or
    /// VobSub)" is a problem they can fix by downloading an external track.
    /// </param>
    /// <param name="CueCount">How many lines were indexed.</param>
    public sealed record QuoteCoverage(
        Guid ItemId,
        string Title,
        int? Year,
        bool Indexed,
        string Reason,
        int CueCount);

    /// <summary>One subtitle track that could be extracted.</summary>
    /// <param name="Index">The stream index, which is what extraction is given.</param>
    /// <param name="Language">Its language code, when it declares one.</param>
    /// <param name="DisplayTitle">How Jellyfin names it.</param>
    /// <param name="Codec">srt, ass, pgssub and so on.</param>
    /// <param name="IsForced">Forced tracks caption only foreign speech, so they are near-empty.</param>
    /// <param name="IsDefault">Whether the file marks it as the default.</param>
    /// <param name="IsExternal">Whether it is a sidecar file rather than embedded.</param>
    /// <param name="IsImage">
    /// Whether it holds pictures of words. Extraction cannot read those, and a list
    /// that does not say so invites picking one and wondering why nothing came out.
    /// </param>
    public sealed record SubtitleTrackOption(
        int Index,
        string Language,
        string DisplayTitle,
        string Codec,
        bool IsForced,
        bool IsDefault,
        bool IsExternal,
        bool IsImage);

    /// <summary>Reads and writes extracted dialogue.</summary>
    public interface IQuoteStore
    {
        /// <summary>Loads one item's extraction, or null.</summary>
        /// <param name="itemId">The item.</param>
        /// <param name="cancellationToken">Cancellation token.</param>
        /// <returns>The track, or null.</returns>
        Task<QuoteTrack?> LoadAsync(Guid itemId, CancellationToken cancellationToken);

        /// <summary>Writes one item's extraction.</summary>
        /// <param name="track">The track.</param>
        /// <param name="cancellationToken">Cancellation token.</param>
        /// <returns>A task.</returns>
        Task SaveAsync(QuoteTrack track, CancellationToken cancellationToken);

        /// <summary>Loads every extraction.</summary>
        /// <param name="cancellationToken">Cancellation token.</param>
        /// <returns>Every stored track.</returns>
        Task<IReadOnlyList<QuoteTrack>> LoadAllAsync(CancellationToken cancellationToken);

        /// <summary>Writes the coverage report.</summary>
        /// <param name="coverage">Every item and its outcome.</param>
        /// <param name="cancellationToken">Cancellation token.</param>
        /// <returns>A task.</returns>
        Task SaveCoverageAsync(IReadOnlyList<QuoteCoverage> coverage, CancellationToken cancellationToken);

        /// <summary>Reads the coverage report.</summary>
        /// <param name="cancellationToken">Cancellation token.</param>
        /// <returns>The report, newest write wins.</returns>
        Task<IReadOnlyList<QuoteCoverage>> LoadCoverageAsync(CancellationToken cancellationToken);

        /// <summary>Deletes every extraction.</summary>
        /// <param name="cancellationToken">Cancellation token.</param>
        /// <returns>A task.</returns>
        Task DeleteAsync(CancellationToken cancellationToken);

        /// <summary>Forgets one item's extraction, so the next run redoes it.</summary>
        /// <param name="itemId">The item.</param>
        /// <param name="cancellationToken">Cancellation token.</param>
        /// <returns>Whether there was anything to forget.</returns>
        /// <remarks>
        /// Extraction is skipped when a track already exists and the file behind it is
        /// unchanged. That is right for a rebuild and wrong for a track that picked the
        /// wrong language — the file has not changed and never will, so nothing short
        /// of forgetting it will make the next run look again.
        /// </remarks>
        Task<bool> ForgetAsync(Guid itemId, CancellationToken cancellationToken);
    }

    /// <summary>
    /// Default <see cref="IQuoteStore"/>: one file per item under
    /// <c>data/concierge/quotes</c>.
    /// </summary>
    /// <remarks>
    /// A file per item, and that is the whole resumability story. Extraction is a
    /// long job that <em>will</em> be interrupted — installing any plugin tears the
    /// host down mid-task — so each item is written the moment it is read, and a
    /// restart simply skips what is already there.
    /// <para>
    /// Only cleaned cues are stored; windows are rebuilt on load. Windowing is pure
    /// and fast, and deriving it means the window size can be changed without
    /// re-extracting anything.
    /// </para>
    /// </remarks>
    public sealed class QuoteStore : IQuoteStore
    {
        private const string CoverageFile = "coverage.json";

        private static readonly JsonSerializerOptions SerializerOptions = new() { WriteIndented = false };

        private readonly string _directory;
        private readonly ILogger<QuoteStore> _logger;
        private readonly SemaphoreSlim _gate = new(1, 1);

        public QuoteStore(IApplicationPaths applicationPaths, ILogger<QuoteStore> logger)
        {
            ArgumentNullException.ThrowIfNull(applicationPaths);

            _logger = logger;
            _directory = Path.Combine(applicationPaths.DataPath, "concierge", "quotes");
        }

        /// <inheritdoc />
        public async Task<QuoteTrack?> LoadAsync(Guid itemId, CancellationToken cancellationToken)
        {
            var path = PathFor(itemId);
            if (!File.Exists(path))
            {
                return null;
            }

            try
            {
                await using var stream = File.OpenRead(path);
                return await JsonSerializer
                    .DeserializeAsync<QuoteTrack>(stream, SerializerOptions, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // A corrupt file for one film costs that film its dialogue, and the
                // next extraction run replaces it. It must not take the others down.
                _logger.LogWarning(ex, "Concierge: could not read extracted dialogue for {ItemId}", itemId);
                return null;
            }
        }

        /// <inheritdoc />
        public async Task SaveAsync(QuoteTrack track, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(track);

            await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                Directory.CreateDirectory(_directory);
                await WriteJsonAsync(PathFor(track.ItemId), track, cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                _gate.Release();
            }
        }

        /// <inheritdoc />
        public async Task<IReadOnlyList<QuoteTrack>> LoadAllAsync(CancellationToken cancellationToken)
        {
            var tracks = new List<QuoteTrack>();
            if (!Directory.Exists(_directory))
            {
                return tracks;
            }

            foreach (var file in Directory.EnumerateFiles(_directory, "item_*.json"))
            {
                cancellationToken.ThrowIfCancellationRequested();

                try
                {
                    await using var stream = File.OpenRead(file);
                    var track = await JsonSerializer
                        .DeserializeAsync<QuoteTrack>(stream, SerializerOptions, cancellationToken)
                        .ConfigureAwait(false);

                    if (track is not null)
                    {
                        tracks.Add(track);
                    }
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _logger.LogWarning(ex, "Concierge: skipping unreadable dialogue file {File}", file);
                }
            }

            return tracks;
        }

        /// <inheritdoc />
        public async Task SaveCoverageAsync(
            IReadOnlyList<QuoteCoverage> coverage,
            CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(coverage);

            await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                Directory.CreateDirectory(_directory);
                await WriteJsonAsync(
                        Path.Combine(_directory, CoverageFile), coverage, cancellationToken)
                    .ConfigureAwait(false);
            }
            finally
            {
                _gate.Release();
            }
        }

        /// <inheritdoc />
        public async Task<IReadOnlyList<QuoteCoverage>> LoadCoverageAsync(CancellationToken cancellationToken)
        {
            var path = Path.Combine(_directory, CoverageFile);
            if (!File.Exists(path))
            {
                return [];
            }

            try
            {
                await using var stream = File.OpenRead(path);
                return await JsonSerializer
                    .DeserializeAsync<List<QuoteCoverage>>(stream, SerializerOptions, cancellationToken)
                    .ConfigureAwait(false) ?? [];
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning(ex, "Concierge: could not read the dialogue coverage report");
                return [];
            }
        }

        /// <inheritdoc />
        /// <inheritdoc />
        public async Task<bool> ForgetAsync(Guid itemId, CancellationToken cancellationToken)
        {
            await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                var file = PathFor(itemId);

                if (!File.Exists(file))
                {
                    return false;
                }

                File.Delete(file);
                _logger.LogInformation(
                    "Concierge: forgot the extracted dialogue for {ItemId}; the next extraction will look again",
                    itemId);

                return true;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Concierge: could not forget the dialogue for {ItemId}", itemId);
                return false;
            }
            finally
            {
                _gate.Release();
            }
        }

        public async Task DeleteAsync(CancellationToken cancellationToken)
        {
            await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                if (Directory.Exists(_directory))
                {
                    Directory.Delete(_directory, recursive: true);
                }

                _logger.LogInformation(
                    "Concierge: extracted dialogue deleted. Quote search is off until the extraction task runs again.");
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Concierge: could not delete extracted dialogue");
            }
            finally
            {
                _gate.Release();
            }
        }

        private string PathFor(Guid itemId)
            => Path.Combine(_directory, "item_" + itemId.ToString("N", CultureInfo.InvariantCulture) + ".json");

        private static async Task WriteJsonAsync<T>(string path, T value, CancellationToken cancellationToken)
        {
            var temporary = path + ".tmp";

            await using (var stream = File.Create(temporary))
            {
                await JsonSerializer.SerializeAsync(stream, value, SerializerOptions, cancellationToken)
                    .ConfigureAwait(false);
            }

            File.Move(temporary, path, overwrite: true);
        }
    }
}
