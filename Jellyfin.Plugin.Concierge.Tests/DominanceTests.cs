using System.Collections.Generic;
using Jellyfin.Plugin.Concierge.Core.Query;
using Xunit;

namespace Jellyfin.Plugin.Concierge.Tests
{
    /// <summary>
    /// §4.2's third native rule: a Native route only stands if keyword retrieval
    /// produced a clear winner.
    /// </summary>
    /// <remarks>
    /// This is what "michael scott" needed. Scott Pilgrim vs. the World scored 5.93
    /// against The Office's 5.55 — a 7% edge, which is a coin toss dressed as an
    /// answer. Routing Native on it meant the re-ranker, which knows perfectly well
    /// who Michael Scott is, never saw the query.
    /// </remarks>
    public class DominanceTests
    {
        [Fact]
        public void TheMeasuredMichaelScottScores_AreNotDominant()
        {
            // The actual numbers off the owner's library.
            Assert.False(QueryRouter.HasDominantWinner([5.93, 5.55, 5.21, 4.98]));
        }

        [Fact]
        public void AClearWinnerIsDominant()
        {
            // "death love" ranked Love, Death & Robots at 7.81 against 6.52 — still
            // not a runaway — but a real title lookup looks like this.
            Assert.True(QueryRouter.HasDominantWinner([9.0, 3.1, 2.8]));
        }

        [Fact]
        public void ASingleHitIsDominantByDefinition()
        {
            // Nothing else matched at all, so there is nothing to be ambiguous with.
            Assert.True(QueryRouter.HasDominantWinner([4.2]));
        }

        [Fact]
        public void NoHitsIsNotDominant()
        {
            // Keyword retrieval found nothing, so it certainly did not answer the
            // question — the semantic half should get a turn.
            Assert.False(QueryRouter.HasDominantWinner([]));
        }

        [Fact]
        public void AFlatDistributionIsNeverDominant()
        {
            Assert.False(QueryRouter.HasDominantWinner([5.0, 4.9, 4.8, 4.7]));
        }

        [Fact]
        public void AZeroRunnerUpCountsAsDominant()
        {
            // Guards the division as much as anything: one real hit and noise below.
            Assert.True(QueryRouter.HasDominantWinner([3.0, 0.0]));
        }

        [Theory]
        [InlineData(1.34, false)]
        [InlineData(1.36, true)]
        public void TheThresholdIsWhereItSays(double ratio, bool expected)
        {
            Assert.Equal(expected, QueryRouter.HasDominantWinner([ratio, 1.0]));
        }
    }

    /// <summary>
    /// The re-rank asks for fewer than the whole shortlist, and that is only safe
    /// because the parser fills the rest back in.
    /// </summary>
    public class RerankPartialOrderTests
    {
        [Fact]
        public void AskingForTwelveOfTwentyFour_LosesNothing()
        {
            // Latency, measured: ordering all forty took 8-22 seconds against a
            // 2.5-second budget, almost all of it the model emitting entries one
            // token at a time. Asking for twelve is safe by construction.
            var response = """
                {"order":[{"i":5,"why":"a"},{"i":0,"why":"b"},{"i":9,"why":"c"}]}
                """;

            var outcome = Jellyfin.Plugin.Concierge.Core.Ranking.RerankParser.Parse(response, 24);

            Assert.Equal(24, outcome.Order.Count);
            Assert.Equal([5, 0, 9], new List<int>
            {
                outcome.Order[0].Index, outcome.Order[1].Index, outcome.Order[2].Index,
            });

            // Everything unplaced keeps the order retrieval gave it, which is the
            // answer that would have been served with no re-rank at all.
            Assert.Equal(21, outcome.Omitted);
        }
    }
}
