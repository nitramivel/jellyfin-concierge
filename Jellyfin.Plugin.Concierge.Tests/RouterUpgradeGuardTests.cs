using Jellyfin.Plugin.Concierge.Core.Query;
using Xunit;

namespace Jellyfin.Plugin.Concierge.Tests
{
    /// <summary>
    /// A thin query must not be promoted to the paid path just because its keyword
    /// scores are flat.
    /// </summary>
    /// <remarks>
    /// The upgrade in <c>SearchService</c> exists for a real case: two genuine titles
    /// tie on keywords and only a model can tell which was meant. But a flat
    /// distribution has a second, far commoner cause — a query so thin that everything
    /// matches it weakly and nothing matches it well. That is noise, not ambiguity.
    /// <para>
    /// Measured on the evaluation set: under the paid path, seven of eight
    /// deliberately title-shaped queries left the free native route, against two on
    /// the free run. <c>s</c>, <c>bla</c>, <c>the of</c> and <c>blade</c> each bought
    /// seconds of latency and a model call for a query the native list already
    /// answered — against hard rule 2 (native never gets slower) and hard rule 11 (the
    /// router is the biggest lever on cost and perceived quality).
    /// </para>
    /// </remarks>
    public class RouterUpgradeGuardTests
    {
        [Theory]
        [InlineData("michael scott")]      // the case the upgrade was built for
        [InlineData("dark comedy")]
        [InlineData("lord of the rings")]
        public void AQueryWithRealWords_MayStillBeUpgraded(string query)
        {
            Assert.True(QueryRouter.IsWorthUpgrading(query), $"should remain upgradable: {query}");
        }

        [Theory]
        [InlineData("s")]                  // one letter
        [InlineData("bla")]                // one fragment
        [InlineData("blade")]              // one word: a title, not a description
        [InlineData("fargo")]
        [InlineData("the of")]             // two words, neither of them one
        [InlineData("")]
        [InlineData("   ")]
        public void AThinQuery_IsNeverWorthPayingAModelToRead(string query)
        {
            Assert.False(QueryRouter.IsWorthUpgrading(query), $"should not be upgradable: {query}");
        }

        [Fact]
        public void TheGuardIsIndependentOfTheScores()
        {
            // Deliberately separate from HasDominantWinner. One asks "is there anything
            // here worth disambiguating", the other "is it actually ambiguous", and
            // collapsing them would mean a thin query with one strong hit and a rich
            // query with none behaved the same way.
            Assert.True(QueryRouter.HasDominantWinner([10.0, 1.0]));
            Assert.False(QueryRouter.HasDominantWinner([5.93, 5.55]));

            // …and the guard says nothing about either of those distributions.
            Assert.False(QueryRouter.IsWorthUpgrading("s"));
            Assert.True(QueryRouter.IsWorthUpgrading("michael scott"));
        }
    }
}
