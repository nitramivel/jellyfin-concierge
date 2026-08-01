using System.Collections.Generic;
using MediaBrowser.Controller.Entities;

namespace Jellyfin.Plugin.Concierge.Services.Library
{
    /// <summary>
    /// Counts from a cheap look at the library.
    /// </summary>
    /// <param name="Items">Movies and series inside a configured library folder.</param>
    /// <param name="Orphaned">
    /// Rows whose path sits outside every configured library folder — left behind
    /// when a folder is removed or a mount renamed. Indistinguishable from real
    /// items in Jellyfin, and they play back as nothing.
    /// </param>
    public sealed record LibraryHealth(int Items, int Orphaned);

    /// <summary>
    /// Enumerates the media library for the indexer.
    /// </summary>
    /// <remarks>
    /// Returns Jellyfin's own entities rather than a projection: turning a
    /// <see cref="BaseItem"/> into indexed text is <c>Core/Documents/ItemDocument</c>'s
    /// job, and it is pure so it can be tested. This layer does only the part Core
    /// cannot — asking the server what exists.
    /// </remarks>
    public interface ILibraryScanner
    {
        /// <summary>
        /// Counts real and orphaned library rows without projecting them.
        /// </summary>
        /// <returns>The counts.</returns>
        LibraryHealth Inspect();

        /// <summary>
        /// Scans the library for movies and series, plus episodes when requested.
        /// Virtual items (e.g. missing episodes) and items outside every configured
        /// library folder are excluded.
        /// </summary>
        /// <param name="includeEpisodes">
        /// Whether individual episodes are included alongside their series. Off by
        /// default: episodes multiply the index by roughly 15x and mostly duplicate
        /// their series' semantics.
        /// </param>
        /// <returns>The items to index.</returns>
        IReadOnlyList<BaseItem> Scan(bool includeEpisodes);

        /// <summary>
        /// Scans the music library for tracks that carry lyrics.
        /// </summary>
        /// <remarks>
        /// Separate from <see cref="Scan"/> because songs are a different kind of
        /// thing: they are never re-ranked against films, and their text arrives
        /// already parsed and time-stamped rather than needing extraction.
        /// </remarks>
        /// <returns>Audio items inside a configured library folder.</returns>
        IReadOnlyList<BaseItem> ScanAudio();
    }
}
