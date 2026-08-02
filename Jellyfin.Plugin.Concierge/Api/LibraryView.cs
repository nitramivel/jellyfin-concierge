using System;
using System.Collections.Generic;

namespace Jellyfin.Plugin.Concierge.Api
{
    /// <summary>
    /// One item as it appears in the library list.
    /// </summary>
    /// <remarks>
    /// Counts rather than text. The list is the whole library at once and the point of
    /// it is to make the gaps visible at a glance — an item with no asks is an item no
    /// description will ever find, and that has to be spottable without opening it.
    /// </remarks>
    /// <param name="ItemId">The item.</param>
    /// <param name="Title">Its title.</param>
    /// <param name="Year">Its year, or null.</param>
    /// <param name="Kind">Movie, Series, and so on.</param>
    /// <param name="Genres">Its genres.</param>
    /// <param name="Enriched">Whether the model had anything to say about it.</param>
    /// <param name="PremiseChars">How much premise came back.</param>
    /// <param name="Moments">How many moments.</param>
    /// <param name="Themes">How many themes.</param>
    /// <param name="Asks">How many asks — what a vague search actually matches against.</param>
    /// <param name="Spoiler">Whether the enrichment gives away an ending.</param>
    /// <param name="Rows">Vector rows carrying this item.</param>
    /// <param name="Cues">Lines of dialogue extracted, or 0 when none were.</param>
    public sealed record LibraryItemSummary(
        Guid ItemId,
        string Title,
        int? Year,
        string Kind,
        IReadOnlyList<string> Genres,
        bool Enriched,
        int PremiseChars,
        int Moments,
        int Themes,
        int Asks,
        bool Spoiler,
        int Rows,
        int Cues);

    /// <summary>
    /// Everything Concierge holds for one item.
    /// </summary>
    /// <param name="Item">Its list entry, so a detail view needs no second lookup.</param>
    /// <param name="OriginalTitle">The original-language title, when it differs.</param>
    /// <param name="Tags">Its tags.</param>
    /// <param name="Studios">Its studios.</param>
    /// <param name="People">The cast and crew that were indexed.</param>
    /// <param name="OfficialRating">Its certificate.</param>
    /// <param name="RuntimeMinutes">Its runtime.</param>
    /// <param name="Overview">The library's own synopsis.</param>
    /// <param name="Premise">What the model says actually happens.</param>
    /// <param name="MomentList">The moments it named.</param>
    /// <param name="ThemeList">The themes it named.</param>
    /// <param name="AskList">The sentences a half-remembering searcher is matched against.</param>
    /// <param name="VectorRows">Every embedded row for this item, with the text embedded.</param>
    /// <param name="QuoteSample">A few extracted lines, so the extraction can be eyeballed.</param>
    /// <param name="QuoteSourcePath">Where the dialogue came from.</param>
    /// <param name="QuoteExtractedUtc">When it was extracted.</param>
    public sealed record LibraryItemDetail(
        LibraryItemSummary Item,
        string OriginalTitle,
        IReadOnlyList<string> Tags,
        IReadOnlyList<string> Studios,
        IReadOnlyList<string> People,
        string OfficialRating,
        int? RuntimeMinutes,
        string Overview,
        string Premise,
        IReadOnlyList<string> MomentList,
        IReadOnlyList<string> ThemeList,
        IReadOnlyList<string> AskList,
        IReadOnlyList<LibraryVectorRow> VectorRows,
        IReadOnlyList<string> QuoteSample,
        string QuoteSourcePath,
        DateTime? QuoteExtractedUtc);

    /// <summary>One embedded row.</summary>
    /// <param name="Kind">Document, Vibe or Ask.</param>
    /// <param name="Text">The text that was embedded, verbatim.</param>
    public sealed record LibraryVectorRow(string Kind, string Text);

    /// <summary>
    /// The library list, with the totals that say how healthy it is.
    /// </summary>
    /// <param name="Items">Every indexed item.</param>
    /// <param name="Total">How many there are.</param>
    /// <param name="Enriched">How many the model knew.</param>
    /// <param name="WithoutAsks">
    /// How many carry no asks. These are findable by title and overview only, which is
    /// the single number on this page most worth watching.
    /// </param>
    /// <param name="WithQuotes">How many have extracted dialogue.</param>
    /// <param name="Generation">Which index build this is.</param>
    public sealed record LibraryView(
        IReadOnlyList<LibraryItemSummary> Items,
        int Total,
        int Enriched,
        int WithoutAsks,
        int WithQuotes,
        long Generation);
}
