using System;
using Jellyfin.Plugin.Concierge.Core.Documents;
using Xunit;

namespace Jellyfin.Plugin.Concierge.Tests
{
    /// <summary>
    /// An episode has to carry its show, or it cannot be identified by anything.
    /// </summary>
    /// <remarks>
    /// A run enriching 4,998 episodes logged titles like "The Wand", "Sow, Do You Like
    /// Them Apples" and "Have You Seen the Muffin Mess". None of those is answerable:
    /// not by a reader of the log, not by a model being asked to describe it, and not
    /// by somebody searching for the show it belongs to.
    /// </remarks>
    public class EpisodeContextTests
    {
        private static ItemDocument Episode(string series, int? season, int? number, string title)
            => new(
                Guid.NewGuid(), "Episode", title, string.Empty, 2014,
                [], [], [], [], string.Empty, 11, "An episode.",
                null, Guid.NewGuid(), series, season, number);

        private static ItemDocument Film(string title)
            => new(
                Guid.NewGuid(), "Movie", title, string.Empty, 1999,
                [], [], [], [], string.Empty, 120, "A film.");

        [Fact]
        public void AnEpisodeIsNamedByItsShowAndItsNumber()
        {
            Assert.Equal(
                "Adventure Time S6E13 — The Wand",
                Episode("Adventure Time", 6, 13, "The Wand").FullTitle);
        }

        [Fact]
        public void AnEpisodeWithoutNumbersStillNamesItsShow()
        {
            Assert.Equal(
                "Adventure Time — The Wand",
                Episode("Adventure Time", null, null, "The Wand").FullTitle);
        }

        [Fact]
        public void AFilmIsJustItsTitle()
        {
            Assert.Equal("The Matrix", Film("The Matrix").FullTitle);
        }

        /// <summary>
        /// The show's name is searchable on every one of its episodes.
        /// </summary>
        /// <remarks>
        /// Without it, "adventure time" matches the series row and nothing else, and
        /// an episode is reachable only by its own obscure name — which is exactly the
        /// name nobody remembers.
        /// </remarks>
        [Fact]
        public void TheShowsNameIsIndexedOnItsEpisodes()
        {
            var fields = Episode("Adventure Time", 6, 13, "The Wand").RenderFields();

            Assert.Contains(
                fields,
                f => f.Field == DocumentField.Title && f.Text == "Adventure Time");
            Assert.Contains(
                fields,
                f => f.Field == DocumentField.Title && f.Text == "The Wand");
        }

        [Fact]
        public void AFilmGainsNoPhantomSeriesTitle()
        {
            var fields = Film("The Matrix").RenderFields();

            Assert.Single(fields, f => f.Field == DocumentField.Title);
        }

        /// <summary>
        /// An index written before episodes had a parent still loads.
        /// </summary>
        [Fact]
        public void ADocumentWithoutSeriesFieldsIsUnchanged()
        {
            var before = Film("Memento");

            Assert.Null(before.SeriesId);
            Assert.Equal(string.Empty, before.SeriesName);
            Assert.Equal("Memento", before.FullTitle);
        }
    }
}
