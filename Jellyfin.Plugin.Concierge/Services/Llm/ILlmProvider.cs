using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Jellyfin.Plugin.Concierge.Services.Llm
{
    /// <summary>
    /// Which JSON object the model is being asked for. A provider that supports
    /// structured outputs hands the shape to the API so valid JSON becomes a
    /// guarantee rather than a hope; the rest rely on the prompt describing it.
    /// </summary>
    /// <remarks>
    /// Kept provider-neutral on purpose: the enum names the contract, and each
    /// provider translates it into whatever its own schema dialect is. Providers
    /// without structured outputs ignore it, which is why the prompts must keep
    /// describing the shape in full.
    /// <para>
    /// Every member needs a case in <em>both</em> structured builders — OpenAI
    /// strict mode and Gemini's dialect look alike and are not — and the schema must
    /// declare exactly the fields the prompt asks for. Curator learned that one
    /// expensively: a prompt requesting a field its schema forbade left the model
    /// with no legal place to put it, so it wrote the value into the previous
    /// string and corrupted 17 of 232 records.
    /// </para>
    /// </remarks>
    public enum ResponseShape
    {
        /// <summary>
        /// No schema. The request carries no <c>response_format</c> or
        /// <c>responseSchema</c> and the prompt alone states the contract.
        /// </summary>
        None = 0,

        /// <summary>
        /// <c>{"items":[{"i","known","premise","moments","themes","asks","spoiler"}]}</c>
        /// — the index-time enrichment pass.
        /// </summary>
        Enrichment = 1,
    }

    /// <summary>
    /// One request to the LLM. The user prompt is split at the point where it stops
    /// being reusable: <paramref name="CacheablePrefix"/> is byte-identical across
    /// the calls of a run, <paramref name="VariableSuffix"/> is not. Providers that
    /// support prompt caching mark the boundary; the rest concatenate.
    /// </summary>
    /// <param name="SystemPrompt">The system prompt.</param>
    /// <param name="CacheablePrefix">The reusable leading portion of the user prompt.</param>
    /// <param name="VariableSuffix">The per-request trailing portion of the user prompt.</param>
    /// <param name="MaxOutputTokens">Maximum output tokens for this call.</param>
    /// <param name="Shape">The JSON object being asked for; used by providers with structured outputs.</param>
    /// <param name="ConversationId">
    /// Groups the calls of one run so a provider can route them to the same cache.
    /// Null omits the routing hint entirely.
    /// </param>
    public sealed record LlmRequest(
        string SystemPrompt,
        string CacheablePrefix,
        string VariableSuffix,
        int MaxOutputTokens,
        ResponseShape Shape = ResponseShape.None,
        string? ConversationId = null)
    {
        /// <summary>
        /// Gets the whole user prompt, for providers with no caching support.
        /// </summary>
        public string UserPrompt => CacheablePrefix + VariableSuffix;
    }

    /// <summary>
    /// The result of one LLM call, with authoritative token usage from the provider.
    /// </summary>
    /// <param name="Text">The model's text output.</param>
    /// <param name="InputTokens">Uncached input tokens billed, as reported by the provider.</param>
    /// <param name="OutputTokens">Output tokens billed, as reported by the provider.</param>
    /// <param name="Truncated">Whether the output was cut off by the output-token cap.</param>
    /// <param name="CacheWriteTokens">Input tokens written to the prompt cache; 0 when unsupported.</param>
    /// <param name="CacheReadTokens">Input tokens served from the prompt cache; 0 when unsupported.</param>
    /// <param name="ThinkingTokens">
    /// The share of <paramref name="OutputTokens"/> spent reasoning rather than
    /// answering, where the provider reports it separately; 0 when unknown.
    /// Included in the output total because it is billed as output — this is the
    /// breakdown, not an addition. Worth surfacing because thinking and the visible
    /// answer compete for one output cap, and thinking winning that race is how a
    /// response gets truncated mid-JSON.
    /// </param>
    public sealed record LlmResult(
        string Text,
        long InputTokens,
        long OutputTokens,
        bool Truncated,
        long CacheWriteTokens = 0,
        long CacheReadTokens = 0,
        long ThinkingTokens = 0);

    /// <summary>
    /// One request in a batch submission, paired with the caller's key for it.
    /// </summary>
    /// <param name="CustomId">Caller-assigned key; results are matched back by this.</param>
    /// <param name="Request">The request.</param>
    public sealed record BatchLlmRequest(string CustomId, LlmRequest Request);

    /// <summary>
    /// One result from a batch submission. <paramref name="Result"/> is null when
    /// that request errored or expired, in which case <paramref name="Error"/> says why.
    /// </summary>
    /// <param name="CustomId">The key supplied on the corresponding request.</param>
    /// <param name="Result">The completion, or null if this request did not succeed.</param>
    /// <param name="Error">A short description of the failure, or null on success.</param>
    public sealed record BatchLlmResult(string CustomId, LlmResult? Result, string? Error);

    /// <summary>
    /// A provider that can submit many requests as one asynchronous job.
    /// </summary>
    /// <remarks>
    /// Implemented only where the backend actually offers it; callers must fall back
    /// to <see cref="ILlmProvider.CompleteAsync"/> when a provider does not. The
    /// intended user is index-time enrichment, which is a background task over the
    /// whole library and the one paid pass with no latency budget to respect — the
    /// two per-query passes must never go anywhere near this.
    /// </remarks>
    public interface IBatchLlmProvider
    {
        /// <summary>
        /// Submits every request as one job, waits for it to finish, and returns the
        /// results. Ordering is not preserved — match on <see cref="BatchLlmResult.CustomId"/>,
        /// never on position.
        /// </summary>
        /// <param name="requests">The requests, each with a unique custom id.</param>
        /// <param name="cancellationToken">Cancellation token.</param>
        /// <returns>One result per submitted request.</returns>
        Task<IReadOnlyList<BatchLlmResult>> CompleteBatchAsync(
            IReadOnlyList<BatchLlmRequest> requests,
            CancellationToken cancellationToken);
    }

    /// <summary>
    /// A chat-completion-shaped LLM backend. Implementations are stateless and
    /// carry their own endpoint, credentials, and wire format.
    /// </summary>
    public interface ILlmProvider
    {
        /// <summary>
        /// Gets the model identifier requests are sent to, for logging and tagging.
        /// </summary>
        string ModelId { get; }

        /// <summary>
        /// Sends one prompt and returns the model's text plus token usage.
        /// </summary>
        /// <param name="request">The request.</param>
        /// <param name="cancellationToken">Cancellation token.</param>
        /// <returns>The completion result.</returns>
        Task<LlmResult> CompleteAsync(LlmRequest request, CancellationToken cancellationToken);
    }
}
