using System;
using System.IO;
using System.Linq;
using Jellyfin.Plugin.Concierge.Services.Indexing;
using Xunit;

namespace Jellyfin.Plugin.Concierge.Tests
{
    public class ConfigurationPageIndexTests
    {
        private static readonly string Page = ReadPage();

        [Fact]
        public void IndexTabOffersAnExplicitCostWarnedRegeneration()
        {
            Assert.Contains("id=\"ConciergeRegenerateIndex\"", Page, StringComparison.Ordinal);
            Assert.Contains("This can cost as much as the first build", Page, StringComparison.Ordinal);
            Assert.Contains("window.confirm(warning)", Page, StringComparison.Ordinal);
            Assert.Contains("Concierge/Index/Regenerate", Page, StringComparison.Ordinal);
        }

        [Fact]
        public void BuildProgressLivesBesideTheIndexControls()
        {
            var heading = Page.IndexOf("<h3>Index builds</h3>", StringComparison.Ordinal);
            var section = Page.LastIndexOf(
                "data-concierge-tab=\"index\"",
                heading,
                StringComparison.Ordinal);

            Assert.True(heading > 0);
            Assert.True(section > 0);
        }

        [Fact]
        public void ScheduledTaskKeyHasOneSharedDefinition()
        {
            Assert.Equal("ConciergeIndexBuild", IndexBuildTask.TaskKey);
            Assert.Equal("ConciergeEnrichmentBank", EnrichmentBankTask.TaskKey);
        }

        /// <summary>
        /// A setting is only real once it is on the page, read into the form, and
        /// written back out.
        /// </summary>
        /// <remarks>
        /// Stored configuration beats code defaults on this plugin: changing a default
        /// in <c>PluginConfiguration</c> does nothing to a live install, because the
        /// saved XML is what the server reads. So a field with an input but no save
        /// line is not a small bug — it is a setting that silently cannot be changed,
        /// and it looks completely normal in the browser.
        /// </remarks>
        [Theory]
        [InlineData("EnrichmentConcurrency")]
        [InlineData("EnrichmentBatchSize")]
        [InlineData("EmbeddingBatchSize")]
        [InlineData("MaxOutputTokens")]
        public void ASettingOnThePage_IsBothLoadedAndSaved(string id)
        {
            Assert.Contains($"id=\"{id}\"", Page, StringComparison.Ordinal);
            Assert.Contains($"page.querySelector('#{id}').value = config.{id}", Page, StringComparison.Ordinal);
            Assert.Contains($"config.{id} = parseInt(page.querySelector('#{id}').value", Page, StringComparison.Ordinal);
        }

        private static string ReadPage()
        {
            var assembly = typeof(Plugin).Assembly;
            var name = assembly.GetManifestResourceNames()
                .Single(n => n.EndsWith("configPage.html", StringComparison.Ordinal));

            using var stream = assembly.GetManifestResourceStream(name)!;
            using var reader = new StreamReader(stream);
            return reader.ReadToEnd();
        }
    }
}
