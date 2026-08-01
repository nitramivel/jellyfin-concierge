using System.Collections.Generic;

namespace Jellyfin.Plugin.Concierge.Core.Query
{
    /// <summary>Whether the searcher asked about their own viewing history.</summary>
    public enum WatchState
    {
        /// <summary>No constraint.</summary>
        Any = 0,

        /// <summary>"that I haven't seen".</summary>
        Unwatched = 1,

        /// <summary>"that one I watched last year".</summary>
        Watched = 2,

        /// <summary>"one of my favourites".</summary>
        Favorite = 3,
    }

    /// <summary>
    /// The real constraints hiding in a sentence.
    /// </summary>
    /// <remarks>
    /// <b>Every field here is a hypothesis, not a command.</b> A model that decides
    /// "90s" means exactly <c>[1990, 1999]</c> and excludes a 1989 film the searcher
    /// had in mind is worse than no filter at all, so these are applied as a ranking
    /// boost first and a hard cut only when enough candidates survive it (§4.5).
    /// </remarks>
    /// <param name="Types">Movie, Series, Episode. Empty means no constraint.</param>
    /// <param name="YearFrom">Earliest year, or null.</param>
    /// <param name="YearTo">Latest year, or null.</param>
    /// <param name="Genres">Genres named in the query.</param>
    /// <param name="People">Cast or crew named in the query.</param>
    /// <param name="RuntimeMaxMinutes">"under two hours", or null.</param>
    /// <param name="WatchState">Whether viewing history was mentioned.</param>
    public sealed record SearchFilters(
        IReadOnlyList<string> Types,
        int? YearFrom,
        int? YearTo,
        IReadOnlyList<string> Genres,
        IReadOnlyList<string> People,
        int? RuntimeMaxMinutes,
        WatchState WatchState)
    {
        /// <summary>An empty filter set — the result of a query with no constraints in it.</summary>
        public static SearchFilters None { get; } = new([], null, null, [], [], null, WatchState.Any);

        /// <summary>Gets whether anything here would narrow the candidate set.</summary>
        public bool IsEmpty =>
            Types.Count == 0
            && YearFrom is null
            && YearTo is null
            && Genres.Count == 0
            && People.Count == 0
            && RuntimeMaxMinutes is null
            && WatchState == WatchState.Any;
    }

    /// <summary>
    /// What the plan pass read out of the searcher's sentence.
    /// </summary>
    /// <remarks>
    /// The model's <em>first</em> job is not to find anything. It is to read the
    /// sentence: separate what the searcher is describing from the constraints they
    /// mentioned in passing, so retrieval can match on the former and rank by the
    /// latter.
    /// </remarks>
    /// <param name="Semantic">
    /// What they are actually describing, with the constraint words stripped out. It
    /// is this that gets embedded, not the raw query — "90s sci-fi under two hours I
    /// haven't seen" embeds far better as "science fiction".
    /// </param>
    /// <param name="Filters">The constraints, as hypotheses.</param>
    /// <param name="Quote">
    /// Set when the searcher is reciting dialogue rather than describing a plot.
    /// Phase 3 acts on it; phase 2 only records that it was detected.
    /// </param>
    public sealed record SearchPlan(string Semantic, SearchFilters Filters, string? Quote)
    {
        /// <summary>
        /// The plan for a query that was never sent to a model.
        /// </summary>
        /// <remarks>
        /// The plan pass is skippable — when the router saw no constraint-like
        /// language there is nothing for it to extract, and skipping saves the call
        /// and about 400ms on the most common Concierge query. This is what that
        /// looks like: the raw query as the semantic text and no filters.
        /// </remarks>
        /// <param name="query">The raw query.</param>
        /// <returns>An unfiltered plan.</returns>
        public static SearchPlan Passthrough(string query)
            => new(query ?? string.Empty, SearchFilters.None, null);
    }
}
