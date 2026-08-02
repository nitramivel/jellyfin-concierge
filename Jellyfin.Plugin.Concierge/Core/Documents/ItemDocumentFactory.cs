using System;
using System.Collections.Generic;
using MediaBrowser.Controller.Entities;

namespace Jellyfin.Plugin.Concierge.Core.Documents
{
    /// <summary>
    /// Projects a Jellyfin item onto the pure <see cref="ItemDocument"/> the rest of
    /// the pipeline works with.
    /// </summary>
    /// <remarks>
    /// The only place in <c>Core/</c> that touches a Jellyfin type, and it touches
    /// an entity rather than a service — which is the line that keeps everything
    /// downstream testable against a fixture library.
    /// <para>
    /// <b>Only persisted properties are read.</b> Anything that walks parent folders
    /// — <c>Episode.Series</c> is the notable one — resolves through server statics
    /// and cannot run outside a live server, so reaching for it here would work in
    /// production and break every test.
    /// </para>
    /// </remarks>
    public static class ItemDocumentFactory
    {
        /// <summary>
        /// Builds a document for one item.
        /// </summary>
        /// <param name="item">The library item.</param>
        /// <param name="people">
        /// Cast, directors and writers, already resolved. Passed in rather than
        /// looked up, because resolving people needs a library service and this must
        /// not take one.
        /// </param>
        /// <param name="enrichment">Enrichment, when it has already been generated.</param>
        /// <returns>The document.</returns>
        public static ItemDocument FromItem(
            BaseItem item,
            IReadOnlyList<string>? people = null,
            Enrichment? enrichment = null)
        {
            ArgumentNullException.ThrowIfNull(item);

            var episode = item as MediaBrowser.Controller.Entities.TV.Episode;

            return new ItemDocument(
                item.Id,
                item.GetBaseItemKind().ToString(),
                item.Name ?? string.Empty,
                item.OriginalTitle ?? string.Empty,
                item.ProductionYear,
                item.Genres ?? [],
                item.Tags ?? [],
                item.Studios ?? [],
                people ?? [],
                item.OfficialRating ?? string.Empty,
                RuntimeMinutes(item.RunTimeTicks),
                item.Overview ?? string.Empty,
                enrichment,

                // An episode without its show is unidentifiable. "The Wand" means
                // nothing on its own; "Adventure Time S6E13 — The Wand" is a thing
                // somebody could search for, and a line in a log somebody could read.
                episode?.SeriesId,
                episode?.SeriesName ?? string.Empty,
                episode?.ParentIndexNumber,
                episode?.IndexNumber);
        }

        private static int? RuntimeMinutes(long? ticks)
            => ticks is { } value && value > 0 ? (int)TimeSpan.FromTicks(value).TotalMinutes : null;
    }
}
