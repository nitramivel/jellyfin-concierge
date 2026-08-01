using System;
using System.Collections.Generic;

namespace Jellyfin.Plugin.Concierge.Core
{
    /// <summary>
    /// Decides whether a library item still lives inside one of the server's
    /// configured library folders.
    /// </summary>
    /// <remarks>
    /// Jellyfin does not delete an item's database row when its library folder is
    /// removed or its mount point is renamed. The row keeps its old path, keeps
    /// <c>LocationType=FileSystem</c>, keeps a media source, and keeps coming back
    /// from <c>GetItemsResult</c> — it looks exactly like a real item and plays back
    /// as nothing. <c>IsVirtualItem=false</c> does not exclude it; that flag is for
    /// missing-episode stubs, which is a different thing.
    /// <para>
    /// Measured on the owner's server: 36 of 298 movies and series sat under
    /// <c>/storage/</c>, a mount that no longer exists and is not a library root.
    /// For Curator that meant unplayable items in generated playlists. For a search
    /// box it is worse in kind: a dead row indexed here is a result the user
    /// searched for, found, clicked, and got nothing from — the single most
    /// convincing way to prove the feature is broken.
    /// </para>
    /// </remarks>
    public static class LibraryPathFilter
    {
        /// <summary>
        /// Whether an item's path sits inside one of the configured library folders.
        /// </summary>
        /// <remarks>
        /// Fails open: with no known roots — a server that reports none, or a test
        /// that does not care — every item is kept. Excluding the whole library
        /// because the folder list could not be read would be a far worse failure
        /// than indexing a few dead rows.
        /// </remarks>
        /// <param name="path">The item's path.</param>
        /// <param name="roots">Configured library folder paths.</param>
        /// <returns>True when the item should be indexed.</returns>
        public static bool IsInsideLibrary(string? path, IReadOnlyCollection<string>? roots)
        {
            if (roots is null || roots.Count == 0)
            {
                return true;
            }

            if (string.IsNullOrWhiteSpace(path))
            {
                // Every kind Concierge indexes — movie, series, episode — carries a
                // path. One that does not cannot be shown to belong anywhere.
                return false;
            }

            foreach (var root in roots)
            {
                if (IsUnder(path, root))
                {
                    return true;
                }
            }

            return false;
        }

        /// <summary>
        /// Whether <paramref name="path"/> is the root itself or sits beneath it.
        /// </summary>
        /// <remarks>
        /// Compared on a separator boundary so "/data/Movies" does not swallow
        /// "/data/Movies2". Case-insensitive because Jellyfin runs on Windows too,
        /// where it would otherwise reject a whole library over a drive-letter case.
        /// </remarks>
        private static bool IsUnder(string path, string? root)
        {
            if (string.IsNullOrWhiteSpace(root))
            {
                return false;
            }

            var trimmed = root.TrimEnd('/', '\\');
            if (trimmed.Length == 0)
            {
                // A root of "/" contains everything.
                return true;
            }

            if (!path.StartsWith(trimmed, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            if (path.Length == trimmed.Length)
            {
                return true;
            }

            var next = path[trimmed.Length];
            return next == '/' || next == '\\';
        }
    }
}
