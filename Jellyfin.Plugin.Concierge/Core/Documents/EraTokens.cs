using System.Collections.Generic;
using System.Globalization;

namespace Jellyfin.Plugin.Concierge.Core.Documents
{
    /// <summary>
    /// Turns a production year into the words people actually use for it.
    /// </summary>
    /// <remarks>
    /// <b>Why this exists.</b> "nostalgic 90s classics" is a completely ordinary
    /// search, and without this it matches nothing lexically: the document says
    /// <c>1995</c> and the query says <c>90s</c>, which share no token. The vector
    /// half will carry "nostalgic" and "classics" but has no reliable grip on the
    /// decade, so the era half of the query is simply lost — and it is the half the
    /// user is most sure about.
    /// <para>
    /// Writing the decade into the document at index time fixes that for free, with
    /// no model in the loop. It is deliberately <em>not</em> a substitute for the
    /// plan pass's year filter (§4.3): that turns "90s" into a real
    /// <c>[1990,1999]</c> constraint and can be applied as a hard cut. This is the
    /// weaker, free version that keeps era queries working on the phase-1 path and
    /// keeps working when the budget is gone (hard rule 4).
    /// </para>
    /// <para>
    /// Emitted as index-side vocabulary only. The query side needs no matching
    /// transform: "90s" in a query already tokenizes to the same token this writes.
    /// </para>
    /// </remarks>
    public static class EraTokens
    {
        /// <summary>
        /// The spoken names of each decade, by its first year.
        /// </summary>
        /// <remarks>
        /// Short-form ("90s") and long-form ("nineties") both appear because both are
        /// ordinary search vocabulary and neither stems to the other.
        /// </remarks>
        private static readonly Dictionary<int, string[]> DecadeWords = new()
        {
            [1920] = ["20s", "twenties"],
            [1930] = ["30s", "thirties"],
            [1940] = ["40s", "forties"],
            [1950] = ["50s", "fifties"],
            [1960] = ["60s", "sixties"],
            [1970] = ["70s", "seventies"],
            [1980] = ["80s", "eighties"],
            [1990] = ["90s", "nineties"],
            [2000] = ["00s", "noughties", "aughts"],
            [2010] = ["10s", "twenty tens"],
            [2020] = ["20s", "twenties"],
        };

        /// <summary>
        /// Renders the searchable era vocabulary for a year: the year itself, the
        /// decade in full, and the ways people say it.
        /// </summary>
        /// <param name="year">The production year, or null.</param>
        /// <returns>Space-separated tokens, or empty when there is no year.</returns>
        /// <summary>
        /// The decade a year falls in, spelled the way people say it.
        /// </summary>
        /// <param name="year">The year, or null.</param>
        /// <returns>e.g. "1990s", or empty when there is no usable year.</returns>
        /// <remarks>
        /// Its own method rather than the second word of <see cref="Render"/>. A
        /// caller picking a token out of that string by position is a caller that
        /// breaks silently the first time the order changes.
        /// </remarks>
        public static string Decade(int? year)
            => year is { } value && value >= 1880 && value <= 2200
                ? (value / 10 * 10).ToString(CultureInfo.InvariantCulture) + "s"
                : string.Empty;

        public static string Render(int? year)
        {
            if (year is not { } value || value < 1880 || value > 2200)
            {
                return string.Empty;
            }

            var decade = value / 10 * 10;
            var tokens = new List<string>(5)
            {
                value.ToString(CultureInfo.InvariantCulture),
                decade.ToString(CultureInfo.InvariantCulture) + "s",
            };

            if (DecadeWords.TryGetValue(decade, out var words))
            {
                tokens.AddRange(words);
            }

            return string.Join(' ', tokens);
        }
    }
}
