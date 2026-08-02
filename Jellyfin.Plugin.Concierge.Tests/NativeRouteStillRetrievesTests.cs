using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Concierge.Configuration;
using Jellyfin.Plugin.Concierge.Core.Budget;
using Jellyfin.Plugin.Concierge.Services.Budget;
using Jellyfin.Plugin.Concierge.Services.Llm;
using Jellyfin.Plugin.Concierge.Core.Documents;
using Jellyfin.Plugin.Concierge.Core.Retrieval;
using Jellyfin.Plugin.Concierge.Services;
using Jellyfin.Plugin.Concierge.Services.Embeddings;
using Jellyfin.Plugin.Concierge.Services.Indexing;
using Jellyfin.Plugin.Concierge.Services.Quotes;
using Jellyfin.Plugin.Concierge.Services.Runs;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Jellyfin.Plugin.Concierge.Tests
{
    /// <summary>
    /// A Native route means "spend nothing on this query", not "return nothing".
    /// </summary>
    /// <remarks>
    /// Measured on the owner's library on 1 Aug 2026, before this was fixed:
    /// <c>robots</c> ranked <i>Love, Death &amp; Robots</i> first and <i>Mr. Robot</i>
    /// seventh on keywords alone, and <c>death love</c> ranked <i>Love, Death &amp;
    /// Robots</i> first with a dominant score. Both returned nothing, because the
    /// Native route short-circuited before retrieval ran — and Jellyfin's own
    /// substring match cannot rescue either, since "robots" does not occur inside
    /// "Mr. Robot" and "death love" is the right words in the wrong order.
    /// </remarks>
    public class NativeRouteStillRetrievesTests
    {
        private sealed class StubIndexStore : IIndexStore
        {
            private readonly ConciergeIndex _index;

            public StubIndexStore(ConciergeIndex index) => _index = index;

            public Task<ConciergeIndex?> LoadAsync(EmbeddingProfile profile, CancellationToken ct)
                => Task.FromResult<ConciergeIndex?>(_index);

            public Task SaveAsync(
                IndexState state,
                IReadOnlyList<ItemDocument> documents,
                IReadOnlyList<VectorRowSource> rows,
                IReadOnlyList<float[]> vectors,
                CancellationToken ct) => Task.CompletedTask;

            public Task<IReadOnlyDictionary<Guid, StoredEnrichment>> LoadEnrichmentAsync(CancellationToken ct)
                => Task.FromResult<IReadOnlyDictionary<Guid, StoredEnrichment>>(
                    new Dictionary<Guid, StoredEnrichment>());

            public Task SaveEnrichmentAsync(IReadOnlyCollection<StoredEnrichment> e, CancellationToken ct)
                => Task.CompletedTask;

            public Task<IndexState?> LoadStateAsync(CancellationToken ct)
                => Task.FromResult<IndexState?>(_index.State);

            public Task DeleteAsync(CancellationToken ct) => Task.CompletedTask;

            public Task<bool> ForgetAsync(Guid itemId, CancellationToken ct) => Task.FromResult(false);
        }

        /// <summary>Throws if used, so a Native query touching the network fails loudly.</summary>
        private sealed class ExplodingEmbeddingFactory : IEmbeddingProviderFactory
        {
            public bool WasUsed { get; private set; }

            public IEmbeddingProvider Create(PluginConfiguration config) => Create(new EmbeddingProfile());

            public IEmbeddingProvider Create(EmbeddingProfile profile)
            {
                WasUsed = true;
                throw new InvalidOperationException("a Native query must not embed anything");
            }
        }

        private sealed class NullQueryLog : IQueryLogStore
        {
            public List<QueryRunRecord> Records { get; } = [];

            public Task RecordAsync(QueryRunRecord run, CancellationToken ct)
            {
                Records.Add(run);
                return Task.CompletedTask;
            }

            public Task<IReadOnlyList<QueryRunRecord>> RecentAsync(int count, CancellationToken ct)
                => Task.FromResult<IReadOnlyList<QueryRunRecord>>(Records);

            public Task<IReadOnlyList<QueryRunRecord>> SinceAsync(DateTime fromUtc, CancellationToken ct)
                => Task.FromResult<IReadOnlyList<QueryRunRecord>>(
                    Records.Where(r => r.StartedUtc >= fromUtc).ToList());
        }

        /// <summary>Throws if used, so a Native query reaching a model fails loudly.</summary>
        private sealed class ExplodingLlmFactory : ILlmProviderFactory
        {
            public Jellyfin.Plugin.Concierge.Services.Llm.ILlmProvider Create(PluginConfiguration config)
                => throw new InvalidOperationException("a Native query must not call a model");

            public Jellyfin.Plugin.Concierge.Services.Llm.ILlmProvider Create(
                ModelProfile profile, bool globalEnableThinking)
                => throw new InvalidOperationException("a Native query must not call a model");
        }

        private sealed class NullSpendStore : ISpendStore
        {
            public List<(SpendKind Kind, decimal Amount)> Recorded { get; } = [];

            public void Record(SpendKind kind, decimal amountUsd, string? userId = null)
                => Recorded.Add((kind, amountUsd));

            public decimal QuerySpendThisMonth() => 0m;

            public decimal EnrichmentSpendThisMonth() => 0m;

            public int PaidQueriesInLastHour(string? userId) => 0;
        }

        private static SearchService Service(
            IEmbeddingProviderFactory embeddings,
            IQueryLogStore log,
            ISpendStore? spend = null)
            => new(
                new StubIndexStore(BuildIndex()),
                embeddings,
                new ExplodingLlmFactory(),
                log,
                spend ?? new NullSpendStore(),
                new QuoteIndexProvider(new EmptyQuoteStore(), NullLogger<QuoteIndexProvider>.Instance),
                NullLogger<SearchService>.Instance);

        /// <summary>No extracted dialogue, which is every install before the task runs.</summary>
        private sealed class EmptyQuoteStore : IQuoteStore
        {
            public Task<QuoteTrack?> LoadAsync(Guid itemId, CancellationToken ct)
                => Task.FromResult<QuoteTrack?>(null);

            public Task SaveAsync(QuoteTrack track, CancellationToken ct) => Task.CompletedTask;

            public Task<bool> ForgetAsync(Guid itemId, CancellationToken ct) => Task.FromResult(false);

            public Task<IReadOnlyList<QuoteTrack>> LoadAllAsync(CancellationToken ct)
                => Task.FromResult<IReadOnlyList<QuoteTrack>>([]);

            public Task SaveCoverageAsync(IReadOnlyList<QuoteCoverage> c, CancellationToken ct)
                => Task.CompletedTask;

            public Task<IReadOnlyList<QuoteCoverage>> LoadCoverageAsync(CancellationToken ct)
                => Task.FromResult<IReadOnlyList<QuoteCoverage>>([]);

            public Task DeleteAsync(CancellationToken ct) => Task.CompletedTask;
        }

        private static ConciergeIndex BuildIndex()
        {
            var documents = FixtureLibrary.All;
            var (rows, texts) = VectorRowPlanner.Plan(documents, 8);
            var vectors = texts.Select(ConceptEmbedder.Embed).ToList();

            return new ConciergeIndex(
                new IndexState(1, "stub", vectors[0].Length, string.Empty, string.Empty,
                    DateTime.UtcNow, documents.Count, rows.Count, documents.Count),
                documents,
                Bm25Index.Build(documents),
                VectorIndex.Build(rows, vectors));
        }

        private static PluginConfiguration Config() => new()
        {
            EmbeddingProfiles = [new EmbeddingProfile { Id = "e", Name = "Stub", Model = "stub" }],
            DefaultEmbeddingProfileId = "e",
            MaxResults = 10,
        };

        /// <summary>
        /// The preview: the free half of the pipeline, answered on its own.
        /// </summary>
        /// <remarks>
        /// Latency here is model generation and nothing else — 6.4 s at the median
        /// against 11 ms of pipeline. The way to make a search feel instant is
        /// therefore not to make the model faster but to stop making anyone wait for
        /// it: keyword retrieval already has a real answer in about a millisecond,
        /// and the ranked one replaces it when it arrives.
        /// <para>
        /// These assert the "free" half of that literally. The factories throw on
        /// use, so a preview that reaches an embedding provider or a model fails the
        /// test rather than quietly costing money on every keystroke.
        /// </para>
        /// </remarks>
        [Theory]
        [InlineData("dark and twisted")]
        [InlineData("nostalgic 90s classics")]
        [InlineData("lambs silence")]
        public async Task APreviewSpendsNothingEvenOnAQueryThatWouldNormallyPay(string query)
        {
            var embeddings = new ExplodingEmbeddingFactory();
            var spend = new NullSpendStore();
            var service = Service(embeddings, new NullQueryLog(), spend);

            var result = await service.SearchAsync(
                query, null, Config(), CancellationToken.None, preview: true);

            Assert.NotEmpty(result.Hits);
            Assert.False(embeddings.WasUsed);
            Assert.Empty(spend.Recorded);
            Assert.Equal(0m, result.CostUsd);
            Assert.False(result.Reranked);
        }

        /// <summary>
        /// Previews stay out of the query log.
        /// </summary>
        /// <remarks>
        /// They fire on every keystroke and cost nothing. Writing them to an
        /// append-only file whose entire purpose is the record of what searches cost
        /// would bury that record under rows that cost nothing.
        /// </remarks>
        [Fact]
        public async Task APreviewIsNotWrittenToTheQueryLog()
        {
            var log = new NullQueryLog();
            var service = Service(new ExplodingEmbeddingFactory(), log);

            await service.SearchAsync(
                "dark and twisted", null, Config(), CancellationToken.None, preview: true);

            Assert.Empty(log.Records);

            // …while a real search still is.
            await service.SearchAsync("fargo", null, Config(), CancellationToken.None);

            Assert.Single(log.Records);
        }

        /// <summary>
        /// A preview never becomes a paid query by the back door.
        /// </summary>
        /// <remarks>
        /// The dominant-winner rule upgrades a Native route to the full pipeline when
        /// keyword scores are too close to trust. That rule must not fire on a
        /// preview: the caller asked for the free answer, and an upgrade would spend
        /// money on a request made every 250 ms.
        /// </remarks>
        [Fact]
        public async Task ANoClearWinnerDoesNotUpgradeAPreviewIntoASpend()
        {
            var embeddings = new ExplodingEmbeddingFactory();
            var service = Service(embeddings, new NullQueryLog());

            var result = await service.SearchAsync(
                "the", null, Config(), CancellationToken.None, preview: true);

            Assert.False(embeddings.WasUsed);
            Assert.Equal(0m, result.CostUsd);
        }

        [Theory]
        [InlineData("fargo", "Fargo")]
        [InlineData("lebowski", "The Big Lebowski")]
        [InlineData("memento", "Memento")]
        public async Task ATitleLookup_StillGetsResults(string query, string expected)
        {
            var embeddings = new ExplodingEmbeddingFactory();
            var log = new NullQueryLog();
            var service = Service(embeddings, log);

            var result = await service.SearchAsync(query, null, Config(), CancellationToken.None);

            Assert.Equal("Native", result.Route);
            Assert.NotEmpty(result.Hits);
            Assert.Equal(expected, result.Hits[0].Name);

            // Free and instant: no embedding call, so no network and no cost.
            Assert.False(embeddings.WasUsed);
            Assert.Equal(0m, result.CostUsd);
        }

        [Fact]
        public async Task WordsInTheWrongOrder_StillFindTheTitle()
        {
            // The "death love" case: every word names something, so the router calls
            // it a title lookup — and a substring match fails while BM25 does not.
            var service = Service(new ExplodingEmbeddingFactory(), new NullQueryLog());

            var result = await service.SearchAsync("lambs silence", null, Config(), CancellationToken.None);

            Assert.Equal("Native", result.Route);
            Assert.Equal("The Silence of the Lambs", result.Hits[0].Name);
        }

        [Fact]
        public async Task APluralTypedAgainstASingularTitle_StillMatches()
        {
            // The "robots" case: stemming is why the keyword half beats a substring
            // match, and it only helps if retrieval is allowed to run at all.
            var service = Service(new ExplodingEmbeddingFactory(), new NullQueryLog());

            var result = await service.SearchAsync("lambs", null, Config(), CancellationToken.None);

            Assert.NotEmpty(result.Hits);
            Assert.Contains(result.Hits, h => h.Name == "The Silence of the Lambs");
        }

        [Fact]
        public async Task TheQueryLog_RecordsWhatCameBack()
        {
            var log = new NullQueryLog();
            var service = Service(new ExplodingEmbeddingFactory(), log);

            await service.SearchAsync("fargo", null, Config(), CancellationToken.None);

            var record = Assert.Single(log.Records);
            Assert.NotNull(record.TopHits);
            Assert.Contains("Fargo", record.TopHits!);
        }
    }

    /// <summary>
    /// Every item owns a short vector of nothing but tone, so a mood query has
    /// something focused to match.
    /// </summary>
    public class VectorRowPlannerTests
    {
        [Fact]
        public void EveryEnrichedItem_GetsAVibeRow()
        {
            var (rows, texts) = VectorRowPlanner.Plan(FixtureLibrary.All, 8);

            var vibes = rows.Where(r => r.Kind == VectorRowKind.Vibe).ToList();
            Assert.Equal(FixtureLibrary.All.Count, vibes.Count);
            Assert.Equal(rows.Count, texts.Count);
        }

        [Fact]
        public void TheVibeRow_IsShortAndCarriesOnlyToneAndGenre()
        {
            var (rows, texts) = VectorRowPlanner.Plan(FixtureLibrary.All, 8);
            var index = rows.FindIndex(r =>
                r.Kind == VectorRowKind.Vibe && r.ItemId == FixtureLibrary.Id("Se7en"));

            var vibe = texts[index];

            Assert.Contains("dark", vibe, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("twisted", vibe, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("Thriller", vibe, StringComparison.OrdinalIgnoreCase);

            // The whole point is that it is not the document row. If the plot leaks in
            // here, a mood query is averaged against a plot summary again.
            Assert.DoesNotContain("detective", vibe, StringComparison.OrdinalIgnoreCase);
            Assert.True(vibe.Length < 200, $"vibe row should stay short, was {vibe.Length} chars");
        }

        [Fact]
        public void AnItemWithNoEnrichment_StillGetsItsDocumentRow()
        {
            var bare = FixtureLibrary.All[0] with { Enrichment = null };
            var (rows, _) = VectorRowPlanner.Plan([bare], 8);

            var row = Assert.Single(rows);
            Assert.Equal(VectorRowKind.Document, row.Kind);
        }

        [Fact]
        public void MaxAsksPerItem_IsHonoured()
        {
            var (rows, _) = VectorRowPlanner.Plan(FixtureLibrary.All, 2);

            foreach (var group in rows.Where(r => r.Kind == VectorRowKind.Ask).GroupBy(r => r.ItemId))
            {
                Assert.True(group.Count() <= 2);
            }
        }
    }
}
