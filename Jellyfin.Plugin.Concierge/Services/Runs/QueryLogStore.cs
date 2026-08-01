using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Common.Configuration;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Concierge.Services.Runs
{
    /// <summary>
    /// Default <see cref="IQueryLogStore"/>: one capped JSON file under the plugin's
    /// data directory.
    /// </summary>
    public sealed class QueryLogStore : IQueryLogStore
    {
        /// <summary>How many queries the log keeps before evicting the oldest.</summary>
        public const int MaxEntries = 200;

        private readonly string _path;
        private readonly ILogger<QueryLogStore> _logger;
        private readonly SemaphoreSlim _gate = new(1, 1);

        private static readonly JsonSerializerOptions SerializerOptions = new()
        {
            WriteIndented = false,
        };

        public QueryLogStore(IApplicationPaths applicationPaths, ILogger<QueryLogStore> logger)
        {
            ArgumentNullException.ThrowIfNull(applicationPaths);

            _logger = logger;
            _path = Path.Combine(applicationPaths.DataPath, "concierge", "runs.json");
        }

        /// <inheritdoc />
        public async Task RecordAsync(QueryRunRecord run, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(run);

            await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                var entries = await ReadAsync(cancellationToken).ConfigureAwait(false);

                var updated = new List<QueryRunRecord>(Math.Min(entries.Count + 1, MaxEntries)) { run };
                for (var i = 0; i < entries.Count && updated.Count < MaxEntries; i++)
                {
                    updated.Add(entries[i]);
                }

                Directory.CreateDirectory(Path.GetDirectoryName(_path)!);

                // Written to a sibling and moved into place, so a crash mid-write
                // leaves the previous log intact rather than a truncated file that
                // fails to parse and loses every recorded query at once.
                var temporary = _path + ".tmp";
                await using (var stream = File.Create(temporary))
                {
                    await JsonSerializer.SerializeAsync(stream, updated, SerializerOptions, cancellationToken)
                        .ConfigureAwait(false);
                }

                File.Move(temporary, _path, overwrite: true);

                _logger.LogInformation(
                    "Concierge query {Id}: {Route}, {Results} result(s) in {Duration}ms, {Calls} model call(s), ${Cost}",
                    run.Id,
                    run.Route,
                    run.ResultCount,
                    run.DurationMs,
                    run.Calls?.Count ?? 0,
                    run.TotalCostUsd);
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
            await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                var entries = await ReadAsync(cancellationToken).ConfigureAwait(false);
                if (count <= 0 || count >= entries.Count)
                {
                    return entries;
                }

                return entries.GetRange(0, count);
            }
            finally
            {
                _gate.Release();
            }
        }

        /// <summary>
        /// Reads the log, treating any failure as an empty log.
        /// </summary>
        /// <remarks>
        /// A corrupt or unreadable log is not worth failing a search over, and it is
        /// self-healing: the next write replaces it wholesale.
        /// </remarks>
        private async Task<List<QueryRunRecord>> ReadAsync(CancellationToken cancellationToken)
        {
            if (!File.Exists(_path))
            {
                return [];
            }

            try
            {
                await using var stream = File.OpenRead(_path);
                var entries = await JsonSerializer
                    .DeserializeAsync<List<QueryRunRecord>>(stream, SerializerOptions, cancellationToken)
                    .ConfigureAwait(false);
                return entries ?? [];
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning(ex, "Concierge: the query log could not be read; starting a new one");
                return [];
            }
        }
    }
}
