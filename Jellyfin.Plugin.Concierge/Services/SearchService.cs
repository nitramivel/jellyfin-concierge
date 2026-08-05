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
using Jellyfin.Plugin.Concierge.Services.Quotes;
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
    /// <param name="Quotes">
    /// Lines of dialogue that matched, when the searcher quoted something. Free: no
    /// model and no embedding, just the text out of their own subtitle files.
    /// </param>
    public sealed record SearchResponse(
        string Route,
        string RouteReason,
        IReadOnlyList<SearchHit> Hits,
        int DurationMs,
        decimal CostUsd,
        string? Degraded,
        bool Cached = false,
        bool Reranked = false,
        SearchPlan? Plan = null,
        IReadOnlyList<QuoteResult>? Quotes = null);

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
        private readonly QuoteIndexProvider _quotes;
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
            QuoteIndexProvider quotes,
            ILogger<SearchService> logger)
        {
            _store = store;
            _embeddingFactory = embeddingFactory;
            _llmFactory = llmFactory;
            _queryLog = queryLog;
            _spend = spend;
            _quotes = quotes;
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
        /// <param name="preview">
        /// When true, answer from keyword retrieval alone and spend nothing: no
        /// embedding, no plan pass, no re-rank.
        /// </param>
        public async Task<SearchResponse> SearchAsync(
            string query,
            Guid? userId,
            PluginConfiguration config,
            CancellationToken cancellationToken,
            bool preview = false)
        {
            ArgumentNullException.ThrowIfNull(config);

            var stopwatch = Stopwatch.StartNew();
            var started = DateTime.UtcNow;
            var runId = Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture)[..8];
            var calls = new List<QueryCallRecord>();

            var index = await GetIndexAsync(config, cancellationToken).ConfigureAwait(false);
            var decision = QueryRouter.Decide(query, index?.Lexical);

            // Dialogue search runs before anything paid, and costs nothing. A quoted
            // query is somebody reciting a line, and if their own subtitle files hold
            // it there is no reason to ask a model — or to care whether a model has
            // ever heard of the film, which for anything released this year it has not.
            var quotes = await SearchQuotesAsync(query, decision, config, cancellationToken)
                .ConfigureAwait(false);

            if (index is null)
            {
                return await FinishAsync(
                    config, runId, started, query, userId, stopwatch,
                    new SearchResponse(
                        decision.Route.ToString(), decision.Reason, [], 0, 0m,
                        quotes.Count > 0
                            ? null
                            : "no usable index — run the Concierge index task",
                        Quotes: quotes),
                    calls, cancellationToken).ConfigureAwait(false);
            }

            // A repeat is free and instant. The key carries the index generation, so a
            // rebuild invalidates every answer at once without a sweep.
            _cache.Resize(config.QueryCacheSize);
            var cacheKey = QueryNormalizer.Key(query, userId, index.State.Generation)
                + (preview ? "|preview" : string.Empty);

            if (_cache.TryGet(cacheKey, out var cached) && cached is not null)
            {
                stopwatch.Stop();
                var hit = cached with { DurationMs = (int)stopwatch.ElapsedMilliseconds, Cached = true, CostUsd = 0m };

                if (!preview)
                {
                    await RecordAsync(config, runId, started, query, userId, hit, [], cancellationToken)
                        .ConfigureAwait(false);
                }

                return hit;
            }

            // A Native route means "spend nothing", not "return nothing". Keyword
            // retrieval is local and free, so it always runs; what Native skips is the
            // embedding call and both model passes.
            // A preview is the free half of the pipeline, served on its own. Measured
            // on this library: 0 ms at the median, 110 ms at the worst, against 6.4 s
            // for the full path. Nothing here is a judgement about what the answer
            // should be — it is the answer that already exists while the good one is
            // still being written.
            var lexicalOnly = preview || decision.Route == QueryRoute.Native;

            // Computed even for a provisional Native route, because that route may be
            // upgraded once the keyword scores are in and the upgrade must know what
            // it is allowed to spend.
            var budget = BudgetDecision.ForQuery(
                _spend.QuerySpendThisMonth(),
                config.MonthlyBudgetUsd,
                _spend.PaidQueriesInLastHour(UserKey(userId)),
                config.PaidQueriesPerUserPerHour,
                config.EnablePlanPass,
                config.EnableRerankPass);

            // Collected by the paid passes when a provider refuses. Both of them treat
            // a failure as "serve the free answer", which is right — but until this
            // existed it was also completely silent: the exception became a warning in
            // the server log, and because a call record is only added after a response
            // comes back, the query log recorded no call, no error and $0.00. A search
            // whose model layer was entirely down looked identical to a cheap one.
            //
            // Measured on this install: 41 consecutive HTTP 400s from the re-rank
            // provider across three hours, and the only visible symptom was that
            // results were worse.
            string? passFailure = null;

            // ── Plan ────────────────────────────────────────────────────────────
            var plan = SearchPlan.Passthrough(query);
            var normalized = ModelProfiles.Normalize(config);

            if (!lexicalOnly
                && budget.AllowsAnySpend
                && config.EnablePlanPass
                && decision.MayCarryConstraints)
            {
                (plan, var planError) = await RunPlanAsync(
                        query, config, normalized, calls, cancellationToken)
                    .ConfigureAwait(false);

                if (planError is not null)
                {
                    passFailure = "the planning model is not answering — " + planError;
                }
            }

            // ── Retrieve ────────────────────────────────────────────────────────
            var pool = Math.Max(config.MaxResults, config.RerankShortlistSize) * 3;
            var lexical = index.Lexical.Search(plan.Semantic, pool);
            IReadOnlyList<ScoredItem> vector = [];

            // §4.2's third native rule, and it can only be applied here because it is
            // the one that has to see the answer first. A Native route claims the
            // keyword index already knows what was meant — so if its top hit is not a
            // clear winner, that claim was wrong and the query deserves the full
            // pipeline.
            //
            // Measured: "michael scott" scored Scott Pilgrim 5.93 against The Office
            // 5.55. A 7% edge is a coin toss, and routing Native on it meant the
            // re-ranker — which knows perfectly well who Michael Scott is — never saw
            // the query at all.
            if (lexicalOnly
                && !preview
                && budget.AllowsAnySpend
                && QueryRouter.IsWorthUpgrading(query)
                && !QueryRouter.HasDominantWinner(lexical.Select(h => h.Score).ToList()))
            {
                lexicalOnly = false;
                decision = decision with
                {
                    Route = QueryRoute.Both,
                    Reason = decision.Reason + ", but no clear keyword winner",
                };
            }

            // Computed here rather than beside the budget, because `lexicalOnly` is
            // not final until the upgrade above has had its say. Reading it early
            // meant an upgraded query reported no reason at all while quietly
            // spending nothing.
            var degraded = lexicalOnly || string.IsNullOrEmpty(budget.Reason) ? null : budget.Reason;
            degraded ??= passFailure;

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

            _logger.LogDebug(
                "Concierge: retrieval for {Route} — {Lexical} keyword, {Vector} semantic hit(s)",
                decision.Route,
                lexical.Count,
                vector.Count);

            var fused = RankFusion.Fuse(lexical, vector);
            var byId = index.Documents.ToDictionary(d => d.ItemId);

            // ── Filter, failing open ────────────────────────────────────────────
            var filtered = FilterApplication.Apply(fused, byId, plan.Filters);

            // ── Re-rank ─────────────────────────────────────────────────────────
            var ordered = filtered.Results;
            var explanations = new Dictionary<Guid, string>();
            var reranked = false;
            var rerankedCount = 0;

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
                            e => degraded ??= "the re-ranking model is not answering — " + e,
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
                            explanations[shortlist[entry.Index].ItemId] =
                                Shorten(entry.Why, config.RerankWhyMaxChars);
                        }

                        reranked = true;
                        rerankedCount = outcome.Ranked;
                    }
                }
            }

            var hits = ordered
                .Take(HowManyToShow(config, rerankedCount))
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
                Plan: plan.Filters.IsEmpty ? null : plan,
                Quotes: quotes);

            if (cost > 0)
            {
                _spend.Record(SpendKind.Query, cost, UserKey(userId));
            }

            // Only worth remembering an answer that cost something or took real work.
            _cache.Set(cacheKey, response);

            // Previews are not logged. They are free by construction, they fire on
            // every keystroke, and writing them to an append-only file whose purpose
            // is the record of what searches cost would bury that record in rows that
            // cost nothing.
            if (!preview)
            {
                await RecordAsync(config, runId, started, query, userId, response, calls, cancellationToken)
                    .ConfigureAwait(false);
            }

            return response;
        }

        /// <summary>
        /// Searches extracted dialogue when the query is a quotation.
        /// </summary>
        /// <remarks>
        /// Only for quoted input. Running it on every query would surface a line of
        /// dialogue whenever somebody typed a common word, which is noise — the
        /// quotation marks are the searcher saying "these are the words that were
        /// said", and that is exactly the signal this needs.
        /// </remarks>
        private async Task<IReadOnlyList<QuoteResult>> SearchQuotesAsync(
            string query,
            RouteDecision decision,
            PluginConfiguration config,
            CancellationToken cancellationToken)
        {
            if (!config.EnableQuoteSearch || !decision.Reason.Contains("quoted", StringComparison.Ordinal))
            {
                return [];
            }

            try
            {
                var phrase = query.Trim().Trim('"', '\u201c', '\u201d', '\'');
                return await _quotes
                    .SearchAsync(phrase, 10, config.QuoteWindowWords, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning(ex, "Concierge: dialogue search failed; falling back to the item index");
                return [];
            }
        }

        /// <summary>
        /// One line naming what a provider refused, short enough for a status strip.
        /// </summary>
        /// <param name="ex">The failure.</param>
        /// <returns>The first line of its message.</returns>
        /// <remarks>
        /// The message only, never the stack: this reaches the search page and the
        /// query log, and a provider's own wording ("Google API returned 400") is the
        /// part that tells somebody which setting to go and change.
        /// </remarks>
        private static string Describe(Exception ex)
        {
            var line = (ex.Message ?? "the provider failed").Split('\n')[0].Trim();
            return line.Length > 160 ? line[..160] + "\u2026" : line;
        }

        /// <summary>
        /// A token that also gives up when a search has waited longer than it should.
        /// </summary>
        /// <param name="config">The configuration.</param>
        /// <param name="cancellationToken">The caller's token.</param>
        /// <returns>A linked source; dispose it with the call.</returns>
        private static CancellationTokenSource QueryDeadline(
            PluginConfiguration config, CancellationToken cancellationToken)
        {
            var source = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            var seconds = config.QueryTimeoutSeconds;

            if (seconds > 0)
            {
                source.CancelAfter(TimeSpan.FromSeconds(seconds));
            }

            return source;
        }

        /// <summary>
        /// The re-rank's output cap, falling back to the shared one when unset.
        /// </summary>
        /// <param name="config">The configuration.</param>
        /// <returns>The cap to send.</returns>
        /// <remarks>
        /// Zero or negative means an install saved before this setting existed, which
        /// must keep behaving as it did rather than silently acquiring a ceiling.
        /// </remarks>
        /// <summary>
        /// How many entries to ask the re-rank for: the configured count, or the number
        /// that will actually be shown.
        /// </summary>
        /// <param name="config">The configuration.</param>
        /// <returns>The count to request.</returns>
        private static int RerankReturned(PluginConfiguration config)
            => config.RerankReturnCount > 0
                ? config.RerankReturnCount
                : Math.Max(1, config.MaxResults);

        private static int RerankCap(PluginConfiguration config)
            => config.RerankMaxOutputTokens > 0
                ? config.RerankMaxOutputTokens
                : config.MaxOutputTokens;

        /// <summary>
        /// Whether a cancellation was something giving up rather than the caller leaving.
        /// </summary>
        /// <param name="caller">The request's own token.</param>
        /// <returns>True when nobody asked for this and it should degrade.</returns>
        /// <remarks>
        /// <b>Deliberately not tied to our own deadline.</b> There are at least three
        /// ways a model call ends in an <see cref="OperationCanceledException"/> and
        /// only one of them means "stop": our query deadline, the HttpClient's own ten
        /// minute timeout — which throws <see cref="TaskCanceledException"/>, a
        /// cancellation by inheritance — and the caller actually going away.
        /// <para>
        /// Both passes used to exclude every cancellation from their catch, so the
        /// first two escaped the degrade path and left the search as an unhandled
        /// exception. Observed as <c>Error processing request: "The operation was
        /// canceled"</c> on <c>POST /Concierge/Search</c>, which the client shows as a
        /// failed search — for a pipeline that had already produced free results and
        /// was required by hard rule 4 to serve them.
        /// </para>
        /// </remarks>
        private static bool NotTheCallersDoing(CancellationToken caller)
            => !caller.IsCancellationRequested;

        private async Task<(SearchPlan Plan, string? Error)> RunPlanAsync(
            string query,
            PluginConfiguration config,
            ModelProfiles.NormalizedProfiles normalized,
            List<QueryCallRecord> calls,
            CancellationToken cancellationToken)
        {
            using var deadline = QueryDeadline(config, cancellationToken);

            try
            {
                // Hard rule 12: both passes resolve from this one Normalize result.
                var profile = ModelProfiles.Resolve(normalized, config.PlanModelProfileId);
                var provider = _llmFactory.Create(
                    profile,
                    ThinkingPolicy.For(config, ThinkingPass.Plan, profile));

                var request = new LlmRequest(
                    PlanPromptBuilder.SystemPrompt,
                    PlanPromptBuilder.Build(query),
                    string.Empty,
                    Math.Min(config.MaxOutputTokens, 800),
                    ResponseShape.SearchPlan);

                // Logged before the call, not after. A line that only appears on
                // completion cannot tell you where a search is stuck, which is the one
                // question worth asking while it is still running.
                _logger.LogInformation(
                    "Concierge: plan pass calling {Model} ({Provider}), cap {Cap} tokens, thinking {Thinking}",
                    provider.ModelId,
                    profile.Provider,
                    Math.Min(config.MaxOutputTokens, 800),
                    ThinkingPolicy.For(config, ThinkingPass.Plan, profile) ? "on" : "off");

                var stopwatch = Stopwatch.StartNew();
                var result = await provider.CompleteAsync(request, deadline.Token).ConfigureAwait(false);
                stopwatch.Stop();

                _logger.LogInformation(
                    "Concierge: plan pass answered in {Ms}ms — {Out} token(s), {Think} thinking",
                    stopwatch.ElapsedMilliseconds,
                    result.OutputTokens,
                    result.ThinkingTokens);

                calls.Add(Record(QueryPass.Plan, profile, provider.ModelId, result, stopwatch));
                return (PlanParser.Parse(result.Text, query), null);
            }
            catch (OperationCanceledException) when (NotTheCallersDoing(cancellationToken))
            {
                // Ours, not the caller's. A search that waits is still a search that
                // has already answered for free.
                _logger.LogWarning(
                    "Concierge: the plan pass was abandoned before it answered (deadline {Seconds}s); "
                    + "searching on the raw query",
                    config.QueryTimeoutSeconds);

                return (SearchPlan.Passthrough(query), "the planning model did not answer in time");
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // A failed plan is not a failed search — retrieval works perfectly well
                // from the raw query.
                _logger.LogWarning(ex, "Concierge: the plan pass failed; searching on the raw query");
                return (SearchPlan.Passthrough(query), Describe(ex));
            }
        }

        /// <summary>
        /// Holds a reason to its limit.
        /// </summary>
        /// <param name="why">What the model wrote.</param>
        /// <param name="maxChars">The limit, in characters.</param>
        /// <returns>The reason, cut at a word boundary if it was too long.</returns>
        /// <remarks>
        /// Asking is not the same as being obeyed. The prompt has asked for brevity
        /// since the first release and the model has been writing roughly two and a
        /// half times the requested length throughout, so the limit is also applied
        /// here — where it is a fact rather than a request.
        /// <para>
        /// This does not save any time: the tokens were already generated and paid
        /// for by the time we see them. It exists so that a model ignoring the limit
        /// costs latency and not a broken card.
        /// </para>
        /// </remarks>
        public static string Shorten(string why, int maxChars)
        {
            var limit = Math.Max(10, maxChars);

            if (string.IsNullOrEmpty(why) || why.Length <= limit)
            {
                return why ?? string.Empty;
            }

            var cut = why[..limit];
            var space = cut.LastIndexOf(' ');

            return (space > limit / 2 ? cut[..space] : cut).TrimEnd(',', ';', ' ', '-') + "\u2026";
        }

        /// <summary>
        /// How many results to show.
        /// </summary>
        /// <param name="config">The effective configuration.</param>
        /// <param name="rankedByModel">
        /// How many the re-rank pass actually placed, or 0 when it did not run.
        /// </param>
        /// <returns>The number of hits to return.</returns>
        /// <remarks>
        /// A fixed count is a lie about how many things matched. "beatles" on this
        /// library has nine good answers and "im your freaky nicki" has one; padding
        /// both to the same number fills the difference with whatever ranked tenth,
        /// and a reader cannot tell the padding from the answer.
        /// <para>
        /// So when the model has said which ones it would actually show, that count
        /// is the answer. The floor exists because a degenerate reply naming one item
        /// should still leave something to look at beside it, and the ceiling is
        /// whatever the caller asked for. When the re-rank did not run there is no
        /// opinion to honour, and the configured maximum stands — the fused order is
        /// a ranking, not a judgement about where the good answers stop.
        /// </para>
        /// </remarks>
        public static int HowManyToShow(PluginConfiguration config, int rankedByModel)
        {
            ArgumentNullException.ThrowIfNull(config);

            var ceiling = Math.Max(1, config.MaxResults);

            return rankedByModel > 0
                ? Math.Clamp(rankedByModel, Math.Min(MinimumRerankedResults, ceiling), ceiling)
                : ceiling;
        }

        /// <summary>
        /// The fewest results a successful re-rank may reduce the answer to.
        /// </summary>
        private const int MinimumRerankedResults = 3;

        private async Task<RerankOutcome?> RunRerankAsync(
            Action<string> reportFailure,
            string query,
            IReadOnlyList<ItemDocument> shortlist,
            PluginConfiguration config,
            ModelProfiles.NormalizedProfiles normalized,
            List<QueryCallRecord> calls,
            CancellationToken cancellationToken)
        {
            using var deadline = QueryDeadline(config, cancellationToken);

            try
            {
                var profile = ModelProfiles.Resolve(normalized, config.RerankModelProfileId);
                var provider = _llmFactory.Create(
                    profile,
                    ThinkingPolicy.For(config, ThinkingPass.Rerank, profile));

                // The candidate list changes every query, so there is nothing a later
                // call could read back from a cache — everything goes in the prefix and
                // no cache marker is written.
                var prompt = RerankPromptBuilder.BuildCandidates(shortlist)
                    + RerankPromptBuilder.BuildInstruction(
                        query,
                        shortlist.Count,
                        config.RerankWhyMaxChars,
                        config.RerankExplainCount,
                        RerankReturned(config));

                var request = new LlmRequest(
                    RerankPromptBuilder.SystemPrompt,
                    prompt,
                    string.Empty,

                    // Its own cap, not enrichment's. See RerankMaxOutputTokens.
                    RerankCap(config),
                    ResponseShape.Rerank);

                _logger.LogInformation(
                    "Concierge: re-rank calling {Model} ({Provider}) on {Items} item(s), cap {Cap} tokens, "
                    + "thinking {Thinking}",
                    provider.ModelId,
                    profile.Provider,
                    shortlist.Count,
                    RerankCap(config),
                    ThinkingPolicy.For(config, ThinkingPass.Rerank, profile) ? "on" : "off");

                var stopwatch = Stopwatch.StartNew();
                var result = await provider.CompleteAsync(request, deadline.Token).ConfigureAwait(false);
                stopwatch.Stop();

                _logger.LogInformation(
                    "Concierge: re-rank answered in {Ms}ms — {Out} token(s), {Think} thinking, truncated {Trunc}",
                    stopwatch.ElapsedMilliseconds,
                    result.OutputTokens,
                    result.ThinkingTokens,
                    result.Truncated);

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
            catch (OperationCanceledException) when (NotTheCallersDoing(cancellationToken))
            {
                _logger.LogWarning(
                    "Concierge: the re-rank pass was abandoned before it answered (deadline {Seconds}s); "
                    + "serving the fused order",
                    config.QueryTimeoutSeconds);

                reportFailure("the re-ranking model did not answer in time");
                return null;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // The fused order is a perfectly good answer. Slightly worse, free.
                _logger.LogWarning(ex, "Concierge: the re-rank pass failed; serving the fused order");
                reportFailure(Describe(ex));
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
            PluginConfiguration config,
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
            await RecordAsync(config, runId, started, query, userId, finished, calls, cancellationToken)
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
            PluginConfiguration config,
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

                // Dropping the words keeps every number a breakdown needs and removes
                // the part that is a record of what people searched for.
                config.LogQueryText ? query : string.Empty,
                UserKey(userId),
                response.Route,
                calls,
                response.Hits.Count,
                response.DurationMs,
                response.Degraded,
                null,
                response.Hits.Take(5).Select(h => h.Name).ToList(),

                // These three are optional parameters, so leaving them off compiled
                // fine and logged a constant. Every one of 264 entries read
                // "Reranked: false" while 196 of them had paid for a re-rank call,
                // which makes the query log actively misleading about the one field
                // that says whether a search used the expensive path.
                response.Cached,
                response.Reranked,
                response.Quotes?.Count ?? 0);

            return _queryLog.RecordAsync(record, cancellationToken);
        }
    }
}
