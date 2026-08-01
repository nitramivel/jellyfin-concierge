using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Concierge.Core.Budget;
using MediaBrowser.Common.Configuration;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Concierge.Services.Budget
{
    /// <summary>
    /// Records what has been spent, and answers what is left.
    /// </summary>
    public interface ISpendStore
    {
        /// <summary>Records one spend.</summary>
        /// <param name="kind">Query or enrichment.</param>
        /// <param name="amountUsd">What it cost.</param>
        /// <param name="userId">Who caused it, for rate limiting.</param>
        void Record(SpendKind kind, decimal amountUsd, string? userId = null);

        /// <summary>Query spend so far this calendar month.</summary>
        /// <returns>The total in USD.</returns>
        decimal QuerySpendThisMonth();

        /// <summary>Enrichment spend so far this calendar month.</summary>
        /// <returns>The total in USD.</returns>
        decimal EnrichmentSpendThisMonth();

        /// <summary>How many paid queries this user has made in the last hour.</summary>
        /// <param name="userId">The user, or null.</param>
        /// <returns>The count.</returns>
        int PaidQueriesInLastHour(string? userId);
    }

    /// <summary>
    /// Default <see cref="ISpendStore"/>: one small JSON file, held in memory and
    /// flushed lazily.
    /// </summary>
    /// <remarks>
    /// <b>Persisted, because a cap that resets on restart is not a cap.</b> Held in
    /// memory because it is read before every paid query and reading a file there
    /// would put disk work inside the latency budget.
    /// <para>
    /// Writes are debounced rather than immediate: losing the last few seconds of
    /// entries to a hard kill costs a fraction of a cent, and writing a file on every
    /// search costs far more than that in wear and latency.
    /// </para>
    /// </remarks>
    public sealed class SpendStore : ISpendStore
    {
        /// <summary>How long a change may sit in memory before it is written.</summary>
        private static readonly TimeSpan FlushAfter = TimeSpan.FromSeconds(20);

        private static readonly JsonSerializerOptions SerializerOptions = new() { WriteIndented = false };

        private readonly string _path;
        private readonly ILogger<SpendStore> _logger;
        private readonly object _gate = new();

        private List<SpendEntry> _entries = [];
        private DateTime _lastFlush = DateTime.MinValue;
        private bool _dirty;
        private bool _loaded;

        public SpendStore(IApplicationPaths applicationPaths, ILogger<SpendStore> logger)
        {
            ArgumentNullException.ThrowIfNull(applicationPaths);

            _logger = logger;
            _path = Path.Combine(applicationPaths.DataPath, "concierge", "spend.json");
        }

        /// <inheritdoc />
        public void Record(SpendKind kind, decimal amountUsd, string? userId = null)
        {
            if (amountUsd <= 0 && kind == SpendKind.Query)
            {
                // A free query still counts against the rate limit only if it paid for
                // something. A cache hit or a native route must not.
                return;
            }

            lock (_gate)
            {
                EnsureLoaded();
                _entries.Add(new SpendEntry(DateTime.UtcNow, kind, amountUsd, userId));
                _dirty = true;
                FlushIfDue();
            }
        }

        /// <inheritdoc />
        public decimal QuerySpendThisMonth() => SpentThisMonth(SpendKind.Query);

        /// <inheritdoc />
        public decimal EnrichmentSpendThisMonth() => SpentThisMonth(SpendKind.Enrichment);

        /// <inheritdoc />
        public int PaidQueriesInLastHour(string? userId)
        {
            lock (_gate)
            {
                EnsureLoaded();
                return SpendLedger.PaidQueriesInLastHour(_entries, userId, DateTime.UtcNow);
            }
        }

        private decimal SpentThisMonth(SpendKind kind)
        {
            lock (_gate)
            {
                EnsureLoaded();
                return SpendLedger.SpentThisMonth(_entries, kind, DateTime.UtcNow);
            }
        }

        private void EnsureLoaded()
        {
            if (_loaded)
            {
                return;
            }

            _loaded = true;

            try
            {
                if (File.Exists(_path))
                {
                    _entries = JsonSerializer.Deserialize<List<SpendEntry>>(
                        File.ReadAllText(_path), SerializerOptions) ?? [];
                    _entries = [.. SpendLedger.Prune(_entries, DateTime.UtcNow)];
                }
            }
            catch (Exception ex)
            {
                // An unreadable ledger reads as "nothing spent", which errs toward
                // letting searches through rather than locking the plugin out. The
                // alternative — assuming the cap is blown — would break search over a
                // corrupt file.
                _logger.LogWarning(ex, "Concierge: the spend ledger could not be read; starting a new one");
                _entries = [];
            }
        }

        private void FlushIfDue()
        {
            if (!_dirty || DateTime.UtcNow - _lastFlush < FlushAfter)
            {
                return;
            }

            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(_path)!);

                _entries = [.. SpendLedger.Prune(_entries, DateTime.UtcNow)];

                var temporary = _path + ".tmp";
                File.WriteAllText(temporary, JsonSerializer.Serialize(_entries, SerializerOptions));
                File.Move(temporary, _path, overwrite: true);

                _dirty = false;
                _lastFlush = DateTime.UtcNow;
            }
            catch (Exception ex)
            {
                // Never fatal. A search that worked must not fail because its
                // bookkeeping could not be written.
                _logger.LogWarning(ex, "Concierge: the spend ledger could not be written");
            }
        }
    }
}
