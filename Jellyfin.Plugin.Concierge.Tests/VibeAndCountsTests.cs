using System;
using Jellyfin.Plugin.Concierge.Configuration;
using Jellyfin.Plugin.Concierge.Core.Documents;
using Jellyfin.Plugin.Concierge.Core.Llm;
using Xunit;

namespace Jellyfin.Plugin.Concierge.Tests
{
    /// <summary>
    /// The mood row, which is the only thing a mood query has to land on.
    /// </summary>
    public class VibeRowTests
    {
        private static ItemDocument Film(int? year = 1999, params string[] genres)
            => new(
                Guid.NewGuid(), "Movie", "Fight Club", string.Empty, year,
                genres, [], [], [], string.Empty, 139, "A film.");

        private static Enrichment With(params string[] themes)
            => new("A premise.", [], themes, [], false);

        /// <summary>
        /// Prose, not a comma dump.
        /// </summary>
        /// <remarks>
        /// The other side of this comparison is somebody typing "something dark and
        /// twisted from the nineties" — a phrase. "Drama, Thriller, alienation,
        /// bleak" is not one, and embedding models put prose nearer prose. The words
        /// are the same; only the shape changes.
        /// </remarks>
        [Fact]
        public void ItReadsAsASentence()
        {
            var text = VectorRowPlanner.Vibe(Film(1999, "Drama", "Thriller"), With("alienation", "bleak"));

            Assert.Contains("a film drama, thriller", text, StringComparison.Ordinal);
            Assert.Contains("It is about alienation, bleak.", text, StringComparison.Ordinal);
        }

        /// <summary>
        /// The decade is in, because mood queries carry one so often.
        /// </summary>
        [Fact]
        public void ItCarriesTheDecade()
        {
            Assert.Contains(
                "from the 1990s",
                VectorRowPlanner.Vibe(Film(1995), With("bleak")),
                StringComparison.Ordinal);
        }

        [Fact]
        public void AnItemWithNoYearSaysNothingAboutWhen()
        {
            Assert.DoesNotContain(
                "from the",
                VectorRowPlanner.Vibe(Film(null), With("bleak")),
                StringComparison.Ordinal);
        }

        /// <summary>
        /// A theme repeating a genre wastes one of the few words this row has.
        /// </summary>
        [Fact]
        public void AThemeThatRepeatsAGenreIsNotSaidTwice()
        {
            var text = VectorRowPlanner.Vibe(Film(1999, "Horror"), With("horror", "dread"));

            Assert.Equal(1, Occurrences(text.ToLowerInvariant(), "horror"));
        }

        /// <summary>
        /// It stays short, which is its entire advantage over the document row.
        /// </summary>
        /// <remarks>
        /// The same themes sit inside the document row too, where a title, cast,
        /// studios and a full overview dilute seven words of tone to nothing. Anything
        /// added here trades away the one thing that makes this row work.
        /// </remarks>
        [Fact]
        public void ItStaysShort()
        {
            var text = VectorRowPlanner.Vibe(
                Film(1999, "Drama", "Thriller"),
                With("alienation", "bleak", "masculinity", "consumerism", "identity", "dread"));

            Assert.True(text.Length < 200, $"vibe row was {text.Length} chars");
            Assert.Equal(1, Occurrences(text, "bleak"));
        }

        [Fact]
        public void TelevisionSaysSo()
        {
            var series = new ItemDocument(
                Guid.NewGuid(), "Series", "The Wire", string.Empty, 2002,
                ["Crime"], [], [], [], string.Empty, null, string.Empty);

            Assert.StartsWith("television", VectorRowPlanner.Vibe(series, With("institutions")), StringComparison.Ordinal);
        }

        private static int Occurrences(string haystack, string needle)
        {
            var count = 0;
            var at = 0;

            while ((at = haystack.IndexOf(needle, at, StringComparison.Ordinal)) >= 0)
            {
                count++;
                at += needle.Length;
            }

            return count;
        }
    }

    /// <summary>
    /// The counts the enrichment prompt is given.
    /// </summary>
    public class EnrichmentCountTests
    {
        [Fact]
        public void EveryCountIsStatedOnceAndInTheInstruction()
        {
            var rules = EnrichmentPromptBuilder.SystemPrompt;

            // Ranges in the rules are what made a configured 12 produce 8.
            Assert.DoesNotContain("6-10", rules, StringComparison.Ordinal);
            Assert.DoesNotContain("4-8", rules, StringComparison.Ordinal);
            Assert.DoesNotContain("3-6", rules, StringComparison.Ordinal);

            var instruction = EnrichmentPromptBuilder.BuildInstruction(5, 14, 9, 7);

            Assert.Contains("14 entries", instruction, StringComparison.Ordinal);
            Assert.Contains("9 in \"themes\"", instruction, StringComparison.Ordinal);
            Assert.Contains("7 in \"moments\"", instruction, StringComparison.Ordinal);
        }
    }

    /// <summary>
    /// Episodes can run on their own model, at their own thinking setting.
    /// </summary>
    public class EpisodeModelTests
    {
        private static ModelProfile Profile(string id) => new() { Id = id, Name = id, Model = id };

        [Fact]
        public void EpisodesFollowTheirOwnThinkingSetting()
        {
            var config = new PluginConfiguration
            {
                EnableThinking = false,
                EnrichmentThinking = ThinkingMode.On,
                EpisodeThinking = ThinkingMode.Off,
            };

            Assert.True(ThinkingPolicy.For(config, ThinkingPass.Enrichment, Profile("a")));
            Assert.False(ThinkingPolicy.For(config, ThinkingPass.Episode, Profile("a")));
        }

        [Fact]
        public void EpisodesLeftAloneFollowTheGlobalDefault()
        {
            var config = new PluginConfiguration { EnableThinking = true };

            Assert.True(ThinkingPolicy.For(config, ThinkingPass.Episode, Profile("a")));
        }

        [Fact]
        public void ItSaysThatTheEpisodePassDecided()
        {
            var config = new PluginConfiguration { EpisodeThinking = ThinkingMode.Off };

            Assert.Contains(
                "set on the episode pass",
                ThinkingPolicy.Explain(config, ThinkingPass.Episode, Profile("a")),
                StringComparison.Ordinal);
        }

        /// <summary>
        /// An install that has never set this keeps one model for everything.
        /// </summary>
        [Fact]
        public void NoEpisodeProfileMeansTheEnrichmentProfile()
        {
            Assert.Equal(string.Empty, new PluginConfiguration().EpisodeModelProfileId);
        }
    }
}
