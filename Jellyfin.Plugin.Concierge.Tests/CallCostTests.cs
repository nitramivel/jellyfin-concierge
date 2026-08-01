using Jellyfin.Plugin.Concierge.Configuration;
using Jellyfin.Plugin.Concierge.Core.Llm;
using Jellyfin.Plugin.Concierge.Services.Runs;
using Xunit;

namespace Jellyfin.Plugin.Concierge.Tests
{
    public class CallCostTests
    {
        private static ModelProfile Priced(
            decimal input = 1m,
            decimal output = 5m,
            decimal cached = 0m)
            => new()
            {
                Id = "p",
                InputCostPerMillion = input,
                OutputCostPerMillion = output,
                CachedInputCostPerMillion = cached,
            };

        [Fact]
        public void ForChat_PricesInputAndOutputAtTheProfilesOwnRates()
        {
            var cost = CallCost.ForChat(Priced(input: 2m, output: 10m), 1_000_000, 100_000);

            Assert.Equal(2m + 1m, cost);
        }

        [Fact]
        public void ForChat_ChargesCacheReads()
        {
            // Hard rule 10. Cache reads are cheap, which is not the same as free — a
            // ledger that treats them as free reports a cached query at zero.
            var cost = CallCost.ForChat(Priced(input: 2m, output: 0m), 0, 0, cacheReadTokens: 1_000_000);

            Assert.Equal(1m, cost);
        }

        [Fact]
        public void ForChat_UnsetCachedRate_IsHalfTheInputRate()
        {
            var explicitRate = CallCost.ForChat(Priced(input: 4m, cached: 2m), 0, 0, cacheReadTokens: 1_000_000);
            var implied = CallCost.ForChat(Priced(input: 4m), 0, 0, cacheReadTokens: 1_000_000);

            Assert.Equal(explicitRate, implied);
        }

        [Fact]
        public void ForChat_ChargesCacheWritesAtAPremium()
        {
            var cost = CallCost.ForChat(Priced(input: 1m, output: 0m), 0, 0, cacheWriteTokens: 1_000_000);

            Assert.Equal(CallCost.CacheWritePremium, cost);
        }

        [Fact]
        public void ForChat_WithNoPricesSet_CostsNothingAndStillCounts()
        {
            // A profile with no prices logs token counts without a cost line rather
            // than refusing to record the call.
            var cost = CallCost.ForChat(new ModelProfile(), 500_000, 100_000);

            Assert.Equal(0m, cost);
        }

        [Fact]
        public void ForEmbedding_PricesOneRateAgainstOneCount()
        {
            var profile = new EmbeddingProfile { InputCostPerMillion = 0.02m };

            Assert.Equal(0.02m, CallCost.ForEmbedding(profile, 1_000_000));
        }

        /// <summary>
        /// Hard rule 12: a query's total is summed from its calls, because no single
        /// rate can price a query that ran two models at two different prices.
        /// </summary>
        [Fact]
        public void QueryTotal_IsSummedFromPerCallCosts_NotFromAggregateTokens()
        {
            var plan = Priced(input: 1m, output: 5m);       // cheap, small
            var rerank = Priced(input: 3m, output: 15m);    // dearer, larger

            var planCost = CallCost.ForChat(plan, 100_000, 1_000);
            var rerankCost = CallCost.ForChat(rerank, 20_000, 2_000);

            var run = new QueryRunRecord(
                "q1",
                System.DateTime.UtcNow,
                "the one where he tattoos the clues on himself",
                UserId: null,
                Route: "concierge",
                Calls:
                [
                    new QueryCallRecord(QueryPass.Plan, "Anthropic", "haiku", 100_000, 1_000, 0, 0, 0, planCost, 320, false),
                    new QueryCallRecord(QueryPass.Rerank, "Anthropic", "sonnet", 20_000, 2_000, 0, 0, 0, rerankCost, 780, false),
                ],
                ResultCount: 5,
                DurationMs: 1_240);

            Assert.Equal(planCost + rerankCost, run.TotalCostUsd);

            // What the rule forbids: one blended rate over summed tokens. It lands on
            // a different number, and that difference is the bug the rule prevents.
            var summedTokensAtOneRate = CallCost.ForChat(plan, 120_000, 3_000);
            Assert.NotEqual(summedTokensAtOneRate, run.TotalCostUsd);
        }

        [Fact]
        public void QueryTotal_WithNoCalls_IsZero()
        {
            // The router sent it straight to native search: free, and recorded.
            var run = new QueryRunRecord(
                "q2",
                System.DateTime.UtcNow,
                "blade",
                UserId: null,
                Route: "native",
                Calls: [],
                ResultCount: 3,
                DurationMs: 12);

            Assert.Equal(0m, run.TotalCostUsd);
        }
    }
}
