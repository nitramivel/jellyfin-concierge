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
    /// Enrichment batches are independent, so they may run several at a time — without
    /// losing, double-counting or interleaving anything.
    /// </summary>
    /// <remarks>
    /// The pass is wall-clock bound on the model and nothing else: measured at 55
    /// seconds per batch of ten, which is eight hours for a 5,272-item library. Nothing
    /// in one batch informs another, so the serial loop was costing hours for no
    /// reason. What it did give away for free was safety — one writer, one counter, no
    /// races — and these tests are what replaces that.
    /// </remarks>
    public class EnrichmentConcurrencyTests
    {
        /// <summary>Answers slowly, and records how many callers were ever inside at once.</summary>
        private sealed class ConcurrencyProbe : ILlmProvider
        {
            private int _inFlight;
            private int _peak;

            public string ModelId => "probe-model";

            public int Peak => Volatile.Read(ref _peak);

            public int Calls;

            public async Task<LlmResult> CompleteAsync(LlmRequest request, CancellationToken cancellationToken)
            {
                Interlocked.Increment(ref Calls);

                var now = Interlocked.Increment(ref _inFlight);
                for (var seen = Volatile.Read(ref _peak); now > seen;)
                {
                    var prior = Interlocked.CompareExchange(ref _peak, now, seen);
                    if (prior == seen)
                    {
                        break;
                    }

                    seen = prior;
                }

                try
                {
                    // Long enough that a serial loop cannot fake overlap.
                    await Task.Delay(40, cancellationToken).ConfigureAwait(false);
                }
                finally
                {
                    Interlocked.Decrement(ref _inFlight);
                }

                var items = Enumerable.Range(0, 2).Select(i =>
                    $$"""
                    {"i":{{i}},"known":true,"premise":"p","moments":["m"],
                     "themes":["dark"],"asks":["the one with the thing"],"spoiler":false}
                    """);

                return new LlmResult("{\"items\":[" + string.Join(",", items) + "]}", 100, 200, false);
            }
        }

        private sealed class StubFactory : ILlmProviderFactory
        {
            private readonly ILlmProvider _provider;

            public StubFactory(ILlmProvider provider) => _provider = provider;

            public ILlmProvider Create(PluginConfiguration config) => _provider;

            public ILlmProvider Create(ModelProfile profile, bool globalEnableThinking) => _provider;
        }

        private sealed class CountingRunLog : IIndexRunLog
        {
            private readonly object _lock = new();

            public Guid RunId { get; } = Guid.NewGuid();

            public List<RunItemRecord> Items { get; } = [];

            public List<int> BatchNumbers { get; } = [];

            public void Step(string step, string message, IReadOnlyDictionary<string, object?>? detail = null)
            {
                lock (_lock)
                {
                    if (step == "enrichment.batch" && detail?.TryGetValue("batch", out var n) == true && n is int b)
                    {
                        BatchNumbers.Add(b);
                    }
                }
            }

            public void Progress(double percent)
            {
            }

            public void LlmCall(
                string pass, int batch, int itemCount, TimeSpan duration, LlmRequest request,
                LlmResult? result, string outcome, string? error, string model, string provider,
                RunPricing pricing)
            {
            }

            public void EmbeddingCall(
                int batch, int rowCount, TimeSpan duration, long inputTokens, decimal cost,
                string model, string provider, string? error = null)
            {
            }

            public void ItemEnriched(RunItemRecord item)
            {
                lock (_lock)
                {
                    Items.Add(item);
                }
            }

            public void ItemNotEnriched(string title, string reason)
            {
            }

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

        private static PluginConfiguration Config(int concurrency) => new()
        {
            ModelProfiles = [new ModelProfile { Id = "p", Name = "Stub", Model = "stub-model" }],
            DefaultModelProfileId = "p",
            EnrichmentBatchSize = 2,
            MaxOutputTokens = 4000,
            EnrichmentConcurrency = concurrency,
        };

        private static List<ItemDocument> Documents(int count) =>
            Enumerable.Range(0, count).Select(i => new ItemDocument(
                Guid.NewGuid(), "Movie", $"Film {i}", string.Empty, 1999,
                [], [], [], [], string.Empty, 100, $"Overview {i}")).ToList();

        private static async Task<(EnrichmentRunResult Run, ConcurrencyProbe Probe, CountingRunLog Log)> RunAsync(
            int items, int concurrency)
        {
            var probe = new ConcurrencyProbe();
            var log = new CountingRunLog();

            var run = await new EnrichmentService(new StubFactory(probe), NullLogger<EnrichmentService>.Instance)
                .EnrichAsync(
                    Documents(items),
                    Config(concurrency),
                    log,
                    (_, _) => Task.CompletedTask,
                    null,
                    CancellationToken.None);

            return (run, probe, log);
        }

        [Fact]
        public async Task TheDefaultOfOne_KeepsThePassStrictlySerial()
        {
            // An existing install must behave exactly as it did before this existed.
            var (run, probe, _) = await RunAsync(20, concurrency: 1);

            Assert.Equal(1, probe.Peak);
            Assert.Equal(20, run.Enrichment.Count);
        }

        [Fact]
        public async Task RaisingIt_ActuallyOverlapsCalls()
        {
            var (run, probe, _) = await RunAsync(40, concurrency: 4);

            Assert.True(probe.Peak > 1, $"expected overlapping calls, peak was {probe.Peak}");
            Assert.True(probe.Peak <= 4, $"exceeded the configured limit: peak was {probe.Peak}");
            Assert.Equal(40, run.Enrichment.Count);
        }

        [Fact]
        public async Task RunningInParallel_LosesAndDuplicatesNothing()
        {
            // The whole risk of the change: five threads folding into one results list
            // and five running counters.
            var documents = Documents(40);
            var probe = new ConcurrencyProbe();
            var log = new CountingRunLog();

            var run = await new EnrichmentService(new StubFactory(probe), NullLogger<EnrichmentService>.Instance)
                .EnrichAsync(
                    documents,
                    Config(concurrency: 5),
                    log,
                    (_, _) => Task.CompletedTask,
                    null,
                    CancellationToken.None);

            // Every item exactly once, and every one of them a document we asked about.
            Assert.Equal(40, run.Enrichment.Count);
            Assert.Equal(40, run.Enrichment.Select(e => e.ItemId).Distinct().Count());
            Assert.All(run.Enrichment, e => Assert.Contains(documents, d => d.ItemId == e.ItemId));

            // 40 items at 2 per batch is 20 batches, each reporting once, numbered 1..20
            // with no repeats — out of order is fine, but a number must mean one batch.
            Assert.Equal(20, probe.Calls);
            Assert.Equal(Enumerable.Range(1, 20), log.BatchNumbers.OrderBy(x => x));
        }

        [Fact]
        public async Task ParallelAndSerial_ProduceTheSameWork()
        {
            var serial = await RunAsync(30, concurrency: 1);
            var parallel = await RunAsync(30, concurrency: 6);

            Assert.Equal(serial.Run.Enrichment.Count, parallel.Run.Enrichment.Count);
            Assert.Equal(serial.Probe.Calls, parallel.Probe.Calls);

            // Same batches sent means the same bill: concurrency buys time, not money.
            Assert.Equal(serial.Run.CostUsd, parallel.Run.CostUsd);
        }
    }
}
