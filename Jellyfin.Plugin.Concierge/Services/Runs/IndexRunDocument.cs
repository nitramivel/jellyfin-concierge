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

        /// <summary>Gets or sets the totals, written at flush time.</summary>
        public RunTotals Totals { get; set; } = new(0, 0, 0, 0, 0, 0, 0, 0m, 0, 0, 0m, 0m);
    }
}
