using System;
using System.Collections.Generic;
using System.Linq;

namespace Jellyfin.Plugin.Concierge.Core.Budget
{
    /// <summary>Which budget a spend draws down.</summary>
    /// <remarks>
    /// <b>These two must never share a pot</b>, and the reason is a first-run
    /// disaster: a full index build costs a couple of dollars, so if it drew down the
    /// monthly search budget it would exhaust it on the day someone installed the
    /// plugin and leave search degraded for the rest of the month. The worst possible
    /// first impression, caused entirely by an accounting decision.
    /// </remarks>
    public enum SpendKind
    {
        /// <summary>A per-query pass — plan or re-rank.</summary>
        Query = 0,

        /// <summary>An index-time enrichment call.</summary>
        Enrichment = 1,
    }

    /// <summary>One recorded spend.</summary>
    /// <param name="AtUtc">When it happened.</param>
    /// <param name="Kind">Which budget it draws down.</param>
    /// <param name="AmountUsd">What it cost.</param>
    /// <param name="UserId">Who caused it, for per-user rate limiting. Null for background work.</param>
    public sealed record SpendEntry(DateTime AtUtc, SpendKind Kind, decimal AmountUsd, string? UserId = null);

    /// <summary>
    /// Pure arithmetic over recorded spends.
    /// </summary>
    /// <remarks>
    /// Kept pure and separate from anything that writes files, because these are the
    /// numbers a monthly cap acts on and a rounding or window error here silently
    /// turns a cap off — or turns search off.
    /// </remarks>
    public static class SpendLedger
    {
        /// <summary>
        /// What has been spent in the calendar month containing <paramref name="nowUtc"/>.
        /// </summary>
        /// <remarks>
        /// Calendar month, not a rolling 30 days: people budget in months, and a
        /// rolling window means the cap lifts at an hour nobody can predict.
        /// </remarks>
        /// <param name="entries">Every recorded spend.</param>
        /// <param name="kind">Which budget to total.</param>
        /// <param name="nowUtc">The current time.</param>
        /// <returns>The total in USD.</returns>
        public static decimal SpentThisMonth(
            IEnumerable<SpendEntry> entries,
            SpendKind kind,
            DateTime nowUtc)
        {
            ArgumentNullException.ThrowIfNull(entries);

            return entries
                .Where(e => e.Kind == kind
                    && e.AtUtc.Year == nowUtc.Year
                    && e.AtUtc.Month == nowUtc.Month)
                .Sum(e => e.AmountUsd);
        }

        /// <summary>
        /// How many paid queries a user has made in the last hour.
        /// </summary>
        /// <remarks>
        /// A rolling hour here, unlike the monthly window, because a rate limit is
        /// about bursts and a clock-aligned bucket lets someone spend twice the limit
        /// across a boundary.
        /// </remarks>
        /// <param name="entries">Every recorded spend.</param>
        /// <param name="userId">The user, or null for anonymous.</param>
        /// <param name="nowUtc">The current time.</param>
        /// <returns>The count.</returns>
        public static int PaidQueriesInLastHour(
            IEnumerable<SpendEntry> entries,
            string? userId,
            DateTime nowUtc)
        {
            ArgumentNullException.ThrowIfNull(entries);

            var since = nowUtc.AddHours(-1);

            return entries.Count(e => e.Kind == SpendKind.Query
                && e.AtUtc >= since
                && string.Equals(e.UserId, userId, StringComparison.Ordinal));
        }

        /// <summary>
        /// Drops entries too old to affect any decision, so the ledger stays small.
        /// </summary>
        /// <param name="entries">Every recorded spend.</param>
        /// <param name="nowUtc">The current time.</param>
        /// <returns>The entries still worth keeping.</returns>
        public static IReadOnlyList<SpendEntry> Prune(IEnumerable<SpendEntry> entries, DateTime nowUtc)
        {
            ArgumentNullException.ThrowIfNull(entries);

            // Keep the previous month as well as this one: a cap read at 00:01 on the
            // first would otherwise have nothing behind it to explain last month's
            // number to whoever asks where the money went.
            var cutoff = new DateTime(nowUtc.Year, nowUtc.Month, 1, 0, 0, 0, DateTimeKind.Utc).AddMonths(-1);

            return entries.Where(e => e.AtUtc >= cutoff).ToList();
        }
    }
}
