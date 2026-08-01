using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Serialization;

namespace Jellyfin.Plugin.Concierge.Services.Runs
{
    /// <summary>
    /// Which pass of a query a recorded call belongs to.
    /// </summary>
    /// <remarks>
    /// Only three of these ever cost money — <see cref="Plan"/>, <see cref="Rerank"/>
    /// and <see cref="Enrichment"/> — which is hard rule 3 stated as a type.
    /// <see cref="Route"/> and <see cref="Retrieve"/> appear so a query's timing can
    /// be read end to end, and a call recorded against either of them with a
    /// non-zero cost is a bug in the shape the rule exists to prevent.
    /// </remarks>
    public enum QueryPass
    {
        /// <summary>Deciding whether this is a natural-language query at all. Free and pure.</summary>
        Route = 0,

        /// <summary>Reading the sentence into a search plan. Paid, per query.</summary>
        Plan = 1,

        /// <summary>BM25 + vector retrieval and fusion. Free.</summary>
        Retrieve = 2,

        /// <summary>Ordering the shortlist and explaining it. Paid, per query.</summary>
        Rerank = 3,

        /// <summary>Index-time item enrichment. Paid, once per item.</summary>
        Enrichment = 4,

        /// <summary>Turning text into vectors, at index time or query time.</summary>
        Embedding = 5,
    }

    /// <summary>
    /// One model call inside a query, with the tokens the provider reported and what
    /// they cost at the calling profile's prices.
    /// </summary>
    /// <remarks>
    /// <b><see cref="Model"/> and <see cref="Provider"/> are per call, not per
    /// query</b> (hard rule 12). A query normally runs a small model for the plan and
    /// a larger one for the re-rank; reporting one model for the whole query is how
    /// a bug report gets read against the wrong thing entirely.
    /// </remarks>
    /// <param name="Pass">Which pass made the call.</param>
    /// <param name="Provider">The provider kind, as text, for display.</param>
    /// <param name="Model">The model id that produced this output.</param>
    /// <param name="InputTokens">Uncached input tokens billed.</param>
    /// <param name="OutputTokens">Output tokens billed, thinking included.</param>
    /// <param name="CacheReadTokens">Input tokens served from the prompt cache. Charged, not free.</param>
    /// <param name="CacheWriteTokens">Input tokens written to the prompt cache.</param>
    /// <param name="ThinkingTokens">The reasoning share of <paramref name="OutputTokens"/>.</param>
    /// <param name="EstimatedCostUsd">What this one call cost.</param>
    /// <param name="DurationMs">Wall-clock time for the call.</param>
    /// <param name="Truncated">Whether the output hit the cap.</param>
    public sealed record QueryCallRecord(
        QueryPass Pass,
        string Provider,
        string Model,
        long InputTokens,
        long OutputTokens,
        long CacheReadTokens,
        long CacheWriteTokens,
        long ThinkingTokens,
        decimal EstimatedCostUsd,
        int DurationMs,
        bool Truncated);

    /// <summary>
    /// One recorded query, end to end.
    /// </summary>
    /// <remarks>
    /// <b>Open question 5 is unresolved and lives here.</b> This record carries the
    /// text the user typed and the id of the user who typed it, which is what makes
    /// a bad result diagnosable — and also a log of what everyone in the household
    /// searched for. Whether the log stays admin-visible per user, is anonymized, or
    /// drops the query text entirely is a decision the owner owes before anything
    /// ships, because retrofitting privacy onto stored history is painful and
    /// deleting it is the only remedy after the fact.
    /// </remarks>
    /// <param name="Id">A short id for this query, for cross-referencing the server log.</param>
    /// <param name="StartedUtc">When the query began.</param>
    /// <param name="Query">The text the user typed.</param>
    /// <param name="UserId">Who searched. See the privacy note above.</param>
    /// <param name="Route">What the router decided: native, or the full pipeline.</param>
    /// <param name="Calls">Every model call the query made, in order.</param>
    /// <param name="ResultCount">How many items were returned.</param>
    /// <param name="DurationMs">Wall-clock time for the whole query.</param>
    /// <param name="Degraded">
    /// Why a paid pass was skipped, or null when none was. Budget exhausted, provider
    /// down, key wrong and rate limited all land here rather than in
    /// <paramref name="Error"/> — under hard rule 4 they are ordinary operation that
    /// served free results, not failures.
    /// </param>
    /// <param name="Error">Why the query failed outright, or null.</param>
    /// <param name="TopHits">
    /// The first few titles returned, so a search can be judged after the fact.
    /// </param>
    /// <param name="Cached">
    /// Whether the answer came from the cache. Recorded because a breakdown that
    /// counted cache hits as ordinary searches would understate what the cache is
    /// saving and overstate what a search costs.
    /// </param>
    /// <param name="Reranked">Whether a model ordered the results.</param>
    /// <param name="QuoteHits">How many lines of dialogue matched.</param>
    /// <remarks>
    /// <paramref name="TopHits"/> exists because a result count does not say whether
    /// the answer was any good. "10 results in 311ms" reads identically for a perfect
    /// search and a useless one, and by the time anyone wonders which it was the
    /// results are long gone.
    /// </remarks>
    public sealed record QueryRunRecord(
        string Id,
        DateTime StartedUtc,
        string Query,
        string? UserId,
        string Route,
        IReadOnlyList<QueryCallRecord> Calls,
        int ResultCount,
        int DurationMs,
        string? Degraded = null,
        string? Error = null,
        IReadOnlyList<string>? TopHits = null,
        bool Cached = false,
        bool Reranked = false,
        int QuoteHits = 0)
    {
        /// <summary>
        /// Gets what the query cost, summed from its calls.
        /// </summary>
        /// <remarks>
        /// A computed property rather than a stored field so it cannot drift from the
        /// calls it is made of, and so nothing can be tempted to fill it in by
        /// multiplying total tokens by one rate — which hard rule 12 forbids, because
        /// no single rate can price a two-model query.
        /// </remarks>
        [JsonIgnore]
        public decimal TotalCostUsd => Calls?.Sum(c => c.EstimatedCostUsd) ?? 0m;
    }
}
