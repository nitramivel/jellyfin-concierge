using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Jellyfin.Plugin.Concierge.Services.Runs
{
    /// <summary>
    /// Stores what recent queries did, cost, and how long they took.
    /// </summary>
    /// <remarks>
    /// One capped file rather than Curator's file-per-run. Curator logs a weekly
    /// task; Concierge logs a search box, and a household generates orders of
    /// magnitude more of them — a file each would fill a directory with thousands of
    /// tiny JSON documents inside a month.
    /// </remarks>
    public interface IQueryLogStore
    {
        /// <summary>
        /// Appends one query to the log, evicting the oldest entries past the cap.
        /// </summary>
        /// <remarks>
        /// Never throws. A search that succeeded must not be reported as failed
        /// because writing its log line did not work.
        /// </remarks>
        /// <param name="run">The query to record.</param>
        /// <param name="cancellationToken">Cancellation token.</param>
        /// <returns>A task.</returns>
        Task RecordAsync(QueryRunRecord run, CancellationToken cancellationToken);

        /// <summary>
        /// Reads recent queries, newest first.
        /// </summary>
        /// <param name="count">How many to return at most.</param>
        /// <param name="cancellationToken">Cancellation token.</param>
        /// <returns>The recorded queries.</returns>
        Task<IReadOnlyList<QueryRunRecord>> RecentAsync(int count, CancellationToken cancellationToken);
    }
}
