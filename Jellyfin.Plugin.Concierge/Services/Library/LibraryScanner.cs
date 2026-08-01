using System;
using System.Collections.Generic;
using Jellyfin.Data.Enums;
using Jellyfin.Plugin.Concierge.Core;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Concierge.Services.Library
{
    /// <summary>
    /// Default <see cref="ILibraryScanner"/> backed by <see cref="ILibraryManager"/>.
    /// Query shape follows SmartLists: recursive, virtual items excluded.
    /// </summary>
    public class LibraryScanner : ILibraryScanner
    {
        private readonly ILibraryManager _libraryManager;
        private readonly ILogger<LibraryScanner> _logger;

        public LibraryScanner(ILibraryManager libraryManager, ILogger<LibraryScanner> logger)
        {
            _libraryManager = libraryManager;
            _logger = logger;
        }

        /// <inheritdoc />
        public LibraryHealth Inspect()
        {
            var items = _libraryManager.GetItemsResult(new InternalItemsQuery
            {
                IncludeItemTypes = [BaseItemKind.Movie, BaseItemKind.Series],
                Recursive = true,
                IsVirtualItem = false,
            }).Items;

            var roots = LibraryRoots();
            var orphaned = 0;
            foreach (var item in items)
            {
                if (!LibraryPathFilter.IsInsideLibrary(item.Path, roots))
                {
                    orphaned++;
                }
            }

            return new LibraryHealth(items.Count - orphaned, orphaned);
        }

        /// <inheritdoc />
        public IReadOnlyList<BaseItem> Scan(bool includeEpisodes)
        {
            var kinds = includeEpisodes
                ? new[] { BaseItemKind.Movie, BaseItemKind.Series, BaseItemKind.Episode }
                : new[] { BaseItemKind.Movie, BaseItemKind.Series };

            var items = _libraryManager.GetItemsResult(new InternalItemsQuery
            {
                IncludeItemTypes = kinds,
                Recursive = true,
                IsVirtualItem = false,
            }).Items;

            var roots = LibraryRoots();
            var kept = new List<BaseItem>(items.Count);
            var orphaned = 0;

            foreach (var item in items)
            {
                // A library folder that was removed or remounted leaves its items
                // behind, path and all. They are indistinguishable from real ones
                // here and play back as nothing, so they must never be indexed.
                if (!LibraryPathFilter.IsInsideLibrary(item.Path, roots))
                {
                    orphaned++;
                    continue;
                }

                kept.Add(item);
            }

            if (orphaned > 0)
            {
                _logger.LogWarning(
                    "Concierge: {Orphaned} item(s) sit outside every configured library folder and were left out "
                    + "of the index. These are rows left behind by a removed or remounted library; they play back "
                    + "as nothing. A library scan in Jellyfin clears them.",
                    orphaned);
            }

            _logger.LogInformation(
                "Library scan: {Count} item(s) to index ({Orphaned} outside the library), episodes {Episodes}",
                kept.Count,
                orphaned,
                includeEpisodes ? "included" : "excluded");

            return kept;
        }

        /// <summary>
        /// The server's configured library folder paths.
        /// </summary>
        /// <remarks>
        /// Returns an empty list rather than throwing when the folder list cannot be
        /// read; <see cref="LibraryPathFilter"/> treats that as "keep everything",
        /// which is the right way to fail.
        /// </remarks>
        private IReadOnlyCollection<string> LibraryRoots()
        {
            try
            {
                var roots = new List<string>();
                foreach (var folder in _libraryManager.GetVirtualFolders())
                {
                    if (folder.Locations is null)
                    {
                        continue;
                    }

                    foreach (var location in folder.Locations)
                    {
                        if (!string.IsNullOrWhiteSpace(location))
                        {
                            roots.Add(location);
                        }
                    }
                }

                return roots;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Concierge: could not read the library folder list; scanning everything");
                return [];
            }
        }
    }
}
