using System;
using System.Collections;
using System.Linq;
using Jellyfin.Plugin.Concierge.Api;
using Jellyfin.Plugin.Concierge.Configuration;
using Xunit;

namespace Jellyfin.Plugin.Concierge.Tests
{
    /// <summary>
    /// A request that asks for a different number of results still runs on every
    /// setting the owner saved.
    /// </summary>
    /// <remarks>
    /// The web client always sends a limit, so every real search goes through this
    /// copy. It was written field by field and had drifted to 29 of 45 settings, which
    /// meant sixteen of them silently reverted to code defaults on every search —
    /// including the entire re-rank shape: how many entries to ask for, how many get a
    /// reason, how long a reason may be, its output cap, its thinking mode, and the
    /// query deadline.
    /// <para>
    /// It went unnoticed because the values that mattered happened to equal their
    /// defaults. Changing one would have done nothing, with no error and no log line —
    /// the exact failure this codebase keeps finding. A hand-written list drifts every
    /// time a setting is added, so the copy is now reflective and this test fails the
    /// moment it stops being complete.
    /// </para>
    /// </remarks>
    public class PerRequestLimitKeepsSettingsTests
    {
        /// <summary>Values that differ from every default, so a missed copy shows up.</summary>
        private static PluginConfiguration Distinctive() => new()
        {
            MaxResults = 7,
            RerankReturnCount = 11,
            RerankExplainCount = 3,
            RerankWhyMaxChars = 41,
            RerankMaxOutputTokens = 1234,
            RerankShortlistSize = 23,
            QueryTimeoutSeconds = 17,
            MaxOutputTokens = 4321,
            SearchDebounceMs = 1100,
            SearchInputMaxLength = 512,
            QuoteWindowWords = 33,
            EnrichmentConcurrency = 4,
            MaxThemesPerItem = 5,
            MaxMomentsPerItem = 3,
            HideJellyseerrIcon = false,
            EnableThinking = true,
            PlanThinking = ThinkingMode.Off,
            RerankThinking = ThinkingMode.On,
            EnrichmentThinking = ThinkingMode.Off,
            EpisodeThinking = ThinkingMode.On,
            EpisodeModelProfileId = "episodes",
            RerankModelProfileId = "rerank",
            PlanModelProfileId = "plan",
        };

        [Fact]
        public void EverySettingSurvivesAPerRequestLimit()
        {
            var original = Distinctive();
            var copy = ConciergeController.CloneWithLimit(original, 20);

            var missed = typeof(PluginConfiguration).GetProperties()
                .Where(p => p.CanRead && p.CanWrite && p.Name != nameof(PluginConfiguration.MaxResults))
                .Where(p => !Same(p.GetValue(original), p.GetValue(copy)))
                .Select(p => p.Name)
                .ToList();

            Assert.True(
                missed.Count == 0,
                "a per-request limit dropped these settings: " + string.Join(", ", missed));
        }

        [Fact]
        public void OnlyTheResultCountChanges()
        {
            var copy = ConciergeController.CloneWithLimit(Distinctive(), 20);

            Assert.Equal(20, copy.MaxResults);
            Assert.Equal(11, copy.RerankReturnCount);
            Assert.Equal(3, copy.RerankExplainCount);
        }

        [Fact]
        public void TheOriginalIsNotMutated()
        {
            // Every other in-flight request is reading the shared configuration object.
            var original = Distinctive();
            ConciergeController.CloneWithLimit(original, 20);

            Assert.Equal(7, original.MaxResults);
        }

        private static bool Same(object? a, object? b)
        {
            if (a is null || b is null)
            {
                return a is null && b is null;
            }

            // Arrays are carried by reference, which is the intent: a shallow copy.
            return a is IEnumerable and not string
                ? ReferenceEquals(a, b)
                : a.Equals(b);
        }
    }
}
