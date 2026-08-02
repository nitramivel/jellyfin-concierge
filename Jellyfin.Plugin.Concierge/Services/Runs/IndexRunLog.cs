using System;
using System.Collections.Generic;
using Jellyfin.Plugin.Concierge.Configuration;
using Jellyfin.Plugin.Concierge.Services.Llm;

namespace Jellyfin.Plugin.Concierge.Services.Runs
{
    /// <summary>
    /// The rates one call was billed at.
    /// </summary>
    /// <remarks>
    /// Carried per call rather than per run because an index build may use one model
    /// for enrichment and a different one for anything added later, and hard rule 12
    /// forbids pricing a multi-model run at a single rate.
    /// </remarks>
    /// <param name="InputCostPerMillion">Input rate.</param>
    /// <param name="OutputCostPerMillion">Output rate.</param>
    /// <param name="CachedInputCostPerMillion">Cache-read rate. Cheap, never free.</param>
    public sealed record RunPricing(
        decimal InputCostPerMillion,
        decimal OutputCostPerMillion,
        decimal CachedInputCostPerMillion)
    {
        /// <summary>Reads the rates off a model profile.</summary>
        /// <param name="profile">The profile.</param>
        /// <returns>Its rates.</returns>
        public static RunPricing From(ModelProfile profile)
        {
            ArgumentNullException.ThrowIfNull(profile);

            return new RunPricing(
                profile.InputCostPerMillion,
                profile.OutputCostPerMillion,
                profile.EffectiveCachedInputCostPerMillion());
        }
    }

    /// <summary>
    /// The recorder for one index build. Handed down the call chain so every stage
    /// writes into the same file.
    /// </summary>
    /// <remarks>
    /// <b>Every method here is best-effort and must never throw.</b> A build that
    /// failed because its diagnostics failed would be strictly worse than one with no
    /// diagnostics at all — and this one spends money, so losing a completed pass to
    /// a logging bug would mean losing the money with it.
    /// </remarks>
    public interface IIndexRunLog
    {
        /// <summary>Gets the run's id, which names its file.</summary>
        Guid RunId { get; }

        /// <summary>
        /// Records one step of the run.
        /// </summary>
        /// <param name="step">A stable machine-readable name, e.g. "library.scanned".</param>
        /// <param name="message">A one-line human summary.</param>
        /// <param name="detail">Structured payload for this step, or null.</param>
        void Step(string step, string message, IReadOnlyDictionary<string, object?>? detail = null);

        /// <summary>Records progress, mirroring what the scheduled task reports.</summary>
        /// <param name="percent">Progress from 0 to 100.</param>
        void Progress(double percent);

        /// <summary>
        /// Records one model exchange in full, <b>including attempts that failed</b>.
        /// </summary>
        /// <remarks>
        /// Failures are the entries worth having. A batch that 400s, truncates, or
        /// returns unparseable JSON has still been paid for in most cases, and a log
        /// that only records successes makes a run look cheaper and healthier than it
        /// was.
        /// </remarks>
        /// <param name="pass">Which pass, e.g. "enrichment".</param>
        /// <param name="batch">Which batch this covers, 1-based.</param>
        /// <param name="itemCount">How many items were in the batch.</param>
        /// <param name="duration">Wall-clock time for the call.</param>
        /// <param name="request">What was sent.</param>
        /// <param name="result">What came back, or null when the call threw.</param>
        /// <param name="outcome">"ok", "truncated", "unparseable", or "error".</param>
        /// <param name="error">The failure message, when there was one.</param>
        /// <param name="model">The model that produced this output.</param>
        /// <param name="provider">Its provider.</param>
        /// <param name="pricing">This call's own rates.</param>
        void LlmCall(
            string pass,
            int batch,
            int itemCount,
            TimeSpan duration,
            LlmRequest request,
            LlmResult? result,
            string outcome,
            string? error,
            string model,
            string provider,
            RunPricing pricing);

        /// <summary>
        /// Records one embedding call.
        /// </summary>
        /// <param name="batch">Which batch, 1-based.</param>
        /// <param name="rowCount">How many rows were embedded.</param>
        /// <param name="duration">Wall-clock time.</param>
        /// <param name="inputTokens">Tokens billed, as reported by the provider.</param>
        /// <param name="cost">What it cost.</param>
        /// <param name="model">The embedding model.</param>
        /// <param name="provider">Its provider.</param>
        /// <param name="error">The failure message, when there was one.</param>
        void EmbeddingCall(
            int batch,
            int rowCount,
            TimeSpan duration,
            long inputTokens,
            decimal cost,
            string model,
            string provider,
            string? error = null);

        /// <summary>
        /// Records what one item actually got out of a batch.
        /// </summary>
        /// <remarks>
        /// The call record says what a batch cost; this says what an item got for it.
        /// Together they answer the question a bill cannot: not "what did I spend" but
        /// "on what, and was it any good".
        /// </remarks>
        /// <param name="item">The item's outcome and what came back for it.</param>
        void ItemEnriched(RunItemRecord item);

        /// <summary>
        /// Names an item the run could not enrich, and why.
        /// </summary>
        /// <remarks>
        /// The single most useful thing this log holds. "3 failed" in a summary line
        /// is unactionable; three titles with a reason each is a bug report.
        /// </remarks>
        /// <param name="title">The item's title.</param>
        /// <param name="reason">"unknown-to-model", "omitted", "batch-failed", or "truncated".</param>
        void ItemNotEnriched(string title, string reason);

        /// <summary>Marks the run finished and flushes the file.</summary>
        void Complete();

        /// <summary>
        /// Marks the run cancelled and flushes the file.
        /// </summary>
        /// <remarks>
        /// Distinct from <see cref="Fail"/>: a cancelled run is one somebody stopped
        /// on purpose, and thanks to checkpointing it keeps everything it had already
        /// paid for. Recording it as a failure would make a deliberate stop look like
        /// a defect.
        /// </remarks>
        void Cancel();

        /// <summary>Marks the run failed and flushes the file.</summary>
        /// <param name="error">What went wrong.</param>
        void Fail(string error);
    }

    /// <summary>One run, reduced to what a list row shows.</summary>
    /// <param name="RunId">The run.</param>
    /// <param name="Trigger">"scheduled" or "manual".</param>
    /// <param name="StartedUtc">When it began.</param>
    /// <param name="FinishedUtc">When it ended, or null while running.</param>
    /// <param name="Status">running, completed, cancelled or failed.</param>
    /// <param name="Percent">Progress, 0-100.</param>
    /// <param name="Phase">What it is doing now, or what it last did.</param>
    /// <param name="ItemsIndexed">Items in the finished index.</param>
    /// <param name="ItemsEnriched">Items carrying non-empty enrichment.</param>
    /// <param name="RowsEmbedded">Vector rows embedded this run.</param>
    /// <param name="RowsReused">Rows that needed no embedding.</param>
    /// <param name="CostUsd">Total, summed from per-call costs.</param>
    /// <param name="Error">Why it failed, when it did.</param>
    public sealed record IndexRunSummary(
        Guid RunId,
        string Trigger,
        DateTime StartedUtc,
        DateTime? FinishedUtc,
        string Status,
        double Percent,
        string Phase,
        int ItemsIndexed,
        int ItemsEnriched,
        int RowsEmbedded,
        int RowsReused,
        decimal CostUsd,
        string? Error,
        string Models,
        decimal? ProjectedCostUsd,
        long? ProjectedTotalMs);

    /// <summary>
    /// Opens run logs. One file per run, in their own directory.
    /// </summary>
    public interface IIndexRunLogStore
    {
        /// <summary>
        /// Starts recording a new run.
        /// </summary>
        /// <param name="trigger">"scheduled" or "manual".</param>
        /// <param name="settings">The settings that shaped the run.</param>
        /// <returns>The recorder.</returns>
        IIndexRunLog Begin(string trigger, IReadOnlyDictionary<string, object?> settings);

        /// <summary>Lists recorded runs, newest first.</summary>
        /// <param name="limit">The most to return.</param>
        /// <returns>The summaries.</returns>
        IReadOnlyList<IndexRunSummary> List(int limit = 25);

        /// <summary>
        /// A live snapshot of the run in flight, read from memory, or null when
        /// nothing is running.
        /// </summary>
        /// <remarks>
        /// From memory on purpose: the config page polls this to move a progress bar,
        /// and pulling a whole run document — every prompt in full — off disk on each
        /// poll would be absurd.
        /// </remarks>
        /// <returns>The current run, or null.</returns>
        IndexRunSummary? Current();

        /// <summary>Reads one run's whole document as stored.</summary>
        /// <param name="runId">The run.</param>
        /// <returns>The JSON, or null when there is no such run.</returns>
        string? ReadRaw(Guid runId);
    }

    /// <summary>
    /// The recorder used when nothing is recording — tests, and any caller with no
    /// run log to hand. Every method does nothing.
    /// </summary>
    public sealed class NullIndexRunLog : IIndexRunLog
    {
        /// <summary>Gets the shared instance.</summary>
        public static NullIndexRunLog Instance { get; } = new();

        /// <inheritdoc />
        public Guid RunId => Guid.Empty;

        /// <inheritdoc />
        public void Step(string step, string message, IReadOnlyDictionary<string, object?>? detail = null)
        {
        }

        /// <inheritdoc />
        public void Progress(double percent)
        {
        }

        /// <inheritdoc />
        public void LlmCall(
            string pass,
            int batch,
            int itemCount,
            TimeSpan duration,
            LlmRequest request,
            LlmResult? result,
            string outcome,
            string? error,
            string model,
            string provider,
            RunPricing pricing)
        {
        }

        /// <inheritdoc />
        public void EmbeddingCall(
            int batch,
            int rowCount,
            TimeSpan duration,
            long inputTokens,
            decimal cost,
            string model,
            string provider,
            string? error = null)
        {
        }

        /// <inheritdoc />
        public void ItemEnriched(RunItemRecord item)
        {
        }

        public void ItemNotEnriched(string title, string reason)
        {
        }

        /// <inheritdoc />
        public void Complete()
        {
        }

        /// <inheritdoc />
        public void Cancel()
        {
        }

        /// <inheritdoc />
        public void Fail(string error)
        {
        }
    }
}
