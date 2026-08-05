namespace Jellyfin.Plugin.Concierge.Configuration
{
    /// <summary>
    /// One saved way of calling a chat model: the provider, the model identifier,
    /// the credential, and what that combination costs.
    /// <para>
    /// A profile is deliberately self-contained. Pricing lives here rather than on
    /// <see cref="PluginConfiguration"/> because Concierge runs several passes at
    /// once against deliberately different models — a cheap one reads the sentence,
    /// a better one orders the results, and the best affordable one enriches the
    /// index. A shared price block cannot price that query, and hard rule 10 says
    /// the cost line must be right. Switching profile switches the price with it.
    /// </para>
    /// </summary>
    /// <remarks>
    /// A mutable class with a parameterless constructor, not a record: Jellyfin
    /// persists plugin configuration with <see cref="System.Xml.Serialization.XmlSerializer"/>,
    /// which requires both.
    /// </remarks>
    public class ModelProfile
    {
        /// <summary>
        /// Gets or sets the stable identifier for this profile.
        /// <para>
        /// Referenced by <see cref="PluginConfiguration.DefaultModelProfileId"/> and
        /// by each per-pass assignment. It must survive renaming and reordering, so
        /// nothing may key a profile by its name or its position — both are things
        /// the owner can change at will.
        /// </para>
        /// </summary>
        public string Id { get; set; } = string.Empty;

        /// <summary>
        /// Gets or sets the display name shown in the profile list, e.g. "Haiku 4.5"
        /// or "Local Llama". Free text; duplicates are legal and harmless because
        /// <see cref="Id"/> is what anything actually resolves against.
        /// </summary>
        public string Name { get; set; } = string.Empty;

        /// <summary>
        /// Gets or sets the LLM provider this profile calls.
        /// </summary>
        public LlmProviderKind Provider { get; set; } = LlmProviderKind.Anthropic;

        /// <summary>
        /// Gets or sets the model identifier sent to the provider.
        /// </summary>
        public string Model { get; set; } = string.Empty;

        /// <summary>
        /// Gets or sets the provider API key. Stored in plaintext in the plugin
        /// configuration file.
        /// </summary>
        public string ApiKey { get; set; } = string.Empty;

        /// <summary>
        /// Gets or sets an optional base URL override. Empty means the provider's
        /// default endpoint; required for <see cref="LlmProviderKind.OpenAiCompatible"/>.
        /// </summary>
        public string BaseUrl { get; set; } = string.Empty;

        /// <summary>
        /// Gets or sets whether this profile lets the model think before answering.
        /// Defaults to following the global <c>EnableThinking</c>.
        /// </summary>
        /// <remarks>
        /// On the profile rather than on each pass, because "does this model think"
        /// is part of how to call the model — the same thing the provider, key and
        /// prices are. It matters more here than it did in Curator: a search box has
        /// about 2.5 seconds before it feels broken, and the plan pass sits directly
        /// in that budget. Keeping one model as two profiles — thinking and not — is
        /// how the re-rank reasons while the plan pass stays fast.
        /// <para>
        /// Note that this maps to adaptive-vs-disabled, never to a token budget:
        /// <c>budget_tokens</c> is gone from the Anthropic API, and thinking counts
        /// against the same output cap as the answer.
        /// </para>
        /// </remarks>
        public ThinkingMode Thinking { get; set; } = ThinkingMode.Inherit;

        /// <summary>
        /// Gets or sets the reasoning budget to ask this model for when thinking is
        /// off. Negative means work it out.
        /// </summary>
        /// <remarks>
        /// Google only. Most Gemini models take <c>thinkingBudget: 0</c> and stop
        /// reasoning; some refuse zero outright and fail the whole request, and the
        /// provider then has to discover what they will take by being rejected — two
        /// wasted round trips before the first useful one.
        /// <para>
        /// Setting a number here skips that discovery: it is sent first and zero is
        /// never offered. It also bounds the reasoning, which the fallback of dropping
        /// the field does not — measured on gemini-3.6-flash, unbounded reasoning was
        /// 1,178 of 1,445 output tokens on a re-rank.
        /// </para>
        /// </remarks>
        public int ThinkingBudget { get; set; } = -1;

        /// <summary>
        /// Gets or sets this profile's input price in USD per million tokens, used
        /// for the per-query cost line. 0 logs token counts without cost.
        /// </summary>
        public decimal InputCostPerMillion { get; set; }

        /// <summary>
        /// Gets or sets this profile's cache-read price in USD per million tokens.
        /// Blank falls back to half <see cref="InputCostPerMillion"/> — half of
        /// <em>this</em> profile's input rate, never another profile's.
        /// </summary>
        public decimal CachedInputCostPerMillion { get; set; }

        /// <summary>
        /// Gets or sets this profile's output price in USD per million tokens, used
        /// for the per-query cost line. 0 logs token counts without cost.
        /// </summary>
        public decimal OutputCostPerMillion { get; set; }

        /// <summary>
        /// Resolves this profile's thinking setting against the global default.
        /// </summary>
        /// <param name="globalEnableThinking">The configuration-wide setting.</param>
        /// <returns>Whether the model may think.</returns>
        public bool ThinkingResolved(bool globalEnableThinking) => Thinking switch
        {
            ThinkingMode.On => true,
            ThinkingMode.Off => false,
            _ => globalEnableThinking,
        };

        /// <summary>
        /// Gets the effective cache-read rate: this profile's own, or half its input
        /// rate when unset.
        /// </summary>
        /// <returns>USD per million cached input tokens.</returns>
        public decimal EffectiveCachedInputCostPerMillion()
            => CachedInputCostPerMillion > 0 ? CachedInputCostPerMillion : InputCostPerMillion / 2m;
    }
}
