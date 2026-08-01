using System;
using System.Collections.Generic;
using System.Linq;
using Jellyfin.Plugin.Concierge.Core.Budget;
using Jellyfin.Plugin.Concierge.Core.Documents;
using Jellyfin.Plugin.Concierge.Core.Query;
using Jellyfin.Plugin.Concierge.Core.Ranking;
using Jellyfin.Plugin.Concierge.Core.Retrieval;
using Xunit;

namespace Jellyfin.Plugin.Concierge.Tests
{
    public class PlanParserTests
    {
        [Fact]
        public void AWellFormedPlan_IsRead()
        {
            var json = """
                {"semantic":"science fiction","filters":{"types":["Movie"],"yearFrom":1990,
                "yearTo":1999,"genres":["Sci-Fi"],"people":[],"runtimeMaxMinutes":120,
                "watchState":"unwatched"},"quote":null}
                """;

            var plan = PlanParser.Parse(json, "90s sci-fi under two hours I haven't seen");

            Assert.Equal("science fiction", plan.Semantic);
            Assert.Equal(1990, plan.Filters.YearFrom);
            Assert.Equal(1999, plan.Filters.YearTo);
            Assert.Equal(120, plan.Filters.RuntimeMaxMinutes);
            Assert.Equal(WatchState.Unwatched, plan.Filters.WatchState);
            Assert.Null(plan.Quote);
        }

        [Theory]
        [InlineData("not json at all")]
        [InlineData("")]
        [InlineData(null)]
        [InlineData("{\"semantic\":")]
        public void AnUnreadableAnswer_FallsBackToTheRawQuery(string? response)
        {
            // A malformed plan must cost the call and nothing else. Retrieval can work
            // perfectly well from what the searcher actually typed.
            var plan = PlanParser.Parse(response, "the one with the dog");

            Assert.Equal("the one with the dog", plan.Semantic);
            Assert.True(plan.Filters.IsEmpty);
        }

        [Fact]
        public void AMissingSemanticField_FallsBackWithoutLosingTheFilters()
        {
            var plan = PlanParser.Parse(
                """{"filters":{"yearFrom":1990,"yearTo":1999}}""", "90s films");

            Assert.Equal("90s films", plan.Semantic);
            Assert.Equal(1990, plan.Filters.YearFrom);
        }

        [Theory]
        [InlineData(1066)]
        [InlineData(9999)]
        [InlineData(0)]
        public void AYearNoLibraryCouldHold_IsDiscarded(int year)
        {
            // A hallucinated year would empty the results for a reason the searcher
            // could never work out.
            var plan = PlanParser.Parse(
                "{\"semantic\":\"x\",\"filters\":{\"yearFrom\":" + year + "}}", "x");

            Assert.Null(plan.Filters.YearFrom);
        }

        [Fact]
        public void SwappedYearBounds_ArePutBackInOrder()
        {
            var plan = PlanParser.Parse(
                """{"semantic":"x","filters":{"yearFrom":1999,"yearTo":1990}}""", "x");

            Assert.Equal(1990, plan.Filters.YearFrom);
            Assert.Equal(1999, plan.Filters.YearTo);
        }

        [Fact]
        public void NumbersWrittenAsStrings_AreAccepted()
        {
            var plan = PlanParser.Parse(
                """{"semantic":"x","filters":{"runtimeMaxMinutes":"90"}}""", "x");

            Assert.Equal(90, plan.Filters.RuntimeMaxMinutes);
        }

        [Theory]
        [InlineData("unwatched", WatchState.Unwatched)]
        [InlineData("UNSEEN", WatchState.Unwatched)]
        [InlineData("favourite", WatchState.Favorite)]
        [InlineData("favorite", WatchState.Favorite)]
        [InlineData("nonsense", WatchState.Any)]
        [InlineData("", WatchState.Any)]
        public void WatchState_AcceptsBothSpellingsAndDefaultsToAny(string value, WatchState expected)
        {
            var plan = PlanParser.Parse(
                "{\"semantic\":\"x\",\"filters\":{\"watchState\":\"" + value + "\"}}", "x");

            Assert.Equal(expected, plan.Filters.WatchState);
        }
    }

    public class FilterApplicationTests
    {
        private static readonly IReadOnlyList<ItemDocument> Library = FixtureLibrary.All;

        private static Dictionary<Guid, ItemDocument> ById() => Library.ToDictionary(d => d.ItemId);

        private static List<FusedResult> AllCandidates() =>
            Library.Select((d, i) => new FusedResult(d.ItemId, 1.0 / (i + 1), i + 1, null)).ToList();

        [Fact]
        public void NoFilters_ChangesNothing()
        {
            var candidates = AllCandidates();
            var outcome = FilterApplication.Apply(candidates, ById(), SearchFilters.None);

            Assert.Equal(candidates.Count, outcome.Results.Count);
            Assert.False(outcome.HardCut);
        }

        [Fact]
        public void AFilterLeavingPlenty_IsAppliedAsACut()
        {
            // The fixture library is all movies, so a Movie filter keeps everything.
            var filters = SearchFilters.None with { Types = ["Movie"] };

            var outcome = FilterApplication.Apply(AllCandidates(), ById(), filters, null, minimumSurvivors: 5);

            Assert.True(outcome.HardCut);
            Assert.Equal(Library.Count, outcome.Results.Count);
        }

        [Fact]
        public void AFilterLeavingTooFew_IsDemotedRatherThanApplied()
        {
            // Hard rule 8. Only three fixtures are from 1995-1996, so a cut here would
            // throw away everything else on the strength of one small model's guess.
            var filters = SearchFilters.None with { YearFrom = 1995, YearTo = 1996 };

            var outcome = FilterApplication.Apply(AllCandidates(), ById(), filters);

            Assert.False(outcome.HardCut);
            Assert.Equal(Library.Count, outcome.Results.Count);
            Assert.Contains("demoted", outcome.Explanation, StringComparison.Ordinal);
        }

        [Fact]
        public void ADemotedFilter_StillPutsTheMatchesFirst()
        {
            var filters = SearchFilters.None with { YearFrom = 1995, YearTo = 1996 };

            var outcome = FilterApplication.Apply(AllCandidates(), ById(), filters);
            var byId = ById();

            var years = outcome.Results
                .Take(outcome.Matched)
                .Select(r => byId[r.ItemId].Year!.Value);

            Assert.All(years, y => Assert.InRange(y, 1995, 1996));
        }

        [Fact]
        public void AFilterMatchingNothing_NeverEmptiesTheResults()
        {
            // The failure that makes people stop using a search box.
            var filters = SearchFilters.None with { YearFrom = 2100, YearTo = 2200 };

            var outcome = FilterApplication.Apply(AllCandidates(), ById(), filters);

            Assert.Equal(Library.Count, outcome.Results.Count);
            Assert.Equal(0, outcome.Matched);
        }

        [Fact]
        public void AnItemWithNoYear_SurvivesAYearFilter()
        {
            // Incomplete metadata is not an answer to the question that was asked.
            var undated = Library[0] with { Year = null, ItemId = Guid.NewGuid() };
            var docs = new Dictionary<Guid, ItemDocument> { [undated.ItemId] = undated };
            var candidates = new List<FusedResult> { new(undated.ItemId, 1.0, 1, null) };

            var outcome = FilterApplication.Apply(
                candidates, docs, SearchFilters.None with { YearFrom = 1990, YearTo = 1999 },
                null, minimumSurvivors: 1);

            Assert.True(outcome.HardCut);
            Assert.Single(outcome.Results);
        }

        [Fact]
        public void WatchState_IsIgnoredWhenItCannotBeEvaluated()
        {
            // Core has no per-user data. Pretending otherwise would silently filter on
            // a guess.
            var filters = SearchFilters.None with { WatchState = WatchState.Unwatched };

            var outcome = FilterApplication.Apply(
                AllCandidates(), ById(), filters, null, minimumSurvivors: 5);

            Assert.Equal(Library.Count, outcome.Matched);
        }
    }

    public class RerankParserTests
    {
        [Fact]
        public void AFullOrdering_IsUsedAsGiven()
        {
            var outcome = RerankParser.Parse(
                """{"order":[{"i":2,"why":"amnesia, tattoos"},{"i":0,"why":"a"},{"i":1,"why":"b"}]}""", 3);

            Assert.Equal([2, 0, 1], outcome.Order.Select(o => o.Index));
            Assert.Equal("amnesia, tattoos", outcome.Order[0].Why);
            Assert.Equal(3, outcome.Ranked);
            Assert.Equal(0, outcome.Omitted);
        }

        [Fact]
        public void AnOmittedItem_KeepsItsPlaceInsteadOfDisappearing()
        {
            // Hard rule 7. The model ordered two of five; the other three are still
            // results somebody searched for.
            var outcome = RerankParser.Parse("""{"order":[{"i":3},{"i":1}]}""", 5);

            Assert.Equal(5, outcome.Order.Count);
            Assert.Equal([3, 1, 0, 2, 4], outcome.Order.Select(o => o.Index));
            Assert.Equal(3, outcome.Omitted);
        }

        [Fact]
        public void AnInventedIndex_IsDiscardedAndCounted()
        {
            var outcome = RerankParser.Parse("""{"order":[{"i":99},{"i":0}]}""", 2);

            Assert.Equal(1, outcome.Invented);
            Assert.DoesNotContain(outcome.Order, o => o.Index == 99);
            Assert.Equal(2, outcome.Order.Count);
        }

        [Fact]
        public void ARepeatedIndex_IsIgnoredTheSecondTime()
        {
            var outcome = RerankParser.Parse("""{"order":[{"i":1},{"i":1},{"i":0}]}""", 2);

            Assert.Equal([1, 0], outcome.Order.Select(o => o.Index));
        }

        [Theory]
        [InlineData("nonsense")]
        [InlineData("")]
        [InlineData(null)]
        [InlineData("{\"order\":\"not an array\"}")]
        public void AnUnreadableAnswer_LeavesTheFusedOrderIntact(string? response)
        {
            var outcome = RerankParser.Parse(response, 4);

            Assert.Equal([0, 1, 2, 3], outcome.Order.Select(o => o.Index));
            Assert.Equal(4, outcome.Omitted);
        }

        [Fact]
        public void ABareNumberList_IsAccepted()
        {
            // A model told to return an ordering sometimes just returns the ordering.
            var outcome = RerankParser.Parse("""{"order":[2,0,1]}""", 3);

            Assert.Equal([2, 0, 1], outcome.Order.Select(o => o.Index));
        }

        [Fact]
        public void AnOverlongExplanation_IsTrimmed()
        {
            var why = new string('x', 400);
            var outcome = RerankParser.Parse($$"""{"order":[{"i":0,"why":"{{why}}"}]}""", 1);

            Assert.True(outcome.Order[0].Why.Length <= RerankParser.MaxWhyLength + 1);
        }

        [Fact]
        public void AnEmptyShortlist_ReturnsNothing()
        {
            Assert.Empty(RerankParser.Parse("""{"order":[{"i":0}]}""", 0).Order);
        }
    }

    public class BudgetDecisionTests
    {
        private static BudgetOutcome Decide(
            decimal spent = 0,
            decimal cap = 10,
            int thisHour = 0,
            int hourly = 0,
            bool plan = true,
            bool rerank = true)
            => BudgetDecision.ForQuery(spent, cap, thisHour, hourly, plan, rerank);

        [Fact]
        public void WellUnderBudget_RunsEverything()
        {
            Assert.Equal(BudgetVerdict.Full, Decide(spent: 1m).Verdict);
        }

        [Fact]
        public void NearTheCap_DropsTheRerankFirst()
        {
            // The re-rank is roughly five times the plan pass, so dropping it buys
            // most of the remaining month at a fraction of the quality loss.
            var outcome = Decide(spent: 9m, cap: 10m);

            Assert.Equal(BudgetVerdict.PlanOnly, outcome.Verdict);
            Assert.False(outcome.AllowsRerank);
            Assert.True(outcome.AllowsAnySpend);
        }

        [Fact]
        public void AtTheCap_DegradesToFreeRetrievalAndSaysSo()
        {
            var outcome = Decide(spent: 10m, cap: 10m);

            Assert.Equal(BudgetVerdict.FreeOnly, outcome.Verdict);
            Assert.False(outcome.AllowsAnySpend);
            Assert.Contains("still searching", outcome.Reason, StringComparison.Ordinal);
        }

        [Fact]
        public void NoCap_NeverDegrades()
        {
            Assert.Equal(BudgetVerdict.Full, Decide(spent: 10_000m, cap: 0m).Verdict);
        }

        [Fact]
        public void TheRateLimit_DegradesRatherThanRefusing()
        {
            var outcome = Decide(thisHour: 20, hourly: 20);

            Assert.Equal(BudgetVerdict.FreeOnly, outcome.Verdict);
            Assert.Contains("rate limit", outcome.Reason, StringComparison.Ordinal);
        }

        [Fact]
        public void KillSwitches_AreHonouredAboveAnyArithmetic()
        {
            Assert.Equal(BudgetVerdict.FreeOnly, Decide(plan: false, rerank: false).Verdict);
            Assert.Equal(BudgetVerdict.PlanOnly, Decide(rerank: false).Verdict);
        }

        [Fact]
        public void EnrichmentIsBudgetedSeparately()
        {
            // A first index build must not exhaust the month's search budget on the
            // day someone installs the plugin.
            Assert.True(BudgetDecision.AllowsEnrichment(0m, 5m, 2.5m));
            Assert.False(BudgetDecision.AllowsEnrichment(4m, 5m, 2.5m));
            Assert.True(BudgetDecision.AllowsEnrichment(999m, 0m, 100m));
        }
    }

    public class SpendLedgerTests
    {
        private static readonly DateTime Now = new(2026, 8, 15, 12, 0, 0, DateTimeKind.Utc);

        [Fact]
        public void OnlyThisCalendarMonthCounts()
        {
            SpendEntry[] entries =
            [
                new(Now.AddDays(-1), SpendKind.Query, 1m),
                new(Now.AddMonths(-1), SpendKind.Query, 5m),
            ];

            Assert.Equal(1m, SpendLedger.SpentThisMonth(entries, SpendKind.Query, Now));
        }

        [Fact]
        public void QueryAndEnrichmentAreTotalledApart()
        {
            SpendEntry[] entries =
            [
                new(Now, SpendKind.Query, 1m),
                new(Now, SpendKind.Enrichment, 3m),
            ];

            Assert.Equal(1m, SpendLedger.SpentThisMonth(entries, SpendKind.Query, Now));
            Assert.Equal(3m, SpendLedger.SpentThisMonth(entries, SpendKind.Enrichment, Now));
        }

        [Fact]
        public void TheRateLimitWindowIsRollingAndPerUser()
        {
            SpendEntry[] entries =
            [
                new(Now.AddMinutes(-10), SpendKind.Query, 0.01m, "levi"),
                new(Now.AddMinutes(-20), SpendKind.Query, 0.01m, "levi"),
                new(Now.AddHours(-2), SpendKind.Query, 0.01m, "levi"),
                new(Now.AddMinutes(-5), SpendKind.Query, 0.01m, "someone-else"),
                new(Now.AddMinutes(-5), SpendKind.Enrichment, 1m, "levi"),
            ];

            Assert.Equal(2, SpendLedger.PaidQueriesInLastHour(entries, "levi", Now));
            Assert.Equal(1, SpendLedger.PaidQueriesInLastHour(entries, "someone-else", Now));
        }

        [Fact]
        public void PruningKeepsLastMonthSoTheNumberCanStillBeExplained()
        {
            SpendEntry[] entries =
            [
                new(Now, SpendKind.Query, 1m),
                new(Now.AddMonths(-1), SpendKind.Query, 1m),
                new(Now.AddMonths(-6), SpendKind.Query, 1m),
            ];

            Assert.Equal(2, SpendLedger.Prune(entries, Now).Count);
        }
    }
}
