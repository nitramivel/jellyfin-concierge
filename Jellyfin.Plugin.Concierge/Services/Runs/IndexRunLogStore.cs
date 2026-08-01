using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using Jellyfin.Plugin.Concierge.Services.Llm;
using MediaBrowser.Common.Configuration;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Concierge.Services.Runs
{
    /// <summary>
    /// Default <see cref="IIndexRunLogStore"/>: one JSON file per run under
    /// <c>data/concierge/runs</c>.
    /// </summary>
    /// <remarks>
    /// A file per run, unlike the query log's single capped list, because index
    /// builds are rare and each is worth keeping whole. A build that spent money and
    /// went wrong is the thing you most want a full record of.
    /// </remarks>
    public sealed class IndexRunLogStore : IIndexRunLogStore
    {
        /// <summary>How many run files are kept before the oldest are removed.</summary>
        public const int MaxRuns = 25;

        private static readonly JsonSerializerOptions SerializerOptions = new() { WriteIndented = true };

        private readonly string _directory;
        private readonly ILogger<IndexRunLogStore> _logger;
        private readonly object _gate = new();

        private RunLog? _current;

        public IndexRunLogStore(IApplicationPaths applicationPaths, ILogger<IndexRunLogStore> logger)
        {
            ArgumentNullException.ThrowIfNull(applicationPaths);

            _logger = logger;
            _directory = Path.Combine(applicationPaths.DataPath, "concierge", "runs");
        }

        /// <inheritdoc />
        public IIndexRunLog Begin(string trigger, IReadOnlyDictionary<string, object?> settings)
        {
            var log = new RunLog(this, trigger, settings);
            lock (_gate)
            {
                _current = log;
            }

            return log;
        }

        /// <inheritdoc />
        public IndexRunSummary? Current()
        {
            lock (_gate)
            {
                return _current is { Finished: false } running ? running.Summarize() : null;
            }
        }

        /// <inheritdoc />
        public IReadOnlyList<IndexRunSummary> List(int limit = 25)
        {
            var summaries = new List<IndexRunSummary>();

            // The in-flight run first, since it is not on disk in final form yet.
            var live = Current();
            if (live is not null)
            {
                summaries.Add(live);
            }

            try
            {
                if (Directory.Exists(_directory))
                {
                    var files = new DirectoryInfo(_directory)
                        .GetFiles("run_*.json")
                        .OrderByDescending(f => f.LastWriteTimeUtc)
                        .Take(Math.Max(1, limit));

                    foreach (var file in files)
                    {
                        var document = Read(file.FullName);
                        if (document is null || (live is not null && document.RunId == live.RunId))
                        {
                            continue;
                        }

                        summaries.Add(Summarize(document));
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Concierge: could not list index runs");
            }

            return summaries.Take(Math.Max(1, limit)).ToList();
        }

        /// <inheritdoc />
        public string? ReadRaw(Guid runId)
        {
            var path = PathFor(runId);
            try
            {
                return File.Exists(path) ? File.ReadAllText(path) : null;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Concierge: could not read index run {RunId}", runId);
                return null;
            }
        }

        private string PathFor(Guid runId)
            => Path.Combine(_directory, "run_" + runId.ToString("N", CultureInfo.InvariantCulture) + ".json");

        private IndexRunDocument? Read(string path)
        {
            try
            {
                return JsonSerializer.Deserialize<IndexRunDocument>(File.ReadAllText(path), SerializerOptions);
            }
            catch
            {
                return null;
            }
        }

        private static IndexRunSummary Summarize(IndexRunDocument d) => new(
            d.RunId,
            d.Trigger,
            d.StartedUtc,
            d.FinishedUtc,
            d.Status,
            d.Percent,
            d.Phase,
            d.ItemsIndexed,
            d.ItemsEnriched,
            d.RowsEmbedded,
            d.RowsReused,
            d.Totals.TotalCostUsd,
            d.Error);

        /// <summary>
        /// Writes a run document, and prunes the oldest once there are too many.
        /// </summary>
        /// <remarks>
        /// Swallows everything. This is called from inside a paid pass, and a
        /// permission problem on the log directory must not be able to end a run that
        /// has already spent money.
        /// </remarks>
        private void Flush(IndexRunDocument document)
        {
            try
            {
                Directory.CreateDirectory(_directory);

                var path = PathFor(document.RunId);
                var temporary = path + ".tmp";
                File.WriteAllText(temporary, JsonSerializer.Serialize(document, SerializerOptions));
                File.Move(temporary, path, overwrite: true);

                Prune();
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Concierge: could not write the index run log");
            }
        }

        private void Prune()
        {
            try
            {
                var files = new DirectoryInfo(_directory)
                    .GetFiles("run_*.json")
                    .OrderByDescending(f => f.LastWriteTimeUtc)
                    .Skip(MaxRuns);

                foreach (var file in files)
                {
                    file.Delete();
                }
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Concierge: could not prune old index runs");
            }
        }

        /// <summary>
        /// The recorder itself. Holds the document in memory and flushes it
        /// periodically so a crash mid-run still leaves a usable record.
        /// </summary>
        private sealed class RunLog : IIndexRunLog
        {
            /// <summary>Flush cadence. Frequent enough to survive a crash, rare enough not to thrash the disk.</summary>
            private const int FlushEveryCalls = 5;

            private const int PromptPreviewChars = 2000;
            private const int ResponsePreviewChars = 8000;

            private readonly IndexRunLogStore _store;
            private readonly IndexRunDocument _document;
            private readonly object _lock = new();

            private int _sinceFlush;

            public RunLog(IndexRunLogStore store, string trigger, IReadOnlyDictionary<string, object?> settings)
            {
                _store = store;
                _document = new IndexRunDocument
                {
                    RunId = Guid.NewGuid(),
                    Trigger = trigger,
                    StartedUtc = DateTime.UtcNow,
                    Settings = new Dictionary<string, object?>(settings),
                };
            }

            public Guid RunId => _document.RunId;

            public bool Finished { get; private set; }

            public IndexRunSummary Summarize()
            {
                lock (_lock)
                {
                    _document.Totals = ComputeTotals();
                    return IndexRunLogStore.Summarize(_document);
                }
            }

            public void Step(string step, string message, IReadOnlyDictionary<string, object?>? detail = null)
            {
                try
                {
                    lock (_lock)
                    {
                        _document.Steps.Add(new RunStepRecord(DateTime.UtcNow, step, message, detail));
                        _document.Phase = step;

                        // Counts that a list row shows are lifted out of step detail as
                        // they arrive, so a summary needs no knowledge of step names.
                        if (detail is not null)
                        {
                            Lift(detail, "items", v => _document.ItemsIndexed = v);
                            Lift(detail, "enriched", v => _document.ItemsEnriched = v);
                            Lift(detail, "embedded", v => _document.RowsEmbedded = v);
                            Lift(detail, "reused", v => _document.RowsReused = v);
                        }
                    }

                    FlushIfDue(force: false);
                }
                catch
                {
                    // Diagnostics must never take down the pass they describe.
                }
            }

            public void Progress(double percent)
            {
                try
                {
                    lock (_lock)
                    {
                        _document.Percent = Math.Clamp(percent, 0, 100);
                    }
                }
                catch
                {
                }
            }

            public void LlmCall(
                string pass,
                int batch,
                int itemCount,
                TimeSpan duration,
                LlmRequest request,
                LlmResult? result,
                string outcome,
                string? error,
                string model,
                string provider,
                RunPricing pricing)
            {
                try
                {
                    var prompt = request is null ? string.Empty : request.SystemPrompt + "\n\n" + request.UserPrompt;
                    var response = result?.Text ?? string.Empty;

                    var cost = result is null
                        ? 0m
                        : ((result.InputTokens * pricing.InputCostPerMillion)
                            + (result.OutputTokens * pricing.OutputCostPerMillion)
                            + (result.CacheReadTokens * pricing.CachedInputCostPerMillion)
                            + (result.CacheWriteTokens * pricing.InputCostPerMillion * Core.Llm.CallCost.CacheWritePremium))
                           / 1_000_000m;

                    lock (_lock)
                    {
                        _document.Calls.Add(new RunCallRecord(
                            DateTime.UtcNow - duration,
                            pass,
                            batch,
                            itemCount,
                            provider,
                            model,
                            (int)duration.TotalMilliseconds,
                            result?.InputTokens ?? 0,
                            result?.OutputTokens ?? 0,
                            result?.CacheReadTokens ?? 0,
                            result?.CacheWriteTokens ?? 0,
                            result?.ThinkingTokens ?? 0,
                            cost,
                            outcome,
                            error,
                            prompt.Length,
                            response.Length,
                            Truncate(prompt, PromptPreviewChars),
                            Truncate(response, ResponsePreviewChars)));
                    }

                    FlushIfDue(force: false);
                }
                catch
                {
                }
            }

            public void EmbeddingCall(
                int batch,
                int rowCount,
                TimeSpan duration,
                long inputTokens,
                decimal cost,
                string model,
                string provider,
                string? error = null)
            {
                try
                {
                    lock (_lock)
                    {
                        _document.Embeddings.Add(new RunEmbeddingRecord(
                            DateTime.UtcNow - duration,
                            batch,
                            rowCount,
                            provider,
                            model,
                            (int)duration.TotalMilliseconds,
                            inputTokens,
                            cost,
                            error));
                    }

                    FlushIfDue(force: false);
                }
                catch
                {
                }
            }

            public void ItemNotEnriched(string title, string reason)
            {
                try
                {
                    lock (_lock)
                    {
                        _document.NotEnriched.Add(new NotEnrichedRecord(title, reason));
                    }
                }
                catch
                {
                }
            }

            public void Complete() => Finish("completed", null);

            public void Cancel() => Finish("cancelled", null);

            public void Fail(string error) => Finish("failed", error);

            private void Finish(string status, string? error)
            {
                try
                {
                    lock (_lock)
                    {
                        _document.Status = status;
                        _document.Error = error;
                        _document.FinishedUtc = DateTime.UtcNow;
                        Finished = true;
                    }

                    FlushIfDue(force: true);
                }
                catch
                {
                }
            }

            private void FlushIfDue(bool force)
            {
                IndexRunDocument snapshot;
                lock (_lock)
                {
                    if (!force && ++_sinceFlush < FlushEveryCalls)
                    {
                        return;
                    }

                    _sinceFlush = 0;
                    _document.Totals = ComputeTotals();
                    snapshot = _document;
                }

                _store.Flush(snapshot);
            }

            /// <summary>
            /// Sums the totals from the per-call records.
            /// </summary>
            /// <remarks>
            /// Hard rule 12: added up call by call, never derived by multiplying
            /// aggregate token counts by one rate. A run that enriches on one model
            /// and embeds on another has two prices in it.
            /// </remarks>
            private RunTotals ComputeTotals()
            {
                var calls = _document.Calls;
                var embeddings = _document.Embeddings;

                return new RunTotals(
                    calls.Count,
                    calls.Count(c => c.Outcome != "ok"),
                    calls.Sum(c => c.InputTokens),
                    calls.Sum(c => c.OutputTokens),
                    calls.Sum(c => c.CacheReadTokens),
                    calls.Sum(c => c.CacheWriteTokens),
                    calls.Sum(c => c.ThinkingTokens),
                    calls.Sum(c => c.CostUsd),
                    embeddings.Count,
                    embeddings.Sum(e => e.InputTokens),
                    embeddings.Sum(e => e.CostUsd),
                    calls.Sum(c => c.CostUsd) + embeddings.Sum(e => e.CostUsd));
            }

            private static void Lift(
                IReadOnlyDictionary<string, object?> detail,
                string key,
                Action<int> set)
            {
                if (detail.TryGetValue(key, out var value) && value is int number)
                {
                    set(number);
                }
            }

            private static string Truncate(string text, int max)
                => text.Length <= max ? text : text[..max] + "…";
        }
    }
}
