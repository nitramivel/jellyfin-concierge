using System;
using System.Collections.Generic;
using System.Linq;
using Jellyfin.Plugin.Concierge.Core.Usage;
using Jellyfin.Plugin.Concierge.Services.Runs;
using Xunit;

namespace Jellyfin.Plugin.Concierge.Tests
{
    /// <summary>
    /// The arithmetic behind "what has this cost me".
    /// </summary>
    /// <remarks>
    /// Pinned because a report is where a wrong number gets believed and acted on.
    /// Every total is summed call by call — hard rule 12 — and never derived by
    /// multiplying aggregate tokens by a single rate.
    /// </remarks>
    public class UsageRollupTests
    {
        private static readonly DateTime August = new(2026, 8, 10, 12, 0, 0, DateTimeKind.Utc);

        private static QueryCallRecord Call(
            QueryPass pass, string model, long input, long output, decimal cost)
            => new(pass, "OpenAi", model, input, output, 0, 0, 0, cost, 100, false);

        private static QueryRunRecord Run(
            string id,
            DateTime at,
            string route = "Concierge",
            string? user = "levi",
            bool cached = false,
            int durationMs = 300,
            params QueryCallRecord[] calls)
            => new(id, at, "q", user, route, calls, 10, durationMs, null, null, null, cached);

        [Fact]
        public void AnEmptyLogProducesAnEmptyReport()
        {
            var report = UsageRollup.Build([]);

            Assert.Equal(0, report.Totals.Queries);
            Assert.Equal(0m, report.Totals.CostUsd);
            Assert.Null(report.FromUtc);
        }

        [Fact]
        public void TotalsAreSummedCallByCall()
        {
            var runs = new[]
            {
                Run("a", August, calls:
                [
                    Call(QueryPass.Plan, "haiku", 600, 120, 0.0003m),
                    Call(QueryPass.Rerank, "sonnet", 2000, 300, 0.0140m),
                ]),
                Run("b", August, calls: [Call(QueryPass.Embedding, "embed-3", 20, 0, 0.0000004m)]),
            };

            var report = UsageRollup.Build(runs);

            Assert.Equal(2, report.Totals.Queries);
            Assert.Equal(3, report.Totals.Calls);
            Assert.Equal(0.0003m + 0.0140m + 0.0000004m, report.Totals.CostUsd);
            Assert.Equal(2620, report.Totals.InputTokens);
        }

        [Fact]
        public void FreeAndPaidSearchesAreCountedApart()
        {
            // The router's report card, and the most useful number in the whole
            // report: the plan's cost model turns on it more than on model choice.
            var runs = new[]
            {
                Run("a", August, route: "Native"),
                Run("b", August, route: "Native"),
                Run("c", August, calls: [Call(QueryPass.Rerank, "sonnet", 100, 50, 0.01m)]),
            };

            var report = UsageRollup.Build(runs);

            Assert.Equal(2, report.Totals.Free);
            Assert.Equal(1, report.Totals.Paid);
            Assert.Equal(2.0 / 3.0, report.Totals.FreeShare, 3);
        }

        [Fact]
        public void CachedSearchesAreCountedSoTheCacheCanBeCredited()
        {
            var runs = new[] { Run("a", August, cached: true), Run("b", August) };

            Assert.Equal(1, UsageRollup.Build(runs).Totals.Cached);
        }

        [Fact]
        public void CostPerQueryAndCostPerPaidQueryAreDifferentNumbers()
        {
            // Counting free searches into the average makes a plugin look cheaper
            // than a paid search actually is; leaving them out makes the monthly
            // bill look bigger than it is. Both are reported.
            var runs = new[]
            {
                Run("a", August, route: "Native"),
                Run("b", August, calls: [Call(QueryPass.Rerank, "sonnet", 100, 50, 0.02m)]),
            };

            var totals = UsageRollup.Build(runs).Totals;

            Assert.Equal(0.01m, totals.CostPerQueryUsd);
            Assert.Equal(0.02m, totals.CostPerPaidQueryUsd);
        }

        [Fact]
        public void ByModelSplitsATwoModelSearchAcrossItsModels()
        {
            // The whole reason model is per call rather than per query. One search,
            // two models, two lines in the breakdown.
            var runs = new[]
            {
                Run("a", August, calls:
                [
                    Call(QueryPass.Plan, "haiku", 600, 120, 0.001m),
                    Call(QueryPass.Rerank, "sonnet", 2000, 300, 0.014m),
                ]),
            };

            var byModel = UsageRollup.Build(runs).ByModel;

            Assert.Equal(2, byModel.Count);
            Assert.Equal("sonnet", byModel[0].Key);
            Assert.Equal(0.014m, byModel[0].CostUsd);
        }

        [Fact]
        public void ACallBucketCountsDistinctSearchesNotCalls()
        {
            // Summing the Queries column across buckets must not double-count a
            // search that made two calls — that is a real trap in a report.
            var runs = new[]
            {
                Run("a", August, calls:
                [
                    Call(QueryPass.Rerank, "sonnet", 10, 10, 0.001m),
                    Call(QueryPass.Rerank, "sonnet", 10, 10, 0.001m),
                ]),
            };

            var bucket = Assert.Single(UsageRollup.Build(runs).ByModel);

            Assert.Equal(1, bucket.Queries);
            Assert.Equal(2, bucket.Calls);
        }

        [Fact]
        public void MonthsAreGroupedAndOrderedNewestFirst()
        {
            var runs = new[]
            {
                Run("a", new DateTime(2026, 7, 5, 0, 0, 0, DateTimeKind.Utc)),
                Run("b", August),
                Run("c", August),
            };

            var byMonth = UsageRollup.Build(runs).ByMonth;

            Assert.Equal("2026-08", byMonth[0].Key);
            Assert.Equal(2, byMonth[0].Queries);
            Assert.Equal("2026-07", byMonth[1].Key);
        }

        [Fact]
        public void LatencyIsReportedAtThe95thPercentileNotJustTheMean()
        {
            // A mean of 400ms hides a tenth of searches taking twelve seconds, and it
            // is those the searcher remembers.
            //
            // Two slow samples in twenty, not one: nearest-rank p95 over twenty values
            // is the nineteenth of them, so a lone outlier is the hundredth percentile
            // rather than the ninety-fifth. That is correct and worth stating, because
            // it means p95 only moves once slowness is a pattern.
            var runs = Enumerable.Range(0, 20)
                .Select(i => Run($"r{i}", August, durationMs: i >= 18 ? 12000 : 200))
                .ToList();

            var totals = UsageRollup.Build(runs).Totals;

            Assert.Equal(12000, totals.P95Ms);
            Assert.True(totals.MeanMs < 1500, $"mean should stay low, was {totals.MeanMs}ms");
        }

        [Fact]
        public void AnAnonymousSearchIsBucketedRatherThanDropped()
        {
            var report = UsageRollup.Build([Run("a", August, user: null)]);

            Assert.Equal("anonymous", Assert.Single(report.ByUser).Key);
        }

        [Fact]
        public void RunsWithNoCallsDoNotBreakTheGrouping()
        {
            var report = UsageRollup.Build([Run("a", August, route: "Native")]);

            Assert.Empty(report.ByModel);
            Assert.Equal(1, report.Totals.Queries);
            Assert.Equal(0m, report.Totals.CostUsd);
        }
    }
}
