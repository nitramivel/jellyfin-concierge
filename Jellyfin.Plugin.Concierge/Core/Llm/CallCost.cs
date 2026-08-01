using Jellyfin.Plugin.Concierge.Configuration;

namespace Jellyfin.Plugin.Concierge.Core.Llm
{
    /// <summary>
    /// What one model call cost, in USD, from the tokens the provider reported and
    /// the prices on the profile that was called.
    /// </summary>
    /// <remarks>
    /// Pure arithmetic, kept out of the providers and out of the run log, because
    /// hard rule 10 says the cost line must be right and the only way to know it is
    /// right is to pin it with tests.
    /// <para>
    /// <b>Cost is computed per call and summed, never recomputed from aggregate
    /// token totals</b> (hard rule 12). A Concierge query routinely runs two
    /// different models at two different prices; there is no single rate that can
    /// price it, and a total derived from summed tokens times one rate is wrong by
    /// whatever the two models' prices differ by.
    /// </para>
    /// </remarks>
    public static class CallCost
    {
        private const decimal PerMillion = 1_000_000m;

        /// <summary>
        /// Anthropic's multiplier for writing to the prompt cache at the 1-hour TTL.
        /// </summary>
        /// <remarks>
        /// A cache write is billed above the normal input rate — 1.25x at the default
        /// 5-minute window, 2x at the hour. The providers here mark the hour, because
        /// five minutes is too short to survive the gap between the calls of a run,
        /// so this is the rate that applies. Anthropic is the only provider that
        /// reports cache-write tokens at all; everywhere else the count is 0 and this
        /// multiplies nothing.
        /// </remarks>
        public const decimal CacheWritePremium = 2m;

        /// <summary>
        /// Prices one chat call.
        /// </summary>
        /// <remarks>
        /// Every input token is charged exactly once, at one of three rates:
        /// <list type="bullet">
        /// <item><description>
        /// <paramref name="uncachedInputTokens"/> at the profile's input rate. Every
        /// provider is normalized to report the <em>uncached remainder</em> here —
        /// OpenAI, Grok and Gemini all report a total that includes the cached span,
        /// and their providers subtract it before this point. Skipping that
        /// subtraction understates nothing and <em>over</em>states input, but pairing
        /// an unsubtracted total with the cached charge below double-bills the cached
        /// span, which is how a cost line quietly runs ~25% high.
        /// </description></item>
        /// <item><description>
        /// <paramref name="cacheReadTokens"/> at the profile's cached rate.
        /// <b>Cache reads are charged, not free</b> — they are cheap, which is not
        /// the same thing, and a ledger that treats them as free reports a cached
        /// query as costing nothing at all.
        /// </description></item>
        /// <item><description>
        /// <paramref name="cacheWriteTokens"/> at a premium over the input rate.
        /// </description></item>
        /// </list>
        /// Thinking tokens are not charged separately: they are billed as output and
        /// are already inside <paramref name="outputTokens"/>. Adding them again
        /// would double-count the most expensive part of a reasoning call.
        /// </remarks>
        /// <param name="profile">The profile that was called, carrying its own prices.</param>
        /// <param name="uncachedInputTokens">Input tokens billed at the full rate.</param>
        /// <param name="outputTokens">Output tokens, thinking included.</param>
        /// <param name="cacheReadTokens">Input tokens served from the prompt cache.</param>
        /// <param name="cacheWriteTokens">Input tokens written to the prompt cache.</param>
        /// <returns>The estimated cost in USD.</returns>
        public static decimal ForChat(
            ModelProfile profile,
            long uncachedInputTokens,
            long outputTokens,
            long cacheReadTokens = 0,
            long cacheWriteTokens = 0)
        {
            if (profile is null)
            {
                return 0m;
            }

            var input = uncachedInputTokens * profile.InputCostPerMillion;
            var output = outputTokens * profile.OutputCostPerMillion;
            var cacheRead = cacheReadTokens * profile.EffectiveCachedInputCostPerMillion();
            var cacheWrite = cacheWriteTokens * profile.InputCostPerMillion * CacheWritePremium;

            return (input + output + cacheRead + cacheWrite) / PerMillion;
        }

        /// <summary>
        /// Prices one embedding call.
        /// </summary>
        /// <remarks>
        /// One rate and one token count. Embeddings have no output to bill and no
        /// prompt cache to read from, which is exactly why
        /// <see cref="EmbeddingProfile"/> carries a single price rather than
        /// inheriting three meaningless ones.
        /// </remarks>
        /// <param name="profile">The embedding profile that was called.</param>
        /// <param name="inputTokens">Input tokens billed.</param>
        /// <returns>The estimated cost in USD.</returns>
        public static decimal ForEmbedding(EmbeddingProfile profile, long inputTokens)
        {
            if (profile is null)
            {
                return 0m;
            }

            return inputTokens * profile.InputCostPerMillion / PerMillion;
        }
    }
}
