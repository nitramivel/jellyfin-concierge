using System;
using System.Collections.Generic;

namespace Jellyfin.Plugin.Concierge.Services.Runs
{
    /// <summary>One recorded step.</summary>
    /// <param name="At">When it happened.</param>
    /// <param name="Step">The machine-readable name.</param>
    /// <param name="Message">The human summary.</param>
    /// <param name="Detail">Structured payload, or null.</param>
    public sealed record RunStepRecord(
        DateTime At,
        string Step,
        string Message,
        IReadOnlyDictionary<string, object?>? Detail);

    /// <summary>One model call, with everything needed to price and debug it.</summary>
    /// <param name="At">When it started.</param>
    /// <param name="Pass">Which pass made it.</param>
    /// <param name="Batch">Which batch, 1-based.</param>
    /// <param name="ItemCount">Items covered.</param>
    /// <param name="Provider">The provider.</param>
    /// <param name="Model">The model that produced the output.</param>
    /// <param name="DurationMs">Wall-clock time.</param>
    /// <param name="InputTokens">Uncached input tokens billed.</param>
    /// <param name="OutputTokens">Output tokens billed, thinking included.</param>
    /// <param name="CacheReadTokens">Input served from cache. Charged, not free.</param>
    /// <param name="CacheWriteTokens">Input written to cache.</param>
    /// <param name="ThinkingTokens">The reasoning share of the output.</param>
    /// <param name="CostUsd">What this one call cost.</param>
    /// <param name="Outcome">ok, truncated, unparseable or error.</param>
    /// <param name="Error">The failure message, when there was one.</param>
    /// <param name="PromptChars">Full prompt length, before truncation for storage.</param>
    /// <param name="ResponseChars">Full response length, before truncation for storage.</param>
    /// <param name="PromptPreview">The head of the prompt.</param>
    /// <param name="ResponsePreview">The head of the response — where enrichment quality is actually visible.</param>
    public sealed record RunCallRecord(
        DateTime At,
        string Pass,
        int Batch,
        int ItemCount,
        string Provider,
        string Model,
        int DurationMs,
        long InputTokens,
        long OutputTokens,
        long CacheReadTokens,
        long CacheWriteTokens,
        long ThinkingTokens,
        decimal CostUsd,
        string Outcome,
        string? Error,
        int PromptChars,
        int ResponseChars,
        string PromptPreview,
        string ResponsePreview);

    /// <summary>One embedding call.</summary>
    /// <param name="At">When it started.</param>
    /// <param name="Batch">Which batch, 1-based.</param>
    /// <param name="RowCount">Rows embedded.</param>
    /// <param name="Provider">The provider.</param>
    /// <param name="Model">The embedding model.</param>
    /// <param name="DurationMs">Wall-clock time.</param>
    /// <param name="InputTokens">Tokens billed, as reported.</param>
    /// <param name="CostUsd">What it cost.</param>
    /// <param name="Error">The failure message, when there was one.</param>
    public sealed record RunEmbeddingRecord(
        DateTime At,
        int Batch,
        int RowCount,
        string Provider,
        string Model,
        int DurationMs,
        long InputTokens,
        decimal CostUsd,
        string? Error);

    /// <summary>
    /// What one item actually got out of the run.
    /// </summary>
    /// <remarks>
    /// The call records say what a <em>batch</em> cost. This says what an item got for
    /// it, which is the question you have when a rebuild costs twenty times what the
    /// last one did: not "what did I spend" but "on what, and was it worth it".
    /// <para>
    /// <see cref="CostUsd"/> is its batch's cost divided by the batch, so it is a
    /// share rather than a measurement — items are billed together and there is no
    /// honest way to split a batch by item. It is recorded anyway because a per-item
    /// figure is what makes two models comparable.
    /// </para>
    /// </remarks>
    /// <param name="Title">The item.</param>
    /// <param name="Year">Its year, when it has one.</param>
    /// <param name="Batch">Which batch it was in, 1-based.</param>
    /// <param name="Outcome">enriched, unknown-to-model, omitted, batch-failed or truncated.</param>
    /// <param name="PremiseChars">How much premise came back.</param>
    /// <param name="Moments">How many moments.</param>
    /// <param name="Themes">How many themes.</param>
    /// <param name="Asks">How many asks — the doc2query sentences a vague search matches.</param>
    /// <param name="Spoiler">Whether the model flagged its own answer as spoiling something.</param>
    /// <param name="CostUsd">Its share of the batch.</param>
    public sealed record RunItemRecord(
        string Title,
        int? Year,
        int Batch,
        string Outcome,
        int PremiseChars,
        int Moments,
        int Themes,
        int Asks,
        bool Spoiler,
        decimal CostUsd);

    /// <summary>
    /// One model's share of a run.
    /// </summary>
    /// <remarks>
    /// A run can enrich on one model and embed on another, and after 0.17 it can use
    /// a different model for every pass. A single chat total cannot express that, and
    /// "why was this run expensive" is unanswerable without it — the last rebuild's
    /// whole story was that one line changed from gpt-5.6-luna to claude-opus-5.
    /// </remarks>
    /// <param name="Provider">The provider.</param>
    /// <param name="Model">The model.</param>
    /// <param name="Pass">Which pass it ran.</param>
    /// <param name="Calls">Calls made.</param>
    /// <param name="Items">Items covered.</param>
    /// <param name="InputTokens">Uncached input.</param>
    /// <param name="OutputTokens">Output.</param>
    /// <param name="ThinkingTokens">Reasoning tokens.</param>
    /// <param name="DurationMs">Time inside those calls.</param>
    /// <param name="CostUsd">What they cost.</param>
    /// <param name="InputCostPerMillion">The price used, so a total can be checked by hand.</param>
    /// <param name="OutputCostPerMillion">The price used, so a total can be checked by hand.</param>
    public sealed record RunModelTotals(
        string Provider,
        string Model,
        string Pass,
        int Calls,
        int Items,
        long InputTokens,
        long OutputTokens,
        long ThinkingTokens,
        int DurationMs,
        decimal CostUsd,
        decimal InputCostPerMillion,
        decimal OutputCostPerMillion);

    /// <summary>
    /// Where an unfinished run was heading.
    /// </summary>
    /// <remarks>
    /// A cancelled run's cost is not the interesting number — the interesting number
    /// is the one you avoided. The last rebuild stopped after 30 of 269 items having
    /// spent $0.40; what mattered was that finishing would have been $3.60 and 36
    /// minutes. That is a fact the log should state rather than one the reader should
    /// have to work out.
    /// </remarks>
    /// <param name="ItemsDone">Items enriched before it stopped.</param>
    /// <param name="ItemsRemaining">Items that never got there.</param>
    /// <param name="CostSoFarUsd">Spent.</param>
    /// <param name="ProjectedTotalCostUsd">What finishing would have cost at this rate.</param>
    /// <param name="ProjectedTotalMs">How long finishing would have taken at this rate.</param>
    public sealed record RunProjection(
        int ItemsDone,
        int ItemsRemaining,
        decimal CostSoFarUsd,
        decimal ProjectedTotalCostUsd,
        long ProjectedTotalMs);

    /// <summary>An item that came out of the run without enrichment, and why.</summary>
    /// <param name="Title">The item.</param>
    /// <param name="Reason">unknown-to-model, omitted, batch-failed or truncated.</param>
    public sealed record NotEnrichedRecord(string Title, string Reason);

    /// <summary>
    /// A run's totals, <b>summed from its per-call costs</b>.
    /// </summary>
    /// <remarks>
    /// Never recomputed from aggregate token counts at a single rate — hard rule 12.
    /// A run that enriches on one model and embeds on another has two prices in it,
    /// and no single rate can express that.
    /// </remarks>
    /// <param name="Calls">Model calls made.</param>
    /// <param name="FailedCalls">How many did not return usable output.</param>
    /// <param name="InputTokens">Uncached input across all calls.</param>
    /// <param name="OutputTokens">Output across all calls.</param>
    /// <param name="CacheReadTokens">Cache reads across all calls.</param>
    /// <param name="CacheWriteTokens">Cache writes across all calls.</param>
    /// <param name="ThinkingTokens">Reasoning tokens across all calls.</param>
    /// <param name="ChatCostUsd">What the chat passes cost.</param>
    /// <param name="EmbeddingCalls">Embedding calls made.</param>
    /// <param name="EmbeddingTokens">Tokens embedded.</param>
    /// <param name="EmbeddingCostUsd">What embedding cost.</param>
    /// <param name="TotalCostUsd">Everything, added up.</param>
    public sealed record RunTotals(
        int Calls,
        int FailedCalls,
        long InputTokens,
        long OutputTokens,
        long CacheReadTokens,
        long CacheWriteTokens,
        long ThinkingTokens,
        decimal ChatCostUsd,
        int EmbeddingCalls,
        long EmbeddingTokens,
        decimal EmbeddingCostUsd,
        decimal TotalCostUsd);

    /// <summary>
    /// One index build, as stored on disk.
    /// </summary>
    /// <remarks>
    /// A mutable class rather than a record: it is written to across the whole run
    /// and serialized repeatedly, so it is a document being edited rather than a
    /// value being passed.
    /// </remarks>
    public sealed class IndexRunDocument
    {
        /// <summary>Gets or sets the run id.</summary>
        public Guid RunId { get; set; }

        /// <summary>Gets or sets what started it — "scheduled" or "manual".</summary>
        public string Trigger { get; set; } = string.Empty;

        /// <summary>Gets or sets when it began.</summary>
        public DateTime StartedUtc { get; set; }

        /// <summary>Gets or sets when it ended, or null while running.</summary>
        public DateTime? FinishedUtc { get; set; }

        /// <summary>Gets or sets running, completed, cancelled or failed.</summary>
        public string Status { get; set; } = "running";

        /// <summary>Gets or sets why it failed, when it did.</summary>
        public string? Error { get; set; }

        /// <summary>Gets or sets progress, 0-100.</summary>
        public double Percent { get; set; }

        /// <summary>Gets or sets what it is doing now.</summary>
        public string Phase { get; set; } = "starting";

        /// <summary>Gets or sets the settings that shaped the run.</summary>
        public Dictionary<string, object?> Settings { get; set; } = [];

        /// <summary>Gets or sets how many items the finished index holds.</summary>
        public int ItemsIndexed { get; set; }

        /// <summary>Gets or sets how many carry non-empty enrichment.</summary>
        public int ItemsEnriched { get; set; }

        /// <summary>
        /// How many items this run set out to enrich.
        /// </summary>
        /// <remarks>
        /// The denominator a projection needs. Without it a cancelled run can say what
        /// it spent and not what it was going to.
        /// </remarks>
        public int ItemsPlanned { get; set; }

        /// <summary>Gets or sets how many vector rows were embedded this run.</summary>
        public int RowsEmbedded { get; set; }

        /// <summary>Gets or sets how many rows needed no embedding.</summary>
        public int RowsReused { get; set; }

        /// <summary>Gets or sets the steps, in order.</summary>
        public List<RunStepRecord> Steps { get; set; } = [];

        /// <summary>Gets or sets every model call, failures included.</summary>
        public List<RunCallRecord> Calls { get; set; } = [];

        /// <summary>Gets or sets every embedding call.</summary>
        public List<RunEmbeddingRecord> Embeddings { get; set; } = [];

        /// <summary>Gets or sets the items that came out unenriched, with reasons.</summary>
        public List<NotEnrichedRecord> NotEnriched { get; set; } = [];

        /// <summary>What each item got, in the order it was processed.</summary>
        public List<RunItemRecord> Items { get; set; } = [];

        /// <summary>What each model did and charged.</summary>
        public List<RunModelTotals> ByModel { get; set; } = [];

        /// <summary>Where an unfinished run was heading, or null once it finished.</summary>
        public RunProjection? Projection { get; set; }

        /// <summary>Gets or sets the totals, written at flush time.</summary>
        public RunTotals Totals { get; set; } = new(0, 0, 0, 0, 0, 0, 0, 0m, 0, 0, 0m, 0m);
    }
}
