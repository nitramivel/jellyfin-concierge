using System;
using System.Collections.Generic;
using Jellyfin.Plugin.Concierge.Api;
using Jellyfin.Plugin.Concierge.Core.Documents;
using Xunit;

namespace Jellyfin.Plugin.Concierge.Tests
{
    /// <summary>
    /// Whether an item's stored enrichment is the enrichment a search would use.
    /// </summary>
    /// <remarks>
    /// Found on the owner's server: <c>rows.json</c> from 1 Aug, <c>enrichment.json</c>
    /// from 2 Aug, and three runs in between that all checkpointed their paid work and
    /// were cancelled before the embedding phase. Every one of 264 items disagreed
    /// between the two files, searches were quietly still using the older answers, and
    /// nothing in the product said so — it was noticed because two lists on a page
    /// happened to show different text.
    /// <para>
    /// A false negative here recreates exactly that: money spent, answers stored, and
    /// no way to tell they are not live.
    /// </para>
    /// </remarks>
    public class PendingIndexTests
    {
        private static readonly Dictionary<Guid, int> NoRows = [];
        private static readonly Dictionary<Guid, int> NoCues = [];

        private static ItemDocument Item(Guid id, params string[] asks)
            => new(
                id, "Movie", "The 40 Year Old Virgin", string.Empty, 2005,
                [], [], [], [], string.Empty, null, string.Empty,
                new Enrichment("A clerk.", [], ["awkwardness"], asks, false));

        private static LibraryItemSummary Summarize(
            ItemDocument document, Dictionary<Guid, List<string>> embedded)
            => ConciergeController.Summarize(document, NoRows, NoCues, embedded);

        [Fact]
        public void MatchingTextIsNotPending()
        {
            var id = Guid.NewGuid();
            var asks = new[] { "the one where his friends find out", "guy who collects action figures" };

            var summary = Summarize(
                Item(id, asks),
                new Dictionary<Guid, List<string>> { [id] = [.. asks] });

            Assert.False(summary.Pending);
        }

        /// <summary>
        /// The case that was actually on disk.
        /// </summary>
        [Fact]
        public void DifferentTextIsPending()
        {
            var id = Guid.NewGuid();

            var summary = Summarize(
                Item(id, "the one where his friends find out he's never had sex"),
                new Dictionary<Guid, List<string>>
                {
                    [id] = ["the comedy where a 40-year-old guy is still a virgin"],
                });

            Assert.True(summary.Pending);
        }

        /// <summary>
        /// Enrichment with nothing embedded at all is the clearest pending case.
        /// </summary>
        [Fact]
        public void EnrichmentWithNoEmbeddedRowsIsPending()
        {
            var id = Guid.NewGuid();

            Assert.True(Summarize(Item(id, "a real ask"), []).Pending);
        }

        /// <summary>
        /// An item the model never knew is not pending — there is nothing to embed.
        /// </summary>
        /// <remarks>
        /// Storing emptiness is the correct outcome for an obscure title, and flagging
        /// every one of those as "waiting to be indexed" would bury the real ones.
        /// </remarks>
        [Fact]
        public void AnItemWithNoAsksIsNotPending()
        {
            var id = Guid.NewGuid();

            Assert.False(Summarize(Item(id), []).Pending);
        }

        /// <summary>
        /// The cap must not read as a mismatch.
        /// </summary>
        /// <remarks>
        /// Only the first <c>MaxAsksPerItem</c> asks are embedded, so an item with more
        /// stored than embedded is correct rather than stale — comparing the full lists
        /// would flag the entire library forever and make the warning worthless.
        /// </remarks>
        [Fact]
        public void MoreStoredAsksThanTheCapEmbedsIsNotPending()
        {
            var id = Guid.NewGuid();
            var asks = new[] { "a", "b", "c", "d", "e", "f", "g", "h", "i", "j" };

            var summary = Summarize(
                Item(id, asks),
                new Dictionary<Guid, List<string>> { [id] = ["a", "b", "c", "d", "e", "f", "g", "h"] });

            Assert.False(summary.Pending);
        }

        [Fact]
        public void WhitespaceAloneIsNotAMismatch()
        {
            var id = Guid.NewGuid();

            var summary = Summarize(
                Item(id, "  the one where his friends find out  "),
                new Dictionary<Guid, List<string>> { [id] = ["the one where his friends find out"] });

            Assert.False(summary.Pending);
        }
    }
}
