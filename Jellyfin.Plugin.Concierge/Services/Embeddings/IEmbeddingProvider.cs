using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Concierge.Configuration;

namespace Jellyfin.Plugin.Concierge.Services.Embeddings
{
    /// <summary>
    /// Whether the text being embedded is something being searched for, or
    /// something being searched over.
    /// </summary>
    /// <remarks>
    /// This is the single most quietly dangerous parameter in the plugin. Most
    /// retrieval embedding models are trained <em>asymmetrically</em>: a query and a
    /// passage are marked differently, and using the wrong marker degrades recall
    /// with no error, no exception, and no symptom other than results being worse
    /// than they should be. Every backend expresses it differently — a text prefix
    /// for the local models, <c>taskType</c> for Google, <c>input_type</c> for
    /// Voyage — so the caller states the intent and the provider translates it. No
    /// call site ever writes a prefix by hand.
    /// </remarks>
    public enum EmbeddingPurpose
    {
        /// <summary>Text being indexed and later searched over.</summary>
        Document = 0,

        /// <summary>Text the user is searching for.</summary>
        Query = 1,
    }

    /// <summary>
    /// The result of one embedding call.
    /// </summary>
    /// <param name="Vectors">
    /// One vector per input text, <b>in the order the inputs were given</b>.
    /// Providers that return an out-of-order array are re-sorted before this point.
    /// </param>
    /// <param name="InputTokens">
    /// Input tokens billed, as reported by the provider; 0 when it reports none
    /// (local servers generally do not).
    /// </param>
    public sealed record EmbeddingResult(IReadOnlyList<float[]> Vectors, long InputTokens);

    /// <summary>
    /// A backend that turns text into vectors.
    /// </summary>
    /// <remarks>
    /// Separate from <see cref="Llm.ILlmProvider"/> rather than an overload of it,
    /// because the shapes have nothing in common: no system prompt, no output cap,
    /// no thinking, no truncation, no cache — and a batch of inputs answered by a
    /// batch of vectors rather than one prompt answered by one text.
    /// </remarks>
    public interface IEmbeddingProvider
    {
        /// <summary>
        /// Gets the model identifier requests are sent to, for logging and for the
        /// index's identity.
        /// </summary>
        string ModelId { get; }

        /// <summary>
        /// Gets the vector width this provider was configured to request, or 0 for
        /// whatever the model natively returns.
        /// </summary>
        int Dimensions { get; }

        /// <summary>
        /// Embeds a batch of texts.
        /// </summary>
        /// <param name="texts">The texts. An empty list returns an empty result without a call.</param>
        /// <param name="purpose">Whether these are queries or documents.</param>
        /// <param name="cancellationToken">Cancellation token.</param>
        /// <returns>One vector per input, in input order.</returns>
        Task<EmbeddingResult> EmbedAsync(
            IReadOnlyList<string> texts,
            EmbeddingPurpose purpose,
            CancellationToken cancellationToken);
    }

    /// <summary>
    /// Builds the <see cref="IEmbeddingProvider"/> for an embedding profile.
    /// </summary>
    /// <remarks>
    /// An interface for the same reason <see cref="Llm.ILlmProviderFactory"/> is
    /// one: orchestration takes the interface, so the indexer and the query path can
    /// both be driven by a stub that returns canned vectors (hard rule 5). Nothing
    /// in the test suite may reach a real embedding endpoint.
    /// </remarks>
    public interface IEmbeddingProviderFactory
    {
        /// <summary>
        /// Creates the provider for the configuration's selected embedding profile.
        /// </summary>
        /// <param name="config">The plugin configuration.</param>
        /// <returns>The provider.</returns>
        IEmbeddingProvider Create(PluginConfiguration config);

        /// <summary>
        /// Creates the provider described by one embedding profile.
        /// </summary>
        /// <param name="profile">The embedding profile to call.</param>
        /// <returns>The provider.</returns>
        IEmbeddingProvider Create(EmbeddingProfile profile);
    }
}
