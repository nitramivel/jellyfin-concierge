using System;
using System.Collections.Generic;
using System.Linq;
using Jellyfin.Plugin.Concierge.Core.Subtitles;
using MediaBrowser.Model.Entities;
using Xunit;

namespace Jellyfin.Plugin.Concierge.Tests
{
    public class SrtParserTests
    {
        private const string Sample =
            "1\n" +
            "00:00:12,000 --> 00:00:14,500\n" +
            "I'm walking here!\n" +
            "\n" +
            "2\n" +
            "00:01:02,250 --> 00:01:05,000\n" +
            "You can't handle\n" +
            "the truth!\n" +
            "\n";

        [Fact]
        public void ParsesCuesWithTimingsAndJoinsWrappedLines()
        {
            var cues = SrtParser.Parse(Sample);

            Assert.Equal(2, cues.Count);
            Assert.Equal(TimeSpan.FromSeconds(12), cues[0].Start);
            Assert.Equal("I'm walking here!", cues[0].Text);

            // A cue wrapped across two lines is one line of dialogue, and joining it
            // is what lets an exact phrase search find it.
            Assert.Equal("You can't handle the truth!", cues[1].Text);
        }

        [Fact]
        public void ABomDoesNotEatTheFirstCue()
        {
            Assert.Single(SrtParser.Parse("﻿1\n00:00:01,000 --> 00:00:02,000\nHello.\n"));
        }

        [Fact]
        public void PeriodDecimalSeparatorsAreAccepted()
        {
            var cues = SrtParser.Parse("1\n00:00:01.500 --> 00:00:02.000\nHello.\n");

            Assert.Equal(TimeSpan.FromSeconds(1.5), cues[0].Start);
        }

        [Fact]
        public void TrailingPositionDataDoesNotBreakTheParse()
        {
            var cues = SrtParser.Parse(
                "1\n00:00:01,000 --> 00:00:02,000  X1:100 X2:200\nHello.\n");

            Assert.Single(cues);
        }

        [Fact]
        public void ACueWithNoTimingIsSkippedRatherThanFailingTheFile()
        {
            // Losing one line is nothing. Losing a film is not.
            var cues = SrtParser.Parse(
                "1\nnot a timing line\nJunk.\n\n2\n00:00:05,000 --> 00:00:06,000\nReal dialogue.\n");

            Assert.Single(cues);
            Assert.Equal("Real dialogue.", cues[0].Text);
        }

        [Theory]
        [InlineData("")]
        [InlineData(null)]
        [InlineData("complete nonsense")]
        public void UnusableInputReturnsNothing(string? content)
        {
            Assert.Empty(SrtParser.Parse(content));
        }
    }

    public class CueCleanerTests
    {
        [Theory]
        [InlineData("<i>Italic line.</i>", "Italic line.")]
        [InlineData("{\\an8}Positioned line.", "Positioned line.")]
        [InlineData("- Two speakers here.", "Two speakers here.")]
        [InlineData("VINCENT: Say what again.", "Say what again.")]
        [InlineData("MAN ON TV: The news.", "The news.")]
        public void FormattingAndSpeakersAreStripped(string raw, string expected)
        {
            Assert.Equal(expected, CueCleaner.CleanLine(raw));
        }

        [Theory]
        [InlineData("[door creaks]")]
        [InlineData("(SIRENS WAILING)")]
        [InlineData("♪ upbeat music ♪")]
        [InlineData("♪ theme continues ♪")]
        public void SdhAnnotationsAreRemovedEntirely(string raw)
        {
            // These are descriptions of sound. Left in, a search for something tense
            // would rank whichever film has the most [ominous music] in it.
            Assert.Equal(string.Empty, CueCleaner.CleanLine(raw));
        }

        [Fact]
        public void LyricsSurviveEvenThoughTheyCarryTheSameMarker()
        {
            // The plan says strip note-wrapped cues, which would be right for
            // descriptions and wrong for these. "Let it go" is a line people search
            // for, and it arrives wrapped in exactly the same character.
            Assert.Equal(
                "Let it go, let it go, can't hold it back anymore",
                CueCleaner.CleanLine("♪ Let it go, let it go, can't hold it back anymore ♪"));
        }

        [Fact]
        public void AnAnnotationBesideDialogueLeavesTheDialogue()
        {
            Assert.Equal("Get down!", CueCleaner.CleanLine("[explosion] Get down!"));
        }

        [Theory]
        [InlineData("Subtitles by OpenSubtitles")]
        [InlineData("Sync by honeybunny  www.addic7ed.com")]
        [InlineData("Translated by someone")]
        public void SubtitleAuthorCreditsAreDropped(string raw)
        {
            Assert.Equal(string.Empty, CueCleaner.CleanLine(raw));
        }

        [Fact]
        public void ConsecutiveDuplicatesAreCollapsed()
        {
            // Rips repeat a cue across several timings. Every copy kept would let one
            // line outrank a film that says something once.
            var cues = new List<Cue>
            {
                new(TimeSpan.Zero, TimeSpan.FromSeconds(2), "Hello there."),
                new(TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(4), "Hello there."),
                new(TimeSpan.FromSeconds(4), TimeSpan.FromSeconds(6), "General Kenobi."),
            };

            var cleaned = CueCleaner.Clean(cues);

            Assert.Equal(2, cleaned.Count);
        }

        [Fact]
        public void TheRawLineIsKeptForDisplay()
        {
            var cleaned = CueCleaner.Clean(
                [new Cue(TimeSpan.Zero, TimeSpan.FromSeconds(2), "<i>VINCENT: Say what again.</i>")]);

            Assert.Equal("Say what again.", cleaned[0].Text);
            Assert.Equal("<i>VINCENT: Say what again.</i>", cleaned[0].Raw);
        }
    }

    public class TrackSelectorTests
    {
        private static MediaStream Subtitle(
            int index = 0,
            string? language = "eng",
            bool text = true,
            bool forced = false,
            bool sdh = false,
            bool external = false)
            => new()
            {
                Type = MediaStreamType.Subtitle,
                Index = index,
                Language = language,
                Codec = text ? "subrip" : "pgssub",
                IsForced = forced,
                IsHearingImpaired = sdh,
                IsExternal = external,
            };

        [Fact]
        public void AForcedTrackIsNeverChosen()
        {
            // The rule that matters most. A forced track carries a few dozen
            // foreign-language lines, so indexing one looks like success and produces
            // a film nobody can find by anything said in it.
            var choice = TrackSelector.Choose([Subtitle(forced: true)]);

            Assert.False(choice.Found);
            Assert.Contains("forced", choice.Reason, StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public void ImageSubtitlesAreReportedNotChosen()
        {
            var choice = TrackSelector.Choose([Subtitle(text: false)]);

            Assert.False(choice.Found);
            Assert.Contains("image-only", choice.Reason, StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public void ACleanTrackBeatsAHearingImpairedOne()
        {
            var choice = TrackSelector.Choose([Subtitle(index: 0, sdh: true), Subtitle(index: 1)]);

            Assert.Equal(1, choice.Stream!.Index);
        }

        [Fact]
        public void SdhIsTakenWhenItIsTheOnlyTextTrack()
        {
            // Better than nothing by a wide margin, once its annotations are stripped.
            var choice = TrackSelector.Choose([Subtitle(sdh: true)]);

            Assert.True(choice.Found);
            Assert.Contains("hearing-impaired", choice.Reason, StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public void ThePreferredLanguageWins()
        {
            var choice = TrackSelector.Choose(
                [Subtitle(index: 0, language: "fre"), Subtitle(index: 1, language: "en")]);

            Assert.Equal(1, choice.Stream!.Index);
        }

        [Fact]
        public void AnotherLanguageIsUsedWhenNothingMatchesButSaysSo()
        {
            var choice = TrackSelector.Choose([Subtitle(language: "fre")]);

            Assert.True(choice.Found);
            Assert.Contains("preferred language", choice.Reason, StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public void AnItemWithVideoButNoSubtitlesIsReportedPlainly()
        {
            var video = new MediaStream { Type = MediaStreamType.Video, Index = 0 };

            Assert.Contains(
                "no subtitles", TrackSelector.Choose([video]).Reason, StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public void AnItemWithNoStreamsAtAllIsReportedSeparately()
        {
            // Different cause, different fix: no streams means the item has not been
            // probed, where no subtitles means it has and there are none.
            Assert.Contains(
                "no media streams", TrackSelector.Choose([]).Reason, StringComparison.OrdinalIgnoreCase);
        }
    }

    public class CueWindowingTests
    {
        private static List<CleanCue> Track(params string[] lines)
            => lines.Select((l, i) => new CleanCue(
                TimeSpan.FromSeconds(i * 3), TimeSpan.FromSeconds((i * 3) + 2), l, l)).ToList();

        [Fact]
        public void WindowsOverlapSoAQuoteCannotFallBetweenThem()
        {
            var cues = Track(
                "one two three four", "five six seven eight", "nine ten eleven twelve",
                "thirteen fourteen fifteen sixteen", "seventeen eighteen nineteen twenty");

            var windows = CueWindowing.Build(Guid.NewGuid(), cues, windowWords: 8);

            Assert.True(windows.Count > 1);

            // Consecutive windows must share cues, or a phrase spanning the boundary
            // would exist in neither.
            var first = windows[0];
            var second = windows[1];
            Assert.True(second.FirstCue < first.FirstCue + first.CueCount);
        }

        [Fact]
        public void ASingleCueLongerThanTheWindowStillTerminates()
        {
            // A monologue on one line would loop forever without the minimum step.
            var cues = Track(string.Join(' ', Enumerable.Repeat("word", 200)), "short one");

            var windows = CueWindowing.Build(Guid.NewGuid(), cues, windowWords: 10);

            Assert.NotEmpty(windows);
            Assert.True(windows.Count < 10);
        }

        [Fact]
        public void WindowsCarryTheTimeOfTheirFirstAndLastCue()
        {
            var windows = CueWindowing.Build(Guid.NewGuid(), Track("a b c", "d e f"), windowWords: 100);

            Assert.Equal(TimeSpan.Zero, windows[0].Start);
            Assert.Equal(TimeSpan.FromSeconds(5), windows[0].End);
        }

        [Fact]
        public void ContextReturnsTheRawSurroundingLines()
        {
            var cues = Track("before", "the line", "after", "later");

            var context = CueWindowing.Context(cues, 1, before: 1, after: 1);

            Assert.Equal(["before", "the line", "after"], context);
        }

        [Fact]
        public void AnEmptyTrackProducesNoWindows()
        {
            Assert.Empty(CueWindowing.Build(Guid.NewGuid(), []));
        }
    }

    public class PhraseIndexTests
    {
        private static readonly Guid Film = Guid.NewGuid();
        private static readonly Guid Other = Guid.NewGuid();

        private static PhraseIndex Build()
        {
            var windows = new List<QuoteWindow>
            {
                new(Film, TimeSpan.FromMinutes(12), TimeSpan.FromMinutes(12.5),
                    "Hey I'm walking here I'm walking here what are you doing", 0, 3),
                new(Film, TimeSpan.FromMinutes(40), TimeSpan.FromMinutes(40.5),
                    "You want answers I want the truth you can't handle the truth", 10, 3),
                new(Other, TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(5.5),
                    "No I am your father that's not true that's impossible", 4, 3),
            };

            return PhraseIndex.Build(windows);
        }

        [Fact]
        public void AVerbatimQuoteIsFoundExactly()
        {
            var hits = Build().Search("I'm walking here");

            Assert.NotEmpty(hits);
            Assert.True(hits[0].Exact);
            Assert.Equal(Film, hits[0].ItemId);
            Assert.Equal(TimeSpan.FromMinutes(12), hits[0].Start);
        }

        [Fact]
        public void AVerbatimQuoteAcrossTheMiddleOfAWindowIsFound()
        {
            var hits = Build().Search("you can't handle the truth");

            Assert.True(hits[0].Exact);
            Assert.Equal(TimeSpan.FromMinutes(40), hits[0].Start);
        }

        [Fact]
        public void AMisrememberedQuoteStillFindsTheFilm()
        {
            // "Luke, I am your father" is not a line in any Star Wars film. This is
            // the whole reason stage 2 exists, and it costs nothing.
            var hits = Build().Search("Luke I am your father");

            Assert.NotEmpty(hits);
            Assert.False(hits[0].Exact);
            Assert.Equal(Other, hits[0].ItemId);
        }

        [Fact]
        public void ExactHitsOutrankNearMisses()
        {
            var hits = Build().Search("I'm walking here");

            Assert.Equal(1.0, hits[0].Score);
        }

        [Fact]
        public void OneHitPerItemEvenThoughWindowsOverlap()
        {
            var windows = new List<QuoteWindow>
            {
                new(Film, TimeSpan.Zero, TimeSpan.FromSeconds(5), "say hello to my little friend", 0, 2),
                new(Film, TimeSpan.FromSeconds(3), TimeSpan.FromSeconds(8), "hello to my little friend now", 1, 2),
            };

            var hits = PhraseIndex.Build(windows).Search("my little friend");

            Assert.Single(hits);
        }

        [Fact]
        public void AWordNobodySaysReturnsNothing()
        {
            Assert.Empty(Build().Search("supercalifragilistic"));
        }

        [Theory]
        [InlineData("")]
        [InlineData(null)]
        public void EmptyInputReturnsNothing(string? phrase)
        {
            Assert.Empty(Build().Search(phrase));
        }

        [Fact]
        public void FuzzyCanBeTurnedOffForCallersThatWantOnlyVerbatim()
        {
            Assert.Empty(Build().Search("Luke I am your father", allowFuzzy: false));
        }
    }
}
