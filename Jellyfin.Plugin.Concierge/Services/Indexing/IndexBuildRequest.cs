using System.Threading;

namespace Jellyfin.Plugin.Concierge.Services.Indexing
{
    /// <summary>
    /// Carries a one-shot full-regeneration request from the admin API to the
    /// scheduled-task instance that Jellyfin owns.
    /// </summary>
    /// <remarks>
    /// The task manager is the right owner for a build that can run for minutes: it
    /// supplies progress, cancellation and host-shutdown handling. The HTTP request
    /// therefore only records intent and queues that task. An atomic flag makes two
    /// clicks idempotent and lets a task consume the request exactly once.
    /// </remarks>
    public sealed class IndexBuildRequest
    {
        private const int Idle = 0;
        private const int Pending = 1;
        private const int Running = 2;

        private int _state;

        /// <summary>Requests that the next index build regenerate every paid artifact.</summary>
        public void RequestFullRegeneration()
            => Interlocked.CompareExchange(ref _state, Pending, Idle);

        /// <summary>Consumes a pending full-regeneration request.</summary>
        /// <returns>True exactly once for each pending request.</returns>
        public bool ConsumeFullRegeneration()
            => Interlocked.CompareExchange(ref _state, Running, Pending) == Pending;

        /// <summary>Marks the consumed regeneration finished, failed or cancelled.</summary>
        public void CompleteFullRegeneration()
            => Interlocked.CompareExchange(ref _state, Idle, Running);
    }
}
