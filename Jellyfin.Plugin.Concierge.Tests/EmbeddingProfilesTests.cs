using System;
using System.Linq;
using Jellyfin.Plugin.Concierge.Configuration;
using Jellyfin.Plugin.Concierge.Core.Llm;
using Xunit;

namespace Jellyfin.Plugin.Concierge.Tests
{
    public class EmbeddingProfilesTests
    {
        private static PluginConfiguration ConfigWith(params EmbeddingProfile[] profiles)
            => new() { EmbeddingProfiles = profiles };

        [Fact]
        public void Normalize_BlankIdAndName_AreFilledIn()
        {
            var config = ConfigWith(new EmbeddingProfile
            {
                Provider = EmbeddingProviderKind.OpenAi,
                Model = "text-embedding-3-small",
            });

            var result = EmbeddingProfiles.Normalize(config);

            Assert.NotEmpty(result.Profiles[0].Id);
            Assert.Equal("OpenAi text-embedding-3-small", result.Profiles[0].Name);
            Assert.True(result.Changed);
        }

        [Fact]
        public void Normalize_DanglingDefaultId_FallsBackToTheFirstProfile()
        {
            var config = ConfigWith(new EmbeddingProfile { Id = "real", Model = "m" });
            config.DefaultEmbeddingProfileId = "deleted";

            var result = EmbeddingProfiles.Normalize(config);

            Assert.Equal("real", result.DefaultProfileId);
        }

        [Fact]
        public void Normalize_FullySpecifiedProfile_RoundTripsUnchanged()
        {
            var saved = new EmbeddingProfile
            {
                Id = "9a8b7c6d5e4f3a2b1c0d9e8f7a6b5c4d",
                Name = "Local bge-m3",
                Provider = EmbeddingProviderKind.OpenAiCompatible,
                Model = "bge-m3",
                ApiKey = string.Empty,
                BaseUrl = "http://localhost:11434/v1",
                Dimensions = 1024,
                QueryPrefix = "query: ",
                DocumentPrefix = "passage: ",
                InputCostPerMillion = 0m,
            };

            var config = ConfigWith(saved);
            config.DefaultEmbeddingProfileId = saved.Id;

            var result = EmbeddingProfiles.Normalize(config);

            Assert.False(result.Changed);
            Assert.Same(saved, result.Profiles.Single());
            Assert.Equal("query: ", saved.QueryPrefix);
            Assert.Equal("passage: ", saved.DocumentPrefix);
            Assert.Equal(1024, saved.Dimensions);
        }

        [Theory]
        [InlineData("bge-m3", "query: ", "passage: ")]
        [InlineData("BAAI/bge-m3", "query: ", "passage: ")]
        [InlineData("bge-m3:latest", "query: ", "passage: ")]
        [InlineData("multilingual-e5-large", "query: ", "passage: ")]
        [InlineData("nomic-embed-text", "search_query: ", "search_document: ")]
        public void Normalize_KnownAsymmetricModel_GetsItsPrefixes(string model, string query, string document)
        {
            // Getting these wrong has no error and no symptom beyond worse results,
            // which is exactly why they are defaulted rather than left to whoever
            // reads the model card.
            var config = ConfigWith(new EmbeddingProfile { Id = "a", Model = model });

            var result = EmbeddingProfiles.Normalize(config);

            Assert.Equal(query, result.Profiles[0].QueryPrefix);
            Assert.Equal(document, result.Profiles[0].DocumentPrefix);
        }

        [Theory]
        [InlineData("text-embedding-3-small")]
        [InlineData("voyage-3")]
        [InlineData("gemini-embedding-001")]
        public void Normalize_SymmetricModel_GetsNoPrefixes(string model)
        {
            var config = ConfigWith(new EmbeddingProfile { Id = "a", Model = model });

            var result = EmbeddingProfiles.Normalize(config);

            Assert.Equal(string.Empty, result.Profiles[0].QueryPrefix);
            Assert.Equal(string.Empty, result.Profiles[0].DocumentPrefix);
        }

        [Fact]
        public void ApplyDefaultPrefixes_DoesNotOverwriteADeliberatelyClearedPrefix()
        {
            // Clearing one half is a legitimate choice for a fine-tune that dropped
            // the asymmetry. Restoring it on the next read would silently undo them.
            var profile = new EmbeddingProfile { Model = "bge-m3", QueryPrefix = "custom: " };

            var changed = EmbeddingProfiles.ApplyDefaultPrefixes(profile);

            Assert.False(changed);
            Assert.Equal("custom: ", profile.QueryPrefix);
            Assert.Equal(string.Empty, profile.DocumentPrefix);
        }

        [Fact]
        public void Resolve_BlankId_MeansTheDefaultProfile()
        {
            var fallback = new EmbeddingProfile { Id = "a", Model = "one" };
            var chosen = new EmbeddingProfile { Id = "b", Model = "two" };
            var config = ConfigWith(fallback, chosen);
            config.DefaultEmbeddingProfileId = "b";

            var normalized = EmbeddingProfiles.Normalize(config);

            Assert.Same(chosen, EmbeddingProfiles.Resolve(normalized, string.Empty));
        }

        [Fact]
        public void Resolve_NoProfiles_ThrowsAnActionableError()
        {
            var normalized = EmbeddingProfiles.Normalize(new PluginConfiguration());

            var ex = Assert.Throws<InvalidOperationException>(() => EmbeddingProfiles.Resolve(normalized, null));
            Assert.Contains("Models tab", ex.Message, StringComparison.Ordinal);
        }
    }
}
