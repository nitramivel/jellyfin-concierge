using System;
using System.Globalization;
using System.Text;

namespace Jellyfin.Plugin.Concierge.Core.Query
{
    /// <summary>
    /// Reduces a query to the form the cache keys on.
    /// </summary>
    /// <remarks>
    /// Deliberately conservative. Every pair of queries this folds together will be
    /// served the same answer, so the bar is "these are unambiguously the same
    /// search" — not "these are similar". Stemming or synonym folding here would
    /// hand somebody a cached answer to a question they did not ask.
    /// </remarks>
    public static class QueryNormalizer
    {
        /// <summary>
        /// Lowercases, collapses whitespace, and strips trailing punctuation.
        /// </summary>
        /// <param name="query">The raw query.</param>
        /// <returns>The normalized form.</returns>
        public static string Normalize(string? query)
        {
            if (string.IsNullOrWhiteSpace(query))
            {
                return string.Empty;
            }

            var text = new StringBuilder(query.Length);
            var lastWasSpace = false;

            foreach (var c in query.Trim())
            {
                if (char.IsWhiteSpace(c))
                {
                    if (!lastWasSpace)
                    {
                        text.Append(' ');
                        lastWasSpace = true;
                    }

                    continue;
                }

                lastWasSpace = false;
                text.Append(char.ToLower(c, CultureInfo.InvariantCulture));
            }

            // Only trailing punctuation, and only the kinds that carry no meaning
            // here. A quoted query means dialogue search, so quotes are left alone.
            return text.ToString().TrimEnd('.', '!', '?', ',', ';', ' ');
        }

        /// <summary>
        /// The full cache key.
        /// </summary>
        /// <remarks>
        /// <b>The user and the index generation are both part of it.</b> Watch-state
        /// filters make results per-user, so one person's "that I haven't seen" must
        /// never be served to somebody else. And every index write bumps the
        /// generation, which invalidates every cached answer at once without a sweep.
        /// </remarks>
        /// <param name="query">The raw query.</param>
        /// <param name="userId">Who is searching, or null.</param>
        /// <param name="indexGeneration">The index generation the answer came from.</param>
        /// <returns>The key.</returns>
        public static string Key(string? query, Guid? userId, long indexGeneration)
        {
            return string.Create(
                CultureInfo.InvariantCulture,
                $"{indexGeneration}|{userId?.ToString("N", CultureInfo.InvariantCulture) ?? "-"}|{Normalize(query)}");
        }
    }
}
