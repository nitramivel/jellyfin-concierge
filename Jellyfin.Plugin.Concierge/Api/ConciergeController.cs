using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Concierge.Services;
using Jellyfin.Plugin.Concierge.Services.Indexing;
using Jellyfin.Plugin.Concierge.Services.Runs;
using MediaBrowser.Common.Api;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Concierge.Api
{
    /// <summary>A search request.</summary>
    /// <param name="Query">The user's text.</param>
    /// <param name="UserId">Who is searching, or null.</param>
    /// <param name="Limit">How many results to return; 0 uses the configured default.</param>
    public sealed record SearchRequest(string Query, Guid? UserId, int Limit = 0);

    /// <summary>What the index currently holds.</summary>
    /// <param name="HasIndex">Whether a usable index exists.</param>
    /// <param name="Generation">The index generation, or 0.</param>
    /// <param name="Items">Indexed items.</param>
    /// <param name="Rows">Vector rows.</param>
    /// <param name="Enriched">Items carrying non-empty enrichment.</param>
    /// <param name="EmbeddingModel">The model the vectors were written by.</param>
    /// <param name="Dimensions">The vector width.</param>
    /// <param name="BuiltUtc">When it was last built.</param>
    /// <param name="Note">A plain-language note when something needs attention.</param>
    public sealed record IndexStatus(
        bool HasIndex,
        long Generation,
        int Items,
        int Rows,
        int Enriched,
        string EmbeddingModel,
        int Dimensions,
        DateTime? BuiltUtc,
        string Note);

    /// <summary>
    /// Concierge's HTTP surface.
    /// </summary>
    /// <remarks>
    /// Search is available to any authenticated user, because searching is what
    /// ordinary users do. Everything that spends money or changes the index requires
    /// elevation.
    /// </remarks>
    [ApiController]
    [Route("Concierge")]
    public class ConciergeController : ControllerBase
    {
        private readonly SearchService _search;
        private readonly IIndexStore _store;
        private readonly IRunLogStore _runLog;
        private readonly ILogger<ConciergeController> _logger;

        public ConciergeController(
            SearchService search,
            IIndexStore store,
            IRunLogStore runLog,
            ILogger<ConciergeController> logger)
        {
            _search = search;
            _store = store;
            _runLog = runLog;
            _logger = logger;
        }

        /// <summary>
        /// Searches the library.
        /// </summary>
        /// <param name="request">The query.</param>
        /// <param name="cancellationToken">Cancellation token.</param>
        /// <response code="200">Results, possibly empty when the router chose native search.</response>
        /// <returns>The results.</returns>
        // Bare [Authorize] — any authenticated user — rather than RequiresElevation.
        // Searching is what ordinary users do, and an endpoint only admins can reach
        // cannot back a search box. 10.11 has no named policy for "any signed-in
        // user"; the default policy already means exactly that.
        [HttpPost("Search")]
        [Authorize]
        [ProducesResponseType(StatusCodes.Status200OK)]
        public async Task<ActionResult<SearchResponse>> Search(
            [FromBody] SearchRequest request,
            CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(request);

            var config = Plugin.Instance?.Configuration;
            if (config is null)
            {
                return Ok(new SearchResponse("Native", "plugin not loaded", [], 0, 0m, "plugin not loaded"));
            }

            // An empty or oversized limit falls back to the configured default rather
            // than letting a caller ask for the whole library.
            var effective = config;
            if (request.Limit is > 0 and <= 200)
            {
                effective = CloneWithLimit(config, request.Limit);
            }

            var result = await _search
                .SearchAsync(request.Query ?? string.Empty, request.UserId, effective, cancellationToken)
                .ConfigureAwait(false);

            return Ok(result);
        }

        /// <summary>
        /// Reports what the index holds.
        /// </summary>
        /// <param name="cancellationToken">Cancellation token.</param>
        /// <response code="200">The status.</response>
        /// <returns>The status.</returns>
        [HttpGet("Status")]
        [Authorize(Policy = Policies.RequiresElevation)]
        [ProducesResponseType(StatusCodes.Status200OK)]
        public async Task<ActionResult<IndexStatus>> Status(CancellationToken cancellationToken)
        {
            var state = await _store.LoadStateAsync(cancellationToken).ConfigureAwait(false);
            if (state is null)
            {
                return Ok(new IndexStatus(
                    false, 0, 0, 0, 0, string.Empty, 0, null,
                    "No index yet. Run \"Build the Concierge search index\" from Scheduled Tasks."));
            }

            var note = state.EnrichedCount == 0 && state.ItemCount > 0
                ? "The index holds no enrichment, so plot and mood searches will be weak. "
                  + "Check that an enrichment model profile is set."
                : string.Empty;

            return Ok(new IndexStatus(
                true,
                state.Generation,
                state.ItemCount,
                state.RowCount,
                state.EnrichedCount,
                state.EmbeddingModel,
                state.Dimensions,
                state.BuiltUtc,
                note));
        }

        /// <summary>
        /// Reads recent queries and what they cost.
        /// </summary>
        /// <param name="limit">How many to return.</param>
        /// <param name="cancellationToken">Cancellation token.</param>
        /// <response code="200">The recorded queries, newest first.</response>
        /// <returns>The queries.</returns>
        [HttpGet("Runs")]
        [Authorize(Policy = Policies.RequiresElevation)]
        [ProducesResponseType(StatusCodes.Status200OK)]
        public async Task<ActionResult<IReadOnlyList<QueryRunRecord>>> Runs(
            [FromQuery] int limit,
            CancellationToken cancellationToken)
        {
            var records = await _runLog
                .RecentAsync(limit <= 0 ? 50 : limit, cancellationToken)
                .ConfigureAwait(false);

            return Ok(records);
        }

        /// <summary>
        /// Deletes the index.
        /// </summary>
        /// <remarks>
        /// Safe: the index is a cache and the library is read-only, so this restores
        /// exactly the behaviour the server had before Concierge was installed. It
        /// does throw away the enrichment, which is the part that cost money.
        /// </remarks>
        /// <param name="cancellationToken">Cancellation token.</param>
        /// <response code="204">Deleted.</response>
        /// <returns>No content.</returns>
        [HttpPost("Index/Delete")]
        [Authorize(Policy = Policies.RequiresElevation)]
        [ProducesResponseType(StatusCodes.Status204NoContent)]
        public async Task<ActionResult> DeleteIndex(CancellationToken cancellationToken)
        {
            await _store.DeleteAsync(cancellationToken).ConfigureAwait(false);
            _search.Invalidate();
            _logger.LogInformation("Concierge: the index was deleted from the plugin settings");
            return NoContent();
        }

        private static Configuration.PluginConfiguration CloneWithLimit(
            Configuration.PluginConfiguration config,
            int limit)
        {
            // A shallow copy so a per-request limit cannot mutate the shared
            // configuration object every other request is reading.
            return new Configuration.PluginConfiguration
            {
                ModelProfiles = config.ModelProfiles,
                EmbeddingProfiles = config.EmbeddingProfiles,
                DefaultModelProfileId = config.DefaultModelProfileId,
                DefaultEmbeddingProfileId = config.DefaultEmbeddingProfileId,
                PlanModelProfileId = config.PlanModelProfileId,
                RerankModelProfileId = config.RerankModelProfileId,
                EnrichmentModelProfileId = config.EnrichmentModelProfileId,
                EmbeddingProfileId = config.EmbeddingProfileId,
                EnableThinking = config.EnableThinking,
                MaxOutputTokens = config.MaxOutputTokens,
                IncludeEpisodes = config.IncludeEpisodes,
                EnableEnrichment = config.EnableEnrichment,
                EnrichmentBatchSize = config.EnrichmentBatchSize,
                MaxAsksPerItem = config.MaxAsksPerItem,
                EmbeddingBatchSize = config.EmbeddingBatchSize,
                MaxResults = limit,
            };
        }
    }
}
