using System.Linq;
using Jellyfin.Plugin.Concierge.Core.Ranking;
using Xunit;

namespace Jellyfin.Plugin.Concierge.Tests
{
    /// <summary>
    /// A response that stops mid-object keeps the entries it finished.
    /// </summary>
    /// <remarks>
    /// Some endpoints cannot be constrained at all. One here rejects
    /// <c>response_format</c> outright — <em>"this model deployment serves free-form
    /// text and does not support constrained decoding"</em> — so its JSON is a request
    /// rather than a guarantee, and it will eventually stop mid-entry.
    /// <para>
    /// Observed: <c>finish_reason: "stop"</c>, 146 tokens of a 4,000 cap, ten rankings
    /// written and the last one left open. The strict parse yielded nothing at all and
    /// the whole shortlist kept its fused order — a paid pass bought a no-op, with nine
    /// complete rankings sitting in the response.
    /// </para>
    /// <para>
    /// This is not JSON repair. Nothing is closed, invented or guessed; an entry either
    /// was fully written or it is ignored, and what is ignored keeps the position
    /// retrieval gave it (hard rule 7).
    /// </para>
    /// </remarks>
    public class RerankSalvageTests
    {
        /// <summary>The real response, fence and all, from the endpoint that prompted this.</summary>
        private const string TruncatedRealResponse =
            """
            ```json
            {
              "order": [
                {"i": 6, "why":"violent fantasies and pitch-black satire"},
                {"i": 7, "why":"identity dissolution and alien unknowability"},
                {"i": 8, "why":"cursed film about lost souls"},
                {"i": 2, "why":"lonely and heartbreaking abandonment"},
                {"i": 29},
                {"i": 1},
                {"i": 20},
                {"i": 13},
                {"i": 15},
                {"i": 9

              ]
            }
            ```
            """;

        [Fact]
        public void AnUnclosedFinalEntry_DoesNotDiscardTheOnesBeforeIt()
        {
            var outcome = RerankParser.Parse(TruncatedRealResponse, 30);

            // Nine complete entries; the tenth was never finished.
            Assert.Equal(9, outcome.Ranked);
            Assert.Equal(21, outcome.Omitted);
            Assert.Equal(0, outcome.Invented);
        }

        [Fact]
        public void TheSalvagedOrderIsTheModelsOrder()
        {
            var outcome = RerankParser.Parse(TruncatedRealResponse, 30);
            var ranked = outcome.Order.Take(9).Select(o => o.Index).ToList();

            Assert.Equal([6, 7, 8, 2, 29, 1, 20, 13, 15], ranked);

            // And the reasons survive with them.
            Assert.Equal("violent fantasies and pitch-black satire", outcome.Order[0].Why);
        }

        [Fact]
        public void TheUnfinishedEntry_IsNotGuessedAt()
        {
            // Index 9 was named but never closed. It must not be read: half an entry is
            // not a statement, and inventing the rest is exactly what this must not do.
            var outcome = RerankParser.Parse(TruncatedRealResponse, 30);
            var ranked = outcome.Order.Take(outcome.Ranked).Select(o => o.Index);

            Assert.DoesNotContain(9, ranked);
        }

        [Fact]
        public void AWellFormedResponse_StillTakesTheStrictPath()
        {
            // Salvage is a fallback, not the road. A valid response must parse exactly
            // as it always did.
            var outcome = RerankParser.Parse("""{"order":[{"i":3,"why":"tense"},{"i":0}]}""", 5);

            Assert.Equal(2, outcome.Ranked);
            Assert.Equal(3, outcome.Omitted);
            Assert.Equal([3, 0], outcome.Order.Take(2).Select(o => o.Index));
        }

        [Fact]
        public void SalvageStillRefusesAnItemTheSearcherDoesNotOwn()
        {
            // Hard rule 1 does not relax because the response was malformed.
            var outcome = RerankParser.Parse(
                """{"order":[{"i":1,"why":"ok"},{"i":99,"why":"nope"},{"i":2""", 5);

            // Two entries closed; only one of them names something in the shortlist.
            // The third was never finished, so it is not an entry at all.
            Assert.Equal(1, outcome.Ranked);
            Assert.Equal(1, outcome.Invented);
            Assert.DoesNotContain(99, outcome.Order.Select(o => o.Index));
        }

        [Fact]
        public void ARepeatedIndexInSalvage_IsStillIgnored()
        {
            var outcome = RerankParser.Parse(
                """{"order":[{"i":1,"why":"first"},{"i":1,"why":"again"},{"i":0""", 4);

            // The repeat is ignored and the unclosed entry is not read, so one ranking
            // survives — the first statement the model made about index 1.
            Assert.Equal(1, outcome.Ranked);
            Assert.Equal("first", outcome.Order[0].Why);
        }

        [Fact]
        public void ResponseWithNoEntriesAtAll_LeavesTheFusedOrderAlone()
        {
            var outcome = RerankParser.Parse("I'm sorry, I can't help with that.", 6);

            Assert.Equal(0, outcome.Ranked);
            Assert.Equal(6, outcome.Omitted);
        }
    }
}
