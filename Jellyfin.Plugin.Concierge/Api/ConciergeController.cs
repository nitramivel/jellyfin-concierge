using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Concierge.Core.Usage;
using Jellyfin.Plugin.Concierge.Services;
using Jellyfin.Plugin.Concierge.Services.Indexing;
using Jellyfin.Plugin.Concierge.Services.Quotes;
using Jellyfin.Plugin.Concierge.Services.Runs;
using MediaBrowser.Common.Api;
using MediaBrowser.Model.Tasks;
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
    /// <param name="Preview">
    /// Answer from keyword retrieval alone, spending nothing. The free half of the
    /// pipeline returns in about a millisecond, so a caller can show something real
    /// immediately and replace it when the full answer arrives seconds later.
    /// </param>
    public sealed record SearchRequest(
        string Query,
        Guid? UserId,
        int Limit = 0,
        bool Preview = false);

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
        private readonly IQueryLogStore _queryLog;
        private readonly IIndexRunLogStore _indexRuns;
        private readonly ITaskManager _taskManager;
        private readonly IndexBuildRequest _indexBuildRequest;
        private readonly IQuoteStore _quotes;
        private readonly ILogger<ConciergeController> _logger;

        public ConciergeController(
            SearchService search,
            IIndexStore store,
            IQueryLogStore queryLog,
            IIndexRunLogStore indexRuns,
            ITaskManager taskManager,
            IndexBuildRequest indexBuildRequest,
            IQuoteStore quotes,
            ILogger<ConciergeController> logger)
        {
            _search = search;
            _store = store;
            _queryLog = queryLog;
            _indexRuns = indexRuns;
            _taskManager = taskManager;
            _indexBuildRequest = indexBuildRequest;
            _quotes = quotes;
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
                .SearchAsync(
                    request.Query ?? string.Empty,
                    request.UserId,
                    effective,
                    cancellationToken,
                    request.Preview)
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
            var records = await _queryLog
                .RecentAsync(limit <= 0 ? 50 : limit, cancellationToken)
                .ConfigureAwait(false);

            return Ok(records);
        }

        /// <summary>
        /// A usage and cost breakdown over recent months.
        /// </summary>
        /// <remarks>
        /// Reads the query log, which is append-only and month-partitioned, so asking
        /// for one month reads one file. Every total is summed from per-call costs
        /// rather than derived from aggregate tokens at a single rate — a report is
        /// precisely where a wrong number gets believed and acted on.
        /// </remarks>
        /// <param name="months">How many months back to include; defaults to 3.</param>
        /// <param name="cancellationToken">Cancellation token.</param>
        /// <response code="200">The breakdown.</response>
        /// <returns>The breakdown.</returns>
        [HttpGet("Usage")]
        [Authorize(Policy = Policies.RequiresElevation)]
        [ProducesResponseType(StatusCodes.Status200OK)]
        public async Task<ActionResult<UsageReport>> Usage(
            [FromQuery] int months,
            CancellationToken cancellationToken)
        {
            var back = Math.Clamp(months <= 0 ? 3 : months, 1, QueryLogStore.MonthsRetained);
            var from = new DateTime(DateTime.UtcNow.Year, DateTime.UtcNow.Month, 1, 0, 0, 0, DateTimeKind.Utc)
                .AddMonths(-(back - 1));

            var records = await _queryLog.SinceAsync(from, cancellationToken).ConfigureAwait(false);
            return Ok(UsageRollup.Build(records));
        }

        /// <summary>
        /// Lists index builds, newest first, with what each cost.
        /// </summary>
        /// <param name="limit">How many to return.</param>
        /// <response code="200">The runs.</response>
        /// <returns>The runs.</returns>
        [HttpGet("Index/Runs")]
        [Authorize(Policy = Policies.RequiresElevation)]
        [ProducesResponseType(StatusCodes.Status200OK)]
        public ActionResult<IReadOnlyList<IndexRunSummary>> IndexRuns([FromQuery] int limit)
            => Ok(_indexRuns.List(limit <= 0 ? 25 : limit));

        /// <summary>
        /// The build in flight, or null when nothing is running.
        /// </summary>
        /// <remarks>
        /// Read from memory so the settings page can poll it to move a progress bar
        /// without pulling a whole run document off disk each time.
        /// </remarks>
        /// <response code="200">The current run, or null.</response>
        /// <returns>The current run.</returns>
        [HttpGet("Index/Current")]
        [Authorize(Policy = Policies.RequiresElevation)]
        [ProducesResponseType(StatusCodes.Status200OK)]
        public ActionResult<IndexRunSummary?> CurrentIndexRun() => Ok(_indexRuns.Current());

        /// <summary>
        /// Queues a complete regeneration of the Concierge search index.
        /// </summary>
        /// <remarks>
        /// Unlike the normal incremental build, this deliberately re-runs enrichment
        /// and embeddings for every item. The old index remains available while the
        /// replacement is built and is only superseded after a successful write.
        /// </remarks>
        /// <response code="202">Regeneration queued.</response>
        /// <response code="409">An index build is already running.</response>
        /// <response code="503">Jellyfin has not registered the index task.</response>
        /// <returns>An action result.</returns>
        [HttpPost("Index/Regenerate")]
        [Authorize(Policy = Policies.RequiresElevation)]
        [ProducesResponseType(StatusCodes.Status202Accepted)]
        [ProducesResponseType(StatusCodes.Status409Conflict)]
        [ProducesResponseType(StatusCodes.Status503ServiceUnavailable)]
        public ActionResult RegenerateIndex()
        {
            var worker = _taskManager.ScheduledTasks.FirstOrDefault(w =>
                string.Equals(w.ScheduledTask.Key, IndexBuildTask.TaskKey, StringComparison.Ordinal));

            if (worker is null)
            {
                return StatusCode(
                    StatusCodes.Status503ServiceUnavailable,
                    "The Concierge index task is not registered.");
            }

            if (worker.State != TaskState.Idle || _indexRuns.Current() is not null)
            {
                return Conflict("An index build is already in progress.");
            }

            _indexBuildRequest.RequestFullRegeneration();
            _taskManager.QueueIfNotRunning<IndexBuildTask>();
            _logger.LogInformation("Concierge: a full index regeneration was requested from the plugin settings");
            return Accepted();
        }

        /// <summary>
        /// One build's whole record: every step, every model call with its tokens and
        /// cost, and every item that came out unenriched with the reason.
        /// </summary>
        /// <param name="runId">The run.</param>
        /// <response code="200">The run document.</response>
        /// <response code="404">No such run.</response>
        /// <returns>The run document as stored.</returns>
        [HttpGet("Index/Runs/{runId}")]
        [Authorize(Policy = Policies.RequiresElevation)]
        [ProducesResponseType(StatusCodes.Status200OK)]
        [ProducesResponseType(StatusCodes.Status404NotFound)]
        public ActionResult IndexRunDetail([FromRoute] Guid runId)
        {
            var raw = _indexRuns.ReadRaw(runId);
            return raw is null ? NotFound() : Content(raw, "application/json");
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

        /// <summary>
        /// What can and cannot be found by its dialogue, and why.
        /// </summary>
        /// <remarks>
        /// The gap is the point of this endpoint. Roughly a quarter of a typical
        /// library has image-only subtitles, which cannot be read without OCR — but
        /// downloading an external text track fixes each one for free, and that is
        /// only actionable if the owner can see which items are affected.
        /// </remarks>
        /// <param name="cancellationToken">Cancellation token.</param>
        /// <response code="200">The coverage report.</response>
        /// <returns>The report.</returns>
        /// <summary>
        /// Everything the index holds, one row per item.
        /// </summary>
        /// <param name="cancellationToken">Cancellation token.</param>
        /// <response code="200">The library as Concierge sees it.</response>
        /// <returns>The library.</returns>
        /// <remarks>
        /// The whole library in one response. It is a few hundred items and a summary
        /// row each, so paging it would add a second request and a page-number bug in
        /// exchange for nothing — and the point of the view is the shape of the whole
        /// thing, which paging hides.
        /// </remarks>
        [HttpGet("Library")]
        [Authorize(Policy = Policies.RequiresElevation)]
        [ProducesResponseType(StatusCodes.Status200OK)]
        public async Task<ActionResult<LibraryView>> Library(CancellationToken cancellationToken)
        {
            var index = await LoadIndexAsync(cancellationToken).ConfigureAwait(false);

            if (index is null)
            {
                return Ok(new LibraryView([], 0, 0, 0, 0, 0));
            }

            var rows = CountRowsByItem(index);
            var cues = await CountCuesByItemAsync(cancellationToken).ConfigureAwait(false);

            var items = index.Documents
                .Select(d => Summarize(d, rows, cues))
                .OrderBy(i => i.Title, StringComparer.OrdinalIgnoreCase)
                .ToList();

            return Ok(new LibraryView(
                items,
                items.Count,
                items.Count(i => i.Enriched),

                // The number worth watching. An item with no asks is findable by title
                // and overview only, which is exactly the search Concierge exists to
                // beat.
                items.Count(i => i.Asks == 0),
                items.Count(i => i.Cues > 0),
                index.State.Generation));
        }

        /// <summary>
        /// Everything Concierge holds for one item, including the text it embedded.
        /// </summary>
        /// <param name="itemId">The item.</param>
        /// <param name="cancellationToken">Cancellation token.</param>
        /// <response code="200">The item.</response>
        /// <response code="404">No such item in the index.</response>
        /// <returns>The item.</returns>
        [HttpGet("Library/{itemId}")]
        [Authorize(Policy = Policies.RequiresElevation)]
        [ProducesResponseType(StatusCodes.Status200OK)]
        [ProducesResponseType(StatusCodes.Status404NotFound)]
        public async Task<ActionResult<LibraryItemDetail>> LibraryItem(
            [FromRoute] Guid itemId,
            CancellationToken cancellationToken)
        {
            var index = await LoadIndexAsync(cancellationToken).ConfigureAwait(false);
            var document = index?.Documents.FirstOrDefault(d => d.ItemId == itemId);

            if (index is null || document is null)
            {
                return NotFound();
            }

            var rows = CountRowsByItem(index);
            var track = await _quotes.LoadAsync(itemId, cancellationToken).ConfigureAwait(false);
            var cues = new Dictionary<Guid, int>();

            if (track is not null)
            {
                cues[itemId] = track.Cues.Count;
            }

            var enrichment = document.Enrichment;
            var stored = await _store.LoadEnrichmentAsync(cancellationToken).ConfigureAwait(false);
            var provenance = Provenance(stored.GetValueOrDefault(itemId));

            return Ok(new LibraryItemDetail(
                Summarize(document, rows, cues),
                document.OriginalTitle,
                document.Tags,
                document.Studios,
                document.People,
                document.OfficialRating,
                document.RuntimeMinutes,
                document.Overview,
                enrichment?.Premise ?? string.Empty,
                enrichment?.Moments ?? [],
                enrichment?.Themes ?? [],
                enrichment?.Asks ?? [],

                // The text that was actually embedded, verbatim. Retrieval is only ever
                // as good as this, and until now the only way to see it was to read
                // rows.json off the server.
                index.Vectors.Sources
                    .Where(s => s.ItemId == itemId)
                    .Select(s => new LibraryVectorRow(s.Kind.ToString(), s.Text))
                    .ToList(),
                track is null ? [] : track.Cues.Take(12).Select(c => c.Text).ToList(),
                track?.SourcePath ?? string.Empty,
                track?.ExtractedUtc,
                provenance));
        }

        /// <summary>
        /// Which build wrote an item's enrichment, and whether that build can still be
        /// opened.
        /// </summary>
        /// <remarks>
        /// An empty run id means the entry predates the tie. Reported as null rather
        /// than as a run that does not exist, because "we did not record this" and
        /// "the run was deleted" are different facts and only one of them is a gap in
        /// the log.
        /// </remarks>
        private ItemProvenance? Provenance(Core.Documents.StoredEnrichment? stored)
        {
            if (stored is null)
            {
                return null;
            }

            var known = stored.RunId != Guid.Empty;

            return new ItemProvenance(
                known ? stored.RunId : null,
                known && _indexRuns.ReadRaw(stored.RunId) is not null,
                stored.GeneratedUtc,
                stored.Model,
                stored.CostUsd,
                stored.SourceHash);
        }

        private static LibraryItemSummary Summarize(
            Core.Documents.ItemDocument document,
            IReadOnlyDictionary<Guid, int> rows,
            IReadOnlyDictionary<Guid, int> cues)
        {
            var e = document.Enrichment;

            return new LibraryItemSummary(
                document.ItemId,
                document.Title,
                document.Year,
                document.Kind,
                document.Genres,
                e is { IsEmpty: false },
                e?.Premise.Length ?? 0,
                e?.Moments.Count ?? 0,
                e?.Themes.Count ?? 0,
                e?.Asks.Count ?? 0,
                e?.Spoiler ?? false,
                rows.GetValueOrDefault(document.ItemId),
                cues.GetValueOrDefault(document.ItemId));
        }

        private static Dictionary<Guid, int> CountRowsByItem(
            Services.Indexing.ConciergeIndex index)
        {
            var rows = new Dictionary<Guid, int>();

            foreach (var source in index.Vectors.Sources)
            {
                rows[source.ItemId] = rows.GetValueOrDefault(source.ItemId) + 1;
            }

            return rows;
        }

        private async Task<Dictionary<Guid, int>> CountCuesByItemAsync(
            CancellationToken cancellationToken)
        {
            try
            {
                var tracks = await _quotes.LoadAllAsync(cancellationToken).ConfigureAwait(false);
                return tracks.ToDictionary(t => t.ItemId, t => t.Cues.Count);
            }
            catch (Exception ex)
            {
                // Dialogue is optional and this view is not. A quote store that cannot
                // be read costs a column, not the page.
                _logger.LogWarning(ex, "Concierge: could not read extracted dialogue for the library view");
                return [];
            }
        }

        private async Task<Services.Indexing.ConciergeIndex?> LoadIndexAsync(
            CancellationToken cancellationToken)
        {
            var config = Plugin.Instance?.Configuration;

            if (config is null)
            {
                return null;
            }

            var profile = Core.Llm.EmbeddingProfiles.Resolve(config, config.EmbeddingProfileId);

            return await _store.LoadAsync(profile, cancellationToken).ConfigureAwait(false);
        }

        [HttpGet("Quotes/Coverage")]
        [Authorize(Policy = Policies.RequiresElevation)]
        [ProducesResponseType(StatusCodes.Status200OK)]
        public async Task<ActionResult<IReadOnlyList<QuoteCoverage>>> QuoteCoverage(
            CancellationToken cancellationToken)
            => Ok(await _quotes.LoadCoverageAsync(cancellationToken).ConfigureAwait(false));

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
                EnablePlanPass = config.EnablePlanPass,
                EnableRerankPass = config.EnableRerankPass,
                RerankShortlistSize = config.RerankShortlistSize,
                MonthlyBudgetUsd = config.MonthlyBudgetUsd,
                EnrichmentBudgetUsd = config.EnrichmentBudgetUsd,
                PaidQueriesPerUserPerHour = config.PaidQueriesPerUserPerHour,
                QueryCacheSize = config.QueryCacheSize,
                EnableQuoteSearch = config.EnableQuoteSearch,
                QuoteIncludeEpisodes = config.QuoteIncludeEpisodes,
                SubtitleLanguage = config.SubtitleLanguage,
                QuoteWindowWords = config.QuoteWindowWords,
                EnableLyricSearch = config.EnableLyricSearch,
                LogQueryText = config.LogQueryText,
            };
        }
    }
}
