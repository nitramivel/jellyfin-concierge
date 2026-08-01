using System;
using System.Collections.Generic;
using System.Linq;
using Jellyfin.Plugin.Concierge.Core.Documents;
using Jellyfin.Plugin.Concierge.Core.Query;
using Jellyfin.Plugin.Concierge.Core.Retrieval;
using Xunit;
using Xunit.Abstractions;

namespace Jellyfin.Plugin.Concierge.Tests
{
    /// <summary>
    /// End-to-end retrieval over the fixture library, for the queries that have no
    /// title in them: moods and eras.
    /// </summary>
    /// <remarks>
    /// These are the searches the plugin exists for and the ones a keyword engine
    /// cannot answer at all. "dark and twisted" names no film, shares no word with
    /// any overview, and is exactly what somebody types on a Friday night.
    /// </remarks>
    public class VibeSearchTests
    {
        private readonly ITestOutputHelper _output;

        public VibeSearchTests(ITestOutputHelper output)
        {
            _output = output;
        }

        private static readonly IReadOnlyList<ItemDocument> Library = FixtureLibrary.All;

        private static Bm25Index Lexical() => Bm25Index.Build(Library);

        /// <summary>
        /// Builds the vector index the way the indexer does: one row for the item's
        /// document, one for each generated phrasing.
        /// </summary>
        private static VectorIndex Vectors()
        {
            var sources = new List<VectorRowSource>();
            var vectors = new List<float[]>();

            foreach (var document in Library)
            {
                var text = document.RenderEmbeddingText();
                sources.Add(new VectorRowSource(document.ItemId, VectorRowKind.Document, text));
                vectors.Add(ConceptEmbedder.Embed(text));

                foreach (var ask in document.Enrichment?.Asks ?? [])
                {
                    sources.Add(new VectorRowSource(document.ItemId, VectorRowKind.Ask, ask));
                    vectors.Add(ConceptEmbedder.Embed(ask));
                }
            }

            return VectorIndex.Build(sources, vectors);
        }

        private IReadOnlyList<string> Search(string query, int take = 5)
        {
            var lexical = Lexical().Search(query, 40);
            var vector = Vectors().Search(ConceptEmbedder.Embed(query), 40);
            var fused = RankFusion.Fuse(lexical, vector);

            var titles = FixtureLibrary.Titles(fused.Take(take).Select(f => f.ItemId));
            _output.WriteLine($"\"{query}\" -> {string.Join(", ", titles)}");
            return titles;
        }

        // ── The two the owner named ──────────────────────────────────────────────

        [Fact]
        public void DarkAndTwisted_SurfacesTheDarkFilms()
        {
            var top = Search("dark and twisted");

            // Three of the four unambiguously dark films should make the top five.
            var dark = new[] { "Se7en", "The Silence of the Lambs", "Oldboy", "Memento" };
            Assert.True(
                top.Count(t => dark.Contains(t)) >= 3,
                $"expected at least three of [{string.Join(", ", dark)}], got [{string.Join(", ", top)}]");

            // And the comfort watches must not be anywhere near it.
            Assert.DoesNotContain("Paddington", top);
            Assert.DoesNotContain("Clueless", top);
            Assert.DoesNotContain("Groundhog Day", top);
        }

        [Fact]
        public void Nostalgic90sClassics_ReturnsOnlyNinetiesFilms()
        {
            var top = Search("nostalgic 90s classics");
            var years = top.Select(t => Library.First(d => d.Title == t).Year).ToList();

            Assert.All(years, year => Assert.InRange(year!.Value, 1990, 1999));
        }

        [Fact]
        public void Nostalgic90sClassics_PrefersTheWarmOnesOverTheGrimOnes()
        {
            // Se7en and The Silence of the Lambs are 90s classics by any measure, but
            // nobody asking for "nostalgic" wants them. The era half of the query is
            // satisfied by seven films; the mood half is what picks between them.
            var top = Search("nostalgic 90s classics", take: 4);

            Assert.Contains(top, t => t is "Groundhog Day" or "Jurassic Park" or "Clueless" or "The Big Lebowski");
            Assert.DoesNotContain("Se7en", top);
            Assert.DoesNotContain("The Silence of the Lambs", top);
        }

        // ── The era mechanism, on its own ────────────────────────────────────────

        [Fact]
        public void EraQuery_WorksWithNoModelAtAll()
        {
            // Purely lexical: no vectors, no embedding call, no money. This is the
            // path that keeps working when the budget is gone, and it is the whole
            // reason the decade is written into the document at index time.
            var lexical = Lexical().Search("90s", 10);
            var titles = FixtureLibrary.Titles(lexical.Select(h => h.ItemId));

            Assert.NotEmpty(titles);
            Assert.All(
                titles,
                title => Assert.InRange(Library.First(d => d.Title == title).Year!.Value, 1990, 1999));
        }

        [Theory]
        [InlineData("nineties")]
        [InlineData("1990s")]
        [InlineData("90s")]
        public void EveryWayOfSayingADecade_FindsTheSameFilms(string phrasing)
        {
            var titles = FixtureLibrary.Titles(Lexical().Search(phrasing, 10).Select(h => h.ItemId));

            Assert.Contains("Groundhog Day", titles);
            Assert.Contains("Fargo", titles);
        }

        [Fact]
        public void EraTokens_AreNotWrittenForItemsWithoutAYear()
        {
            Assert.Equal(string.Empty, EraTokens.Render(null));
        }

        [Fact]
        public void EraTokens_RenderTheYearTheDecadeAndTheSpokenForms()
        {
            var rendered = EraTokens.Render(1995);

            Assert.Contains("1995", rendered, StringComparison.Ordinal);
            Assert.Contains("1990s", rendered, StringComparison.Ordinal);
            Assert.Contains("90s", rendered, StringComparison.Ordinal);
            Assert.Contains("nineties", rendered, StringComparison.Ordinal);
        }

        // ── The semantic half earning its place ──────────────────────────────────

        [Fact]
        public void AMoodWordNoDocumentContains_StillFindsTheRightFilms()
        {
            // "harrowing" appears in no overview, no theme and no phrasing in the
            // fixture library, so keyword search cannot return anything for it. If
            // this passes, it passed through the vector half alone.
            Assert.DoesNotContain(
                Library,
                d => d.RenderFields().Any(f => f.Text.Contains("harrowing", StringComparison.OrdinalIgnoreCase)));

            // The bare word, so the claim is exact: nothing lexical can contribute,
            // and every hit below arrived through the vector half alone.
            Assert.Empty(Lexical().Search("harrowing", 10));

            var top = Search("harrowing");
            Assert.Contains(top, t => t is "Se7en" or "Oldboy" or "The Silence of the Lambs" or "Memento");
        }

        [Fact]
        public void AComfortWatchRequest_FindsTheGentleFilms()
        {
            var top = Search("something gentle and cosy for a rainy afternoon");

            Assert.Contains(top, t => t is "Paddington" or "Amélie" or "Groundhog Day" or "Clueless");
            Assert.DoesNotContain("Oldboy", top);
            Assert.DoesNotContain("Se7en", top);
        }

        // ── The failure the plan calls out by name ───────────────────────────────

        [Fact]
        public void PlotRecall_FindsTheFilmWhoseOverviewDoesNotMentionIt()
        {
            // The canonical case: an overview describes the premise, and people
            // remember a moment. Memento's overview says "a man with short-term
            // memory loss attempts to track down his wife's murderer" and never
            // mentions tattoos — the single thing everyone remembers.
            var memento = Library.First(d => d.Title == "Memento");
            Assert.DoesNotContain("tattoo", memento.Overview, StringComparison.OrdinalIgnoreCase);

            var top = Search("the one where he tattoos the clues on himself");
            Assert.Equal("Memento", top[0]);
        }

        [Fact]
        public void RowsCollapseToItems_SoOneWellEnrichedFilmCannotTakeEverySlot()
        {
            // Se7en owns five vector rows. Without the collapse in VectorIndex.Search
            // it would occupy five of the top ten slots on any query it matches.
            var results = Vectors().Search(ConceptEmbedder.Embed("dark twisted killer"), 40);

            Assert.Equal(results.Select(r => r.ItemId).Distinct().Count(), results.Count);
        }

        // ── Routing ─────────────────────────────────────────────────────────────

        [Fact]
        public void VibeQueries_AreRoutedToConcierge()
        {
            var names = Lexical();

            Assert.Equal(QueryRoute.Concierge, QueryRouter.Decide("nostalgic 90s classics", names).Route);
            Assert.Equal(QueryRoute.Concierge, QueryRouter.Decide("something gentle for a rainy day", names).Route);
            Assert.NotEqual(QueryRoute.Native, QueryRouter.Decide("dark and twisted", names).Route);
        }

        [Fact]
        public void TitleLookups_StayOnTheFreeNativePath()
        {
            var names = Lexical();

            // These must never reach a model: it would cost money to produce a worse
            // answer than substring matching gives away.
            Assert.Equal(QueryRoute.Native, QueryRouter.Decide("fargo", names).Route);
            Assert.Equal(QueryRoute.Native, QueryRouter.Decide("blade", names).Route);
            Assert.Equal(QueryRoute.Native, QueryRouter.Decide("the big lebowski", names).Route);
        }
    }
}
