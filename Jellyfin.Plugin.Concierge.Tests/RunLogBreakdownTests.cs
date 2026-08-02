using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Concierge.Configuration;
using Jellyfin.Plugin.Concierge.Services.Llm;
using Jellyfin.Plugin.Concierge.Services.Runs;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Jellyfin.Plugin.Concierge.Tests
{
    /// <summary>
    /// The counters the live progress panel reads.
    /// </summary>
    /// <remarks>
    /// Step detail keys are lifted into headline counters by name, silently. A step
    /// that sends "items" meaning "batch size" rewrites the library-wide count and
    /// nothing complains — which is exactly what happened: a build showed "0 enriched
    /// of 42", then "0 enriched of 10" from the first batch onward, and the enriched
    /// figure never moved because no step reported one until the run ended.
    /// </remarks>
    public class RunProgressCountersTests : IDisposable
    {
        private readonly string _root = Path.Combine(
            Path.GetTempPath(), "concierge-progress-" + Guid.NewGuid().ToString("N")[..8]);

        private static readonly Dictionary<string, object?> NoSettings = [];

        private IIndexRunLogStore? _store;

        private IIndexRunLog Begin()
        {
            _store = new IndexRunLogStore(new StubPaths(_root), NullLogger<IndexRunLogStore>.Instance);
            return _store.Begin("scheduled", NoSettings);
        }

        // From memory, deliberately: this is what the live panel polls, and steps are
        // only flushed to disk every few calls. Reading the file would test the
        // flush interval rather than the counters.
        private IndexRunSummary Current() => _store!.Current()!;

        [Fact]
        public void ABatchStepDoesNotRewriteTheLibraryWideCount()
        {
            var log = Begin();

            log.Step("library.scanned", "42 items", new Dictionary<string, object?> { ["items"] = 42 });
            log.Step("enrichment.planned", "42 to do", new Dictionary<string, object?> { ["stale"] = 42 });

            // A batch of ten out of forty-two. The library is still forty-two.
            log.Step("enrichment.batch", "batch 1", new Dictionary<string, object?>
            {
                ["batch"] = 1,
                ["batchSize"] = 10,
                ["enriched"] = 10,
            });

            var run = Current();

            Assert.Equal(42, run.ItemsIndexed);
            Assert.Equal(42, run.ItemsPlanned);
            Assert.Equal(10, run.ItemsEnriched);
        }

        [Fact]
        public void TheEnrichedCountClimbsWhileTheRunIsStillGoing()
        {
            var log = Begin();

            log.Step("enrichment.planned", "42 to do", new Dictionary<string, object?> { ["stale"] = 42 });

            foreach (var done in new[] { 10, 20, 30 })
            {
                log.Step("enrichment.batch", "batch", new Dictionary<string, object?>
                {
                    ["enriched"] = done,
                });
            }

            // Cumulative, not per batch: a panel showing the last batch's count would
            // read as ten enriched no matter how long the build had been running.
            Assert.Equal(30, Current().ItemsEnriched);
        }

        public void Dispose()
        {
            try
            {
                if (Directory.Exists(_root))
                {
                    Directory.Delete(_root, recursive: true);
                }
            }
            catch (IOException)
            {
            }
        }

        private sealed class StubPaths : MediaBrowser.Common.Configuration.IApplicationPaths
        {
            public StubPaths(string root) => DataPath = root;

            public string ProgramDataPath => DataPath;

            public string WebPath => DataPath;

            public string ProgramSystemPath => DataPath;

            public string DataPath { get; }

            public string VirtualDataPath => DataPath;

            public string ImageCachePath => DataPath;

            public string PluginsPath => DataPath;

            public string PluginConfigurationsPath => DataPath;

            public string LogDirectoryPath => DataPath;

            public string ConfigurationDirectoryPath => DataPath;

            public string SystemConfigurationFilePath => DataPath;

            public string CachePath { get; set; } = string.Empty;

            public string TempDirectory => DataPath;

            public string TrickplayPath => DataPath;

            public string BackupPath => DataPath;

            public void MakeSanityCheckOrThrow()
            {
            }

            public void CreateAndCheckMarker(string path, string markerName, bool recursive = false)
            {
            }
        }
    }

    /// <summary>
    /// The tie between an item and the build that wrote it.
    /// </summary>
    /// <remarks>
    /// Recorded on the enrichment store rather than left to the run files, because
    /// those are pruned: after a dozen builds the run that produced an item is gone.
    /// The risky half is backward compatibility — an <c>enrichment.json</c> written
    /// before the tie existed has to keep loading, and has to report honestly that it
    /// does not know rather than naming a run that never wrote it.
    /// </remarks>
    public class EnrichmentProvenanceTests
    {
        private static readonly JsonSerializerOptions Json =
            new() { PropertyNameCaseInsensitive = true };

        [Fact]
        public void AnEntryWrittenBeforeTheTieExistedStillLoads()
        {
            // Exactly the shape already on the owner's server.
            const string Old = """
                {"ItemId":"e910fc14-06cb-2b97-17a4-1c6b70d67265",
                 "SourceHash":"abc123",
                 "Enrichment":{"Premise":"A clerk.","Moments":[],"Themes":["a"],"Asks":["b"],"Spoiler":false},
                 "GeneratedUtc":"2026-08-01T05:59:00Z"}
                """;

            var stored = JsonSerializer.Deserialize<Jellyfin.Plugin.Concierge.Core.Documents.StoredEnrichment>(
                Old, Json);

            Assert.NotNull(stored);
            Assert.Equal("A clerk.", stored!.Enrichment.Premise);

            // Unknown, and shaped so a caller can tell unknown from deleted.
            Assert.Equal(Guid.Empty, stored.RunId);
            Assert.Equal(string.Empty, stored.Model);
            Assert.Equal(0m, stored.CostUsd);
        }

        [Fact]
        public void ANewEntryCarriesTheBuildThatWroteIt()
        {
            var run = Guid.NewGuid();
            var stored = new Jellyfin.Plugin.Concierge.Core.Documents.StoredEnrichment(
                Guid.NewGuid(),
                "hash",
                new Jellyfin.Plugin.Concierge.Core.Documents.Enrichment("p", [], [], ["ask"], false),
                DateTime.UtcNow,
                run,
                "claude-opus-5",
                0.0133m);

            var round = JsonSerializer.Deserialize<Jellyfin.Plugin.Concierge.Core.Documents.StoredEnrichment>(
                JsonSerializer.Serialize(stored), Json);

            Assert.Equal(run, round!.RunId);
            Assert.Equal("claude-opus-5", round.Model);
            Assert.Equal(0.0133m, round.CostUsd);
        }

        /// <summary>
        /// A run's items name themselves, so a build can link to what it produced.
        /// </summary>
        [Fact]
        public void ARunsItemsCarryTheirItemId()
        {
            var id = Guid.NewGuid();
            var record = new RunItemRecord(id, "Hereditary", 2018, 1, "enriched", 180, 3, 5, 8, true, 0.01m);

            Assert.Equal(id, record.ItemId);
        }
    }

    /// <summary>
    /// The run log, against the run that prompted it.
    /// </summary>
    /// <remarks>
    /// A rebuild on 2 Aug cost $0.40 in five minutes and was cancelled after 30 of
    /// 269 items. The log said what it spent; it did not say that the model had
    /// changed from gpt-5.6-luna to claude-opus-5, that the rates had gone from
    /// 0.2/1.2 to 5/25, or that finishing would have been $3.60. Answering that took
    /// reading four files by hand. These tests are that question, asked of the log.
    /// </remarks>
    public sealed class RunLogBreakdownTests : IDisposable
    {
        private readonly string _root = Path.Combine(
            Path.GetTempPath(),
            "concierge-runlog-" + Guid.NewGuid().ToString("N")[..8]);

        private static readonly Dictionary<string, object?> NoSettings = [];

        private IIndexRunLogStore Store()
            => new IndexRunLogStore(new StubPaths(_root), NullLogger<IndexRunLogStore>.Instance);

        private static LlmRequest Request()
            => new("system", "user", string.Empty, 12000, ResponseShape.Enrichment);

        private static LlmResult Result(long input, long output)
            => new("{\"items\":[]}", input, output, false);

        private static RunPricing Opus => new(5m, 25m, 0.5m);

        private static RunPricing Luna => new(0.2m, 1.2m, 0.02m);

        [Fact]
        public async Task ItSaysWhichModelSpentTheMoney()
        {
            var store = Store();
            var log = store.Begin("manual-regeneration", NoSettings);

            log.Step("enrichment.planned", "269 to do", new Dictionary<string, object?> { ["stale"] = 269 });

            for (var batch = 1; batch <= 3; batch++)
            {
                log.LlmCall(
                    "enrichment", batch, 10, TimeSpan.FromSeconds(80), Request(), Result(1990, 4938),
                    "ok", null, "claude-opus-5", "Anthropic", Opus);
            }

            log.Cancel();

            var run = await Read(store);
            var opus = Assert.Single(run.ByModel);

            Assert.Equal("claude-opus-5", opus.Model);
            Assert.Equal("enrichment", opus.Pass);
            Assert.Equal(3, opus.Calls);

            // The two numbers that were the whole explanation, on the record.
            Assert.Equal(5m, opus.InputCostPerMillion);
            Assert.Equal(25m, opus.OutputCostPerMillion);
        }

        /// <summary>
        /// Two models in one run are two rows, not one total.
        /// </summary>
        [Fact]
        public async Task ARunWithTwoModelsIsBrokenDownByModel()
        {
            var store = Store();
            var log = store.Begin("scheduled", NoSettings);

            log.LlmCall(
                "enrichment", 1, 10, TimeSpan.FromSeconds(80), Request(), Result(2000, 5000),
                "ok", null, "claude-opus-5", "Anthropic", Opus);
            log.LlmCall(
                "enrichment", 2, 10, TimeSpan.FromSeconds(8), Request(), Result(2000, 5000),
                "ok", null, "gpt-5.6-luna", "OpenAi", Luna);

            log.Complete();

            var run = await Read(store);

            Assert.Equal(2, run.ByModel.Count);

            // Dearest first: the row that explains the bill is the row you see.
            Assert.Equal("claude-opus-5", run.ByModel[0].Model);
            Assert.True(run.ByModel[0].CostUsd > run.ByModel[1].CostUsd);
        }

        /// <summary>
        /// A cancelled run's own cost is the least interesting number in it.
        /// </summary>
        [Fact]
        public async Task ACancelledRunSaysWhatFinishingWouldHaveCost()
        {
            var store = Store();
            var log = store.Begin("manual-regeneration", NoSettings);

            log.Step("enrichment.planned", "269 to do", new Dictionary<string, object?> { ["stale"] = 269 });

            for (var batch = 1; batch <= 3; batch++)
            {
                log.LlmCall(
                    "enrichment", batch, 10, TimeSpan.FromSeconds(80), Request(), Result(1990, 4938),
                    "ok", null, "claude-opus-5", "Anthropic", Opus);

                for (var i = 0; i < 10; i++)
                {
                    log.ItemEnriched(new RunItemRecord(
                        Guid.NewGuid(), $"Film {batch}-{i}", 2020, batch, "enriched", 180, 3, 4, 8, false, 0.0133m));
                }
            }

            log.Cancel();

            var run = await Read(store);
            var projection = run.Projection;

            Assert.NotNull(projection);
            Assert.Equal(30, projection!.ItemsDone);
            Assert.Equal(239, projection.ItemsRemaining);

            // 30 of 269 done, so finishing is roughly nine times what was spent.
            Assert.True(
                projection.ProjectedTotalCostUsd > projection.CostSoFarUsd * 8m,
                $"projected {projection.ProjectedTotalCostUsd} against spent {projection.CostSoFarUsd}");

            // And a wall-clock figure, because "$3.60" and "36 minutes" are different
            // reasons to stop.
            Assert.True(projection.ProjectedTotalMs > TimeSpan.FromMinutes(30).TotalMilliseconds);
        }

        [Fact]
        public async Task AFinishedRunProjectsNothing()
        {
            var store = Store();
            var log = store.Begin("scheduled", NoSettings);

            log.Step("enrichment.planned", "2 to do", new Dictionary<string, object?> { ["stale"] = 2 });
            log.LlmCall(
                "enrichment", 1, 2, TimeSpan.FromSeconds(8), Request(), Result(100, 200),
                "ok", null, "gpt-5.6-luna", "OpenAi", Luna);

            log.ItemEnriched(new RunItemRecord(
                        Guid.NewGuid(), "A", 1999, 1, "enriched", 120, 2, 3, 6, false, 0.001m));
            log.ItemEnriched(new RunItemRecord(
                        Guid.NewGuid(), "B", 2001, 1, "enriched", 130, 2, 3, 6, false, 0.001m));
            log.Complete();

            var run = await Read(store);

            Assert.Null(run.Projection);
        }

        /// <summary>
        /// What each item got, not just what the batch cost.
        /// </summary>
        /// <remarks>
        /// "Premise: 0 characters, asks: 0" is what a model paid for nothing looks
        /// like, and no aggregate makes it visible.
        /// </remarks>
        [Fact]
        public async Task EachItemSaysWhatCameBackForIt()
        {
            var store = Store();
            var log = store.Begin("scheduled", NoSettings);

            log.LlmCall(
                "enrichment", 1, 2, TimeSpan.FromSeconds(8), Request(), Result(100, 200),
                "ok", null, "gpt-5.6-luna", "OpenAi", Luna);

            log.ItemEnriched(new RunItemRecord(
                        Guid.NewGuid(), "Hereditary", 2018, 1, "enriched", 180, 3, 5, 8, true, 0.002m));
            log.ItemEnriched(new RunItemRecord(
                        Guid.NewGuid(), "Backrooms", 2026, 1, "unknown-to-model", 0, 0, 0, 0, false, 0.002m));
            log.Complete();

            var run = await Read(store);

            Assert.Equal(2, run.Items.Count);

            var good = run.Items.Single(i => i.Title == "Hereditary");
            Assert.Equal("enriched", good.Outcome);
            Assert.Equal(8, good.Asks);
            Assert.True(good.Spoiler);

            var empty = run.Items.Single(i => i.Title == "Backrooms");
            Assert.Equal("unknown-to-model", empty.Outcome);
            Assert.Equal(0, empty.Asks);
            Assert.Equal(0, empty.PremiseChars);

            // Paid for either way — that is the point of recording the share.
            Assert.True(empty.CostUsd > 0m);
        }

        /// <summary>
        /// The breakdown survives a run nobody finished.
        /// </summary>
        /// <remarks>
        /// Written on every flush rather than at the end, because the runs worth
        /// reading are the ones that stopped. A summary computed in a completion
        /// handler is a summary that is absent exactly when it is wanted.
        /// </remarks>
        [Fact]
        public async Task TheBreakdownIsOnDiskBeforeTheRunEnds()
        {
            var store = Store();
            var log = store.Begin("manual-regeneration", NoSettings);

            log.Step("enrichment.planned", "100 to do", new Dictionary<string, object?> { ["stale"] = 100 });

            for (var batch = 1; batch <= 6; batch++)
            {
                log.LlmCall(
                    "enrichment", batch, 10, TimeSpan.FromSeconds(80), Request(), Result(1990, 4938),
                    "ok", null, "claude-opus-5", "Anthropic", Opus);

                for (var i = 0; i < 10; i++)
                {
                    log.ItemEnriched(new RunItemRecord(
                        Guid.NewGuid(), $"F{batch}-{i}", 2020, batch, "enriched", 180, 3, 4, 8, false, 0.013m));
                }
            }

            // No Complete(), no Cancel() — the process was killed.
            var run = await Read(store);

            Assert.NotEmpty(run.ByModel);
            Assert.NotNull(run.Projection);
            Assert.Equal("running", run.Status);
        }

        private async Task<IndexRunDocument> Read(IIndexRunLogStore store)
        {
            // Read the file rather than the object: the question is what a later
            // session finds on disk, which is where every one of these answers was
            // needed from.
            var file = Directory.GetFiles(Path.Combine(_root, "concierge", "runs"), "run_*.json")
                .OrderByDescending(File.GetLastWriteTimeUtc)
                .First();

            await Task.Yield();

            return JsonSerializer.Deserialize<IndexRunDocument>(
                await File.ReadAllTextAsync(file),
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;
        }

        public void Dispose()
        {
            try
            {
                if (Directory.Exists(_root))
                {
                    Directory.Delete(_root, recursive: true);
                }
            }
            catch (IOException)
            {
            }
        }

        private sealed class StubPaths : MediaBrowser.Common.Configuration.IApplicationPaths
        {
            public StubPaths(string root) => DataPath = root;

            public string ProgramDataPath => DataPath;

            public string WebPath => DataPath;

            public string ProgramSystemPath => DataPath;

            public string DataPath { get; }

            public string VirtualDataPath => DataPath;

            public string ImageCachePath => DataPath;

            public string PluginsPath => DataPath;

            public string PluginConfigurationsPath => DataPath;

            public string LogDirectoryPath => DataPath;

            public string ConfigurationDirectoryPath => DataPath;

            public string SystemConfigurationFilePath => DataPath;

            public string CachePath { get; set; } = string.Empty;

            public void MakeSanityCheckOrThrow()
            {
            }

            public void CreateAndCheckMarker(string path, string markerName, bool recursive = false)
            {
            }

            public string TempDirectory => DataPath;

            public string TrickplayPath => DataPath;

            public string BackupPath => DataPath;
        }
    }
}
