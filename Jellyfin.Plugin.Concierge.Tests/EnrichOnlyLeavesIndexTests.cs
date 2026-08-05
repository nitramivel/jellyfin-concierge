using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Concierge.Configuration;
using Jellyfin.Plugin.Concierge.Core.Documents;
using Jellyfin.Plugin.Concierge.Core.Retrieval;
using Jellyfin.Plugin.Concierge.Services.Embeddings;
using Jellyfin.Plugin.Concierge.Services.Indexing;
using Jellyfin.Plugin.Concierge.Services.Library;
using Jellyfin.Plugin.Concierge.Services.Llm;
using Jellyfin.Plugin.Concierge.Services.Runs;
using MediaBrowser.Controller.Entities;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Jellyfin.Plugin.Concierge.Tests
{
    /// <summary>
    /// An enrichment-only run banks what it paid for and leaves the live index exactly
    /// as it found it.
    /// </summary>
    /// <remarks>
    /// This is what lets the slow, expensive half run on its own schedule. Episode
    /// enrichment is eight hours and 45% unknown-to-model on this library, and while it
    /// was welded to the index build it also pinned the search index for that whole
    /// time — a working day in which no metadata change could reach search.
    /// <para>
    /// The property under test is a negative one: <c>SaveAsync</c> must not be called.
    /// Negatives do not fail loudly when they regress, which is exactly why this is
    /// written down.
    /// </para>
    /// </remarks>
    public class EnrichOnlyLeavesIndexTests
    {
        /// <summary>An empty library. Enough to drive the pass without a real server.</summary>
        private sealed class EmptyScanner : ILibraryScanner
        {
            public LibraryHealth Inspect() => new(0, 0);

            public IReadOnlyList<BaseItem> Scan(bool includeEpisodes) => [];

            public IReadOnlyList<BaseItem> ScanAudio() => [];
        }

        /// <summary>Records which of the two writes happened, and can be held mid-write.</summary>
        private sealed class RecordingStore : IIndexStore
        {
            public int IndexWrites { get; private set; }

            public int EnrichmentWrites { get; private set; }

            /// <summary>Completed once a build has reached the enrichment write.</summary>
            public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

            /// <summary>Set this to let a held build finish.</summary>
            public TaskCompletionSource? Hold { get; set; }

            public Task<ConciergeIndex?> LoadAsync(EmbeddingProfile profile, CancellationToken ct)
                => Task.FromResult<ConciergeIndex?>(null);

            public Task SaveAsync(
                IndexState state,
                IReadOnlyList<ItemDocument> documents,
                IReadOnlyList<VectorRowSource> rows,
                IReadOnlyList<float[]> vectors,
                CancellationToken ct)
            {
                IndexWrites++;
                return Task.CompletedTask;
            }

            public Task<IReadOnlyDictionary<Guid, StoredEnrichment>> LoadEnrichmentAsync(CancellationToken ct)
                => Task.FromResult<IReadOnlyDictionary<Guid, StoredEnrichment>>(
                    new Dictionary<Guid, StoredEnrichment>());

            public async Task SaveEnrichmentAsync(
                IReadOnlyCollection<StoredEnrichment> enrichment, CancellationToken ct)
            {
                EnrichmentWrites++;
                Entered.TrySetResult();

                if (Hold is not null)
                {
                    await Hold.Task.ConfigureAwait(false);
                }
            }

            public Task<bool> ForgetEnrichmentAsync(Guid itemId, CancellationToken ct)
                => Task.FromResult(false);

            public Task<IndexState?> LoadStateAsync(CancellationToken ct)
                => Task.FromResult<IndexState?>(null);

            public Task DeleteAsync(CancellationToken ct) => Task.CompletedTask;
        }

        private sealed class StubEmbedder : IEmbeddingProvider
        {
            public string ModelId => "stub-embed";

            public int Dimensions => 4;

            public Task<EmbeddingResult> EmbedAsync(
                IReadOnlyList<string> texts, EmbeddingPurpose purpose, CancellationToken ct)
                => Task.FromResult(new EmbeddingResult([], 0));
        }

        private sealed class StubEmbeddingFactory : IEmbeddingProviderFactory
        {
            public IEmbeddingProvider Create(PluginConfiguration config) => new StubEmbedder();

            public IEmbeddingProvider Create(EmbeddingProfile profile) => new StubEmbedder();
        }

        private sealed class NullRunLogStore : IIndexRunLogStore
        {
            public IIndexRunLog Begin(string trigger, IReadOnlyDictionary<string, object?> settings)
                => NullIndexRunLog.Instance;

            public IReadOnlyList<IndexRunSummary> List(int limit = 25) => [];

            public IndexRunSummary? Current() => null;

            public string? ReadRaw(Guid runId) => null;
        }

        private sealed class ExplodingLlmFactory : ILlmProviderFactory
        {
            public ILlmProvider Create(PluginConfiguration config)
                => throw new InvalidOperationException("no model call belongs in this test");

            public ILlmProvider Create(ModelProfile profile, bool globalEnableThinking)
                => throw new InvalidOperationException("no model call belongs in this test");
        }

        private static PluginConfiguration Config() => new()
        {
            EmbeddingProfiles = [new EmbeddingProfile { Id = "e", Name = "Stub", Model = "stub-embed" }],
            DefaultEmbeddingProfileId = "e",
            ModelProfiles = [new ModelProfile { Id = "p", Name = "Stub", Model = "stub-model" }],
            DefaultModelProfileId = "p",
            EnableEnrichment = false,
        };

        private static ItemIndexer Indexer(RecordingStore store)
            => new(
                new EmptyScanner(),

                // Never reached: it is only used to read people off a library item, and
                // this scan returns none.
                null!,
                store,
                new EnrichmentService(new ExplodingLlmFactory(), NullLogger<EnrichmentService>.Instance),
                new StubEmbeddingFactory(),
                new NullRunLogStore(),
                NullLogger<ItemIndexer>.Instance);

        [Fact]
        public async Task AnEnrichOnlyRun_NeverWritesTheIndex()
        {
            var store = new RecordingStore();

            var result = await Indexer(store).BuildAsync(
                Config(), "manual", null, CancellationToken.None, regenerate: false, enrichOnly: true);

            Assert.Equal(0, store.IndexWrites);
            Assert.Equal(1, store.EnrichmentWrites);

            // No rows, nothing embedded: the honest report of a run that produced no
            // index rather than one that produced an empty one.
            Assert.Equal(0, result.Rows);
            Assert.Equal(0, result.Embedded);
        }

        [Fact]
        public async Task AnOrdinaryRun_StillWritesTheIndex()
        {
            // The control. Without it, a build that had silently stopped writing
            // altogether would pass the test above.
            var store = new RecordingStore();

            await Indexer(store).BuildAsync(
                Config(), "manual", null, CancellationToken.None, regenerate: false, enrichOnly: false);

            Assert.Equal(1, store.IndexWrites);
            Assert.Equal(1, store.EnrichmentWrites);
        }

        [Fact]
        public async Task ASecondBuildStartedWhileOneIsRunning_IsRefusedRatherThanRacingIt()
        {
            // Two scheduled tasks can now reach BuildAsync, and both write the
            // enrichment store while the ordinary build also rewrites the index and
            // advances the generation. Two at once would publish over each other.
            var store = new RecordingStore { Hold = new(TaskCreationOptions.RunContinuationsAsynchronously) };
            var indexer = Indexer(store);

            var first = indexer.BuildAsync(
                Config(), "manual", null, CancellationToken.None, regenerate: false, enrichOnly: true);

            await store.Entered.Task;

            var second = await indexer.BuildAsync(
                Config(), "enrichment-only", null, CancellationToken.None, regenerate: false, enrichOnly: true);

            Assert.True(second.Skipped, "the second build should have been refused");
            Assert.Equal(Guid.Empty, second.RunId);

            store.Hold.SetResult();
            var firstResult = await first;

            // The one that held the gate ran normally, and only it wrote anything.
            Assert.False(firstResult.Skipped);
            Assert.Equal(1, store.EnrichmentWrites);
        }

        [Fact]
        public async Task TheGateIsReleased_SoTheNextScheduledRunIsNotBlockedForever()
        {
            // A gate that leaks on the way out would turn one build into a plugin that
            // never indexes again until the server restarts.
            var store = new RecordingStore();
            var indexer = Indexer(store);

            await indexer.BuildAsync(
                Config(), "manual", null, CancellationToken.None, regenerate: false, enrichOnly: true);
            var second = await indexer.BuildAsync(
                Config(), "manual", null, CancellationToken.None, regenerate: false, enrichOnly: true);

            Assert.False(second.Skipped);
            Assert.Equal(2, store.EnrichmentWrites);
        }

        [Fact]
        public void TheEnrichmentTask_ShipsWithNoTriggerSoNothingStartsSpending()
        {
            // Registering the task must not be the same thing as scheduling it. This
            // one costs real money on a large library.
            var task = new EnrichmentBankTask(
                Indexer(new RecordingStore()), NullLogger<EnrichmentBankTask>.Instance);

            Assert.Empty(task.GetDefaultTriggers());
            Assert.Equal("ConciergeEnrichmentBank", task.Key);
        }
    }
}
