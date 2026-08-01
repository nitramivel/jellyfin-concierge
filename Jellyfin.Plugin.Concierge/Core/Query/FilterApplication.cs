using System;
using System.Collections.Generic;
using System.Linq;
using Jellyfin.Plugin.Concierge.Core.Documents;
using Jellyfin.Plugin.Concierge.Core.Retrieval;

namespace Jellyfin.Plugin.Concierge.Core.Query
{
    /// <summary>What applying the plan's filters did to the candidate list.</summary>
    /// <param name="Results">The candidates, reordered and possibly cut.</param>
    /// <param name="HardCut">Whether non-matching items were removed rather than demoted.</param>
    /// <param name="Matched">How many candidates satisfied every filter.</param>
    /// <param name="Explanation">One line for the query log saying what happened.</param>
    public sealed record FilterOutcome(
        IReadOnlyList<FusedResult> Results,
        bool HardCut,
        int Matched,
        string Explanation);

    /// <summary>
    /// Applies the plan's filters to the fused candidates — as a cut when that
    /// leaves enough, and as a demotion when it does not.
    /// </summary>
    /// <remarks>
    /// <b>Filters fail open</b> (hard rule 8). The plan is a hypothesis produced by a
    /// small model from one sentence, and it is wrong often enough that treating it
    /// as a command would quietly delete correct answers. A filter that would leave
    /// fewer than a dozen candidates is far more likely to be a misreading than a
    /// library with nothing in it, so it is demoted to a ranking signal instead: the
    /// non-matching items drop, but they stay reachable.
    /// <para>
    /// The failure this prevents is the one that makes people stop using a search box
    /// — not a wrong answer, an empty page.
    /// </para>
    /// </remarks>
    public static class FilterApplication
    {
        /// <summary>
        /// How many candidates must survive a cut for the cut to be believed.
        /// </summary>
        /// <remarks>
        /// Twelve rather than one, because "it returned something" is not the bar.
        /// The re-rank pass needs a shortlist worth ordering, and a filter that
        /// leaves three items has almost certainly thrown away the right one.
        /// </remarks>
        public const int MinimumSurvivors = 12;

        /// <summary>
        /// Applies filters, cutting or demoting.
        /// </summary>
        /// <param name="fused">The fused candidates, best first.</param>
        /// <param name="documents">The indexed documents, by item id.</param>
        /// <param name="filters">The plan's filters.</param>
        /// <param name="watchStateMatches">
        /// Decides whether an item satisfies a watch-state filter for the searching
        /// user. Null means watch state cannot be evaluated and is ignored — Core has
        /// no access to per-user data and must not pretend otherwise.
        /// </param>
        /// <param name="minimumSurvivors">Override for the fail-open threshold.</param>
        /// <returns>The outcome.</returns>
        public static FilterOutcome Apply(
            IReadOnlyList<FusedResult> fused,
            IReadOnlyDictionary<Guid, ItemDocument> documents,
            SearchFilters filters,
            Func<Guid, WatchState, bool>? watchStateMatches = null,
            int minimumSurvivors = MinimumSurvivors)
        {
            ArgumentNullException.ThrowIfNull(fused);
            ArgumentNullException.ThrowIfNull(documents);
            ArgumentNullException.ThrowIfNull(filters);

            if (filters.IsEmpty || fused.Count == 0)
            {
                return new FilterOutcome(fused, false, fused.Count, "no filters");
            }

            var matching = new List<FusedResult>();
            var rest = new List<FusedResult>();

            foreach (var candidate in fused)
            {
                var document = documents.GetValueOrDefault(candidate.ItemId);
                if (document is not null && Matches(document, filters, watchStateMatches))
                {
                    matching.Add(candidate);
                }
                else
                {
                    rest.Add(candidate);
                }
            }

            if (matching.Count >= minimumSurvivors)
            {
                return new FilterOutcome(
                    matching,
                    true,
                    matching.Count,
                    $"filtered to {matching.Count} of {fused.Count} candidates");
            }

            // Not enough survived, so the filter is more likely wrong than the
            // library is empty. Keep everything, with the matches first — the
            // searcher's constraint still shapes the order, it just cannot delete.
            var demoted = new List<FusedResult>(fused.Count);
            demoted.AddRange(matching);
            demoted.AddRange(rest);

            return new FilterOutcome(
                demoted,
                false,
                matching.Count,
                matching.Count == 0
                    ? "filters matched nothing and were demoted to a ranking signal"
                    : $"only {matching.Count} candidate(s) matched, so filters were demoted rather than applied");
        }

        private static bool Matches(
            ItemDocument document,
            SearchFilters filters,
            Func<Guid, WatchState, bool>? watchStateMatches)
        {
            if (filters.Types.Count > 0
                && !filters.Types.Any(t => string.Equals(t, document.Kind, StringComparison.OrdinalIgnoreCase)))
            {
                return false;
            }

            // An item with no year cannot contradict a year filter, so it is kept.
            // Excluding it would punish incomplete metadata rather than answer the
            // question that was asked.
            if (document.Year is { } year)
            {
                if (filters.YearFrom is { } from && year < from)
                {
                    return false;
                }

                if (filters.YearTo is { } to && year > to)
                {
                    return false;
                }
            }

            if (filters.Genres.Count > 0
                && !filters.Genres.Any(g => document.Genres.Any(
                    d => d.Contains(g, StringComparison.OrdinalIgnoreCase)
                        || g.Contains(d, StringComparison.OrdinalIgnoreCase))))
            {
                return false;
            }

            if (filters.People.Count > 0
                && !filters.People.Any(p => document.People.Any(
                    d => d.Contains(p, StringComparison.OrdinalIgnoreCase))))
            {
                return false;
            }

            if (filters.RuntimeMaxMinutes is { } max
                && document.RuntimeMinutes is { } runtime
                && runtime > max)
            {
                return false;
            }

            if (filters.WatchState != WatchState.Any
                && watchStateMatches is not null
                && !watchStateMatches(document.ItemId, filters.WatchState))
            {
                return false;
            }

            return true;
        }
    }
}
