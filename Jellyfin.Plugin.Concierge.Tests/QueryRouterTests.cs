using Jellyfin.Plugin.Concierge.Core.Query;
using Jellyfin.Plugin.Concierge.Core.Retrieval;
using Xunit;

namespace Jellyfin.Plugin.Concierge.Tests
{
    /// <summary>
    /// The router, pinned by a table of real queries.
    /// </summary>
    /// <remarks>
    /// The plan asks for this table explicitly, because the router is the single
    /// biggest lever on both cost and perceived quality and it must be changeable
    /// without fear. Wrong toward "always Concierge" and the plugin is expensive and
    /// sluggish; wrong toward "rarely Concierge" and it appears not to work.
    /// <para>
    /// Several rows below are queries the owner actually typed on 1 Aug 2026, three
    /// of which the first version got wrong.
    /// </para>
    /// </remarks>
    public class QueryRouterTests
    {
        private static readonly Bm25Index Names = Bm25Index.Build(FixtureLibrary.All);

        // ── Title lookups: must stay free ───────────────────────────────────────

        [Theory]
        [InlineData("fargo")]
        [InlineData("blade")]
        [InlineData("bla")]
        [InlineData("memento")]
        [InlineData("the big lebowski")]
        [InlineData("silence of the lambs")]
        [InlineData("jodie foster")]
        [InlineData("fincher")]
        public void SomebodyTypingATitleTheyKnow_NeverReachesAModel(string query)
        {
            // Every one of these is answered better, faster and for nothing by
            // Jellyfin's own substring match.
            Assert.Equal(QueryRoute.Native, QueryRouter.Decide(query, Names).Route);
        }

        // ── Descriptions: must reach Concierge ──────────────────────────────────

        [Theory]
        [InlineData("the one where they kill the guy's dog")]
        [InlineData("that movie where the guy can't make new memories")]
        [InlineData("something funny but not stupid for a sunday")]
        [InlineData("man trapped in tv show")]
        [InlineData("dark melancholic comedy")]
        [InlineData("murder on my mind")]
        [InlineData("nostalgic 90s classics")]
        [InlineData("90s sci-fi under two hours")]
        [InlineData("dark and twisted")]
        public void ADescription_ReachesConcierge(string query)
        {
            Assert.Equal(QueryRoute.Concierge, QueryRouter.Decide(query, Names).Route);
        }

        // ── The bug the owner found ─────────────────────────────────────────────

        [Theory]
        [InlineData("dark comedy")]
        [InlineData("weed comedy")]
        [InlineData("comedy")]
        [InlineData("bleak thriller")]
        [InlineData("cosy")]
        public void AShortDescriptionThatNamesNothing_IsNotATitleLookup(string query)
        {
            // Measured 1 Aug 2026: all three of "dark comedy", "weed comedy" and
            // "comedy" routed to native and returned nothing at all, because the
            // router assumed two words meant somebody was typing a title.
            //
            // Both, not Concierge: native still renders instantly, and if one of
            // these ever does match a title the user gets that too.
            var decision = QueryRouter.Decide(query, Names);

            Assert.NotEqual(QueryRoute.Native, decision.Route);
            Assert.Equal(QueryRoute.Both, decision.Route);
        }

        [Fact]
        public void AShortQueryThatDoesNameSomething_StillGoesNative()
        {
            // The rule above must not swallow the case it was protecting. "blade
            // runner" is two words and it is a title.
            Assert.Equal(QueryRoute.Native, QueryRouter.Decide("blade runner", Names).Route);
        }

        [Fact]
        public void WithNoIndexYet_ShortQueriesStayNative()
        {
            // Nothing to search, so there is nothing to gain by routing anywhere
            // else — and no dictionary to tell a title from a description with.
            var decision = QueryRouter.Decide("dark comedy", null);

            Assert.Equal(QueryRoute.Native, decision.Route);
            Assert.Contains("too short", decision.Reason, System.StringComparison.Ordinal);
        }

        // ── Constraints and quotes ──────────────────────────────────────────────

        [Theory]
        [InlineData("something from the 80s")]
        [InlineData("under two hours")]
        [InlineData("1994")]
        [InlineData("nineties")]
        public void ATemporalOrLengthConstraint_ReachesConcierge(string query)
        {
            Assert.Equal(QueryRoute.Concierge, QueryRouter.Decide(query, Names).Route);
        }

        [Theory]
        [InlineData("\"I'm walking here\"")]
        [InlineData("“you talking to me”")]
        public void AQuotedString_IsDialogueSearch(string query)
        {
            var decision = QueryRouter.Decide(query, Names);

            Assert.Equal(QueryRoute.Concierge, decision.Route);
            Assert.Contains("quoted", decision.Reason, System.StringComparison.Ordinal);
        }

        // ── Degenerate input ────────────────────────────────────────────────────

        [Theory]
        [InlineData("")]
        [InlineData("   ")]
        [InlineData("!!!")]
        public void NothingSearchable_CostsNothing(string query)
        {
            Assert.Equal(QueryRoute.Native, QueryRouter.Decide(query, Names).Route);
        }

        [Fact]
        public void EveryDecision_SaysWhichRuleFired()
        {
            // The reason is recorded on every query, because the router is the thing
            // most likely to need arguing with later — as it just was.
            foreach (var query in new[] { "fargo", "dark comedy", "the one with the dog", "\"quote\"", "" })
            {
                Assert.False(string.IsNullOrWhiteSpace(QueryRouter.Decide(query, Names).Reason));
            }
        }
    }
}
