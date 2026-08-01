using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;

namespace Jellyfin.Plugin.Concierge.Core.Subtitles
{
    /// <summary>One cue after cleaning, keeping both forms.</summary>
    /// <param name="Start">When it appears.</param>
    /// <param name="End">When it goes away.</param>
    /// <param name="Text">The cleaned dialogue, for searching.</param>
    /// <param name="Raw">The line as it appeared, for display.</param>
    /// <remarks>
    /// Both, deliberately: the user should see the line as it was written and search
    /// it as it means. Showing a stripped line back would look like a transcription
    /// error, and searching the raw one matches stage directions.
    /// </remarks>
    public sealed record CleanCue(TimeSpan Start, TimeSpan End, string Text, string Raw);

    /// <summary>
    /// Strips everything in a subtitle file that is not dialogue.
    /// </summary>
    /// <remarks>
    /// Subtitle text is not prose, and indexing it raw poisons the results. The
    /// worst offenders are SDH annotations — <c>[door creaks]</c>,
    /// <c>(SIRENS WAILING)</c>, <c>♪ music ♪</c>. Those are descriptions of sound,
    /// and they match mood queries wrongly and loudly: a search for something tense
    /// would rank whichever film has the most <c>[ominous music]</c> in it.
    /// </remarks>
    public static partial class CueCleaner
    {
        [GeneratedRegex(@"<[^>]*>", RegexOptions.None, 200)]
        private static partial Regex HtmlTag();

        /// <summary>ASS override blocks: {\an8}, {\pos(190,270)}.</summary>
        [GeneratedRegex(@"\{[^}]*\}", RegexOptions.None, 200)]
        private static partial Regex AssOverride();

        /// <summary>SDH annotations in brackets or parentheses.</summary>
        [GeneratedRegex(@"[\[(][^\])]*[\])]", RegexOptions.None, 200)]
        private static partial Regex Annotation();

        /// <summary>A speaker prefix in caps: "VINCENT:", "MAN ON TV:".</summary>
        [GeneratedRegex(@"^\s*[-–—]?\s*[A-Z][A-Z0-9 .'#\-]{1,24}:\s*", RegexOptions.None, 200)]
        private static partial Regex SpeakerPrefix();

        [GeneratedRegex(@"\s{2,}", RegexOptions.None, 200)]
        private static partial Regex Whitespace();

        /// <summary>
        /// Phrases that mark a cue as the subtitle author advertising, not dialogue.
        /// </summary>
        /// <remarks>
        /// These cluster at the head and tail of ripped files and are among the most
        /// distinctive text in them, so left in they would be a strong match for
        /// almost any query that happened to share a word.
        /// </remarks>
        private static readonly string[] CreditMarkers =
        [
            "opensubtitles", "subscene", "addic7ed", "yify", "subtitles by",
            "subtitled by", "sync by", "synced by", "corrected by", "resync",
            "translated by", "www.", "http://", "https://", "@gmail", "subs by",
            "encoded by", "ripped by",
        ];

        /// <summary>
        /// Cleans a whole track: strips markup and annotations, drops credits and
        /// consecutive duplicates, and discards anything left with no dialogue in it.
        /// </summary>
        /// <param name="cues">The parsed cues.</param>
        /// <returns>The dialogue, in order.</returns>
        public static IReadOnlyList<CleanCue> Clean(IReadOnlyList<Cue> cues)
        {
            ArgumentNullException.ThrowIfNull(cues);

            var cleaned = new List<CleanCue>(cues.Count);
            string? previous = null;

            foreach (var cue in cues)
            {
                var text = CleanLine(cue.Text);
                if (text.Length == 0)
                {
                    continue;
                }

                // Rips routinely repeat a cue across several timings. Keeping every
                // copy would let one line outrank a film that says something once.
                if (string.Equals(text, previous, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                previous = text;
                cleaned.Add(new CleanCue(cue.Start, cue.End, text, cue.Text));
            }

            return cleaned;
        }

        /// <summary>
        /// Cleans one line of subtitle text.
        /// </summary>
        /// <param name="raw">The raw text.</param>
        /// <returns>The dialogue, or empty when the line was not dialogue at all.</returns>
        public static string CleanLine(string? raw)
        {
            if (string.IsNullOrWhiteSpace(raw))
            {
                return string.Empty;
            }

            var text = raw;

            if (IsCredits(text))
            {
                return string.Empty;
            }

            text = HtmlTag().Replace(text, " ");
            text = AssOverride().Replace(text, " ");
            text = Annotation().Replace(text, " ");

            // Musical notes wrap two different things, and the plan's blunt "strip
            // them" would throw away the good one. "♪ ominous music ♪" is a sound
            // description and must go; "♪ Let it go, let it go ♪" is a line somebody
            // will absolutely search for. So the markers come off either way, and the
            // content only goes when it reads as a description of the music rather
            // than the words of it.
            if (IsMusicDescription(text))
            {
                return string.Empty;
            }

            text = text.Replace("♪", " ", StringComparison.Ordinal)
                .Replace("♫", " ", StringComparison.Ordinal);

            // Two speakers in one cue arrive as "- Line one\n- Line two"; the dashes
            // are turn markers, not punctuation.
            text = SpeakerPrefix().Replace(text, string.Empty);
            text = text.TrimStart('-', '–', '—', ' ');

            text = Whitespace().Replace(text, " ").Trim();

            // What is left of "[SIRENS WAILING]" is nothing, and what is left of
            // "- ..." is punctuation. Neither is searchable.
            return HasLetterOrDigit(text) ? text : string.Empty;
        }

        /// <summary>
        /// Words that mean a music cue is describing the score rather than quoting it.
        /// </summary>
        private static readonly string[] MusicDescriptionMarkers =
        [
            "music", "theme", "song", "singing", "instrumental", "playing",
            "continues", "fades", "swells", "jingle", "score",
        ];

        /// <summary>
        /// Whether a note-wrapped cue is a description of the music rather than lyrics.
        /// </summary>
        /// <remarks>
        /// Checked only on cues that carry a note, so a line of dialogue about a song
        /// is unaffected. The test is deliberately narrow — a description is short and
        /// names the music, where lyrics are neither.
        /// </remarks>
        private static bool IsMusicDescription(string text)
        {
            if (!text.Contains('♪', StringComparison.Ordinal)
                && !text.Contains('♫', StringComparison.Ordinal))
            {
                return false;
            }

            var inner = text.Replace("♪", " ", StringComparison.Ordinal)
                .Replace("♫", " ", StringComparison.Ordinal)
                .Trim();

            // Lyrics run on; descriptions are a few words. Anything long enough to be
            // a verse is kept even if it happens to mention a song.
            if (CountWords(inner) > 6)
            {
                return false;
            }

            foreach (var marker in MusicDescriptionMarkers)
            {
                if (inner.Contains(marker, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }

            return false;
        }

        private static int CountWords(string text)
        {
            var count = 0;
            var inWord = false;

            foreach (var c in text)
            {
                if (char.IsWhiteSpace(c))
                {
                    inWord = false;
                }
                else if (!inWord)
                {
                    inWord = true;
                    count++;
                }
            }

            return count;
        }

        private static bool IsCredits(string text)
        {
            foreach (var marker in CreditMarkers)
            {
                if (text.Contains(marker, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }

            return false;
        }

        private static bool HasLetterOrDigit(string text)
        {
            foreach (var c in text)
            {
                if (char.IsLetterOrDigit(c))
                {
                    return true;
                }
            }

            return false;
        }
    }
}
