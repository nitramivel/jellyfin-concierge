using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Jellyfin.Plugin.Concierge.Services.Runs;

namespace Jellyfin.Plugin.Concierge.Core.Usage
{
    /// <summary>One line of a breakdown.</summary>
    /// <param name="Key">What it groups by — a month, a model, a user, a pass.</param>
    /// <param name="Queries">Searches in this bucket.</param>
    /// <param name="Calls">Model calls in this bucket.</param>
    /// <param name="InputTokens">Uncached input tokens.</param>
    /// <param name="OutputTokens">Output tokens, thinking included.</param>
    /// <param name="CostUsd">What it cost.</param>
    public sealed record UsageBucket(
        string Key,
        int Queries,
        int Calls,
        long InputTokens,
        long OutputTokens,
        decimal CostUsd);

    /// <summary>Everything, added up.</summary>
    /// <param name="Queries">Searches recorded.</param>
    /// <param name="Paid">Searches that cost something.</param>
    /// <param name="Free">Searches answered without spending — the router's whole point.</param>
    /// <param name="Cached">Searches served from the cache.</param>
    /// <param name="Calls">Model calls made.</param>
    /// <param name="InputTokens">Uncached input tokens.</param>
    /// <param name="OutputTokens">Output tokens, thinking included.</param>
    /// <param name="CacheReadTokens">Input served from a provider's prompt cache. Charged, not free.</param>
    /// <param name="ThinkingTokens">The reasoning share of the output.</param>
    /// <param name="CostUsd">Total, summed from per-call costs.</param>
    /// <param name="MeanMs">Mean wall-clock time.</param>
    /// <param name="P95Ms">95th-percentile wall-clock time — the number a latency budget is judged on.</param>
    public sealed record UsageTotals(
        int Queries,
        int Paid,
        int Free,
        int Cached,
        int Calls,
        long InputTokens,
        long OutputTokens,
        long CacheReadTokens,
        long ThinkingTokens,
        decimal CostUsd,
        int MeanMs,
        int P95Ms)
    {
        /// <summary>Gets the mean cost of a search, counting the free ones.</summary>
        public decimal CostPerQueryUsd => Queries == 0 ? 0m : CostUsd / Queries;

        /// <summary>Gets the mean cost of the searches that actually paid.</summary>
        public decimal CostPerPaidQueryUsd => Paid == 0 ? 0m : CostUsd / Paid;

        /// <summary>
        /// Gets the share of searches answered for nothing.
        /// </summary>
        /// <remarks>
        /// The single most useful number here. The plan's cost model turns on the
        /// router far more than on the model choice, and this is the router's report
        /// card: a high number means most searches never reached a model.
        /// </remarks>
        public double FreeShare => Queries == 0 ? 0 : (double)Free / Queries;
    }

    /// <summary>A usage breakdown over some span of recorded searches.</summary>
    /// <param name="FromUtc">Earliest search included.</param>
    /// <param name="ToUtc">Latest search included.</param>
    /// <param name="Totals">Everything, added up.</param>
    /// <param name="ByMonth">Per calendar month, newest first.</param>
    /// <param name="ByDay">Per day, newest first.</param>
    /// <param name="ByPass">Per pipeline pass — plan, re-rank, embedding, enrichment.</param>
    /// <param name="ByModel">Per model, so switching provider can be judged on its bill.</param>
    /// <param name="ByRoute">Per routing decision.</param>
    /// <param name="ByUser">Per user. See the privacy note on the store.</param>
    public sealed record UsageReport(
        DateTime? FromUtc,
        DateTime? ToUtc,
        UsageTotals Totals,
        IReadOnlyList<UsageBucket> ByMonth,
        IReadOnlyList<UsageBucket> ByDay,
        IReadOnlyList<UsageBucket> ByPass,
        IReadOnlyList<UsageBucket> ByModel,
        IReadOnlyList<UsageBucket> ByRoute,
        IReadOnlyList<UsageBucket> ByUser);

    /// <summary>
    /// Turns recorded searches into a usage breakdown.
    /// </summary>
    /// <remarks>
    /// Pure, so the arithmetic behind "what did this cost me" is testable. Every
    /// total here is <b>summed from per-call costs</b> and never derived by
    /// multiplying aggregate tokens by a rate — hard rule 12, and it matters more in
    /// a report than anywhere else, because a report is where a wrong number gets
    /// believed and acted on.
    /// </remarks>
    public static class UsageRollup
    {
        /// <summary>
        /// Builds a breakdown.
        /// </summary>
        /// <param name="records">The recorded searches, in any order.</param>
        /// <returns>The report.</returns>
        public static UsageReport Build(IEnumerable<QueryRunRecord> records)
        {
            ArgumentNullException.ThrowIfNull(records);

            var all = records.Where(r => r is not null).ToList();
            if (all.Count == 0)
            {
                return new UsageReport(
                    null, null,
                    new UsageTotals(0, 0, 0, 0, 0, 0, 0, 0, 0, 0m, 0, 0),
                    [], [], [], [], [], []);
            }

            var calls = all.SelectMany(r => (r.Calls ?? []).Select(c => (Run: r, Call: c))).ToList();
            var durations = all.Select(r => r.DurationMs).OrderBy(d => d).ToList();

            var totals = new UsageTotals(
                all.Count,
                all.Count(r => r.TotalCostUsd > 0),
                all.Count(r => r.TotalCostUsd <= 0),
                all.Count(r => r.Cached),
                calls.Count,
                calls.Sum(c => c.Call.InputTokens),
                calls.Sum(c => c.Call.OutputTokens),
                calls.Sum(c => c.Call.CacheReadTokens),
                calls.Sum(c => c.Call.ThinkingTokens),
                all.Sum(r => r.TotalCostUsd),
                (int)durations.Average(),
                Percentile(durations, 0.95));

            return new UsageReport(
                all.Min(r => r.StartedUtc),
                all.Max(r => r.StartedUtc),
                totals,
                GroupRuns(all, r => r.StartedUtc.ToString("yyyy-MM", CultureInfo.InvariantCulture), descending: true),
                GroupRuns(all, r => r.StartedUtc.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), descending: true),
                GroupCalls(calls, c => c.Call.Pass.ToString()),
                GroupCalls(calls, c => c.Call.Model),
                GroupRuns(all, r => string.IsNullOrEmpty(r.Route) ? "Unknown" : r.Route, descending: false),
                GroupRuns(all, r => r.UserId ?? "anonymous", descending: false));
        }

        /// <summary>
        /// Groups whole searches, so <c>Queries</c> counts each search once.
        /// </summary>
        private static IReadOnlyList<UsageBucket> GroupRuns(
            IReadOnlyList<QueryRunRecord> runs,
            Func<QueryRunRecord, string> key,
            bool descending)
        {
            var buckets = runs
                .GroupBy(key)
                .Select(g => new UsageBucket(
                    g.Key,
                    g.Count(),
                    g.Sum(r => (r.Calls ?? []).Count),
                    g.Sum(r => (r.Calls ?? []).Sum(c => c.InputTokens)),
                    g.Sum(r => (r.Calls ?? []).Sum(c => c.OutputTokens)),
                    g.Sum(r => r.TotalCostUsd)));

            // Time buckets read newest first; everything else reads dearest first,
            // because the question being asked of them is "where did the money go".
            return descending
                ? buckets.OrderByDescending(b => b.Key, StringComparer.Ordinal).ToList()
                : buckets.OrderByDescending(b => b.CostUsd).ThenByDescending(b => b.Queries).ToList();
        }

        /// <summary>
        /// Groups individual calls.
        /// </summary>
        /// <remarks>
        /// <c>Queries</c> here counts the distinct searches a bucket touched, not the
        /// calls — a search that makes a plan call and a re-rank call is one search in
        /// each bucket, and summing these columns across buckets would double-count it.
        /// That is a real trap in a report, so the distinction is deliberate.
        /// </remarks>
        private static IReadOnlyList<UsageBucket> GroupCalls(
            IReadOnlyList<(QueryRunRecord Run, QueryCallRecord Call)> calls,
            Func<(QueryRunRecord Run, QueryCallRecord Call), string> key)
        {
            return calls
                .GroupBy(key)
                .Select(g => new UsageBucket(
                    string.IsNullOrEmpty(g.Key) ? "unknown" : g.Key,
                    g.Select(c => c.Run.Id).Distinct(StringComparer.Ordinal).Count(),
                    g.Count(),
                    g.Sum(c => c.Call.InputTokens),
                    g.Sum(c => c.Call.OutputTokens),
                    g.Sum(c => c.Call.EstimatedCostUsd)))
                .OrderByDescending(b => b.CostUsd)
                .ThenByDescending(b => b.Calls)
                .ToList();
        }

        /// <summary>
        /// Nearest-rank percentile over a sorted list.
        /// </summary>
        /// <remarks>
        /// p95 rather than a mean, because latency is judged on its tail. A mean of
        /// 400ms hides a tenth of searches taking twelve seconds, and it is those the
        /// searcher remembers.
        /// </remarks>
        private static int Percentile(IReadOnlyList<int> sorted, double fraction)
        {
            if (sorted.Count == 0)
            {
                return 0;
            }

            var index = (int)Math.Ceiling(fraction * sorted.Count) - 1;
            return sorted[Math.Clamp(index, 0, sorted.Count - 1)];
        }
    }
}
