namespace Jellyfin.Plugin.Concierge.Configuration
{
    /// <summary>
    /// The chat backends a <see cref="ModelProfile"/> can call.
    /// </summary>
    public enum LlmProviderKind
    {
        /// <summary>Anthropic Messages API.</summary>
        Anthropic = 0,

        /// <summary>OpenAI Chat Completions API.</summary>
        OpenAi = 1,

        /// <summary>Any OpenAI-compatible endpoint (Ollama, LM Studio, vLLM, OpenRouter, ...).</summary>
        OpenAiCompatible = 2,

        /// <summary>Google Gemini, natively — the generateContent API with a response schema.</summary>
        Google = 3,

        /// <summary>xAI Grok. OpenAI-shaped wire format, with structured outputs on.</summary>
        Grok = 4,
    }

    /// <summary>
    /// The embedding backends an <see cref="EmbeddingProfile"/> can call.
    /// </summary>
    /// <remarks>
    /// Deliberately not <see cref="LlmProviderKind"/>. The two lists differ by more
    /// than naming: <b>Anthropic has no embeddings endpoint at all</b> — its
    /// supporting APIs are Batches, Files, Token Counting and Models, and there is
    /// no <c>/v1/embeddings</c> to call. Sharing the chat enum would put an option
    /// in the config page whose only possible outcome is a 404, so the asymmetry is
    /// expressed in the type rather than caught at runtime.
    /// </remarks>
    public enum EmbeddingProviderKind
    {
        /// <summary>OpenAI's embeddings endpoint. The default.</summary>
        OpenAi = 0,

        /// <summary>
        /// Any OpenAI-compatible <c>/v1/embeddings</c> — Ollama, LM Studio, vLLM.
        /// This is what makes a local, free, nothing-leaves-the-house index possible
        /// without a line of new provider code.
        /// </summary>
        OpenAiCompatible = 1,

        /// <summary>Google's Gemini embedding models, via the native path.</summary>
        Google = 2,

        /// <summary>Voyage AI — what Anthropic itself points to for embeddings.</summary>
        Voyage = 3,
    }

    /// <summary>
    /// Whether a profile lets its model think before answering.
    /// </summary>
    public enum ThinkingMode
    {
        /// <summary>Follow the global <see cref="PluginConfiguration.EnableThinking"/>.</summary>
        Inherit = 0,

        /// <summary>Think, whatever the global setting says.</summary>
        On = 1,

        /// <summary>Do not think, whatever the global setting says.</summary>
        Off = 2,
    }
}
