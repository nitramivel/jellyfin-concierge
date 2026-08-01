using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Concierge.Configuration;
using Jellyfin.Plugin.Concierge.Core.Documents;
using Jellyfin.Plugin.Concierge.Core.Llm;
using Jellyfin.Plugin.Concierge.Core.Query;
using Jellyfin.Plugin.Concierge.Core.Retrieval;
using Jellyfin.Plugin.Concierge.Services.Embeddings;
using Jellyfin.Plugin.Concierge.Services.Indexing;
using Jellyfin.Plugin.Concierge.Services.Runs;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Concierge.Services
{
    /// <summary>One result.</summary>
    /// <param name="ItemId">The item.</param>
    /// <param name="Name">Its title, for display and for the evaluation harness.</param>
    /// <param name="Year">Its year, or null.</param>
    /// <param name="Score">The fused score.</param>
    /// <param name="LexicalRank">Where keyword retrieval put it, or null.</param>
    /// <param name="VectorRank">Where semantic retrieval put it, or null.</param>
    /// <param name="Why">
    /// A short line saying why it matched. In phase 1 this is derived from which
    /// retrievers found it; phase 2 replaces it with the re-ranker's own sentence.
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
    /// <param name="CostUsd">What the query cost. Zero on the phase-1 free path.</param>
    /// <param name="Degraded">
    /// Why part of the pipeline was skipped, or null. Never an error: a search box
    /// that returns an error message is a broken search box.
    /// </param>
    public sealed record SearchResponse(
        string Route,
        string RouteReason,
        IReadOnlyList<SearchHit> Hits,
        int DurationMs,
        decimal CostUsd,
        string? Degraded);

    /// <summary>
    /// The end-to-end query. Every entry point calls this.
    /// </summary>
    /// <remarks>
    /// Phase 1: routing, keyword retrieval, semantic retrieval, fusion. <b>No chat
    /// model is called anywhere in here</b> — the only paid step is embedding the
    /// query itself, and even that degrades to keyword-only rather than failing.
    /// The re-rank pass arrives in phase 2 and slots in after fusion.
    /// </remarks>
    public sealed class SearchService
    {
        private readonly IIndexStore _store;
        private readonly IEmbeddingProviderFactory _embeddingFactory;
        private readonly IQueryLogStore _runLog;
        private readonly ILogger<SearchService> _logger;
        private readonly SemaphoreSlim _loadGate = new(1, 1);

        private ConciergeIndex? _index;

        public SearchService(
            IIndexStore store,
            IEmbeddingProviderFactory embeddingFactory,
            IQueryLogStore runLog,
            ILogger<SearchService> logger)
        {
            _store = store;
            _embeddingFactory = embeddingFactory;
            _runLog = runLog;
            _logger = logger;
        }

        /// <summary>
        /// Drops the cached index so the next search reloads it.
        /// </summary>
        public void Invalidate()
        {
            _index = null;
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

            var index = await GetIndexAsync(config, cancellationToken).ConfigureAwait(false);
            var decision = QueryRouter.Decide(query, index?.Lexical);

            // The router says Jellyfin's own search is the right answer. Returning
            // nothing is correct: the client already has those results, and the whole
            // point is that we did not spend anything to agree.
            if (decision.Route == QueryRoute.Native)
            {
                stopwatch.Stop();
                var native = new SearchResponse(
                    decision.Route.ToString(),
                    decision.Reason,
                    [],
                    (int)stopwatch.ElapsedMilliseconds,
                    0m,
                    null);

                await RecordAsync(runId, started, query, userId, native, [], cancellationToken)
                    .ConfigureAwait(false);
                return native;
            }

            if (index is null)
            {
                // No index yet, or one built by a different embedding model. Either
                // way there is nothing to search and native already answered.
                stopwatch.Stop();
                var empty = new SearchResponse(
                    decision.Route.ToString(),
                    decision.Reason,
                    [],
                    (int)stopwatch.ElapsedMilliseconds,
                    0m,
                    "no usable index — run the Concierge index task");

                await RecordAsync(runId, started, query, userId, empty, [], cancellationToken)
                    .ConfigureAwait(false);
                return empty;
            }

            var limit = Math.Max(1, config.MaxResults);

            // Free, and always runs. Candidate pools are deliberately wider than the
            // result list: fusion needs room to disagree.
            var lexical = index.Lexical.Search(query, limit * 3);

            var calls = new List<QueryCallRecord>();
            IReadOnlyList<ScoredItem> vector = [];
            string? degraded = null;

            try
            {
                (vector, var call) = await SearchVectorsAsync(
                        query, index, config, limit * 3, cancellationToken)
                    .ConfigureAwait(false);

                if (call is not null)
                {
                    calls.Add(call);
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // Hard rule 4. The embedding provider being down, misconfigured or out
                // of quota degrades this to keyword-only — which is worse, still
                // useful, and free. It is never surfaced as a failed search.
                degraded = "semantic search unavailable — keyword results only";
                _logger.LogWarning(ex, "Concierge: query embedding failed; serving lexical results only");
            }

            var fused = RankFusion.Fuse(lexical, vector);
            var byId = index.Documents.ToDictionary(d => d.ItemId);

            var hits = fused
                .Take(limit)
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
                        Explain(f));
                })
                .ToList();

            stopwatch.Stop();

            var response = new SearchResponse(
                decision.Route.ToString(),
                decision.Reason,
                hits,
                (int)stopwatch.ElapsedMilliseconds,
                calls.Sum(c => c.EstimatedCostUsd),
                degraded);

            await RecordAsync(runId, started, query, userId, response, calls, cancellationToken)
                .ConfigureAwait(false);

            return response;
        }

        private async Task<(IReadOnlyList<ScoredItem> Hits, QueryCallRecord? Call)> SearchVectorsAsync(
            string query,
            ConciergeIndex index,
            PluginConfiguration config,
            int limit,
            CancellationToken cancellationToken)
        {
            var profile = EmbeddingProfiles.Resolve(config, config.EmbeddingProfileId);
            var embedder = _embeddingFactory.Create(profile);

            var stopwatch = Stopwatch.StartNew();

            // EmbeddingPurpose.Query, never Document. The provider turns that into
            // whatever its model expects — a text prefix, a taskType, an input_type —
            // and getting it wrong degrades results with no error at all.
            var embedded = await embedder
                .EmbedAsync([query], EmbeddingPurpose.Query, cancellationToken)
                .ConfigureAwait(false);

            stopwatch.Stop();

            var call = new QueryCallRecord(
                QueryPass.Embedding,
                profile.Provider.ToString(),
                embedder.ModelId,
                embedded.InputTokens,
                0,
                0,
                0,
                0,
                CallCost.ForEmbedding(profile, embedded.InputTokens),
                (int)stopwatch.ElapsedMilliseconds,
                false);

            return (index.Vectors.Search(embedded.Vectors[0], limit), call);
        }

        /// <summary>
        /// A one-line reason, from which retrievers found the item.
        /// </summary>
        /// <remarks>
        /// Phase 1 has no model to ask, so this says what is actually known rather
        /// than inventing a justification. Agreement between the two retrievers is the
        /// strongest free signal there is.
        /// </remarks>
        private static string Explain(FusedResult result)
        {
            if (result.FoundByBoth)
            {
                return "matches both the words and the meaning";
            }

            return result.LexicalRank is not null
                ? "matches the words you typed"
                : "close in meaning";
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
                    // Nothing configured yet. Not an error worth logging on every
                    // keystroke — the config page already says what is missing.
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
                userId?.ToString("N", CultureInfo.InvariantCulture),
                response.Route,
                calls,
                response.Hits.Count,
                response.DurationMs,
                response.Degraded,
                null,

                // Enough to tell a good answer from a bad one when reading the log
                // back, and few enough not to turn the log into a second index.
                response.Hits.Take(5).Select(h => h.Name).ToList());

            return _runLog.RecordAsync(record, cancellationToken);
        }
    }
}
