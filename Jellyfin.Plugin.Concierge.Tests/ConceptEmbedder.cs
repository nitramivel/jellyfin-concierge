using System;
using System.Collections.Generic;
using Jellyfin.Plugin.Concierge.Core.Retrieval;

namespace Jellyfin.Plugin.Concierge.Tests
{
    /// <summary>
    /// A deterministic stand-in for an embedding model, over a small hand-built
    /// concept space.
    /// </summary>
    /// <remarks>
    /// <b>What this does and does not prove.</b> A real embedding model places
    /// "harrowing" near "bleak" because it was trained to; this places them together
    /// because the table below says so. So a test using it demonstrates that
    /// <em>given</em> an embedder that groups related language, the pipeline
    /// — row collapsing, fusion, weighting — surfaces the right items. It says
    /// nothing about whether any particular real model groups them well. That
    /// question is what <c>eval/</c> exists to answer, and it needs a real model and
    /// the owner's real library.
    /// <para>
    /// It is used instead of random vectors because random vectors would make the
    /// vector half pure noise, and a fusion test where one input is noise only ever
    /// tests the other input.
    /// </para>
    /// </remarks>
    internal static class ConceptEmbedder
    {
        private static readonly string[] Concepts =
        [
            "dark", "twisted", "violent", "tense", "bleak",
            "funny", "warm", "comfort", "whimsical",
            "nostalgic", "adventure", "wonder",
            "scifi", "crime", "romance", "surreal", "noir",
        ];

        /// <summary>
        /// Which concepts a word points at. Both documents and queries are projected
        /// through this same table — an asymmetric lexicon would be a bug of exactly
        /// the kind the query/document prefixes exist to prevent.
        /// </summary>
        private static readonly Dictionary<string, string[]> Lexicon = new(StringComparer.Ordinal)
        {
            // Tone: dark
            ["dark"] = ["dark", "bleak"],
            ["darker"] = ["dark", "bleak"],
            ["grim"] = ["dark", "bleak"],
            ["bleak"] = ["bleak", "dark"],
            ["harrowing"] = ["dark", "bleak", "tense"],
            ["disturbing"] = ["dark", "twisted"],
            ["unsettling"] = ["dark", "twisted", "tense"],
            ["nasty"] = ["dark", "violent"],
            ["brutal"] = ["violent", "dark"],
            ["violent"] = ["violent", "dark"],
            ["dread"] = ["dark", "tense"],
            ["macabre"] = ["dark", "twisted"],
            ["sinister"] = ["dark", "twisted"],

            // Tone: twisted
            ["twisted"] = ["twisted", "dark"],
            ["warped"] = ["twisted", "dark"],
            ["perverse"] = ["twisted", "dark"],
            ["puzzle"] = ["twisted", "surreal"],
            ["unreliable"] = ["twisted", "surreal"],
            ["disorienting"] = ["twisted", "surreal"],
            ["psychological"] = ["twisted", "tense"],
            ["revenge"] = ["violent", "dark"],
            ["predatory"] = ["dark", "tense"],
            ["killer"] = ["crime", "dark"],
            ["serial"] = ["crime", "dark"],
            ["decay"] = ["dark", "bleak"],
            ["horrifying"] = ["dark", "twisted"],

            // Tension
            ["tense"] = ["tense"],
            ["suspense"] = ["tense"],
            ["thriller"] = ["tense", "crime"],
            ["claustrophobic"] = ["tense", "dark"],

            // Tone: warm
            ["warm"] = ["warm", "comfort"],
            ["gentle"] = ["warm", "comfort"],
            ["kind"] = ["warm"],
            ["cosy"] = ["comfort", "warm"],
            ["cozy"] = ["comfort", "warm"],
            ["comfort"] = ["comfort", "warm"],
            ["feelgood"] = ["warm", "funny"],
            ["heartwarming"] = ["warm", "comfort"],
            ["sweet"] = ["warm"],
            ["charming"] = ["warm", "whimsical"],
            ["cheerful"] = ["warm", "funny"],
            ["uplifting"] = ["warm", "comfort"],
            ["sunny"] = ["warm", "funny"],
            ["light"] = ["warm", "funny"],
            ["family"] = ["warm", "comfort"],

            // Humour
            ["funny"] = ["funny"],
            ["comedy"] = ["funny"],
            ["comic"] = ["funny"],
            ["hilarious"] = ["funny"],
            ["silly"] = ["funny"],
            ["deadpan"] = ["funny", "dark"],
            ["absurd"] = ["funny", "surreal"],
            ["shaggy"] = ["funny"],
            ["laugh"] = ["funny"],

            // Era and affection
            ["nostalgic"] = ["nostalgic"],
            ["nostalgia"] = ["nostalgic"],
            ["classic"] = ["nostalgic"],
            ["childhood"] = ["nostalgic", "warm"],
            ["retro"] = ["nostalgic"],
            ["throwback"] = ["nostalgic"],

            // Mode
            ["adventure"] = ["adventure", "wonder"],
            ["wonder"] = ["wonder", "adventure"],
            ["spectacle"] = ["adventure", "wonder"],
            ["blockbuster"] = ["adventure"],
            ["scifi"] = ["scifi"],
            ["dystopian"] = ["scifi", "bleak"],
            ["noir"] = ["noir", "dark"],
            ["crime"] = ["crime"],
            ["romance"] = ["romance"],
            ["romantic"] = ["romance", "warm"],
            ["surreal"] = ["surreal"],
            ["whimsical"] = ["whimsical", "warm"],
            ["playful"] = ["whimsical", "funny"],
            ["melancholy"] = ["bleak", "warm"],
            ["bittersweet"] = ["bleak", "warm"],
        };

        /// <summary>
        /// Projects text onto the concept space.
        /// </summary>
        /// <remarks>
        /// Runs the production <see cref="Tokenizer"/> rather than splitting on
        /// spaces, so document text and query text are tokenized exactly as the real
        /// pipeline would.
        /// </remarks>
        /// <param name="text">The text.</param>
        /// <returns>A vector over <see cref="Concepts"/>.</returns>
        public static float[] Embed(string? text)
        {
            var vector = new float[Concepts.Length];
            var index = new Dictionary<string, int>(StringComparer.Ordinal);
            for (var i = 0; i < Concepts.Length; i++)
            {
                index[Concepts[i]] = i;
            }

            foreach (var token in Tokenizer.Tokenize(text))
            {
                if (!Lexicon.TryGetValue(token, out var concepts))
                {
                    continue;
                }

                foreach (var concept in concepts)
                {
                    // The first concept listed is the primary sense and counts double,
                    // so "brutal" reads as violence tinged with darkness rather than as
                    // an equal mix of both.
                    vector[index[concept]] += concept == concepts[0] ? 2f : 1f;
                }
            }

            return vector;
        }
    }
}
