using System;
using System.Collections.Generic;
using System.Linq;
using Jellyfin.Plugin.Concierge.Core.Retrieval;

namespace Jellyfin.Plugin.Concierge.Core.Subtitles
{
    /// <summary>One line of dialogue that matched.</summary>
    /// <param name="ItemId">The film or episode.</param>
    /// <param name="Window">Which window matched, for pulling context back out.</param>
    /// <param name="Start">Where the line is.</param>
    /// <param name="End">Where the span ends.</param>
    /// <param name="Text">The matched dialogue.</param>
    /// <param name="Score">1.0 for a verbatim hit, below that for a near miss.</param>
    /// <param name="Exact">Whether the phrase was found word for word.</param>
    public sealed record QuoteHit(
        Guid ItemId,
        int Window,
        TimeSpan Start,
        TimeSpan End,
        string Text,
        double Score,
        bool Exact);

    /// <summary>
    /// A positional phrase index over subtitle windows. <b>Stage 1 and 2 of quote
    /// search: no embeddings, no model, no cost.</b>
    /// </summary>
    /// <remarks>
    /// People quote verbatim. "I'm walking here", "You can't handle the truth" — exact
    /// phrase matching answers those perfectly and it is the cheapest thing in the
    /// entire plugin. It ships before anything semantic, and it may well be enough.
    /// <para>
    /// People also quote <em>wrong</em>: "Luke, I am your father" is not a line in any
    /// Star Wars film. Character-trigram similarity catches near misses, still for
    /// free, before anything is spent.
    /// </para>
    /// <para>
    /// <b>Hand-rolled rather than SQLite FTS5</b>, which the plan recommends but which
    /// is a dependency — and hard rule 13 says ask first. Films-only is what makes
    /// hand-rolled viable: roughly 73,000 windows, which this handles comfortably.
    /// Wanting episodes indexed is what would make FTS5 the honest answer, and that is
    /// a conversation to have before writing it rather than after.
    /// </para>
    /// </remarks>
    public sealed class PhraseIndex
    {
        /// <summary>Words shorter than a phrase this long are matched literally only.</summary>
        private const int MinimumTrigramLength = 6;

        private readonly QuoteWindow[] _windows;

        /// <summary>
        /// term → (window, position within that window).
        /// </summary>
        /// <remarks>
        /// A set rather than a list, because the phrase walk asks "is this term at
        /// exactly this position" once per term per candidate. Over three million
        /// postings a linear scan there would make an exact-phrase search take
        /// minutes; membership has to be constant time.
        /// </remarks>
        private readonly Dictionary<string, HashSet<(int Window, int Position)>> _postings;

        private PhraseIndex(QuoteWindow[] windows, Dictionary<string, HashSet<(int, int)>> postings)
        {
            _windows = windows;
            _postings = postings;
        }

        /// <summary>Gets how many windows are indexed.</summary>
        public int WindowCount => _windows.Length;

        /// <summary>
        /// Builds the index.
        /// </summary>
        /// <param name="windows">Every window across every indexed item.</param>
        /// <returns>The index.</returns>
        public static PhraseIndex Build(IReadOnlyList<QuoteWindow> windows)
        {
            ArgumentNullException.ThrowIfNull(windows);

            var postings = new Dictionary<string, HashSet<(int, int)>>(StringComparer.Ordinal);

            for (var w = 0; w < windows.Count; w++)
            {
                // Tokenized but NOT stemmed. A quote is matched as it was said, and
                // stemming would fold "walking" onto "walk" and quietly turn an exact
                // match into an approximate one.
                var tokens = Tokenizer.Tokenize(windows[w].Text);

                for (var p = 0; p < tokens.Count; p++)
                {
                    if (!postings.TryGetValue(tokens[p], out var set))
                    {
                        set = [];
                        postings[tokens[p]] = set;
                    }

                    set.Add((w, p));
                }
            }

            return new PhraseIndex([.. windows], postings);
        }

        /// <summary>
        /// Searches for a quote: verbatim first, then near misses.
        /// </summary>
        /// <param name="phrase">What the searcher typed, quotes already stripped.</param>
        /// <param name="limit">How many hits to return.</param>
        /// <param name="allowFuzzy">Whether to fall back to near matches.</param>
        /// <returns>Hits, best first, at most one per item.</returns>
        public IReadOnlyList<QuoteHit> Search(string? phrase, int limit = 20, bool allowFuzzy = true)
        {
            var terms = Tokenizer.Tokenize(phrase);
            if (terms.Count == 0 || _windows.Length == 0)
            {
                return [];
            }

            var exact = FindExact(terms);
            if (exact.Count > 0 || !allowFuzzy)
            {
                return Rank(exact, limit);
            }

            return Rank(FindFuzzy(phrase!, terms), limit);
        }

        /// <summary>
        /// Finds windows containing the terms consecutively.
        /// </summary>
        /// <remarks>
        /// Walks the rarest term's postings rather than the first term's: a phrase
        /// beginning with "the" would otherwise start from tens of thousands of
        /// positions when a later word narrows it to a handful.
        /// </remarks>
        private List<QuoteHit> FindExact(IReadOnlyList<string> terms)
        {
            var hits = new List<QuoteHit>();

            HashSet<(int Window, int Position)>? rarest = null;
            var rarestAt = 0;

            for (var i = 0; i < terms.Count; i++)
            {
                if (!_postings.TryGetValue(terms[i], out var postings))
                {
                    // A word nobody in the library says: no window can contain the
                    // whole phrase.
                    return hits;
                }

                if (rarest is null || postings.Count < rarest.Count)
                {
                    rarest = postings;
                    rarestAt = i;
                }
            }

            foreach (var (window, position) in rarest!)
            {
                var start = position - rarestAt;
                if (start < 0)
                {
                    continue;
                }

                if (MatchesAt(terms, window, start))
                {
                    hits.Add(new QuoteHit(
                        _windows[window].ItemId,
                        window,
                        _windows[window].Start,
                        _windows[window].End,
                        _windows[window].Text,
                        1.0,
                        true));
                }
            }

            return hits;
        }

        private bool MatchesAt(IReadOnlyList<string> terms, int window, int start)
        {
            for (var i = 0; i < terms.Count; i++)
            {
                if (!_postings.TryGetValue(terms[i], out var postings))
                {
                    return false;
                }

                var wanted = (window, start + i);
                if (!postings.Contains(wanted))
                {
                    return false;
                }
            }

            return true;
        }

        /// <summary>
        /// Scores near misses by character-trigram overlap.
        /// </summary>
        /// <remarks>
        /// Only windows sharing at least one query word are considered, which keeps
        /// this from being a scan of the whole library. Trigrams rather than words
        /// because a misremembered quote is usually right about the sounds and wrong
        /// about the words.
        /// </remarks>
        private List<QuoteHit> FindFuzzy(string phrase, IReadOnlyList<string> terms)
        {
            var candidates = new HashSet<int>();
            foreach (var term in terms)
            {
                if (_postings.TryGetValue(term, out var postings))
                {
                    foreach (var (window, _) in postings)
                    {
                        candidates.Add(window);
                    }
                }
            }

            if (candidates.Count == 0)
            {
                return [];
            }

            var wanted = Trigrams(phrase);
            if (wanted.Count < MinimumTrigramLength - 2)
            {
                // Too short to compare meaningfully — three characters of overlap
                // would match half the library.
                return [];
            }

            var hits = new List<QuoteHit>();

            foreach (var window in candidates)
            {
                var similarity = Similarity(wanted, Trigrams(_windows[window].Text));

                // Below this it is not a misremembered quote, it is a different line
                // that happens to share a word.
                if (similarity >= 0.28)
                {
                    hits.Add(new QuoteHit(
                        _windows[window].ItemId,
                        window,
                        _windows[window].Start,
                        _windows[window].End,
                        _windows[window].Text,
                        similarity,
                        false));
                }
            }

            return hits;
        }

        /// <summary>
        /// Best hit per item, best first.
        /// </summary>
        /// <remarks>
        /// One per item because windows overlap by design: a verbatim quote sits whole
        /// inside two of them, and without this the same line would fill the results
        /// twice over.
        /// </remarks>
        private static IReadOnlyList<QuoteHit> Rank(List<QuoteHit> hits, int limit)
        {
            return hits
                .GroupBy(h => h.ItemId)
                .Select(g => g.OrderByDescending(h => h.Score).ThenBy(h => h.Start).First())
                .OrderByDescending(h => h.Score)
                .ThenBy(h => h.Start)
                .Take(Math.Max(1, limit))
                .ToList();
        }

        private static HashSet<string> Trigrams(string text)
        {
            var folded = string.Join(' ', Tokenizer.Tokenize(text));
            var grams = new HashSet<string>(StringComparer.Ordinal);

            for (var i = 0; i + 3 <= folded.Length; i++)
            {
                grams.Add(folded.Substring(i, 3));
            }

            return grams;
        }

        /// <summary>
        /// Overlap as a fraction of the <em>query's</em> trigrams, not of both.
        /// </summary>
        /// <remarks>
        /// Deliberately asymmetric. A short quote inside a long window should score
        /// high — that is the normal case, since a window is forty words and a quote
        /// is five. Jaccard would punish exactly the shape we are looking for.
        /// </remarks>
        private static double Similarity(HashSet<string> query, HashSet<string> window)
        {
            if (query.Count == 0)
            {
                return 0;
            }

            var shared = query.Count(window.Contains);
            return (double)shared / query.Count;
        }
    }
}
