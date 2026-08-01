using System;
using System.Linq;
using Jellyfin.Plugin.Concierge.Configuration;
using Jellyfin.Plugin.Concierge.Core.Llm;
using Xunit;

namespace Jellyfin.Plugin.Concierge.Tests
{
    public class ModelProfilesTests
    {
        private static PluginConfiguration ConfigWith(params ModelProfile[] profiles)
            => new() { ModelProfiles = profiles };

        [Fact]
        public void Normalize_EmptyConfig_ProducesNoProfilesAndNoDefault()
        {
            // Concierge ships no legacy scalars, so an unconfigured install has
            // nothing to migrate and must not invent a broken profile to show.
            var result = ModelProfiles.Normalize(new PluginConfiguration());

            Assert.Empty(result.Profiles);
            Assert.Equal(string.Empty, result.DefaultProfileId);
        }

        [Fact]
        public void Normalize_BlankId_MintsOne()
        {
            var config = ConfigWith(new ModelProfile { Model = "claude-haiku-4-5-20251001" });

            var result = ModelProfiles.Normalize(config);

            Assert.NotEmpty(result.Profiles[0].Id);
            Assert.True(result.Changed);
        }

        [Fact]
        public void Normalize_DuplicateIds_AreSplitApart()
        {
            var config = ConfigWith(
                new ModelProfile { Id = "same", Model = "a" },
                new ModelProfile { Id = "same", Model = "b" });

            var result = ModelProfiles.Normalize(config);

            Assert.NotEqual(result.Profiles[0].Id, result.Profiles[1].Id);
            Assert.True(result.Changed);
        }

        [Fact]
        public void Normalize_BlankName_IsDescribedFromProviderAndModel()
        {
            var config = ConfigWith(new ModelProfile
            {
                Id = "a",
                Provider = LlmProviderKind.Anthropic,
                Model = "claude-sonnet-5",
            });

            var result = ModelProfiles.Normalize(config);

            Assert.Equal("Anthropic claude-sonnet-5", result.Profiles[0].Name);
        }

        [Fact]
        public void Normalize_DanglingDefaultId_FallsBackToTheFirstProfile()
        {
            // Points at a profile that was deleted. Leaving it dangling would fail
            // every search with a configuration error the owner never made.
            var config = ConfigWith(new ModelProfile { Id = "real", Model = "m" });
            config.DefaultModelProfileId = "deleted";

            var result = ModelProfiles.Normalize(config);

            Assert.Equal("real", result.DefaultProfileId);
            Assert.True(result.Changed);
        }

        [Fact]
        public void Normalize_EmptyListWithADefaultId_ClearsIt()
        {
            var config = new PluginConfiguration { DefaultModelProfileId = "ghost" };

            var result = ModelProfiles.Normalize(config);

            Assert.Equal(string.Empty, result.DefaultProfileId);
            Assert.True(result.Changed);
        }

        /// <summary>
        /// The phase-0 done-when: a saved profile survives normalization untouched.
        /// </summary>
        [Fact]
        public void Normalize_FullySpecifiedProfile_RoundTripsUnchanged()
        {
            var saved = new ModelProfile
            {
                Id = "2f6c1b8e9d4a4f1e8c7b6a5d4e3f2a19",
                Name = "Haiku (fast)",
                Provider = LlmProviderKind.Anthropic,
                Model = "claude-haiku-4-5-20251001",
                ApiKey = "sk-ant-secret",
                BaseUrl = "https://proxy.example/v1",
                Thinking = ThinkingMode.Off,
                InputCostPerMillion = 1m,
                CachedInputCostPerMillion = 0.1m,
                OutputCostPerMillion = 5m,
            };

            var config = ConfigWith(saved);
            config.DefaultModelProfileId = saved.Id;

            var result = ModelProfiles.Normalize(config);

            Assert.False(result.Changed);
            Assert.Same(saved, result.Profiles.Single());
            Assert.Equal("2f6c1b8e9d4a4f1e8c7b6a5d4e3f2a19", saved.Id);
            Assert.Equal("Haiku (fast)", saved.Name);
            Assert.Equal(LlmProviderKind.Anthropic, saved.Provider);
            Assert.Equal("claude-haiku-4-5-20251001", saved.Model);
            Assert.Equal("sk-ant-secret", saved.ApiKey);
            Assert.Equal("https://proxy.example/v1", saved.BaseUrl);
            Assert.Equal(ThinkingMode.Off, saved.Thinking);
            Assert.Equal(1m, saved.InputCostPerMillion);
            Assert.Equal(0.1m, saved.CachedInputCostPerMillion);
            Assert.Equal(5m, saved.OutputCostPerMillion);
            Assert.Equal(saved.Id, result.DefaultProfileId);
        }

        /// <summary>
        /// Normalizing twice must not mint a second id for one profile.
        /// </summary>
        /// <remarks>
        /// This is the property hard rule 12 rests on. If a repeated Normalize
        /// re-minted the id, two passes resolving "the same" profile would compare as
        /// two different ones, and a query would build two identical providers and
        /// report itself as running two models.
        /// <para>
        /// <c>Changed</c> stays true across both calls here on purpose: it means
        /// "the caller should persist this", and nothing in this test writes the
        /// repaired default id back to the configuration. See the test below for the
        /// case where it does.
        /// </para>
        /// </remarks>
        [Fact]
        public void Normalize_RepeatedOnOneConfig_KeepsTheSameIds()
        {
            var config = ConfigWith(new ModelProfile { Model = "m" });

            var first = ModelProfiles.Normalize(config);
            var second = ModelProfiles.Normalize(config);

            Assert.Equal(first.Profiles[0].Id, second.Profiles[0].Id);
            Assert.Equal(first.DefaultProfileId, second.DefaultProfileId);
        }

        [Fact]
        public void Normalize_AfterTheCallerPersistsTheRepair_ReportsNothingFurtherToChange()
        {
            var config = ConfigWith(new ModelProfile { Model = "m" });

            var first = ModelProfiles.Normalize(config);
            config.DefaultModelProfileId = first.DefaultProfileId;

            Assert.False(ModelProfiles.Normalize(config).Changed);
        }

        [Fact]
        public void Resolve_BlankId_MeansTheDefaultProfile()
        {
            // Blank is a real value — "follow the default" — not "unset". An install
            // that has assigned no pass its own model must still run every pass.
            var fallback = new ModelProfile { Id = "a", Model = "cheap" };
            var chosen = new ModelProfile { Id = "b", Model = "good" };
            var config = ConfigWith(fallback, chosen);
            config.DefaultModelProfileId = "b";

            var normalized = ModelProfiles.Normalize(config);

            Assert.Same(chosen, ModelProfiles.Resolve(normalized, null));
            Assert.Same(chosen, ModelProfiles.Resolve(normalized, string.Empty));
            Assert.Same(chosen, ModelProfiles.Resolve(normalized, "   "));
        }

        [Fact]
        public void Resolve_UnknownId_FallsBackToTheDefaultRatherThanThrowing()
        {
            var config = ConfigWith(new ModelProfile { Id = "a", Model = "m" });
            var normalized = ModelProfiles.Normalize(config);

            Assert.Equal("a", ModelProfiles.Resolve(normalized, "since-deleted").Id);
        }

        /// <summary>
        /// Hard rule 12: every pass of one query resolves from a single
        /// <see cref="ModelProfiles.Normalize"/> result, so two passes pointed at one
        /// profile get one profile — by reference, not merely by value.
        /// </summary>
        [Fact]
        public void Resolve_TwoPassesAgainstOneNormalize_ReturnTheSameInstance()
        {
            var config = ConfigWith(new ModelProfile { Model = "shared" });
            config.PlanModelProfileId = string.Empty;
            config.RerankModelProfileId = string.Empty;

            var normalized = ModelProfiles.Normalize(config);
            var plan = ModelProfiles.Resolve(normalized, config.PlanModelProfileId);
            var rerank = ModelProfiles.Resolve(normalized, config.RerankModelProfileId);

            Assert.Same(plan, rerank);
            Assert.Equal(plan.Id, rerank.Id);
        }

        [Fact]
        public void Resolve_NoProfiles_ThrowsAnActionableError()
        {
            var normalized = ModelProfiles.Normalize(new PluginConfiguration());

            var ex = Assert.Throws<InvalidOperationException>(() => ModelProfiles.Resolve(normalized, null));
            Assert.Contains("Models tab", ex.Message, StringComparison.Ordinal);
        }

        [Theory]
        [InlineData(ThinkingMode.Inherit, true, true)]
        [InlineData(ThinkingMode.Inherit, false, false)]
        [InlineData(ThinkingMode.On, false, true)]
        [InlineData(ThinkingMode.Off, true, false)]
        public void ThinkingResolved_ProfileOverridesTheGlobal(ThinkingMode mode, bool global, bool expected)
        {
            var profile = new ModelProfile { Thinking = mode };

            Assert.Equal(expected, profile.ThinkingResolved(global));
        }

        [Fact]
        public void EffectiveCachedRate_WhenUnset_IsHalfThisProfilesOwnInputRate()
        {
            // Half of its OWN input price, never another profile's — the whole reason
            // pricing lives on the profile rather than on the configuration.
            var profile = new ModelProfile { InputCostPerMillion = 3m };

            Assert.Equal(1.5m, profile.EffectiveCachedInputCostPerMillion());
        }

        [Fact]
        public void EffectiveCachedRate_WhenSet_IsUsedAsGiven()
        {
            var profile = new ModelProfile { InputCostPerMillion = 3m, CachedInputCostPerMillion = 0.3m };

            Assert.Equal(0.3m, profile.EffectiveCachedInputCostPerMillion());
        }
    }
}
