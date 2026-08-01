using System;
using System.Collections.Generic;
using System.Linq;

namespace Jellyfin.Plugin.Concierge.Core.Subtitles
{
    /// <summary>A searchable span of dialogue.</summary>
    /// <param name="ItemId">The film or episode it came from.</param>
    /// <param name="Start">Where the span begins.</param>
    /// <param name="End">Where it ends.</param>
    /// <param name="Text">The cleaned dialogue, joined.</param>
    /// <param name="FirstCue">Index of the first cue in the span, for pulling context back out.</param>
    /// <param name="CueCount">How many cues the span covers.</param>
    public sealed record QuoteWindow(
        Guid ItemId,
        TimeSpan Start,
        TimeSpan End,
        string Text,
        int FirstCue,
        int CueCount);

    /// <summary>
    /// Merges cues into overlapping windows.
    /// </summary>
    /// <remarks>
    /// A single cue is too small a unit to search. Subtitle files break lines on
    /// reading speed, not on sentences, so "You can't handle the truth" routinely
    /// spans two cues and an exact phrase search over individual cues would miss it.
    /// <para>
    /// The overlap is what stops a quote falling down the crack between two windows.
    /// At 50%, any phrase shorter than half a window is guaranteed to sit whole
    /// inside at least one of them.
    /// </para>
    /// </remarks>
    public static class CueWindowing
    {
        /// <summary>Target window size in words.</summary>
        public const int DefaultWindowWords = 40;

        /// <summary>
        /// Builds windows over a cleaned track.
        /// </summary>
        /// <param name="itemId">The item these cues belong to.</param>
        /// <param name="cues">The cleaned cues, in order.</param>
        /// <param name="windowWords">Target words per window.</param>
        /// <returns>The windows, in order.</returns>
        public static IReadOnlyList<QuoteWindow> Build(
            Guid itemId,
            IReadOnlyList<CleanCue> cues,
            int windowWords = DefaultWindowWords)
        {
            ArgumentNullException.ThrowIfNull(cues);

            var windows = new List<QuoteWindow>();
            if (cues.Count == 0)
            {
                return windows;
            }

            var target = Math.Max(8, windowWords);
            var wordCounts = cues.Select(c => Math.Max(1, CountWords(c.Text))).ToArray();

            var start = 0;
            while (start < cues.Count)
            {
                var words = 0;
                var end = start;

                while (end < cues.Count && words < target)
                {
                    words += wordCounts[end];
                    end++;
                }

                var span = cues.Skip(start).Take(end - start).ToList();
                windows.Add(new QuoteWindow(
                    itemId,
                    span[0].Start,
                    span[^1].End,
                    string.Join(' ', span.Select(c => c.Text)),
                    start,
                    span.Count));

                if (end >= cues.Count)
                {
                    break;
                }

                // Step forward by half the window. Advancing by at least one cue is
                // what guarantees termination when a single cue is longer than the
                // whole target — a monologue on one line would otherwise loop forever.
                start += Math.Max(1, (end - start) / 2);
            }

            return windows;
        }

        /// <summary>
        /// Pulls the lines around a hit, for showing it in context.
        /// </summary>
        /// <param name="cues">The cleaned cues for the item.</param>
        /// <param name="cueIndex">The cue that matched.</param>
        /// <param name="before">How many lines of lead-in.</param>
        /// <param name="after">How many lines after.</param>
        /// <returns>The surrounding lines, raw as they appeared.</returns>
        public static IReadOnlyList<string> Context(
            IReadOnlyList<CleanCue> cues,
            int cueIndex,
            int before = 1,
            int after = 2)
        {
            ArgumentNullException.ThrowIfNull(cues);

            if (cues.Count == 0)
            {
                return [];
            }

            var from = Math.Max(0, cueIndex - Math.Max(0, before));
            var to = Math.Min(cues.Count - 1, cueIndex + Math.Max(0, after));

            // Raw, not cleaned: the searcher should see the line as it was written.
            return cues.Skip(from).Take(to - from + 1).Select(c => c.Raw).ToList();
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
    }
}
