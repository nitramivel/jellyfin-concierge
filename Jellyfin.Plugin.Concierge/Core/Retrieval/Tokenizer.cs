using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace Jellyfin.Plugin.Concierge.Core.Retrieval
{
    /// <summary>
    /// Splits text into the tokens the lexical index matches on.
    /// </summary>
    /// <remarks>
    /// The same function runs over documents at index time and over queries at
    /// search time. That symmetry is the whole contract: a token written one way and
    /// looked up another simply never matches, and nothing errors to tell you.
    /// </remarks>
    public static class Tokenizer
    {
        /// <summary>
        /// Tokenizes text: case-folded, diacritic-folded, split on anything that is
        /// not a letter or digit, lightly de-pluralized.
        /// </summary>
        /// <param name="text">The text.</param>
        /// <returns>The tokens, in order, with duplicates kept.</returns>
        public static IReadOnlyList<string> Tokenize(string? text)
        {
            if (string.IsNullOrWhiteSpace(text))
            {
                return [];
            }

            var folded = FoldDiacritics(text);
            var tokens = new List<string>();
            var current = new StringBuilder();

            foreach (var c in folded)
            {
                // Letters and digits stay together, so "1990s" and "90s" survive as
                // single tokens — which is exactly what makes an era query match.
                if (char.IsLetterOrDigit(c))
                {
                    current.Append(char.ToLowerInvariant(c));
                    continue;
                }

                Flush(tokens, current);
            }

            Flush(tokens, current);
            return tokens;
        }

        /// <summary>
        /// Strips a trailing plural 's'.
        /// </summary>
        /// <remarks>
        /// Deliberately the only stemming rule. It buys the cases that matter for
        /// this corpus — "classics" ↔ "classic", "movies" ↔ "movie" — while staying
        /// too timid to cause the failures aggressive stemmers cause. Cutting "-ed"
        /// as well would fold "wicked" onto "wick" and quietly ruin every search for
        /// John Wick, which is the kind of bug that takes a day to find.
        /// <para>
        /// Tokens containing a digit are never stemmed, so "90s" stays "90s" rather
        /// than collapsing to the number 90.
        /// </para>
        /// </remarks>
        /// <param name="token">The token.</param>
        /// <returns>The stemmed token.</returns>
        public static string Stem(string token)
        {
            ArgumentNullException.ThrowIfNull(token);

            if (token.Length < 4 || !token.EndsWith('s'))
            {
                return token;
            }

            foreach (var c in token)
            {
                if (char.IsDigit(c))
                {
                    return token;
                }
            }

            // "class", "bus", "axis" — the 's' is part of the word, not a plural.
            if (token.EndsWith("ss", StringComparison.Ordinal)
                || token.EndsWith("us", StringComparison.Ordinal)
                || token.EndsWith("is", StringComparison.Ordinal))
            {
                return token;
            }

            return token[..^1];
        }

        /// <summary>
        /// Tokenizes and stems in one pass — what both the index and the query use.
        /// </summary>
        /// <param name="text">The text.</param>
        /// <returns>The stemmed tokens.</returns>
        public static IReadOnlyList<string> Terms(string? text)
        {
            var tokens = Tokenize(text);
            var terms = new List<string>(tokens.Count);
            foreach (var token in tokens)
            {
                terms.Add(Stem(token));
            }

            return terms;
        }

        private static void Flush(List<string> tokens, StringBuilder current)
        {
            if (current.Length > 0)
            {
                tokens.Add(current.ToString());
                current.Clear();
            }
        }

        /// <summary>
        /// Reduces accented characters to their base letters, so "Amélie" is findable
        /// by typing "amelie".
        /// </summary>
        private static string FoldDiacritics(string text)
        {
            var decomposed = text.Normalize(NormalizationForm.FormD);
            var builder = new StringBuilder(decomposed.Length);

            foreach (var c in decomposed)
            {
                if (CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark)
                {
                    builder.Append(c);
                }
            }

            return builder.ToString().Normalize(NormalizationForm.FormC);
        }
    }
}
