using System;
using System.Collections.Generic;
using System.Linq;
using MediaBrowser.Model.Entities;

namespace Jellyfin.Plugin.Concierge.Core.Subtitles
{
    /// <summary>Why a track was chosen, or why an item has none.</summary>
    /// <param name="Stream">The chosen track, or null.</param>
    /// <param name="Reason">
    /// A short phrase for the coverage report. When nothing was chosen this is what
    /// the owner needs to read to know whether it is fixable.
    /// </param>
    public sealed record TrackChoice(MediaStream? Stream, string Reason)
    {
        /// <summary>Gets whether a usable track was found.</summary>
        public bool Found => Stream is not null;
    }

    /// <summary>
    /// Picks the one subtitle track worth indexing for an item.
    /// </summary>
    /// <remarks>
    /// <b>The rule that matters most is rejecting forced tracks.</b> A forced track
    /// carries only the foreign-language lines — a few dozen cues for a whole film.
    /// Indexing one looks exactly like success: the item gets subtitles, the coverage
    /// report counts it, and the film can never be found by anything anyone actually
    /// says in it.
    /// </remarks>
    public static class TrackSelector
    {
        /// <summary>
        /// Chooses a track.
        /// </summary>
        /// <param name="streams">Every media stream on the item.</param>
        /// <param name="language">Preferred three-letter or two-letter language code.</param>
        /// <returns>The choice, with a reason either way.</returns>
        public static TrackChoice Choose(IReadOnlyList<MediaStream>? streams, string language = "en")
        {
            if (streams is null || streams.Count == 0)
            {
                return new TrackChoice(null, "no media streams");
            }

            var subtitles = streams.Where(s => s.Type == MediaStreamType.Subtitle).ToList();
            if (subtitles.Count == 0)
            {
                return new TrackChoice(null, "no subtitles");
            }

            // Non-negotiable: image subtitles cannot be read without OCR, which is a
            // dependency, a GPU and an error rate. The cheap fix is downloading an
            // external text track, which is the owner's call and not ours.
            var text = subtitles.Where(s => s.IsTextSubtitleStream).ToList();
            if (text.Count == 0)
            {
                return new TrackChoice(null, "image-only subtitles (PGS or VobSub)");
            }

            var usable = text.Where(s => !s.IsForced).ToList();
            if (usable.Count == 0)
            {
                return new TrackChoice(null, "only a forced track, which carries a few dozen lines at most");
            }

            var preferred = usable.Where(s => Matches(s.Language, language)).ToList();
            var pool = preferred.Count > 0 ? preferred : usable;
            var languageNote = preferred.Count > 0 ? string.Empty : " (not in the preferred language)";

            // SDH is worse than a clean track because its annotations have to be
            // stripped, but it is better than nothing by a wide margin — so it is
            // deprioritised rather than excluded.
            var clean = pool.Where(s => !s.IsHearingImpaired).ToList();
            var sdhNote = clean.Count > 0 ? string.Empty : " (hearing-impaired track, annotations stripped)";
            var candidates = clean.Count > 0 ? clean : pool;

            // External last as a tiebreak, only because it needs no extraction —
            // and §6.7's caveat is why it is not preferred outright: external files
            // are sometimes seconds out of sync, and a deep link to the wrong moment
            // is the most visible way this feature can fail.
            var chosen = candidates
                .OrderByDescending(s => s.IsExternal)
                .ThenBy(s => s.Index)
                .First();

            return new TrackChoice(
                chosen,
                (chosen.IsExternal ? "external text track" : "embedded text track") + languageNote + sdhNote);
        }

        private static bool Matches(string? streamLanguage, string wanted)
        {
            if (string.IsNullOrWhiteSpace(streamLanguage) || string.IsNullOrWhiteSpace(wanted))
            {
                return false;
            }

            // Tracks are tagged "en", "eng" and occasionally "en-US" in one library,
            // so compare on the leading two characters rather than for equality.
            return streamLanguage.StartsWith(wanted[..Math.Min(2, wanted.Length)], StringComparison.OrdinalIgnoreCase)
                || wanted.StartsWith(streamLanguage[..Math.Min(2, streamLanguage.Length)], StringComparison.OrdinalIgnoreCase);
        }
    }
}
