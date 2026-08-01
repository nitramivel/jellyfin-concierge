using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Concierge.Configuration;
using Jellyfin.Plugin.Concierge.Core.Budget;
using Jellyfin.Plugin.Concierge.Core.Documents;
using Jellyfin.Plugin.Concierge.Core.Llm;
using Jellyfin.Plugin.Concierge.Core.Query;
using Jellyfin.Plugin.Concierge.Core.Ranking;
using Jellyfin.Plugin.Concierge.Core.Retrieval;
using Jellyfin.Plugin.Concierge.Services.Budget;
using Jellyfin.Plugin.Concierge.Services.Cache;
using Jellyfin.Plugin.Concierge.Services.Embeddings;
using Jellyfin.Plugin.Concierge.Services.Indexing;
using Jellyfin.Plugin.Concierge.Services.Llm;
using Jellyfin.Plugin.Concierge.Services.Runs;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Concierge.Services
{
    /// <summary>One result.</summary>
    /// <param name="ItemId">The item.</param>
    /// <param name="Name">Its title.</param>
    /// <param name="Year">Its year, or null.</param>
    /// <param name="Score">The fused score.</param>
    /// <param name="LexicalRank">Where keyword retrieval put it, or null.</param>
    /// <param name="VectorRank">Where semantic retrieval put it, or null.</param>
    /// <param name="Why">
    /// Why it matched. Written by the re-rank model when that pass ran, and derived
    /// from which retrievers found it when it did not.
    /// </param>
    public sealed record SearchHit(
        Guid ItemId,
        string Name,
        int? Year,
        double Score,
        int? LexicalRank,
        int? VectorRank,
        string Why);

    /// <summary>What a search returned.</summary>
    /// <param name="Route">Native, Both or Concierge.</param>
    /// <param name="RouteReason">The rule that decided it.</param>
    /// <param name="Hits">The results, best first.</param>
    /// <param name="DurationMs">Wall-clock time.</param>
    /// <param name="CostUsd">What the query cost.</param>
    /// <param name="Degraded">
    /// Why part of the pipeline was skipped, or null. Never an error: a search box
    /// that returns an error message is a broken search box.
    /// </param>
    /// <param name="Cached">Whether this came from the cache, and therefore cost nothing.</param>
    /// <param name="Reranked">Whether a model ordered these results.</param>
    /// <param name="Plan">What the plan pass read out of the query, when it ran.</param>
    public sealed record SearchResponse(
        string Route,
        string RouteReason,
        IReadOnlyList<SearchHit> Hits,
        int DurationMs,
        decimal CostUsd,
        string? Degraded,
        bool Cached = false,
        bool Reranked = false,
        SearchPlan? Plan = null);

    /// <summary>
    /// The end-to-end query. Every entry point calls this.
    /// </summary>
    /// <remarks>
    /// Route, cache, plan, retrieve, fuse, filter, re-rank. Money is spent in
    /// exactly two places here — the plan pass and the re-rank pass — and the budget
    /// is consulted before each. Everything degrades: out of budget, rate limited,
    /// provider down or switched off all serve fused retrieval, which is free and
    /// still good.
    /// </remarks>
    public sealed class SearchService
    {
        private readonly IIndexStore _store;
        private readonly IEmbeddingProviderFactory _embeddingFactory;
        private readonly ILlmProviderFactory _llmFactory;
        private readonly IQueryLogStore _queryLog;
        private readonly ISpendStore _spend;
        private readonly ILogger<SearchService> _logger;
        private readonly SemaphoreSlim _loadGate = new(1, 1);
        private readonly QueryCache<SearchResponse> _cache = new();

        private ConciergeIndex? _index;

        public SearchService(
            IIndexStore store,
            IEmbeddingProviderFactory embeddingFactory,
            ILlmProviderFactory llmFactory,
            IQueryLogStore queryLog,
            ISpendStore spend,
            ILogger<SearchService> logger)
        {
            _store = store;
            _embeddingFactory = embeddingFactory;
            _llmFactory = llmFactory;
            _queryLog = queryLog;
            _spend = spend;
            _logger = logger;
        }

        /// <summary>
        /// Drops the cached index and every cached answer so the next search reloads.
        /// </summary>
        public void Invalidate()
        {
            _index = null;
            _cache.Clear();
        }

        /// <summary>
        /// Runs one search.
        /// </summary>
        /// <param name="query">The user's text.</param>
        /// <param name="userId">Who is searching, or null.</param>
        /// <param name="config">The plugin configuration.</param>
        /// <param name="cancellationToken">Cancellation token.</param>
        /// <returns>The results.</returns>
        public async Task<SearchResponse> SearchAsync(
            string query,
            Guid? userId,
            PluginConfiguration config,
            CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(config);

            var stopwatch = Stopwatch.StartNew();
            var started = DateTime.UtcNow;
            var runId = Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture)[..8];
            var calls = new List<QueryCallRecord>();

            var index = await GetIndexAsync(config, cancellationToken).ConfigureAwait(false);
            var decision = QueryRouter.Decide(query, index?.Lexical);

            if (index is null)
            {
                return await FinishAsync(
                    runId, started, query, userId, stopwatch,
                    new SearchResponse(
                        decision.Route.ToString(), decision.Reason, [], 0, 0m,
                        "no usable index — run the Concierge index task"),
                    calls, cancellationToken).ConfigureAwait(false);
            }

            // A repeat is free and instant. The key carries the index generation, so a
            // rebuild invalidates every answer at once without a sweep.
            _cache.Resize(config.QueryCacheSize);
            var cacheKey = QueryNormalizer.Key(query, userId, index.State.Generation);

            if (_cache.TryGet(cacheKey, out var cached) && cached is not null)
            {
                stopwatch.Stop();
                var hit = cached with { DurationMs = (int)stopwatch.ElapsedMilliseconds, Cached = true, CostUsd = 0m };
                await RecordAsync(runId, started, query, userId, hit, [], cancellationToken).ConfigureAwait(false);
                return hit;
            }

            // A Native route means "spend nothing", not "return nothing". Keyword
            // retrieval is local and free, so it always runs; what Native skips is the
            // embedding call and both model passes.
            var lexicalOnly = decision.Route == QueryRoute.Native;

            var budget = lexicalOnly
                ? new BudgetOutcome(BudgetVerdict.FreeOnly, string.Empty, 0m, config.MonthlyBudgetUsd)
                : BudgetDecision.ForQuery(
                    _spend.QuerySpendThisMonth(),
                    config.MonthlyBudgetUsd,
                    _spend.PaidQueriesInLastHour(UserKey(userId)),
                    config.PaidQueriesPerUserPerHour,
                    config.EnablePlanPass,
                    config.EnableRerankPass);

            var degraded = string.IsNullOrEmpty(budget.Reason) ? null : budget.Reason;

            // ── Plan ────────────────────────────────────────────────────────────
            var plan = SearchPlan.Passthrough(query);
            var normalized = ModelProfiles.Normalize(config);

            if (!lexicalOnly
                && budget.AllowsAnySpend
                && config.EnablePlanPass
                && decision.MayCarryConstraints)
            {
                plan = await RunPlanAsync(query, config, normalized, calls, cancellationToken)
                    .ConfigureAwait(false);
            }

            // ── Retrieve ────────────────────────────────────────────────────────
            var pool = Math.Max(config.MaxResults, config.RerankShortlistSize) * 3;
            var lexical = index.Lexical.Search(plan.Semantic, pool);
            IReadOnlyList<ScoredItem> vector = [];

            if (!lexicalOnly)
            {
                try
                {
                    vector = await SearchVectorsAsync(plan.Semantic, index, config, pool, calls, cancellationToken)
                        .ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    // Hard rule 4. The provider being down or misconfigured degrades
                    // this to keyword-only, which is worse, still useful, and free.
                    degraded ??= "semantic search unavailable — keyword results only";
                    _logger.LogWarning(ex, "Concierge: query embedding failed; serving lexical results only");
                }
            }

            var fused = RankFusion.Fuse(lexical, vector);
            var byId = index.Documents.ToDictionary(d => d.ItemId);

            // ── Filter, failing open ────────────────────────────────────────────
            var filtered = FilterApplication.Apply(fused, byId, plan.Filters);

            // ── Re-rank ─────────────────────────────────────────────────────────
            var ordered = filtered.Results;
            var explanations = new Dictionary<Guid, string>();
            var reranked = false;

            if (!lexicalOnly && budget.AllowsRerank && ordered.Count > 1)
            {
                var shortlist = ordered.Take(Math.Max(2, config.RerankShortlistSize)).ToList();
                var documents = shortlist
                    .Select(r => byId.GetValueOrDefault(r.ItemId))
                    .Where(d => d is not null)
                    .Select(d => d!)
                    .ToList();

                if (documents.Count == shortlist.Count)
                {
                    var outcome = await RunRerankAsync(
                            query, documents, config, normalized, calls, cancellationToken)
                        .ConfigureAwait(false);

                    if (outcome is not null)
                    {
                        var rest = ordered.Skip(shortlist.Count).ToList();
                        ordered =
                        [
                            .. outcome.Order.Select(o => shortlist[o.Index]),
                            .. rest,
                        ];

                        foreach (var entry in outcome.Order.Where(o => o.Why.Length > 0))
                        {
                            explanations[shortlist[entry.Index].ItemId] = entry.Why;
                        }

                        reranked = true;
                    }
                }
            }

            var hits = ordered
                .Take(Math.Max(1, config.MaxResults))
                .Select(f =>
                {
                    byId.TryGetValue(f.ItemId, out var document);
                    return new SearchHit(
                        f.ItemId,
                        document?.Title ?? string.Empty,
                        document?.Year,
                        f.Score,
                        f.LexicalRank,
                        f.VectorRank,
                        explanations.TryGetValue(f.ItemId, out var why) ? why : Explain(f));
                })
                .ToList();

            stopwatch.Stop();

            var cost = calls.Sum(c => c.EstimatedCostUsd);
            var response = new SearchResponse(
                decision.Route.ToString(),
                decision.Reason,
                hits,
                (int)stopwatch.ElapsedMilliseconds,
                cost,
                degraded,
                Cached: false,
                Reranked: reranked,
                Plan: plan.Filters.IsEmpty ? null : plan);

            if (cost > 0)
            {
                _spend.Record(SpendKind.Query, cost, UserKey(userId));
            }

            // Only worth remembering an answer that cost something or took real work.
            _cache.Set(cacheKey, response);

            await RecordAsync(runId, started, query, userId, response, calls, cancellationToken)
                .ConfigureAwait(false);

            return response;
        }

        private async Task<SearchPlan> RunPlanAsync(
            string query,
            PluginConfiguration config,
            ModelProfiles.NormalizedProfiles normalized,
            List<QueryCallRecord> calls,
            CancellationToken cancellationToken)
        {
            try
            {
                // Hard rule 12: both passes resolve from this one Normalize result.
                var profile = ModelProfiles.Resolve(normalized, config.PlanModelProfileId);
                var provider = _llmFactory.Create(profile, config.EnableThinking);

                var request = new LlmRequest(
                    PlanPromptBuilder.SystemPrompt,
                    PlanPromptBuilder.Build(query),
                    string.Empty,
                    Math.Min(config.MaxOutputTokens, 800),
                    ResponseShape.SearchPlan);

                var stopwatch = Stopwatch.StartNew();
                var result = await provider.CompleteAsync(request, cancellationToken).ConfigureAwait(false);
                stopwatch.Stop();

                calls.Add(Record(QueryPass.Plan, profile, provider.ModelId, result, stopwatch));
                return PlanParser.Parse(result.Text, query);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // A failed plan is not a failed search — retrieval works perfectly well
                // from the raw query.
                _logger.LogWarning(ex, "Concierge: the plan pass failed; searching on the raw query");
                return SearchPlan.Passthrough(query);
            }
        }

        private async Task<RerankOutcome?> RunRerankAsync(
            string query,
            IReadOnlyList<ItemDocument> shortlist,
            PluginConfiguration config,
            ModelProfiles.NormalizedProfiles normalized,
            List<QueryCallRecord> calls,
            CancellationToken cancellationToken)
        {
            try
            {
                var profile = ModelProfiles.Resolve(normalized, config.RerankModelProfileId);
                var provider = _llmFactory.Create(profile, config.EnableThinking);

                // The candidate list changes every query, so there is nothing a later
                // call could read back from a cache — everything goes in the prefix and
                // no cache marker is written.
                var prompt = RerankPromptBuilder.BuildCandidates(shortlist)
                    + RerankPromptBuilder.BuildInstruction(query, shortlist.Count);

                var request = new LlmRequest(
                    RerankPromptBuilder.SystemPrompt,
                    prompt,
                    string.Empty,
                    config.MaxOutputTokens,
                    ResponseShape.Rerank);

                var stopwatch = Stopwatch.StartNew();
                var result = await provider.CompleteAsync(request, cancellationToken).ConfigureAwait(false);
                stopwatch.Stop();

                calls.Add(Record(QueryPass.Rerank, profile, provider.ModelId, result, stopwatch));

                var outcome = RerankParser.Parse(result.Text, shortlist.Count);

                if (outcome.Invented > 0 || outcome.Omitted > 0)
                {
                    _logger.LogDebug(
                        "Concierge: re-rank placed {Ranked} of {Total} ({Omitted} kept their fused position, "
                        + "{Invented} invented index(es) discarded)",
                        outcome.Ranked,
                        shortlist.Count,
                        outcome.Omitted,
                        outcome.Invented);
                }

                return outcome;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // The fused order is a perfectly good answer. Slightly worse, free.
                _logger.LogWarning(ex, "Concierge: the re-rank pass failed; serving the fused order");
                return null;
            }
        }

        private static QueryCallRecord Record(
            QueryPass pass,
            ModelProfile profile,
            string modelId,
            LlmResult result,
            Stopwatch stopwatch)
            => new(
                pass,
                profile.Provider.ToString(),
                modelId,
                result.InputTokens,
                result.OutputTokens,
                result.CacheReadTokens,
                result.CacheWriteTokens,
                result.ThinkingTokens,
                CallCost.ForChat(
                    profile, result.InputTokens, result.OutputTokens,
                    result.CacheReadTokens, result.CacheWriteTokens),
                (int)stopwatch.ElapsedMilliseconds,
                result.Truncated);

        private async Task<IReadOnlyList<ScoredItem>> SearchVectorsAsync(
            string text,
            ConciergeIndex index,
            PluginConfiguration config,
            int limit,
            List<QueryCallRecord> calls,
            CancellationToken cancellationToken)
        {
            var profile = EmbeddingProfiles.Resolve(config, config.EmbeddingProfileId);
            var embedder = _embeddingFactory.Create(profile);

            var stopwatch = Stopwatch.StartNew();

            // EmbeddingPurpose.Query, never Document. The provider turns that into
            // whatever its model expects, and getting it wrong degrades results with
            // no error at all.
            var embedded = await embedder
                .EmbedAsync([text], EmbeddingPurpose.Query, cancellationToken)
                .ConfigureAwait(false);

            stopwatch.Stop();

            calls.Add(new QueryCallRecord(
                QueryPass.Embedding,
                profile.Provider.ToString(),
                embedder.ModelId,
                embedded.InputTokens,
                0, 0, 0, 0,
                CallCost.ForEmbedding(profile, embedded.InputTokens),
                (int)stopwatch.ElapsedMilliseconds,
                false));

            return index.Vectors.Search(embedded.Vectors[0], limit);
        }

        /// <summary>
        /// A one-line reason from which retrievers found the item.
        /// </summary>
        /// <remarks>
        /// The fallback for when no model wrote one. Says what is actually known
        /// rather than inventing a justification.
        /// </remarks>
        private static string Explain(FusedResult result)
        {
            if (result.FoundByBoth)
            {
                return "matches both the words and the meaning";
            }

            return result.LexicalRank is not null ? "matches the words you typed" : "close in meaning";
        }

        private static string? UserKey(Guid? userId)
            => userId?.ToString("N", CultureInfo.InvariantCulture);

        private async Task<SearchResponse> FinishAsync(
            string runId,
            DateTime started,
            string query,
            Guid? userId,
            Stopwatch stopwatch,
            SearchResponse response,
            IReadOnlyList<QueryCallRecord> calls,
            CancellationToken cancellationToken)
        {
            stopwatch.Stop();
            var finished = response with { DurationMs = (int)stopwatch.ElapsedMilliseconds };
            await RecordAsync(runId, started, query, userId, finished, calls, cancellationToken)
                .ConfigureAwait(false);
            return finished;
        }

        private async Task<ConciergeIndex?> GetIndexAsync(
            PluginConfiguration config,
            CancellationToken cancellationToken)
        {
            if (_index is not null)
            {
                return _index;
            }

            await _loadGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                if (_index is not null)
                {
                    return _index;
                }

                EmbeddingProfile profile;
                try
                {
                    profile = EmbeddingProfiles.Resolve(config, config.EmbeddingProfileId);
                }
                catch (InvalidOperationException)
                {
                    // Nothing configured yet. The config page already says so; this
                    // must not shout on every keystroke.
                    return null;
                }

                _index = await _store.LoadAsync(profile, cancellationToken).ConfigureAwait(false);
                return _index;
            }
            finally
            {
                _loadGate.Release();
            }
        }

        private Task RecordAsync(
            string runId,
            DateTime started,
            string query,
            Guid? userId,
            SearchResponse response,
            IReadOnlyList<QueryCallRecord> calls,
            CancellationToken cancellationToken)
        {
            var record = new QueryRunRecord(
                runId,
                started,
                query,
                UserKey(userId),
                response.Route,
                calls,
                response.Hits.Count,
                response.DurationMs,
                response.Degraded,
                null,
                response.Hits.Take(5).Select(h => h.Name).ToList());

            return _queryLog.RecordAsync(record, cancellationToken);
        }
    }
}
