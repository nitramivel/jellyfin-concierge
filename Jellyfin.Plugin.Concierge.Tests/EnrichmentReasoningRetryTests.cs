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
    /// A reasoning model that spends its whole output budget thinking must not take
    /// the batch down with it.
    /// </summary>
    /// <remarks>
    /// Measured on the owner's server during a regeneration: batch 6 returned 12,000
    /// output tokens of which 12,000 were reasoning tokens, no JSON at all, after 158
    /// seconds. All ten items in it were dropped. Nineteen of the first 140 items were
    /// lost this way before the run was stopped — and because a regeneration discards
    /// the banked enrichment first, "dropped" there means gone rather than stale.
    /// <para>
    /// Reasoning is billed as output and counted against the same cap as the answer,
    /// so there is nothing stopping a model from consuming the entire budget before it
    /// writes a single character of JSON. Asking again with reasoning turned down is
    /// cheap and recovers the batch.
    /// </para>
    /// </remarks>
    public class EnrichmentReasoningRetryTests
    {
        /// <summary>Burns the whole output cap on reasoning and returns no JSON.</summary>
        private sealed class RunawayProvider : ILlmProvider
        {
            public string ModelId => "runaway-model";

            public int Calls { get; private set; }

            public Task<LlmResult> CompleteAsync(LlmRequest request, CancellationToken cancellationToken)
            {
                Calls++;
                return Task.FromResult(new LlmResult(
                    string.Empty, 1444, 12000, Truncated: true, ThinkingTokens: 12000));
            }
        }

        /// <summary>Answers properly, as the same model does with reasoning turned down.</summary>
        private sealed class AnsweringProvider : ILlmProvider
        {
            private readonly int _itemsPerBatch;

            public AnsweringProvider(int itemsPerBatch) => _itemsPerBatch = itemsPerBatch;

            public string ModelId => "plain-model";

            public int Calls { get; private set; }

            public Task<LlmResult> CompleteAsync(LlmRequest request, CancellationToken cancellationToken)
            {
                Calls++;

                var items = Enumerable.Range(0, _itemsPerBatch).Select(i =>
                    $$"""
                    {"i":{{i}},"known":true,"premise":"p","moments":["m"],
                     "themes":["dark"],"asks":["the one with the thing"],"spoiler":false}
                    """);

                return Task.FromResult(new LlmResult(
                    "{\"items\":[" + string.Join(",", items) + "]}", 1444, 3200, false));
            }
        }

        /// <summary>Hands back a different provider depending on the thinking flag.</summary>
        private sealed class ThinkingAwareFactory : ILlmProviderFactory
        {
            private readonly ILlmProvider _thinking;
            private readonly ILlmProvider _plain;

            public ThinkingAwareFactory(ILlmProvider thinking, ILlmProvider plain)
            {
                _thinking = thinking;
                _plain = plain;
            }

            public ILlmProvider Create(PluginConfiguration config) => _thinking;

            public ILlmProvider Create(ModelProfile profile, bool globalEnableThinking)
                => globalEnableThinking ? _thinking : _plain;
        }

        private sealed class RecordingRunLog : IIndexRunLog
        {
            public Guid RunId { get; } = Guid.NewGuid();

            public List<RunItemRecord> Items { get; } = [];

            public List<(string Title, string Reason)> NotEnriched { get; } = [];

            public List<string> Calls { get; } = [];

            public void Step(string step, string message, IReadOnlyDictionary<string, object?>? detail = null)
            {
            }

            public void Progress(double percent)
            {
            }

            public void LlmCall(
                string pass, int batch, int itemCount, TimeSpan duration, LlmRequest request,
                LlmResult? result, string outcome, string? error, string model, string provider,
                RunPricing pricing)
                => Calls.Add(outcome);

            public void EmbeddingCall(
                int batch, int rowCount, TimeSpan duration, long inputTokens, decimal cost,
                string model, string provider, string? error = null)
            {
            }

            public void ItemEnriched(RunItemRecord item) => Items.Add(item);

            public void ItemNotEnriched(string title, string reason) => NotEnriched.Add((title, reason));

            public void Complete()
            {
            }

            public void Cancel()
            {
            }

            public void Fail(string error)
            {
            }
        }

        private static PluginConfiguration Config(ThinkingMode enrichmentThinking) => new()
        {
            ModelProfiles = [new ModelProfile { Id = "p", Name = "Stub", Model = "stub-model" }],
            DefaultModelProfileId = "p",
            EnrichmentBatchSize = 2,
            MaxOutputTokens = 12000,
            EnableThinking = false,
            EnrichmentThinking = enrichmentThinking,
        };

        private static List<ItemDocument> Documents(int count) =>
            Enumerable.Range(0, count).Select(i => new ItemDocument(
                Guid.NewGuid(), "Movie", $"Film {i}", string.Empty, 1999,
                [], [], [], [], string.Empty, 100, $"Overview {i}")).ToList();

        [Fact]
        public async Task AReasoningRunaway_IsRetriedWithoutThinkingRatherThanLosingTheBatch()
        {
            var runaway = new RunawayProvider();
            var plain = new AnsweringProvider(2);
            var runLog = new RecordingRunLog();

            var result = await new EnrichmentService(
                    new ThinkingAwareFactory(runaway, plain),
                    NullLogger<EnrichmentService>.Instance)
                .EnrichAsync(
                    Documents(2),
                    Config(ThinkingMode.On),
                    runLog,
                    (_, _) => Task.CompletedTask,
                    null,
                    CancellationToken.None);

            // The batch was recovered rather than dropped.
            Assert.Equal(2, result.Enrichment.Count);
            Assert.Empty(runLog.NotEnriched);

            // Once thinking, once not.
            Assert.Equal(1, runaway.Calls);
            Assert.Equal(1, plain.Calls);

            // Both calls are on the record: the wasted one is still billed.
            Assert.Equal(["truncated", "ok"], runLog.Calls);
        }

        [Fact]
        public async Task TheRetriedAnswer_IsStoredAgainstTheModelThatActuallyAnsweredIt()
        {
            var plain = new AnsweringProvider(2);

            var result = await new EnrichmentService(
                    new ThinkingAwareFactory(new RunawayProvider(), plain),
                    NullLogger<EnrichmentService>.Instance)
                .EnrichAsync(
                    Documents(2),
                    Config(ThinkingMode.On),
                    new RecordingRunLog(),
                    (_, _) => Task.CompletedTask,
                    null,
                    CancellationToken.None);

            // Attributing a recovered answer to the runaway model would make the
            // per-model breakdown in the run log a lie.
            Assert.All(result.Enrichment, e => Assert.Equal("plain-model", e.Model));
        }

        [Fact]
        public async Task WithThinkingAlreadyOff_ThereIsNoRetryToMake()
        {
            // Nothing to turn down, so the batch fails as it always did. Retrying an
            // identical request would just pay twice for the same refusal. Both sides
            // of the factory are the failing provider, so a retry would show up as a
            // second call rather than as a rescue.
            var runaway = new RunawayProvider();
            var runLog = new RecordingRunLog();

            var result = await new EnrichmentService(
                    new ThinkingAwareFactory(runaway, runaway),
                    NullLogger<EnrichmentService>.Instance)
                .EnrichAsync(
                    Documents(2),
                    Config(ThinkingMode.Off),
                    runLog,
                    (_, _) => Task.CompletedTask,
                    null,
                    CancellationToken.None);

            Assert.Empty(result.Enrichment);
            Assert.Equal(1, runaway.Calls);
            Assert.Equal(2, runLog.NotEnriched.Count);
            Assert.All(runLog.NotEnriched, n => Assert.Equal("truncated", n.Reason));
        }
    }
}
