using Jellyfin.Plugin.Concierge.Configuration;
using Jellyfin.Plugin.Concierge.Core.Llm;
using Xunit;

namespace Jellyfin.Plugin.Concierge.Tests
{
    /// <summary>
    /// Thinking on enrichment, off on searches — the thing that had no direct control.
    /// </summary>
    /// <remarks>
    /// It was reachable before only by making a second profile of the same model with
    /// thinking on and pointing enrichment at it. That works, and nobody would find it.
    /// </remarks>
    public class ThinkingPolicyTests
    {
        private static ModelProfile Profile(ThinkingMode mode = ThinkingMode.Inherit)
            => new() { Id = "p", Name = "GPT 5.6 Luna", Model = "gpt-5.6-luna", Thinking = mode };

        [Fact]
        public void TheAskedForShape_OnForIndexing_OffForSearches()
        {
            var config = new PluginConfiguration
            {
                EnableThinking = false,
                EnrichmentThinking = ThinkingMode.On,
                RerankThinking = ThinkingMode.Off,
            };

            Assert.True(ThinkingPolicy.For(config, ThinkingPass.Enrichment, Profile()));
            Assert.False(ThinkingPolicy.For(config, ThinkingPass.Rerank, Profile()));
        }

        [Fact]
        public void APassLeftAloneFollowsTheGlobalDefault()
        {
            var on = new PluginConfiguration { EnableThinking = true };
            var off = new PluginConfiguration { EnableThinking = false };

            Assert.True(ThinkingPolicy.For(on, ThinkingPass.Rerank, Profile()));
            Assert.False(ThinkingPolicy.For(off, ThinkingPass.Rerank, Profile()));
        }

        /// <summary>
        /// A profile that says on or off means it.
        /// </summary>
        /// <remarks>
        /// The profile override is the reason two profiles of one model exist at all.
        /// If a pass setting could silently beat it, that pattern would break for
        /// anyone already relying on it — and they would have no way to see why.
        /// </remarks>
        [Theory]
        [InlineData(ThinkingMode.On, true)]
        [InlineData(ThinkingMode.Off, false)]
        public void TheProfileWinsOverBothThePassAndTheGlobal(ThinkingMode mode, bool expected)
        {
            var config = new PluginConfiguration
            {
                EnableThinking = !expected,
                RerankThinking = expected ? ThinkingMode.Off : ThinkingMode.On,
            };

            Assert.Equal(expected, ThinkingPolicy.For(config, ThinkingPass.Rerank, Profile(mode)));
        }

        [Fact]
        public void EachPassIsIndependent()
        {
            var config = new PluginConfiguration
            {
                EnableThinking = false,
                PlanThinking = ThinkingMode.On,
                RerankThinking = ThinkingMode.Off,
                EnrichmentThinking = ThinkingMode.On,
            };

            Assert.True(ThinkingPolicy.For(config, ThinkingPass.Plan, Profile()));
            Assert.False(ThinkingPolicy.For(config, ThinkingPass.Rerank, Profile()));
            Assert.True(ThinkingPolicy.For(config, ThinkingPass.Enrichment, Profile()));
        }

        [Fact]
        public void AMissingProfileStillResolves()
        {
            var config = new PluginConfiguration { EnrichmentThinking = ThinkingMode.On };

            Assert.True(ThinkingPolicy.For(config, ThinkingPass.Enrichment, null));
        }

        /// <summary>
        /// Three places can decide this, so it has to be able to say which one did.
        /// </summary>
        /// <remarks>
        /// The setting was silently ignored on OpenAI for nine releases and nothing in
        /// the product could have told anyone. Being able to name the deciding rule is
        /// the difference between a setting that is trusted and one that is toggled
        /// twice and abandoned.
        /// </remarks>
        [Fact]
        public void ItCanSayWhichRuleDecided()
        {
            var config = new PluginConfiguration { EnableThinking = false };

            Assert.Contains(
                "following the global default",
                ThinkingPolicy.Explain(config, ThinkingPass.Rerank, Profile()),
                System.StringComparison.Ordinal);

            config.EnrichmentThinking = ThinkingMode.On;
            Assert.Contains(
                "set on the enrichment pass",
                ThinkingPolicy.Explain(config, ThinkingPass.Enrichment, Profile()),
                System.StringComparison.Ordinal);

            Assert.Contains(
                "set on the profile 'GPT 5.6 Luna'",
                ThinkingPolicy.Explain(config, ThinkingPass.Enrichment, Profile(ThinkingMode.Off)),
                System.StringComparison.Ordinal);
        }

        /// <summary>
        /// An existing install must not change behaviour on upgrade.
        /// </summary>
        [Fact]
        public void AConfigWrittenBeforeThisExistedBehavesExactlyAsBefore()
        {
            // Every new setting defaults to Inherit, so a config that has never seen
            // them resolves to precisely the old global-only answer.
            var before = new PluginConfiguration { EnableThinking = true };

            Assert.True(ThinkingPolicy.For(before, ThinkingPass.Plan, Profile()));
            Assert.True(ThinkingPolicy.For(before, ThinkingPass.Rerank, Profile()));
            Assert.True(ThinkingPolicy.For(before, ThinkingPass.Enrichment, Profile()));
        }
    }
}
