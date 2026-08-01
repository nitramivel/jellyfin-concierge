using System;
using System.Collections.Generic;
using System.Text.Json;
using Jellyfin.Plugin.Concierge.Core.Llm;

namespace Jellyfin.Plugin.Concierge.Core.Documents
{
    /// <summary>
    /// Reads the enrichment pass's answer back into <see cref="Enrichment"/> values,
    /// keyed by batch position.
    /// </summary>
    /// <remarks>
    /// Two rules govern everything here.
    /// <para>
    /// <b>An index outside the batch is discarded</b> (hard rule 1). The model is
    /// only ever given <c>0..n-1</c>, so anything else is a hallucinated reference
    /// and mapping it back would attach one film's plot to another's row.
    /// </para>
    /// <para>
    /// <b>Nothing is better than invention</b> (hard rule 14). <c>known: false</c>,
    /// a missing premise, or empty lists all produce <see cref="Enrichment.Empty"/> —
    /// a real stored value meaning "asked, nothing to say", not an error and not a
    /// reason to retry forever.
    /// </para>
    /// </remarks>
    public static class EnrichmentParser
    {
        /// <summary>The most phrasings kept for one item, however many are returned.</summary>
        public const int MaxAsksPerItem = 12;

        /// <summary>
        /// Parses a batch response.
        /// </summary>
        /// <param name="responseText">The raw model output.</param>
        /// <param name="batchSize">How many items were sent.</param>
        /// <returns>Enrichment by batch index. Items the model omitted are absent.</returns>
        /// <exception cref="FormatException">The response held no usable JSON object.</exception>
        public static IReadOnlyDictionary<int, Enrichment> Parse(string responseText, int batchSize)
        {
            ArgumentNullException.ThrowIfNull(responseText);

            var json = JsonResponse.ExtractObject(responseText);
            using var document = JsonDocument.Parse(json);

            var results = new Dictionary<int, Enrichment>();

            if (!document.RootElement.TryGetProperty("items", out var items)
                || items.ValueKind != JsonValueKind.Array)
            {
                return results;
            }

            foreach (var entry in items.EnumerateArray())
            {
                if (entry.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }

                if (!entry.TryGetProperty("i", out var indexElement)
                    || indexElement.ValueKind != JsonValueKind.Number
                    || !indexElement.TryGetInt32(out var index))
                {
                    continue;
                }

                // Outside the batch, or a repeat of one already read. Either way this
                // cannot be mapped back to an item we actually sent.
                if (index < 0 || index >= batchSize || results.ContainsKey(index))
                {
                    continue;
                }

                results[index] = ReadEnrichment(entry);
            }

            return results;
        }

        private static Enrichment ReadEnrichment(JsonElement entry)
        {
            // An explicit "I don't know this one" is the answer the prompt asks for
            // and is stored as-is. Re-asking would spend money to get the same answer.
            if (entry.TryGetProperty("known", out var known)
                && known.ValueKind == JsonValueKind.False)
            {
                return Enrichment.Empty;
            }

            var premise = ReadString(entry, "premise");
            var moments = ReadStrings(entry, "moments", int.MaxValue);
            var themes = ReadStrings(entry, "themes", int.MaxValue);
            var asks = ReadStrings(entry, "asks", MaxAsksPerItem);
            var spoiler = entry.TryGetProperty("spoiler", out var s) && s.ValueKind == JsonValueKind.True;

            var enrichment = new Enrichment(premise, moments, themes, asks, spoiler);

            // A model that answered but said nothing is the same outcome as one that
            // declined, and collapsing them keeps a single meaning of "empty" downstream.
            return enrichment.IsEmpty ? Enrichment.Empty : enrichment;
        }

        private static string ReadString(JsonElement entry, string name)
        {
            return entry.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
                ? (value.GetString() ?? string.Empty).Trim()
                : string.Empty;
        }

        private static IReadOnlyList<string> ReadStrings(JsonElement entry, string name, int cap)
        {
            if (!entry.TryGetProperty(name, out var array) || array.ValueKind != JsonValueKind.Array)
            {
                return [];
            }

            var values = new List<string>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var element in array.EnumerateArray())
            {
                if (values.Count >= cap)
                {
                    break;
                }

                if (element.ValueKind != JsonValueKind.String)
                {
                    continue;
                }

                var text = (element.GetString() ?? string.Empty).Trim();

                // Duplicates cost a vector row each and add nothing: a repeated
                // phrasing cannot rank its item any higher than the first copy did.
                if (text.Length > 0 && seen.Add(text))
                {
                    values.Add(text);
                }
            }

            return values;
        }
    }
}
