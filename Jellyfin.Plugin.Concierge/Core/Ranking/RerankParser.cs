using System;
using System.Collections.Generic;
using System.Text.Json;
using Jellyfin.Plugin.Concierge.Core.Llm;

namespace Jellyfin.Plugin.Concierge.Core.Ranking
{
    /// <summary>One shortlist position after re-ranking.</summary>
    /// <param name="Index">The batch-local index of the item.</param>
    /// <param name="Why">
    /// The model's one-clause reason, or empty when it gave none. Empty is normal and
    /// harmless — the item keeps its place and simply shows no explanation.
    /// </param>
    public sealed record RerankedItem(int Index, string Why);

    /// <summary>What the re-rank pass changed.</summary>
    /// <param name="Order">Every shortlist index exactly once, in the order to show.</param>
    /// <param name="Ranked">How many the model actually placed.</param>
    /// <param name="Omitted">How many it left out, which kept their fused positions.</param>
    /// <param name="Invented">How many indexes it named that were not on the shortlist.</param>
    public sealed record RerankOutcome(
        IReadOnlyList<RerankedItem> Order,
        int Ranked,
        int Omitted,
        int Invented);

    /// <summary>
    /// Reads the re-rank answer as a <b>preference over the shortlist, never a
    /// replacement for it</b>.
    /// </summary>
    /// <remarks>
    /// Hard rule 7, and it is not a nicety. Anything the model omits, repeats or
    /// invents leaves the fused order in place for those items — so the worst a bad
    /// answer can do is waste the call. The alternative is a model quietly deleting a
    /// correct result from somebody's search, which nobody would ever notice or be
    /// able to report.
    /// <para>
    /// Curator learned this on its recommendation parser. There is no reason to
    /// relearn it here.
    /// </para>
    /// </remarks>
    public static class RerankParser
    {
        /// <summary>The longest explanation kept, before it stops being one clause.</summary>
        public const int MaxWhyLength = 120;

        /// <summary>
        /// Parses a re-rank response into a complete ordering of the shortlist.
        /// </summary>
        /// <param name="responseText">The raw model output.</param>
        /// <param name="shortlistSize">How many candidates were sent.</param>
        /// <returns>
        /// Every index from <c>0..shortlistSize-1</c>, exactly once. Never throws:
        /// an unreadable answer returns the fused order untouched.
        /// </returns>
        public static RerankOutcome Parse(string? responseText, int shortlistSize)
        {
            if (shortlistSize <= 0)
            {
                return new RerankOutcome([], 0, 0, 0);
            }

            var placed = new List<RerankedItem>(shortlistSize);
            var seen = new bool[shortlistSize];
            var invented = 0;

            if (!string.IsNullOrWhiteSpace(responseText))
            {
                try
                {
                    using var document = JsonDocument.Parse(JsonResponse.ExtractObject(responseText));

                    if (document.RootElement.TryGetProperty("order", out var order)
                        && order.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var entry in order.EnumerateArray())
                        {
                            var index = ReadIndex(entry);
                            if (index is null)
                            {
                                continue;
                            }

                            // Outside the shortlist: the model referred to something
                            // that was never sent. Discarding it is what makes it
                            // structurally impossible to return an item the searcher
                            // does not own (hard rule 1).
                            if (index < 0 || index >= shortlistSize)
                            {
                                invented++;
                                continue;
                            }

                            // A repeat cannot mean anything — the item already has a
                            // position, and the second mention would displace something
                            // else for no stated reason.
                            if (seen[index.Value])
                            {
                                continue;
                            }

                            seen[index.Value] = true;
                            placed.Add(new RerankedItem(index.Value, ReadWhy(entry)));
                        }
                    }
                }
                catch (Exception ex) when (ex is FormatException or JsonException)
                {
                    // Unreadable. The fused order below is a perfectly good answer.
                }
            }

            // Everything the model did not place keeps its fused position, appended
            // in the order retrieval produced. This is the whole rule: a silent
            // omission costs an item its promotion, never its existence.
            var omitted = 0;
            for (var i = 0; i < shortlistSize; i++)
            {
                if (!seen[i])
                {
                    placed.Add(new RerankedItem(i, string.Empty));
                    omitted++;
                }
            }

            return new RerankOutcome(placed, placed.Count - omitted, omitted, invented);
        }

        private static int? ReadIndex(JsonElement entry)
        {
            // Accepts both {"i":0,"why":"..."} and a bare 0, because a model told to
            // return an ordering will sometimes just return the ordering.
            if (entry.ValueKind == JsonValueKind.Number && entry.TryGetInt32(out var bare))
            {
                return bare;
            }

            if (entry.ValueKind != JsonValueKind.Object)
            {
                return null;
            }

            foreach (var name in new[] { "i", "index" })
            {
                if (entry.TryGetProperty(name, out var value)
                    && value.ValueKind == JsonValueKind.Number
                    && value.TryGetInt32(out var number))
                {
                    return number;
                }
            }

            return null;
        }

        private static string ReadWhy(JsonElement entry)
        {
            if (entry.ValueKind != JsonValueKind.Object)
            {
                return string.Empty;
            }

            foreach (var name in new[] { "why", "reason" })
            {
                if (entry.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String)
                {
                    var why = (value.GetString() ?? string.Empty).Trim();
                    return why.Length <= MaxWhyLength ? why : why[..MaxWhyLength].TrimEnd() + "…";
                }
            }

            return string.Empty;
        }
    }
}
