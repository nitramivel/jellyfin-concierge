using System;
using System.Collections.Generic;
using System.Linq;
using Jellyfin.Plugin.Concierge.Core.Documents;

namespace Jellyfin.Plugin.Concierge.Core.Retrieval
{
    /// <summary>
    /// A field-weighted BM25 index over the library, in memory, with no dependency.
    /// </summary>
    /// <remarks>
    /// This half of retrieval is non-negotiable and is not a fallback for the vector
    /// half. Semantic search is bad at exactly the things people search for most
    /// confidently — proper nouns, rare names, exact titles — and a pure vector
    /// system fails embarrassingly on "the one with Toni Collette". Keyword matching
    /// is what catches that, for free.
    /// <para>
    /// Field weighting is applied to term frequencies rather than by scoring each
    /// field separately and adding (the full BM25F treatment). The simplification
    /// keeps one saturation curve over the whole document, which is the behaviour
    /// that matters: a term appearing in three fields should not earn three
    /// independent unsaturated scores.
    /// </para>
    /// </remarks>
    public sealed class Bm25Index : Query.INameDictionary
    {
        /// <summary>Term-frequency saturation. The usual default.</summary>
        public const double K1 = 1.2;

        /// <summary>Length normalization strength. The usual default.</summary>
        public const double B = 0.75;

        private readonly Guid[] _itemIds;
        private readonly double[] _lengths;
        private readonly double _averageLength;

        /// <summary>term → (document index, weighted term frequency).</summary>
        private readonly Dictionary<string, List<(int Doc, double Tf)>> _postings;

        /// <summary>
        /// Tokens drawn from titles, people and studios — the names a short query is
        /// likely to be the start of. Used by the router, never for scoring.
        /// </summary>
        private readonly HashSet<string> _nameTokens;

        private Bm25Index(
            Guid[] itemIds,
            double[] lengths,
            Dictionary<string, List<(int, double)>> postings,
            HashSet<string> nameTokens)
        {
            _itemIds = itemIds;
            _lengths = lengths;
            _postings = postings;
            _nameTokens = nameTokens;
            _averageLength = lengths.Length == 0 ? 1.0 : Math.Max(1.0, lengths.Average());
        }

        /// <summary>Gets how many documents are indexed.</summary>
        public int DocumentCount => _itemIds.Length;

        /// <summary>
        /// Builds the index.
        /// </summary>
        /// <param name="documents">The documents to index.</param>
        /// <returns>The index.</returns>
        public static Bm25Index Build(IReadOnlyList<ItemDocument> documents)
        {
            ArgumentNullException.ThrowIfNull(documents);

            var itemIds = new Guid[documents.Count];
            var lengths = new double[documents.Count];
            var postings = new Dictionary<string, List<(int, double)>>(StringComparer.Ordinal);
            var nameTokens = new HashSet<string>(StringComparer.Ordinal);

            for (var doc = 0; doc < documents.Count; doc++)
            {
                var document = documents[doc];
                itemIds[doc] = document.ItemId;

                var weighted = new Dictionary<string, double>(StringComparer.Ordinal);
                double length = 0;

                foreach (var section in document.RenderFields())
                {
                    var weight = FieldWeights.For(section.Field);
                    var terms = Tokenizer.Terms(section.Text);

                    foreach (var term in terms)
                    {
                        weighted.TryGetValue(term, out var existing);
                        weighted[term] = existing + weight;
                        length += weight;

                        if (section.Field is DocumentField.Title
                            or DocumentField.OriginalTitle
                            or DocumentField.People)
                        {
                            nameTokens.Add(term);
                        }
                    }
                }

                lengths[doc] = length;

                foreach (var (term, tf) in weighted)
                {
                    if (!postings.TryGetValue(term, out var list))
                    {
                        list = [];
                        postings[term] = list;
                    }

                    list.Add((doc, tf));
                }
            }

            return new Bm25Index(itemIds, lengths, postings, nameTokens);
        }

        /// <summary>
        /// Scores the query against every document that shares a term with it.
        /// </summary>
        /// <param name="query">The raw query text.</param>
        /// <param name="limit">How many results to return at most.</param>
        /// <returns>Items, best first. Empty when nothing shares a term.</returns>
        public IReadOnlyList<ScoredItem> Search(string query, int limit)
        {
            // Unique terms: a natural-language query repeats ordinary words, and
            // counting "the" three times would let sentence length shift the ranking.
            var terms = Tokenizer.Terms(query).Distinct(StringComparer.Ordinal).ToList();
            if (terms.Count == 0 || DocumentCount == 0)
            {
                return [];
            }

            var scores = new Dictionary<int, double>();

            foreach (var term in terms)
            {
                if (!_postings.TryGetValue(term, out var postings))
                {
                    continue;
                }

                var idf = InverseDocumentFrequency(postings.Count);

                foreach (var (doc, tf) in postings)
                {
                    var norm = 1.0 - B + (B * _lengths[doc] / _averageLength);
                    var contribution = idf * (tf * (K1 + 1.0)) / (tf + (K1 * norm));

                    scores.TryGetValue(doc, out var existing);
                    scores[doc] = existing + contribution;
                }
            }

            return scores
                .OrderByDescending(pair => pair.Value)
                .ThenBy(pair => _itemIds[pair.Key])
                .Take(limit)
                .Select(pair => new ScoredItem(_itemIds[pair.Key], pair.Value))
                .ToList();
        }

        /// <summary>
        /// Whether every token of the text begins a title, person or studio name in
        /// the library.
        /// </summary>
        /// <remarks>
        /// The router's free test for "this is somebody typing a title they already
        /// know" — <c>bla</c>, <c>blade</c>, <c>blade run</c>. Costs a dictionary
        /// scan and no model call.
        /// </remarks>
        /// <param name="text">The query text.</param>
        /// <returns>True when the query looks like the start of a name we hold.</returns>
        public bool LooksLikeKnownName(string text)
        {
            var tokens = Tokenizer.Tokenize(text);
            if (tokens.Count == 0)
            {
                return false;
            }

            foreach (var token in tokens)
            {
                var stem = Tokenizer.Stem(token);
                if (_nameTokens.Contains(stem) || _nameTokens.Contains(token))
                {
                    continue;
                }

                var isPrefix = false;
                foreach (var name in _nameTokens)
                {
                    if (name.StartsWith(token, StringComparison.Ordinal))
                    {
                        isPrefix = true;
                        break;
                    }
                }

                if (!isPrefix)
                {
                    return false;
                }
            }

            return true;
        }

        /// <summary>
        /// Probabilistic IDF, in the form that cannot go negative for a term held by
        /// more than half the corpus.
        /// </summary>
        private double InverseDocumentFrequency(int documentFrequency)
            => Math.Log(1.0 + ((DocumentCount - documentFrequency + 0.5) / (documentFrequency + 0.5)));
    }
}
