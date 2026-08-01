using System.IO;
using System.Xml.Serialization;
using Jellyfin.Plugin.Concierge.Configuration;
using Jellyfin.Plugin.Concierge.Core.Llm;
using Xunit;

namespace Jellyfin.Plugin.Concierge.Tests
{
    /// <summary>
    /// The other half of the phase-0 done-when: a profile survives the actual
    /// persistence mechanism, not just <c>Normalize</c>.
    /// </summary>
    /// <remarks>
    /// Jellyfin writes plugin configuration with <see cref="XmlSerializer"/>, which
    /// is quietly demanding — it needs public mutable properties and a parameterless
    /// constructor, and it <em>silently drops</em> anything it cannot map. A field
    /// that fails to round-trip here does not throw on the server; it comes back
    /// empty on the next restart, which for an API key means the plugin stops
    /// working for reasons nothing in the log explains.
    /// </remarks>
    public class ConfigurationRoundTripTests
    {
        private static PluginConfiguration RoundTrip(PluginConfiguration config)
        {
            var serializer = new XmlSerializer(typeof(PluginConfiguration));
            using var buffer = new MemoryStream();
            serializer.Serialize(buffer, config);
            buffer.Position = 0;
            return (PluginConfiguration)serializer.Deserialize(buffer)!;
        }

        [Fact]
        public void ModelProfile_SurvivesXmlSerialization()
        {
            var config = new PluginConfiguration
            {
                ModelProfiles =
                [
                    new ModelProfile
                    {
                        Id = "abc123",
                        Name = "Sonnet (re-rank)",
                        Provider = LlmProviderKind.Anthropic,
                        Model = "claude-sonnet-5",
                        ApiKey = "sk-ant-secret",
                        BaseUrl = "https://proxy.example/v1",
                        Thinking = ThinkingMode.On,
                        InputCostPerMillion = 3m,
                        CachedInputCostPerMillion = 0.3m,
                        OutputCostPerMillion = 15m,
                    },
                ],
                DefaultModelProfileId = "abc123",
                RerankModelProfileId = "abc123",
            };

            var restored = RoundTrip(config);
            var profile = Assert.Single(restored.ModelProfiles);

            Assert.Equal("abc123", profile.Id);
            Assert.Equal("Sonnet (re-rank)", profile.Name);
            Assert.Equal(LlmProviderKind.Anthropic, profile.Provider);
            Assert.Equal("claude-sonnet-5", profile.Model);
            Assert.Equal("sk-ant-secret", profile.ApiKey);
            Assert.Equal("https://proxy.example/v1", profile.BaseUrl);
            Assert.Equal(ThinkingMode.On, profile.Thinking);
            Assert.Equal(3m, profile.InputCostPerMillion);
            Assert.Equal(0.3m, profile.CachedInputCostPerMillion);
            Assert.Equal(15m, profile.OutputCostPerMillion);
            Assert.Equal("abc123", restored.DefaultModelProfileId);
            Assert.Equal("abc123", restored.RerankModelProfileId);
        }

        [Fact]
        public void EmbeddingProfile_SurvivesXmlSerialization()
        {
            var config = new PluginConfiguration
            {
                EmbeddingProfiles =
                [
                    new EmbeddingProfile
                    {
                        Id = "emb1",
                        Name = "Local bge-m3",
                        Provider = EmbeddingProviderKind.OpenAiCompatible,
                        Model = "bge-m3",
                        BaseUrl = "http://localhost:11434/v1",
                        Dimensions = 1024,
                        QueryPrefix = "query: ",
                        DocumentPrefix = "passage: ",
                        InputCostPerMillion = 0.02m,
                    },
                ],
                DefaultEmbeddingProfileId = "emb1",
            };

            var restored = RoundTrip(config);
            var profile = Assert.Single(restored.EmbeddingProfiles);

            Assert.Equal("emb1", profile.Id);
            Assert.Equal(EmbeddingProviderKind.OpenAiCompatible, profile.Provider);
            Assert.Equal("bge-m3", profile.Model);
            Assert.Equal("http://localhost:11434/v1", profile.BaseUrl);
            Assert.Equal(1024, profile.Dimensions);
            Assert.Equal("query: ", profile.QueryPrefix);
            Assert.Equal("passage: ", profile.DocumentPrefix);
            Assert.Equal(0.02m, profile.InputCostPerMillion);
        }

        [Fact]
        public void SavedConfiguration_RoundTripsThenNormalizesUnchanged()
        {
            // Serialize, restore, normalize — the exact sequence a server restart
            // puts a saved profile through. Nothing may be repaired along the way.
            var config = new PluginConfiguration
            {
                ModelProfiles = [new ModelProfile { Id = "a", Name = "A", Model = "m" }],
                DefaultModelProfileId = "a",
                EmbeddingProfiles = [new EmbeddingProfile { Id = "e", Name = "E", Model = "text-embedding-3-small" }],
                DefaultEmbeddingProfileId = "e",
            };

            var restored = RoundTrip(config);

            Assert.False(ModelProfiles.Normalize(restored).Changed);
            Assert.False(EmbeddingProfiles.Normalize(restored).Changed);
        }

        [Fact]
        public void PerPassAssignments_DefaultToBlankMeaningTheDefaultProfile()
        {
            var restored = RoundTrip(new PluginConfiguration());

            Assert.Equal(string.Empty, restored.PlanModelProfileId);
            Assert.Equal(string.Empty, restored.RerankModelProfileId);
            Assert.Equal(string.Empty, restored.EnrichmentModelProfileId);
            Assert.Equal(string.Empty, restored.EmbeddingProfileId);
        }

        [Fact]
        public void Thinking_IsOffByDefault()
        {
            // Opposite of Curator's choice, and deliberate: two of the three paid
            // passes sit inside a search's latency budget.
            Assert.False(new PluginConfiguration().EnableThinking);
        }
    }
}
