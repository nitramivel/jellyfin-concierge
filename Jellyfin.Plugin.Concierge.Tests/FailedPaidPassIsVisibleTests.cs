using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Concierge.Configuration;
using Jellyfin.Plugin.Concierge.Core.Budget;
using Jellyfin.Plugin.Concierge.Core.Documents;
using Jellyfin.Plugin.Concierge.Core.Retrieval;
using Jellyfin.Plugin.Concierge.Services;
using Jellyfin.Plugin.Concierge.Services.Budget;
using Jellyfin.Plugin.Concierge.Services.Embeddings;
using Jellyfin.Plugin.Concierge.Services.Indexing;
using Jellyfin.Plugin.Concierge.Services.Llm;
using Jellyfin.Plugin.Concierge.Services.Quotes;
using Jellyfin.Plugin.Concierge.Services.Runs;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Jellyfin.Plugin.Concierge.Tests
{
    /// <summary>
    /// A model that refuses must be visible, not merely survivable.
    /// </summary>
    /// <remarks>
    /// Degrading to the free answer when a provider fails is correct and must stay.
    /// What was wrong is that it was silent: the exception became a warning in the
    /// server log, a call record is only added <em>after</em> a response comes back so
    /// the query log showed no call and $0.00, and the response's own reason was
    /// computed before the Native→Both upgrade could change it.
    /// <para>
    /// Measured on the owner's server: a re-rank profile pointing at a model that has
    /// never once succeeded returned HTTP 400 forty-one times over three hours. Every
    /// surface reported a normal search. The only symptom anybody could describe was
    /// "the results seem worse".
    /// </para>
    /// </remarks>
    public class FailedPaidPassIsVisibleTests
    {
        private sealed class RefusingLlmFactory : ILlmProviderFactory
        {
            public ILlmProvider Create(PluginConfiguration config) => new Refusing();

            public ILlmProvider Create(ModelProfile profile, bool globalEnableThinking) => new Refusing();

            private sealed class Refusing : ILlmProvider
            {
                public string ModelId => "broken-model";

                public Task<LlmResult> CompleteAsync(LlmRequest request, CancellationToken ct)
                    => throw new System.Net.Http.HttpRequestException(
                        "Google API returned 400: {\"error\":{\"status\":\"INVALID_ARGUMENT\"}}");
            }
        }

        private sealed class WorkingEmbeddings : IEmbeddingProviderFactory
        {
            public IEmbeddingProvider Create(PluginConfiguration config) => new Embedder();

            public IEmbeddingProvider Create(EmbeddingProfile profile) => new Embedder();

            private sealed class Embedder : IEmbeddingProvider
            {
                public string ModelId => "stub-embed";

                public int Dimensions => 0;

                public Task<EmbeddingResult> EmbedAsync(
                    IReadOnlyList<string> texts, EmbeddingPurpose purpose, CancellationToken ct)
                    => Task.FromResult(new EmbeddingResult(
                        texts.Select(ConceptEmbedder.Embed).ToList(), texts.Count));
            }
        }

        private sealed class RecordingQueryLog : IQueryLogStore
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

        private sealed class NullSpendStore : ISpendStore
        {
            public void Record(SpendKind kind, decimal amount, string? userId)
            {
            }

            public decimal QuerySpendThisMonth() => 0m;

            public decimal EnrichmentSpendThisMonth() => 0m;

            public int PaidQueriesInLastHour(string? userId) => 0;
        }

        private sealed class StubIndexStore : IIndexStore
        {
            private readonly ConciergeIndex _index;

            public StubIndexStore(ConciergeIndex index) => _index = index;

            public Task<ConciergeIndex?> LoadAsync(EmbeddingProfile profile, CancellationToken ct)
                => Task.FromResult<ConciergeIndex?>(_index);

            public Task SaveAsync(
                IndexState state, IReadOnlyList<ItemDocument> documents,
                IReadOnlyList<VectorRowSource> rows, IReadOnlyList<float[]> vectors, CancellationToken ct)
                => Task.CompletedTask;

            public Task<IReadOnlyDictionary<Guid, StoredEnrichment>> LoadEnrichmentAsync(CancellationToken ct)
                => Task.FromResult<IReadOnlyDictionary<Guid, StoredEnrichment>>(
                    new Dictionary<Guid, StoredEnrichment>());

            public Task SaveEnrichmentAsync(IReadOnlyCollection<StoredEnrichment> e, CancellationToken ct)
                => Task.CompletedTask;

            public Task<bool> ForgetEnrichmentAsync(Guid itemId, CancellationToken ct)
                => Task.FromResult(false);

            public Task<IndexState?> LoadStateAsync(CancellationToken ct) => Task.FromResult<IndexState?>(null);

            public Task DeleteAsync(CancellationToken ct) => Task.CompletedTask;
        }

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
            ModelProfiles = [new ModelProfile { Id = "m", Name = "Broken", Model = "broken-model" }],
            DefaultModelProfileId = "m",
            EnablePlanPass = true,
            EnableRerankPass = true,
            MaxResults = 10,
        };

        private static (SearchService Service, RecordingQueryLog Log) Build()
        {
            var log = new RecordingQueryLog();
            var service = new SearchService(
                new StubIndexStore(BuildIndex()),
                new WorkingEmbeddings(),
                new RefusingLlmFactory(),
                log,
                new NullSpendStore(),
                new QuoteIndexProvider(new EmptyQuoteStore(), NullLogger<QuoteIndexProvider>.Instance),
                NullLogger<SearchService>.Instance);

            return (service, log);
        }

        [Fact]
        public async Task WhenTheModelRefuses_TheAnswerSaysSoInsteadOfLookingNormal()
        {
            var (service, _) = Build();

            var result = await service.SearchAsync(
                "something dark and twisted", null, Config(), CancellationToken.None);

            // Still answers — degrading to the free half is the correct behaviour.
            Assert.NotEmpty(result.Hits);

            // But it no longer pretends nothing happened.
            Assert.NotNull(result.Degraded);
            Assert.Contains("not answering", result.Degraded, StringComparison.Ordinal);
        }

        [Fact]
        public async Task TheProvidersOwnWordingSurvives_BecauseItNamesTheSettingToChange()
        {
            var (service, _) = Build();

            var result = await service.SearchAsync(
                "something dark and twisted", null, Config(), CancellationToken.None);

            // "Google API returned 400" is the part that tells somebody which profile
            // is wrong. A generic "search degraded" would not.
            Assert.Contains("400", result.Degraded!, StringComparison.Ordinal);

            // The stack trace is not part of that.
            Assert.DoesNotContain("   at ", result.Degraded!, StringComparison.Ordinal);
            Assert.True(result.Degraded!.Length <= 200, $"too long for a status strip: {result.Degraded.Length}");
        }

        [Fact]
        public async Task TheQueryLogKeepsTheReason_SoAnOutageIsVisibleAfterTheFact()
        {
            // The whole outage was three hours of logged searches that each recorded
            // no call, no error and $0.00. Reading them back proved nothing.
            var (service, log) = Build();

            await service.SearchAsync("something dark and twisted", null, Config(), CancellationToken.None);

            var record = Assert.Single(log.Records);
            Assert.NotNull(record.Degraded);
            Assert.Contains("not answering", record.Degraded!, StringComparison.Ordinal);
        }
    }
}
