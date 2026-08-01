namespace Jellyfin.Plugin.Concierge.Core.Budget
{
    /// <summary>What a query is allowed to spend.</summary>
    /// <remarks>
    /// There is no "refuse". Every one of these still returns results — the only
    /// question is whether a model is involved. A search box that returns an error
    /// message is a broken search box (hard rule 4).
    /// </remarks>
    public enum BudgetVerdict
    {
        /// <summary>Both paid passes may run.</summary>
        Full = 0,

        /// <summary>The plan pass may run; the re-rank may not.</summary>
        PlanOnly = 1,

        /// <summary>No model calls. Fused retrieval only — free, and still good.</summary>
        FreeOnly = 2,
    }

    /// <summary>What the budget allows, and why.</summary>
    /// <param name="Verdict">How much of the pipeline may run.</param>
    /// <param name="Reason">
    /// A plain-language reason, shown to the searcher when it explains a degraded
    /// result and recorded on every query either way.
    /// </param>
    /// <param name="SpentUsd">Spent on queries this calendar month.</param>
    /// <param name="CapUsd">The monthly cap, or 0 when uncapped.</param>
    public sealed record BudgetOutcome(BudgetVerdict Verdict, string Reason, decimal SpentUsd, decimal CapUsd)
    {
        /// <summary>Gets whether any paid pass may run.</summary>
        public bool AllowsAnySpend => Verdict != BudgetVerdict.FreeOnly;

        /// <summary>Gets whether the re-rank may run.</summary>
        public bool AllowsRerank => Verdict == BudgetVerdict.Full;
    }

    /// <summary>
    /// Decides how much of the pipeline a query may pay for.
    /// </summary>
    /// <remarks>
    /// Pure, so the one thing standing between a search box and an unbounded bill is
    /// testable. Concierge spends money when someone types, which is unpredictable
    /// and can be triggered by anyone with an account on the server — that is the
    /// constraint separating this plugin from its sibling, which spends on a schedule
    /// its owner controls.
    /// <para>
    /// <b>Every path here degrades rather than fails.</b> Out of budget, rate limited
    /// or switched off all serve fused retrieval results, which are free and still
    /// good.
    /// </para>
    /// </remarks>
    public static class BudgetDecision
    {
        /// <summary>
        /// The share of the monthly cap after which the re-rank stops running.
        /// </summary>
        /// <remarks>
        /// The re-rank is roughly five times the cost of the plan pass, so dropping it
        /// first buys most of the remaining month at a fraction of the quality loss.
        /// Falling straight from full to free at 100% would make the last day of the
        /// month feel like a different plugin.
        /// </remarks>
        public const decimal RerankCutoffFraction = 0.85m;

        /// <summary>
        /// Decides what a query may spend.
        /// </summary>
        /// <param name="spentThisMonthUsd">Query spend so far this calendar month.</param>
        /// <param name="monthlyCapUsd">The cap; 0 or less means uncapped.</param>
        /// <param name="paidQueriesThisHour">This user's paid queries in the last hour.</param>
        /// <param name="hourlyLimit">Paid queries allowed per user per hour; 0 or less means unlimited.</param>
        /// <param name="planEnabled">Whether the plan pass is switched on.</param>
        /// <param name="rerankEnabled">Whether the re-rank pass is switched on.</param>
        /// <returns>The outcome.</returns>
        public static BudgetOutcome ForQuery(
            decimal spentThisMonthUsd,
            decimal monthlyCapUsd,
            int paidQueriesThisHour,
            int hourlyLimit,
            bool planEnabled,
            bool rerankEnabled)
        {
            // Kill switches first: an owner who turned something off outranks any
            // arithmetic about whether it could be afforded.
            if (!planEnabled && !rerankEnabled)
            {
                return new BudgetOutcome(
                    BudgetVerdict.FreeOnly,
                    "both model passes are switched off",
                    spentThisMonthUsd,
                    monthlyCapUsd);
            }

            if (hourlyLimit > 0 && paidQueriesThisHour >= hourlyLimit)
            {
                return new BudgetOutcome(
                    BudgetVerdict.FreeOnly,
                    $"rate limit reached ({hourlyLimit} paid searches an hour)",
                    spentThisMonthUsd,
                    monthlyCapUsd);
            }

            if (monthlyCapUsd > 0)
            {
                if (spentThisMonthUsd >= monthlyCapUsd)
                {
                    return new BudgetOutcome(
                        BudgetVerdict.FreeOnly,
                        "this month's budget is spent — still searching, just without the model",
                        spentThisMonthUsd,
                        monthlyCapUsd);
                }

                if (spentThisMonthUsd >= monthlyCapUsd * RerankCutoffFraction)
                {
                    return new BudgetOutcome(
                        planEnabled ? BudgetVerdict.PlanOnly : BudgetVerdict.FreeOnly,
                        "close to this month's budget, so results are not being re-ranked",
                        spentThisMonthUsd,
                        monthlyCapUsd);
                }
            }

            if (!rerankEnabled)
            {
                return new BudgetOutcome(
                    BudgetVerdict.PlanOnly,
                    "re-ranking is switched off",
                    spentThisMonthUsd,
                    monthlyCapUsd);
            }

            if (!planEnabled)
            {
                // Nothing to read the sentence, but the shortlist can still be
                // ordered — the re-rank is the pass that carries most of the quality.
                return new BudgetOutcome(
                    BudgetVerdict.Full,
                    "the plan pass is switched off",
                    spentThisMonthUsd,
                    monthlyCapUsd);
            }

            return new BudgetOutcome(BudgetVerdict.Full, string.Empty, spentThisMonthUsd, monthlyCapUsd);
        }

        /// <summary>
        /// Whether an index build may spend, against its own separate ceiling.
        /// </summary>
        /// <param name="spentThisMonthUsd">Enrichment spend so far this calendar month.</param>
        /// <param name="capUsd">The enrichment ceiling; 0 or less means uncapped.</param>
        /// <param name="estimatedUsd">What the build is projected to cost.</param>
        /// <returns>True when the build may proceed.</returns>
        public static bool AllowsEnrichment(decimal spentThisMonthUsd, decimal capUsd, decimal estimatedUsd)
        {
            return capUsd <= 0 || spentThisMonthUsd + estimatedUsd <= capUsd;
        }
    }
}
