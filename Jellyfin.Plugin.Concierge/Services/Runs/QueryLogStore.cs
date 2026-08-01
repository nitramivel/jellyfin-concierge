using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Common.Configuration;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Concierge.Services.Runs
{
    /// <summary>
    /// Default <see cref="IQueryLogStore"/>: one append-only JSONL file per calendar
    /// month under <c>data/concierge/queries</c>.
    /// </summary>
    /// <remarks>
    /// <b>This replaces a 200-entry rolling window, and the change is the point.</b>
    /// The old shape kept the last two hundred searches and rewrote the whole file on
    /// every one of them — so search 201 destroyed search 1, and no usage question
    /// spanning more than a couple of days could ever be answered. A log you cannot
    /// break down only tells you what just happened.
    /// <para>
    /// Three properties follow from JSONL, and each was chosen:
    /// </para>
    /// <list type="bullet">
    /// <item><description>
    /// <b>Append, never rewrite.</b> Recording a search is one write of one line
    /// however much history exists, so the log can grow without the cost of writing
    /// it growing too.
    /// </description></item>
    /// <item><description>
    /// <b>Crash-tolerant by construction.</b> A process killed mid-write leaves one
    /// malformed final line, which the reader skips. The same accident against a
    /// single JSON array loses every record in the file.
    /// </description></item>
    /// <item><description>
    /// <b>Month-partitioned.</b> "What did August cost" reads one file, and retention
    /// is deleting old files rather than rewriting surviving ones.
    /// </description></item>
    /// </list>
    /// </remarks>
    public sealed class QueryLogStore : IQueryLogStore
    {
        /// <summary>How many monthly files are kept.</summary>
        /// <remarks>
        /// Two years, because the questions worth asking of this log are seasonal —
        /// "is it costing more than it used to", "did changing model help". A month of
        /// searches is a few hundred kilobytes, so the whole retention is smaller than
        /// one poster image.
        /// </remarks>
        public const int MonthsRetained = 24;

        private static readonly JsonSerializerOptions SerializerOptions = new() { WriteIndented = false };

        private readonly string _directory;
        private readonly ILogger<QueryLogStore> _logger;
        private readonly SemaphoreSlim _gate = new(1, 1);

        private bool _migrated;

        public QueryLogStore(IApplicationPaths applicationPaths, ILogger<QueryLogStore> logger)
        {
            ArgumentNullException.ThrowIfNull(applicationPaths);

            _logger = logger;
            _directory = Path.Combine(applicationPaths.DataPath, "concierge", "queries");
        }

        /// <inheritdoc />
        public async Task RecordAsync(QueryRunRecord run, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(run);

            await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                Directory.CreateDirectory(_directory);
                MigrateLegacyLog();

                var line = JsonSerializer.Serialize(run, SerializerOptions) + "\n";
                await File.AppendAllTextAsync(PathFor(run.StartedUtc), line, Encoding.UTF8, cancellationToken)
                    .ConfigureAwait(false);

                Prune();

                _logger.LogInformation(
                    "Concierge query {Id}: {Route}, {Results} result(s) in {Duration}ms, {Calls} model call(s), "
                    + "${Cost:F5}{Cached}",
                    run.Id,
                    run.Route,
                    run.ResultCount,
                    run.DurationMs,
                    run.Calls?.Count ?? 0,
                    run.TotalCostUsd,
                    run.Cached ? " (cached)" : string.Empty);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // A search that worked must not be reported as broken because its log
                // line could not be written.
                _logger.LogWarning(ex, "Concierge: could not write the query log");
            }
            finally
            {
                _gate.Release();
            }
        }

        /// <inheritdoc />
        public async Task<IReadOnlyList<QueryRunRecord>> RecentAsync(int count, CancellationToken cancellationToken)
        {
            var wanted = count <= 0 ? int.MaxValue : count;
            var records = new List<QueryRunRecord>();

            // Newest month first, stopping as soon as enough have been read — the
            // common case reads part of one file rather than two years of them.
            foreach (var file in MonthFiles().OrderByDescending(f => f.Name, StringComparer.Ordinal))
            {
                cancellationToken.ThrowIfCancellationRequested();

                var month = await ReadAsync(file.FullName, cancellationToken).ConfigureAwait(false);
                month.Reverse();
                records.AddRange(month);

                if (records.Count >= wanted)
                {
                    break;
                }
            }

            return records.Take(wanted).ToList();
        }

        /// <inheritdoc />
        public async Task<IReadOnlyList<QueryRunRecord>> SinceAsync(
            DateTime fromUtc,
            CancellationToken cancellationToken)
        {
            var records = new List<QueryRunRecord>();
            var earliest = fromUtc.ToString("yyyy-MM", CultureInfo.InvariantCulture);

            foreach (var file in MonthFiles())
            {
                cancellationToken.ThrowIfCancellationRequested();

                // The file name carries the month, so whole files outside the range
                // are skipped without being opened at all.
                var month = MonthOf(file.Name);
                if (month is null || string.CompareOrdinal(month, earliest) < 0)
                {
                    continue;
                }

                foreach (var record in await ReadAsync(file.FullName, cancellationToken).ConfigureAwait(false))
                {
                    if (record.StartedUtc >= fromUtc)
                    {
                        records.Add(record);
                    }
                }
            }

            return records;
        }

        /// <summary>
        /// Folds the old capped <c>runs.json</c> into the monthly files, once.
        /// </summary>
        /// <remarks>
        /// Whatever survived the 200-entry window is still real usage, and throwing it
        /// away on upgrade would put a hole in the first month of every breakdown. The
        /// old file is renamed rather than deleted, so the import can be checked and
        /// cannot run twice.
        /// </remarks>
        private void MigrateLegacyLog()
        {
            if (_migrated)
            {
                return;
            }

            _migrated = true;

            var legacy = Path.Combine(Path.GetDirectoryName(_directory)!, "runs.json");
            if (!File.Exists(legacy))
            {
                return;
            }

            try
            {
                var records = JsonSerializer.Deserialize<List<QueryRunRecord>>(
                    File.ReadAllText(legacy), SerializerOptions) ?? [];

                foreach (var month in records.GroupBy(r => PathFor(r.StartedUtc)))
                {
                    File.AppendAllLines(
                        month.Key,
                        month.OrderBy(r => r.StartedUtc)
                            .Select(r => JsonSerializer.Serialize(r, SerializerOptions)),
                        Encoding.UTF8);
                }

                File.Move(legacy, legacy + ".migrated", overwrite: true);

                _logger.LogInformation(
                    "Concierge: imported {Count} search(es) from the old capped log into the monthly files",
                    records.Count);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Concierge: could not import the old query log; starting fresh");
            }
        }

        private string PathFor(DateTime utc)
            => Path.Combine(
                _directory,
                "queries-" + utc.ToString("yyyy-MM", CultureInfo.InvariantCulture) + ".jsonl");

        private static string? MonthOf(string fileName)
        {
            // queries-2026-08.jsonl
            var name = Path.GetFileNameWithoutExtension(fileName);
            return name.Length == "queries-yyyy-MM".Length && name.StartsWith("queries-", StringComparison.Ordinal)
                ? name["queries-".Length..]
                : null;
        }

        private IReadOnlyList<FileInfo> MonthFiles()
        {
            if (!Directory.Exists(_directory))
            {
                return [];
            }

            try
            {
                return new DirectoryInfo(_directory)
                    .GetFiles("queries-*.jsonl")
                    .OrderBy(f => f.Name, StringComparer.Ordinal)
                    .ToList();
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Concierge: could not list the query log");
                return [];
            }
        }

        /// <summary>
        /// Reads one month, skipping any line that will not parse.
        /// </summary>
        /// <remarks>
        /// Skipping rather than failing is the whole reason for the format. The only
        /// realistic corruption is a half-written final line from a process that was
        /// killed, and losing that one search is nothing.
        /// </remarks>
        private async Task<List<QueryRunRecord>> ReadAsync(string path, CancellationToken cancellationToken)
        {
            var records = new List<QueryRunRecord>();
            var malformed = 0;

            try
            {
                foreach (var line in await File.ReadAllLinesAsync(path, cancellationToken).ConfigureAwait(false))
                {
                    if (string.IsNullOrWhiteSpace(line))
                    {
                        continue;
                    }

                    try
                    {
                        var record = JsonSerializer.Deserialize<QueryRunRecord>(line, SerializerOptions);
                        if (record is not null)
                        {
                            records.Add(record);
                        }
                    }
                    catch (JsonException)
                    {
                        malformed++;
                    }
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning(ex, "Concierge: could not read {File}", path);
            }

            if (malformed > 0)
            {
                _logger.LogDebug("Concierge: skipped {Count} malformed line(s) in {File}", malformed, path);
            }

            return records;
        }

        private void Prune()
        {
            try
            {
                foreach (var file in MonthFiles()
                    .OrderByDescending(f => f.Name, StringComparer.Ordinal)
                    .Skip(MonthsRetained))
                {
                    file.Delete();
                }
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Concierge: could not prune old query logs");
            }
        }
    }
}
