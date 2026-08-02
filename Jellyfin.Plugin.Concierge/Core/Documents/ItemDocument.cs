using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;

namespace Jellyfin.Plugin.Concierge.Core.Documents
{
    /// <summary>
    /// The parts of an item's text, kept apart so each can be weighted separately.
    /// </summary>
    public enum DocumentField
    {
        /// <summary>The display title.</summary>
        Title = 0,

        /// <summary>The original-language title, when it differs.</summary>
        OriginalTitle = 1,

        /// <summary>Cast, directors and writers.</summary>
        People = 2,

        /// <summary>Genres, tags, studios, certificate.</summary>
        Categorical = 3,

        /// <summary>Year and the decade vocabulary that goes with it.</summary>
        Era = 4,

        /// <summary>The library's own overview.</summary>
        Overview = 5,

        /// <summary>Everything the enrichment pass added.</summary>
        Enrichment = 6,
    }

    /// <summary>One field's worth of text from a document.</summary>
    /// <param name="Field">Which field.</param>
    /// <param name="Text">Its text.</param>
    public sealed record DocumentSection(DocumentField Field, string Text);

    /// <summary>
    /// How much each field counts toward a lexical match.
    /// </summary>
    /// <remarks>
    /// One place, because these are the numbers most likely to be fiddled with and
    /// the fiddling needs to be visible in a diff. A title match should beat a
    /// passing mention in an overview by a lot; everything else is shades.
    /// </remarks>
    public static class FieldWeights
    {
        /// <summary>
        /// The weight for one field.
        /// </summary>
        /// <param name="field">The field.</param>
        /// <returns>Its multiplier.</returns>
        public static double For(DocumentField field) => field switch
        {
            DocumentField.Title => 4.0,
            DocumentField.OriginalTitle => 3.0,
            DocumentField.People => 2.0,
            DocumentField.Categorical => 1.5,

            // Same weight as the other categoricals. An era word is a genuine signal
            // ("90s classics") but it is shared by a tenth of the library, so it must
            // narrow the field without ever outranking what the query is actually about.
            DocumentField.Era => 1.5,

            DocumentField.Overview => 1.0,

            // Level with the overview on purpose. Enrichment is longer and more
            // numerous than the overview it supplements, and weighting it above would
            // let a thoroughly-enriched film outrank a better match on volume alone.
            DocumentField.Enrichment => 1.0,

            _ => 1.0,
        };
    }

    /// <summary>
    /// One library item, projected to the text Concierge indexes.
    /// </summary>
    /// <remarks>
    /// Deliberately free of Jellyfin types. Everything interesting about retrieval
    /// is decidable without a server, and keeping this a plain record is what lets
    /// the whole ranking stack be tested against a fixture library rather than a
    /// live one. <see cref="ItemDocumentFactory"/> does the projection.
    /// </remarks>
    /// <param name="ItemId">The Jellyfin item id. Never shown to a model (hard rule 1).</param>
    /// <param name="Kind">Movie, Series or Episode.</param>
    /// <param name="Title">The display title.</param>
    /// <param name="OriginalTitle">The original-language title, or empty.</param>
    /// <param name="Year">Production year, or null.</param>
    /// <param name="Genres">Genres.</param>
    /// <param name="Tags">Tags.</param>
    /// <param name="Studios">Studios.</param>
    /// <param name="People">Top cast plus directors and writers.</param>
    /// <param name="OfficialRating">Certificate, or empty.</param>
    /// <param name="RuntimeMinutes">Runtime in minutes, or null.</param>
    /// <param name="Overview">
    /// The library's overview, <b>whole</b>. Not truncated: cutting before embedding
    /// stores a compression of the first paragraph forever, with nothing downstream
    /// able to tell it happened.
    /// </param>
    /// <param name="Enrichment">What the enrichment pass added, or null before it has run.</param>
    /// <param name="SeriesId">
    /// The show an episode belongs to, or null for anything that is not one.
    /// </param>
    /// <param name="SeriesName">
    /// The show's title. Carried on the episode because an episode called "The Wand"
    /// is not identifiable without it — in a search, in a run log, or in a list.
    /// </param>
    /// <param name="SeasonNumber">Its season, when it has one.</param>
    /// <param name="EpisodeNumber">Its number within that season.</param>
    public sealed record ItemDocument(
        Guid ItemId,
        string Kind,
        string Title,
        string OriginalTitle,
        int? Year,
        IReadOnlyList<string> Genres,
        IReadOnlyList<string> Tags,
        IReadOnlyList<string> Studios,
        IReadOnlyList<string> People,
        string OfficialRating,
        int? RuntimeMinutes,
        string Overview,
        Enrichment? Enrichment = null,

        // Optional so an index written before episodes had a parent still loads. Those
        // documents report no series, which is the truth about what was recorded.
        Guid? SeriesId = null,
        string SeriesName = "",
        int? SeasonNumber = null,
        int? EpisodeNumber = null)
    {
        /// <summary>
        /// Gets the item named so a person could identify it.
        /// </summary>
        /// <remarks>
        /// An episode's own title is not an identifier. "The Wand" in a run log, a
        /// batch prompt or a list is unanswerable; "Adventure Time S6E13 — The Wand"
        /// is a thing somebody can recognise, search for and check.
        /// </remarks>
        public string FullTitle
        {
            get
            {
                if (string.IsNullOrWhiteSpace(SeriesName))
                {
                    return Title;
                }

                var number = SeasonNumber is { } season && EpisodeNumber is { } episode
                    ? string.Create(
                        System.Globalization.CultureInfo.InvariantCulture,
                        $" S{season}E{episode}")
                    : string.Empty;

                return SeriesName + number + " — " + Title;
            }
        }

        /// <summary>
        /// Splits the document into weighted fields for lexical indexing.
        /// </summary>
        /// <returns>The non-empty fields.</returns>
        public IReadOnlyList<DocumentSection> RenderFields()
        {
            var sections = new List<DocumentSection>(7);

            Add(sections, DocumentField.Title, Title);

            // The show's name, weighted as a title, on every one of its episodes.
            // Without it "adventure time" matches the series row and nothing else,
            // and an episode is only ever reachable by its own obscure name.
            if (!string.IsNullOrWhiteSpace(SeriesName))
            {
                Add(sections, DocumentField.Title, SeriesName);
            }

            // Only when it says something the display title does not — otherwise it
            // is the same string counted twice at seven times the weight.
            if (!string.IsNullOrWhiteSpace(OriginalTitle)
                && !string.Equals(OriginalTitle, Title, StringComparison.OrdinalIgnoreCase))
            {
                Add(sections, DocumentField.OriginalTitle, OriginalTitle);
            }

            Add(sections, DocumentField.People, string.Join(' ', People));
            Add(sections, DocumentField.Categorical, string.Join(
                ' ',
                Genres.Concat(Tags).Concat(Studios).Append(OfficialRating).Append(Kind)));
            Add(sections, DocumentField.Era, EraTokens.Render(Year));
            Add(sections, DocumentField.Overview, Overview);
            Add(sections, DocumentField.Enrichment, Enrichment?.RenderIndexText() ?? string.Empty);

            return sections;
        }

        /// <summary>
        /// The text embedded as this item's own vector row.
        /// </summary>
        /// <remarks>
        /// Prose rather than a field dump, because that is the shape the embedding
        /// models were trained on. The enrichment's <c>asks</c> are deliberately left
        /// out — each of those is embedded as its own row, and folding them in here
        /// as well would blur this row into an average of ten different sentences.
        /// What <em>is</em> folded in is themes, which is what a mood query
        /// ("dark and twisted") has to land on.
        /// </remarks>
        /// <returns>The text to embed.</returns>
        public string RenderEmbeddingText()
        {
            var text = new StringBuilder();

            text.Append(Title);
            if (Year is { } year)
            {
                text.Append(" (").Append(year.ToString(CultureInfo.InvariantCulture)).Append(')');
            }

            text.Append(". ").Append(Kind).Append('.');

            AppendList(text, "Genres", Genres);
            AppendList(text, "Tags", Tags);

            if (Enrichment is { } enrichment && enrichment.Themes.Count > 0)
            {
                AppendList(text, "Themes", enrichment.Themes);
            }

            if (People.Count > 0)
            {
                AppendList(text, "Featuring", People.Take(8).ToList());
            }

            if (!string.IsNullOrWhiteSpace(Overview))
            {
                text.Append(' ').Append(Overview.Trim());
            }

            if (Enrichment is { } e)
            {
                if (!string.IsNullOrWhiteSpace(e.Premise))
                {
                    text.Append(' ').Append(e.Premise.Trim());
                }

                if (e.Moments.Count > 0)
                {
                    text.Append(' ').Append(string.Join(". ", e.Moments));
                }
            }

            return text.ToString();
        }

        /// <summary>
        /// The text the document hash is taken over.
        /// </summary>
        /// <remarks>
        /// <b>Library fields only — enrichment is excluded on purpose</b> (§5.3).
        /// The hash answers "has the source changed?", and enrichment is derived from
        /// the source rather than part of it. Including it would make every
        /// enrichment invalidate the hash that keys it, so nothing would ever be
        /// considered fresh.
        /// </remarks>
        /// <returns>The source text.</returns>
        public string RenderSourceText()
        {
            var text = new StringBuilder();
            text.Append(Kind).Append('\n');
            text.Append(Title).Append('\n');
            text.Append(OriginalTitle).Append('\n');
            text.Append(Year?.ToString(CultureInfo.InvariantCulture) ?? string.Empty).Append('\n');
            text.Append(string.Join(',', Genres)).Append('\n');
            text.Append(string.Join(',', Tags)).Append('\n');
            text.Append(string.Join(',', Studios)).Append('\n');
            text.Append(string.Join(',', People)).Append('\n');
            text.Append(OfficialRating).Append('\n');
            text.Append(RuntimeMinutes?.ToString(CultureInfo.InvariantCulture) ?? string.Empty).Append('\n');
            text.Append(Overview);
            return text.ToString();
        }

        private static void Add(List<DocumentSection> sections, DocumentField field, string text)
        {
            var trimmed = text?.Trim();
            if (!string.IsNullOrEmpty(trimmed))
            {
                sections.Add(new DocumentSection(field, trimmed));
            }
        }

        private static void AppendList(StringBuilder text, string label, IReadOnlyList<string> values)
        {
            if (values.Count == 0)
            {
                return;
            }

            text.Append(' ').Append(label).Append(": ").Append(string.Join(", ", values)).Append('.');
        }
    }
}
