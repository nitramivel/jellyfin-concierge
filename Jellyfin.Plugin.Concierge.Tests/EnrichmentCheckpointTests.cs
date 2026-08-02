using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Concierge.Configuration;
using Jellyfin.Plugin.Concierge.Core.Documents;
using Jellyfin.Plugin.Concierge.Services.Indexing;
using Jellyfin.Plugin.Concierge.Services.Llm;
using Jellyfin.Plugin.Concierge.Services.Runs;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Jellyfin.Plugin.Concierge.Tests
{
    /// <summary>
    /// The guarantee that an interrupted enrichment pass keeps what it paid for.
    /// </summary>
    /// <remarks>
    /// This exists because the first version did not have it. A two-hour pass over a
    /// 5,000-item library saved nothing until the very end, so cancelling it — which
    /// is a completely ordinary thing to do on realising episodes were switched on —
    /// threw away every model call already billed for.
    /// </remarks>
    public class EnrichmentCheckpointTests
    {
        /// <summary>Returns a canned enrichment answer, and can fail or cancel on cue.</summary>
        private sealed class StubProvider : ILlmProvider
        {
            private readonly int _itemsPerBatch;
            private readonly Func<int, Action?>? _onCall;

            public StubProvider(int itemsPerBatch, Func<int, Action?>? onCall = null)
            {
                _itemsPerBatch = itemsPerBatch;
                _onCall = onCall;
            }

            public string ModelId => "stub-model";

            public int Calls { get; private set; }

            public Task<LlmResult> CompleteAsync(LlmRequest request, CancellationToken cancellationToken)
            {
                Calls++;

                var effect = _onCall?.Invoke(Calls);
                effect?.Invoke();

                var items = Enumerable.Range(0, _itemsPerBatch).Select(i =>
                    $$"""
                    {"i":{{i}},"known":true,"premise":"p","moments":["m"],
                     "themes":["dark"],"asks":["the one with the thing"],"spoiler":false}
                    """);

                var body = "{\"items\":[" + string.Join(",", items) + "]}";
                return Task.FromResult(new LlmResult(body, 100, 200, false));
            }
        }

        private sealed class StubFactory : ILlmProviderFactory
        {
            private readonly ILlmProvider _provider;

            public StubFactory(ILlmProvider provider) => _provider = provider;

            public ILlmProvider Create(PluginConfiguration config) => _provider;

            public ILlmProvider Create(ModelProfile profile, bool globalEnableThinking) => _provider;
        }

        /// <summary>Captures everything the pass reports, for assertions.</summary>
        private sealed class RecordingRunLog : IIndexRunLog
        {
            public Guid RunId { get; } = Guid.NewGuid();

            public List<string> Steps { get; } = [];

            public List<RunItemRecord> Items { get; } = [];

            public List<(string Pass, string Outcome, string? Error)> Calls { get; } = [];

            public List<(string Title, string Reason)> NotEnriched { get; } = [];

            public string? Terminal { get; private set; }

            public void Step(string step, string message, IReadOnlyDictionary<string, object?>? detail = null)
                => Steps.Add(step);

            public void Progress(double percent)
            {
            }

            public void LlmCall(
                string pass, int batch, int itemCount, TimeSpan duration, LlmRequest request,
                LlmResult? result, string outcome, string? error, string model, string provider,
                RunPricing pricing)
                => Calls.Add((pass, outcome, error));

            public void EmbeddingCall(
                int batch, int rowCount, TimeSpan duration, long inputTokens, decimal cost,
                string model, string provider, string? error = null)
            {
            }

            public void ItemEnriched(RunItemRecord item) => Items.Add(item);

            public void ItemNotEnriched(string title, string reason) => NotEnriched.Add((title, reason));

            public void Complete() => Terminal = "completed";

            public void Cancel() => Terminal = "cancelled";

            public void Fail(string error) => Terminal = "failed";
        }

        private static PluginConfiguration Config(int batchSize = 2) => new()
        {
            ModelProfiles = [new ModelProfile { Id = "p", Name = "Stub", Model = "stub-model" }],
            DefaultModelProfileId = "p",
            EnrichmentBatchSize = batchSize,
            MaxOutputTokens = 4000,
        };

        private static List<ItemDocument> Documents(int count) =>
            Enumerable.Range(0, count).Select(i => new ItemDocument(
                Guid.NewGuid(), "Movie", $"Film {i}", string.Empty, 1999,
                [], [], [], [], string.Empty, 100, $"Overview {i}")).ToList();

        private static EnrichmentService Service(ILlmProvider provider)
            => new(new StubFactory(provider), NullLogger<EnrichmentService>.Instance);

        [Fact]
        public async Task Checkpoints_DuringTheRun_NotOnlyAtTheEnd()
        {
            // 12 items at 2 per batch is 6 batches, so with a checkpoint every 5 there
            // is one mid-run and one at the end.
            var documents = Documents(12);
            var checkpoints = new List<int>();

            var result = await Service(new StubProvider(2)).EnrichAsync(
                documents,
                Config(),
                new RecordingRunLog(),
                (saved, _) =>
                {
                    checkpoints.Add(saved.Count);
                    return Task.CompletedTask;
                },
                null,
                CancellationToken.None);

            Assert.True(checkpoints.Count >= 2, $"expected a mid-run checkpoint, got {checkpoints.Count}");
            Assert.Equal(12, checkpoints[^1]);
            Assert.Equal(12, result.Enrichment.Count);
        }

        [Fact]
        public async Task Cancelling_KeepsEverythingAlreadyPaidFor()
        {
            // The case that motivated all of this: stop the pass part-way and the
            // completed batches must survive.
            var documents = Documents(40);
            using var cts = new CancellationTokenSource();
            var checkpoints = new List<int>();

            // Cancel once six batches (twelve items) have been billed for.
            var provider = new StubProvider(2, call => call == 6 ? cts.Cancel : null);

            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Service(provider).EnrichAsync(
                documents,
                Config(),
                new RecordingRunLog(),
                (saved, _) =>
                {
                    checkpoints.Add(saved.Count);
                    return Task.CompletedTask;
                },
                null,
                cts.Token));

            Assert.NotEmpty(checkpoints);
            Assert.Equal(12, checkpoints[^1]);
        }

        [Fact]
        public async Task Cancelling_CheckpointsEvenWithATokenAlreadyCancelled()
        {
            // The final save must not itself be cancelled by the token that stopped
            // the run — otherwise the rescue attempt fails exactly when it is needed.
            var documents = Documents(20);
            using var cts = new CancellationTokenSource();
            var saved = 0;

            var provider = new StubProvider(2, call => call == 3 ? cts.Cancel : null);

            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Service(provider).EnrichAsync(
                documents,
                Config(),
                new RecordingRunLog(),
                (results, ct) =>
                {
                    ct.ThrowIfCancellationRequested();
                    saved = results.Count;
                    return Task.CompletedTask;
                },
                null,
                cts.Token));

            Assert.Equal(6, saved);
        }

        [Fact]
        public async Task AFailedBatch_IsRecordedPerItemAndDoesNotSinkTheRun()
        {
            var documents = Documents(8);
            var runLog = new RecordingRunLog();

            // The third call throws; the rest succeed.
            var provider = new StubProvider(
                2,
                call => call == 3 ? () => throw new InvalidOperationException("provider exploded") : null);

            var result = await Service(provider).EnrichAsync(
                documents, Config(), runLog, (_, _) => Task.CompletedTask, null, CancellationToken.None);

            Assert.Equal(2, result.Failed);
            Assert.Equal(6, result.Known);
            Assert.Equal(6, result.Enrichment.Count);

            Assert.Contains(runLog.Calls, c => c.Outcome == "error");
            Assert.Equal(2, runLog.NotEnriched.Count(n => n.Reason == "batch-failed"));
        }

        [Fact]
        public async Task AnItemTheModelDoesNotKnow_IsStoredSoItIsNeverAskedAgain()
        {
            // Hard rule 14. "I don't know this one" is a real answer, and storing it
            // is what stops the next run paying to ask the same question.
            var documents = Documents(2);
            var runLog = new RecordingRunLog();

            var provider = new StubProviderReturning(
                "{\"items\":["
                + "{\"i\":0,\"known\":false,\"premise\":\"\",\"moments\":[],\"themes\":[],\"asks\":[],\"spoiler\":false},"
                + "{\"i\":1,\"known\":true,\"premise\":\"p\",\"moments\":[],\"themes\":[\"dark\"],\"asks\":[\"a\"],\"spoiler\":false}"
                + "]}");

            var result = await Service(provider).EnrichAsync(
                documents, Config(), runLog, (_, _) => Task.CompletedTask, null, CancellationToken.None);

            Assert.Equal(1, result.Unknown);
            Assert.Equal(1, result.Known);

            // Both are stored — the unknown one as an empty enrichment.
            Assert.Equal(2, result.Enrichment.Count);
            Assert.Contains(result.Enrichment, e => e.Enrichment.IsEmpty);
            Assert.Contains(runLog.NotEnriched, n => n.Reason == "unknown-to-model");
        }

        [Fact]
        public async Task AnItemTheModelSilentlySkips_IsLeftUnstoredSoTheNextRunRetries()
        {
            // Different from declining: nothing was said about this item at all, so
            // recording an empty answer would put words in the model's mouth and stop
            // it ever being asked again.
            var documents = Documents(2);
            var runLog = new RecordingRunLog();

            var provider = new StubProviderReturning(
                "{\"items\":[{\"i\":0,\"known\":true,\"premise\":\"p\",\"moments\":[],"
                + "\"themes\":[\"t\"],\"asks\":[\"a\"],\"spoiler\":false}]}");

            var result = await Service(provider).EnrichAsync(
                documents, Config(), runLog, (_, _) => Task.CompletedTask, null, CancellationToken.None);

            Assert.Equal(1, result.Failed);
            Assert.Single(result.Enrichment);
            Assert.Contains(runLog.NotEnriched, n => n.Reason == "omitted");
        }

        [Fact]
        public async Task UnparseableOutput_IsRecordedWithoutLosingTheRun()
        {
            var runLog = new RecordingRunLog();
            var provider = new StubProviderReturning("I'm afraid I can't help with that.");

            var result = await Service(provider).EnrichAsync(
                Documents(2), Config(), runLog, (_, _) => Task.CompletedTask, null, CancellationToken.None);

            Assert.Equal(2, result.Failed);
            Assert.Empty(result.Enrichment);
            Assert.Contains(runLog.Calls, c => c.Outcome == "unparseable");
        }

        /// <summary>A provider that always returns one fixed body.</summary>
        private sealed class StubProviderReturning : ILlmProvider
        {
            private readonly string _body;

            public StubProviderReturning(string body) => _body = body;

            public string ModelId => "stub-model";

            public Task<LlmResult> CompleteAsync(LlmRequest request, CancellationToken cancellationToken)
                => Task.FromResult(new LlmResult(_body, 10, 20, false));
        }
    }
}
