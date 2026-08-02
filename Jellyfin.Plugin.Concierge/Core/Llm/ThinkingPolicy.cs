using System;
using Jellyfin.Plugin.Concierge.Configuration;

namespace Jellyfin.Plugin.Concierge.Core.Llm
{
    /// <summary>Which pass is about to run.</summary>
    public enum ThinkingPass
    {
        /// <summary>Reads constraints out of the sentence. Per query.</summary>
        Plan = 0,

        /// <summary>Orders the shortlist and says why. Per query, and the whole wait.</summary>
        Rerank = 1,

        /// <summary>Writes what an item is about. Index build only, once per item.</summary>
        Enrichment = 2,

        /// <summary>
        /// The same job on episodes, which is a different trade: there are twenty
        /// times as many and the model knows far fewer of them.
        /// </summary>
        Episode = 3,
    }

    /// <summary>
    /// Decides whether a model may reason before answering.
    /// </summary>
    /// <remarks>
    /// <b>Thinking is not free and not uniform, which is why it is per pass.</b>
    /// Reasoning tokens are generated before the answer, billed at the output rate,
    /// and counted against the same output cap — so they cost money and, more to the
    /// point, time. Measured on this library: re-rank duration tracks tokens generated
    /// at +0.937 across 80 calls at a flat ~166 tokens per second, and thinking was
    /// 39% of everything generated.
    /// <para>
    /// The trade is opposite at the two ends. Nobody waits on enrichment — it runs once
    /// per item, during a scheduled build, and what it writes is the ceiling on every
    /// search afterwards, so thinking there buys permanent quality for time nobody is
    /// counting. Re-rank runs on every search with a person watching, where the same
    /// tokens are pure latency.
    /// </para>
    /// <para>
    /// <b>Resolution order is most-specific-first:</b> the model profile, then the
    /// pass, then the global default. A profile set explicitly to on or off is a
    /// statement about that model — it exists so one model can be configured twice,
    /// thinking and not — and it must not be silently overridden by a pass setting the
    /// owner may have left alone.
    /// </para>
    /// </remarks>
    public static class ThinkingPolicy
    {
        /// <summary>
        /// Whether this pass, on this profile, may think.
        /// </summary>
        /// <param name="config">The configuration.</param>
        /// <param name="pass">The pass about to run.</param>
        /// <param name="profile">The profile it will run on.</param>
        /// <returns>Whether the model may reason before answering.</returns>
        public static bool For(PluginConfiguration config, ThinkingPass pass, ModelProfile? profile)
        {
            ArgumentNullException.ThrowIfNull(config);

            // Most specific first. A profile that says on or off means it.
            if (profile is not null && profile.Thinking != ThinkingMode.Inherit)
            {
                return profile.Thinking == ThinkingMode.On;
            }

            var forPass = pass switch
            {
                ThinkingPass.Plan => config.PlanThinking,
                ThinkingPass.Rerank => config.RerankThinking,
                ThinkingPass.Enrichment => config.EnrichmentThinking,
                ThinkingPass.Episode => config.EpisodeThinking,
                _ => ThinkingMode.Inherit,
            };

            return forPass switch
            {
                ThinkingMode.On => true,
                ThinkingMode.Off => false,
                _ => config.EnableThinking,
            };
        }

        /// <summary>
        /// Explains a resolution in one line, for the settings page and the run log.
        /// </summary>
        /// <param name="config">The configuration.</param>
        /// <param name="pass">The pass.</param>
        /// <param name="profile">The profile.</param>
        /// <returns>Where the answer came from.</returns>
        /// <remarks>
        /// Three places can decide this and the answer is invisible in the request.
        /// Being able to say <em>which</em> one decided is the difference between a
        /// setting somebody trusts and one they toggle twice and give up on — which is
        /// what happened when this was silently ignored on OpenAI for nine releases.
        /// </remarks>
        public static string Explain(PluginConfiguration config, ThinkingPass pass, ModelProfile? profile)
        {
            ArgumentNullException.ThrowIfNull(config);

            var on = For(config, pass, profile);
            var state = on ? "thinking" : "not thinking";

            if (profile is not null && profile.Thinking != ThinkingMode.Inherit)
            {
                return $"{state} — set on the profile '{profile.Name}'";
            }

            var forPass = pass switch
            {
                ThinkingPass.Plan => config.PlanThinking,
                ThinkingPass.Rerank => config.RerankThinking,
                ThinkingPass.Enrichment => config.EnrichmentThinking,
                ThinkingPass.Episode => config.EpisodeThinking,
                _ => ThinkingMode.Inherit,
            };

            return forPass != ThinkingMode.Inherit
                ? $"{state} — set on the {pass.ToString().ToLowerInvariant()} pass"
                : $"{state} — following the global default";
        }
    }
}
