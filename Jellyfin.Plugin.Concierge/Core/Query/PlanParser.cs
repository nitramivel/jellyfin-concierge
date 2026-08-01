using System;
using System.Collections.Generic;
using System.Text.Json;
using Jellyfin.Plugin.Concierge.Core.Llm;

namespace Jellyfin.Plugin.Concierge.Core.Query
{
    /// <summary>
    /// Reads the plan pass's answer back into a <see cref="SearchPlan"/>.
    /// </summary>
    /// <remarks>
    /// Forgiving by design. The plan is a hypothesis, and a malformed or
    /// half-answered one should degrade to "no constraints" rather than fail the
    /// search — every field this drops just means retrieval sees a slightly less
    /// specific query, which is a far better outcome than an error page.
    /// </remarks>
    public static class PlanParser
    {
        /// <summary>The widest year range worth believing.</summary>
        private const int EarliestYear = 1880;

        private const int LatestYear = 2200;

        /// <summary>
        /// Parses a plan response, falling back to the raw query on anything
        /// unreadable.
        /// </summary>
        /// <param name="responseText">The raw model output.</param>
        /// <param name="query">The original query, used when the response is unusable.</param>
        /// <returns>The plan. Never null, never throws.</returns>
        public static SearchPlan Parse(string? responseText, string query)
        {
            if (string.IsNullOrWhiteSpace(responseText))
            {
                return SearchPlan.Passthrough(query);
            }

            try
            {
                using var document = JsonDocument.Parse(JsonResponse.ExtractObject(responseText));
                var root = document.RootElement;

                var semantic = ReadString(root, "semantic");
                if (string.IsNullOrWhiteSpace(semantic))
                {
                    // The model dropped the one field that matters. Its filters may
                    // still be good, but retrieval needs something to match on, so
                    // fall back to what the searcher actually typed.
                    semantic = query;
                }

                var filters = root.TryGetProperty("filters", out var f) && f.ValueKind == JsonValueKind.Object
                    ? ReadFilters(f)
                    : SearchFilters.None;

                var quote = ReadString(root, "quote");

                return new SearchPlan(
                    semantic.Trim(),
                    filters,
                    string.IsNullOrWhiteSpace(quote) ? null : quote.Trim());
            }
            catch (Exception ex) when (ex is FormatException or JsonException)
            {
                return SearchPlan.Passthrough(query);
            }
        }

        private static SearchFilters ReadFilters(JsonElement element)
        {
            var yearFrom = ReadYear(element, "yearFrom");
            var yearTo = ReadYear(element, "yearTo");

            // A model that swaps the bounds has still told us the range it means.
            if (yearFrom is { } from && yearTo is { } to && from > to)
            {
                (yearFrom, yearTo) = (to, from);
            }

            return new SearchFilters(
                ReadStrings(element, "types"),
                yearFrom,
                yearTo,
                ReadStrings(element, "genres"),
                ReadStrings(element, "people"),
                ReadPositiveInt(element, "runtimeMaxMinutes"),
                ReadWatchState(element));
        }

        private static WatchState ReadWatchState(JsonElement element)
        {
            var value = ReadString(element, "watchState").Trim();

            return value.ToUpperInvariant() switch
            {
                "UNWATCHED" or "UNSEEN" => WatchState.Unwatched,
                "WATCHED" or "SEEN" => WatchState.Watched,
                "FAVORITE" or "FAVOURITE" => WatchState.Favorite,
                _ => WatchState.Any,
            };
        }

        private static int? ReadYear(JsonElement element, string name)
        {
            var value = ReadPositiveInt(element, name);

            // A year outside anything a library could hold is a hallucination, and
            // applying it would empty the results for no reason the searcher could
            // ever work out.
            return value is >= EarliestYear and <= LatestYear ? value : null;
        }

        private static int? ReadPositiveInt(JsonElement element, string name)
        {
            if (!element.TryGetProperty(name, out var value))
            {
                return null;
            }

            // Models write numbers as strings often enough to be worth accepting.
            if (value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var number))
            {
                return number > 0 ? number : null;
            }

            if (value.ValueKind == JsonValueKind.String
                && int.TryParse(value.GetString(), out var parsed))
            {
                return parsed > 0 ? parsed : null;
            }

            return null;
        }

        private static string ReadString(JsonElement element, string name)
            => element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
                ? value.GetString() ?? string.Empty
                : string.Empty;

        private static IReadOnlyList<string> ReadStrings(JsonElement element, string name)
        {
            if (!element.TryGetProperty(name, out var array) || array.ValueKind != JsonValueKind.Array)
            {
                return [];
            }

            var values = new List<string>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var item in array.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.String)
                {
                    continue;
                }

                var text = (item.GetString() ?? string.Empty).Trim();
                if (text.Length > 0 && seen.Add(text))
                {
                    values.Add(text);
                }
            }

            return values;
        }
    }
}
