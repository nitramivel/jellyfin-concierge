using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Concierge.Core.Documents;
using Jellyfin.Plugin.Concierge.Services.Indexing;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Jellyfin.Plugin.Concierge.Tests
{
    /// <summary>
    /// The enrichment store must never lose an answer somebody paid for.
    /// </summary>
    /// <remarks>
    /// Written after it did. A regeneration over a 5,272-item library was stopped 14
    /// batches in, and by then the store had gone from 322 entries to 131: every save
    /// wrote the running run's results directly over the file, so each checkpoint
    /// discarded the 191 answers that run had not reached yet.
    /// <para>
    /// It went unnoticed because nothing else on disk holds the same thing.
    /// <c>docs.json</c> deliberately stores documents stripped of enrichment, and the
    /// only reason the themes and asks were recoverable at all is that
    /// <c>rows.json</c> happens to keep them as flattened embedding text. That was
    /// luck. These tests are the design.
    /// </para>
    /// </remarks>
    public class EnrichmentStoreMergeTests : IDisposable
    {
        private readonly string _root =
            Path.Combine(Path.GetTempPath(), "concierge-store-" + Guid.NewGuid().ToString("N"));

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

        private IndexStore Store()
            => new(new StubPaths(_root), NullLogger<IndexStore>.Instance);

        private static StoredEnrichment Entry(Guid id, string premise, string model = "m")
            => new(
                id,
                "hash-" + premise,
                new Enrichment(premise, ["moment"], ["theme"], ["ask"], false),
                DateTime.UtcNow,
                Guid.NewGuid(),
                model,
                0.004m);

        [Fact]
        public async Task SavingOneRunsResults_KeepsEverythingEarlierRunsPaidFor()
        {
            // The exact shape of the loss: a big store, then a partial run.
            var older = Enumerable.Range(0, 20)
                .Select(i => Entry(Guid.NewGuid(), $"old {i}"))
                .ToList();

            var store = Store();
            await store.SaveEnrichmentAsync(older, CancellationToken.None);

            // A regeneration that got through three items before it was stopped.
            var partial = Enumerable.Range(0, 3)
                .Select(i => Entry(Guid.NewGuid(), $"new {i}"))
                .ToList();

            await store.SaveEnrichmentAsync(partial, CancellationToken.None);

            var loaded = await store.LoadEnrichmentAsync(CancellationToken.None);

            Assert.Equal(23, loaded.Count);
            Assert.All(older, e => Assert.True(loaded.ContainsKey(e.ItemId), $"lost {e.Enrichment.Premise}"));
            Assert.All(partial, e => Assert.True(loaded.ContainsKey(e.ItemId)));
        }

        [Fact]
        public async Task ReAskingAnItem_ReplacesItsAnswerRatherThanDuplicatingIt()
        {
            // What a regeneration is actually for. The newer answer must win, and the
            // item must not appear twice.
            var id = Guid.NewGuid();
            var store = Store();

            await store.SaveEnrichmentAsync([Entry(id, "first", "old-model")], CancellationToken.None);
            await store.SaveEnrichmentAsync([Entry(id, "second", "new-model")], CancellationToken.None);

            var loaded = await store.LoadEnrichmentAsync(CancellationToken.None);

            Assert.Single(loaded);
            Assert.Equal("second", loaded[id].Enrichment.Premise);
            Assert.Equal("new-model", loaded[id].Model);
        }

        [Fact]
        public async Task EachCheckpointOfALongRun_LeavesTheStoreStrictlyLarger()
        {
            // Checkpoints arrive as a growing accumulation, and a store that shrinks
            // part-way through a run is the bug this file exists for.
            var store = Store();
            await store.SaveEnrichmentAsync(
                Enumerable.Range(0, 50).Select(i => Entry(Guid.NewGuid(), $"banked {i}")).ToList(),
                CancellationToken.None);

            var counts = new List<int>();
            var accumulated = new List<StoredEnrichment>();

            foreach (var batch in Enumerable.Range(0, 5))
            {
                accumulated.Add(Entry(Guid.NewGuid(), $"run {batch}"));
                await store.SaveEnrichmentAsync(accumulated, CancellationToken.None);
                counts.Add((await store.LoadEnrichmentAsync(CancellationToken.None)).Count);
            }

            Assert.Equal([51, 52, 53, 54, 55], counts);
        }

        [Fact]
        public async Task SavingNothing_DoesNotEmptyTheStore()
        {
            // A run that enriched nothing — everything cached, or every batch failed —
            // must not read as "delete everything".
            var store = Store();
            var banked = Enumerable.Range(0, 6).Select(i => Entry(Guid.NewGuid(), $"kept {i}")).ToList();

            await store.SaveEnrichmentAsync(banked, CancellationToken.None);
            await store.SaveEnrichmentAsync([], CancellationToken.None);

            Assert.Equal(6, (await store.LoadEnrichmentAsync(CancellationToken.None)).Count);
        }

        public void Dispose()
        {
            GC.SuppressFinalize(this);

            if (Directory.Exists(_root))
            {
                Directory.Delete(_root, recursive: true);
            }
        }
    }
}
