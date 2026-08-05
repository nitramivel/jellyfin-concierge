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
    /// A search whose model fails tries the fallback before settling for the fused
    /// order.
    /// </summary>
    /// <remarks>
    /// Four failure modes have been seen on this install and none of them are exotic:
    /// a provider rejecting the request (HTTP 400 on a thinking budget it would not
    /// take), one that could not be reached (404 from a mistyped base URL), one that
    /// answered in a shape the parser could not read (a null where an object belonged),
    /// and one that never answered at all. Each ended the same way — the fused order,
    /// which is a decent answer and not the one that was paid for.
    /// <para>
    /// The fallback is tried once, only for a genuine failure. A caller who has gone
    /// away is not a failure, and a budget stop or rate limit never reaches this code
    /// at all — those are decided before a provider exists, and asking another model
    /// would spend money the owner has already said not to spend.
    /// </para>
    /// </remarks>
    public class FallbackModelTests
    {
        private sealed class ScriptedFactory : ILlmProviderFactory
        {
            private readonly Dictionary<string, Func<LlmResult>> _byModel;

            public ScriptedFactory(Dictionary<string, Func<LlmResult>> byModel) => _byModel = byModel;

            public List<string> Called { get; } = [];

            public ILlmProvider Create(PluginConfiguration config)
                => throw new InvalidOperationException("passes resolve a profile explicitly");

            public ILlmProvider Create(ModelProfile profile, bool globalEnableThinking)
                => new Scripted(profile.Model, _byModel[profile.Model], Called);

            private sealed class Scripted : ILlmProvider
            {
                private readonly Func<LlmResult> _answer;
                private readonly List<string> _called;

                public Scripted(string model, Func<LlmResult> answer, List<string> called)
                {
                    ModelId = model;
                    _answer = answer;
                    _called = called;
                }

                public string ModelId { get; }

                public Task<LlmResult> CompleteAsync(LlmRequest request, CancellationToken ct)
                {
                    lock (_called)
                    {
                        _called.Add(ModelId);
                    }

                    return Task.FromResult(_answer());
                }
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
                => Task.FromResult<IReadOnlyList<QueryRunRecord>>(Records);
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

            public Task<bool> ForgetEnrichmentAsync(Guid itemId, CancellationToken ct) => Task.FromResult(false);

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

        private static PluginConfiguration Config(string? fallback) => new()
        {
            EmbeddingProfiles = [new EmbeddingProfile { Id = "e", Name = "Stub", Model = "stub" }],
            DefaultEmbeddingProfileId = "e",
            ModelProfiles =
            [
                new ModelProfile { Id = "primary", Name = "Primary", Model = "broken-model" },
                new ModelProfile { Id = "spare", Name = "Spare", Model = "spare-model" },
            ],
            DefaultModelProfileId = "primary",
            FallbackModelProfileId = fallback ?? string.Empty,
            EnableRerankPass = true,
            EnablePlanPass = false,
            MaxResults = 10,
        };

        private static LlmResult GoodRerank()
            => new("""{"order":[{"i":0,"why":"ok"}]}""", 100, 20, false);

        private static (SearchService Service, ScriptedFactory Factory) Build(
            string? fallback, Func<LlmResult> primary, Func<LlmResult>? spare = null)
        {
            var factory = new ScriptedFactory(new Dictionary<string, Func<LlmResult>>
            {
                ["broken-model"] = primary,
                ["spare-model"] = spare ?? GoodRerank,
            });

            var service = new SearchService(
                new StubIndexStore(BuildIndex()),
                new WorkingEmbeddings(),
                factory,
                new NullQueryLog(),
                new NullSpendStore(),
                new QuoteIndexProvider(new EmptyQuoteStore(), NullLogger<QuoteIndexProvider>.Instance),
                NullLogger<SearchService>.Instance);

            return (service, factory);
        }

        [Fact]
        public async Task WhenThePrimaryFails_TheFallbackAnswers()
        {
            var (service, factory) = Build(
                "spare",
                () => throw new System.Net.Http.HttpRequestException("Google API returned 400"));

            var result = await service.SearchAsync(
                "something dark and twisted", null, Config("spare"), CancellationToken.None);

            Assert.Equal(["broken-model", "spare-model"], factory.Called);
            Assert.NotEmpty(result.Hits);

            // Said out loud: you are paying a model you did not choose.
            Assert.NotNull(result.Degraded);
            Assert.Contains("spare-model", result.Degraded, StringComparison.Ordinal);
        }

        [Fact]
        public async Task WhenThePrimaryWorks_TheFallbackIsNeverCalled()
        {
            var (service, factory) = Build("spare", GoodRerank);

            var result = await service.SearchAsync(
                "something dark and twisted", null, Config("spare"), CancellationToken.None);

            Assert.Equal(["broken-model"], factory.Called);

            // The plan pass is off in this configuration and says so; what must not
            // appear is any mention of a fallback that was never needed.
            Assert.DoesNotContain("spare-model", result.Degraded ?? string.Empty, StringComparison.Ordinal);
        }

        [Fact]
        public async Task WithNoFallbackConfigured_ItDegradesExactlyAsBefore()
        {
            var (service, factory) = Build(
                null,
                () => throw new System.Net.Http.HttpRequestException("Google API returned 400"));

            var result = await service.SearchAsync(
                "something dark and twisted", null, Config(null), CancellationToken.None);

            Assert.Equal(["broken-model"], factory.Called);
            Assert.NotEmpty(result.Hits);
            Assert.NotNull(result.Degraded);
        }

        [Fact]
        public async Task WhenBothFail_TheSearchStillAnswers()
        {
            // Hard rule 4. Two failures is still not an error page.
            var (service, factory) = Build(
                "spare",
                () => throw new System.Net.Http.HttpRequestException("primary down"),
                () => throw new System.Net.Http.HttpRequestException("spare down"));

            var result = await service.SearchAsync(
                "something dark and twisted", null, Config("spare"), CancellationToken.None);

            Assert.Equal(["broken-model", "spare-model"], factory.Called);
            Assert.NotEmpty(result.Hits);
            Assert.NotNull(result.Degraded);
        }

        [Fact]
        public async Task AFallbackPointingAtThePrimary_IsNotTried()
        {
            // Asking the model that just failed is a second identical failure at the
            // same price.
            var config = Config("primary");
            var (service, factory) = Build(
                "primary",
                () => throw new System.Net.Http.HttpRequestException("down"));

            await service.SearchAsync("something dark and twisted", null, config, CancellationToken.None);

            Assert.Equal(["broken-model"], factory.Called);
        }
    }
}
