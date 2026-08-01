using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using Jellyfin.Plugin.Concierge.Core.Documents;
using Jellyfin.Plugin.Concierge.Core.Retrieval;
using Xunit;
using Xunit.Abstractions;

namespace Jellyfin.Plugin.Concierge.Tests
{
    /// <summary>
    /// Runs the real lexical index over a real exported index, to answer
    /// "why did this query not find that film?".
    /// </summary>
    /// <remarks>
    /// Skipped unless <c>CONCIERGE_INDEX_DIR</c> points at a directory holding a
    /// copy of <c>docs.json</c> and <c>enrichment.json</c> from a live server. It is
    /// a diagnostic, not a unit test: it asserts nothing about quality, it just
    /// reports what the keyword half actually does.
    /// <para>
    /// Only the lexical half can be reproduced offline — the vector half needs the
    /// embedding model that built the index. That limitation is itself informative:
    /// if a query scores zero here, whatever the user saw came entirely from the
    /// semantic side.
    /// </para>
    /// </remarks>
    public class LiveIndexProbe
    {
        private readonly ITestOutputHelper _output;

        public LiveIndexProbe(ITestOutputHelper output) => _output = output;

        private static string? Directory => Environment.GetEnvironmentVariable("CONCIERGE_INDEX_DIR");

        private static List<ItemDocument> Load(string directory)
        {
            var options = new JsonSerializerOptions();
            var docs = JsonSerializer.Deserialize<List<ItemDocument>>(
                File.ReadAllText(Path.Combine(directory, "docs.json")), options)!;
            var stored = JsonSerializer.Deserialize<List<StoredEnrichment>>(
                File.ReadAllText(Path.Combine(directory, "enrichment.json")), options)!;

            var byItem = stored.ToDictionary(e => e.ItemId, e => e.Enrichment);
            return docs
                .Select(d => byItem.TryGetValue(d.ItemId, out var e) ? d with { Enrichment = e } : d)
                .ToList();
        }

        [Fact]
        public void Probe()
        {
            // A plain early return rather than a skip attribute: that would mean a
            // new package, and hard rule 13 says ask first. A diagnostic is not worth
            // a dependency.
            if (string.IsNullOrWhiteSpace(Directory))
            {
                return;
            }

            var documents = Load(Directory!);
            var index = Bm25Index.Build(documents);
            var titles = documents.ToDictionary(d => d.ItemId, d => d.Title);

            _output.WriteLine($"{documents.Count} documents, "
                + $"{documents.Count(d => d.Enrichment is { IsEmpty: false })} enriched");

            // Query paired with the titles the owner expected it to surface.
            var cases = new (string Query, string[] Expected)[]
            {
                ("robots", ["Love, Death & Robots", "Mr. Robot"]),
                ("death love", ["Love, Death & Robots"]),
                ("sexy", ["Fifty Shades of Grey"]),
                ("erotic", ["Fifty Shades of Grey"]),
                ("bdsm", ["Fifty Shades of Grey"]),
                ("steamy romance", ["Fifty Shades of Grey"]),
            };

            foreach (var (query, expected) in cases)
            {
                var top = index.Search(query, 8);
                _output.WriteLine(string.Empty);
                _output.WriteLine($"\"{query}\"");

                foreach (var (hit, rank) in top.Select((h, i) => (h, i + 1)))
                {
                    _output.WriteLine($"    {rank}. {titles[hit.ItemId]}  ({hit.Score:F3})");
                }

                var all = index.Search(query, documents.Count).ToList();
                foreach (var want in expected)
                {
                    var match = documents.FirstOrDefault(
                        d => d.Title.Contains(want, StringComparison.OrdinalIgnoreCase));
                    if (match is null)
                    {
                        _output.WriteLine($"    [{want}] not in the library");
                        continue;
                    }

                    var position = all.FindIndex(h => h.ItemId == match.ItemId);
                    _output.WriteLine(position < 0
                        ? $"    [{want}] NOT RETURNED by the keyword half"
                        : $"    [{want}] keyword rank {position + 1}");
                }
            }
        }
    }
}
