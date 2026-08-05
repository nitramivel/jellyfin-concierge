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
    /// Forgetting one item drops that item's answer and nothing else.
    /// </summary>
    /// <remarks>
    /// The index build reuses enrichment by source hash, which is exactly what makes an
    /// unchanged nightly run free — and exactly why a bad answer is permanent. Nothing
    /// about the film changes, so nothing ever re-asks. This is how a single item gets
    /// told it is wrong without regenerating the whole library.
    /// <para>
    /// It removes Concierge's own answer, never the library item. The library is
    /// read-only (hard rule 6), and a control whose label says "delete" needs the test
    /// that says what it does not delete.
    /// </para>
    /// </remarks>
    public class ForgetOneItemTests : IDisposable
    {
        private readonly string _root =
            Path.Combine(Path.GetTempPath(), "concierge-forget-" + Guid.NewGuid().ToString("N"));

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

        private IndexStore Store() => new(new StubPaths(_root), NullLogger<IndexStore>.Instance);

        private static StoredEnrichment Entry(Guid id, string premise)
            => new(
                id,
                "hash-" + premise,
                new Enrichment(premise, ["moment"], ["theme"], ["ask"], false),
                DateTime.UtcNow,
                Guid.NewGuid(),
                "model",
                0.004m);

        [Fact]
        public async Task ForgettingOneItem_LeavesEveryOtherAnswerAlone()
        {
            var store = Store();
            var doomed = Guid.NewGuid();
            var keep = Enumerable.Range(0, 9).Select(i => Entry(Guid.NewGuid(), $"keep {i}")).ToList();

            await store.SaveEnrichmentAsync([.. keep, Entry(doomed, "wrong")], CancellationToken.None);
            Assert.Equal(10, (await store.LoadEnrichmentAsync(CancellationToken.None)).Count);

            Assert.True(await store.ForgetEnrichmentAsync(doomed, CancellationToken.None));

            var left = await store.LoadEnrichmentAsync(CancellationToken.None);
            Assert.Equal(9, left.Count);
            Assert.False(left.ContainsKey(doomed));
            Assert.All(keep, e => Assert.True(left.ContainsKey(e.ItemId), $"lost {e.Enrichment.Premise}"));
        }

        [Fact]
        public async Task ForgettingSomethingNotStored_ReportsThatRatherThanPretending()
        {
            var store = Store();
            await store.SaveEnrichmentAsync([Entry(Guid.NewGuid(), "kept")], CancellationToken.None);

            Assert.False(await store.ForgetEnrichmentAsync(Guid.NewGuid(), CancellationToken.None));
            Assert.Single(await store.LoadEnrichmentAsync(CancellationToken.None));
        }

        [Fact]
        public async Task ForgettingWithNothingStoredAtAll_DoesNotFail()
        {
            // A fresh install, or an item opened before the first build.
            Assert.False(await Store().ForgetEnrichmentAsync(Guid.NewGuid(), CancellationToken.None));
        }

        [Fact]
        public async Task AForgottenItem_IsAskedAgainRatherThanReusedByHash()
        {
            // The whole point: the build reuses by source hash, so an item whose file
            // has not changed is never re-asked. Forgetting is what breaks that tie.
            var store = Store();
            var id = Guid.NewGuid();

            await store.SaveEnrichmentAsync([Entry(id, "stale")], CancellationToken.None);
            await store.ForgetEnrichmentAsync(id, CancellationToken.None);

            var left = await store.LoadEnrichmentAsync(CancellationToken.None);

            // No entry means no hash to match, which is what makes the next build treat
            // it as new rather than reusing what it had.
            Assert.False(left.ContainsKey(id));

            // And a later build's answer lands normally.
            await store.SaveEnrichmentAsync([Entry(id, "fresh")], CancellationToken.None);
            Assert.Equal("fresh", (await store.LoadEnrichmentAsync(CancellationToken.None))[id].Enrichment.Premise);
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
