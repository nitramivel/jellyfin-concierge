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
    /// <param name="Pending">
    /// Whether this item's stored enrichment differs from what is actually embedded —
    /// that is, whether the answers on this page are the ones a search would use.
    /// </param>
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
        int Cues,
        bool Pending);

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
    /// <param name="Provenance">Which build wrote this item's enrichment, or null.</param>
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
        DateTime? QuoteExtractedUtc,
        ItemProvenance? Provenance);

    /// <summary>
    /// Which build produced an item's enrichment.
    /// </summary>
    /// <remarks>
    /// Read off the enrichment store rather than the run files, which are pruned. An
    /// item enriched a dozen builds ago still knows what wrote it long after that run
    /// has been deleted — but <see cref="RunAvailable"/> says whether the run itself
    /// can still be opened.
    /// </remarks>
    /// <param name="RunId">The build, or null for anything written before the tie existed.</param>
    /// <param name="RunAvailable">Whether that run's log is still on disk.</param>
    /// <param name="GeneratedUtc">When this answer was written.</param>
    /// <param name="Model">The model that wrote it.</param>
    /// <param name="CostUsd">Its share of the batch it was enriched in.</param>
    /// <param name="SourceHash">
    /// The fingerprint of the item as it was then. A mismatch against the item now is
    /// why a rebuild would re-enrich it.
    /// </param>
    public sealed record ItemProvenance(
        Guid? RunId,
        bool RunAvailable,
        DateTime GeneratedUtc,
        string Model,
        decimal CostUsd,
        string SourceHash);

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
    /// <param name="Pending">
    /// How many items hold enrichment the index has not embedded yet.
    /// </param>
    /// <param name="EnrichmentNewestUtc">When the newest stored enrichment was written.</param>
    /// <param name="IndexBuiltUtc">When the embedded rows were built.</param>
    /// <param name="Generation">Which index build this is.</param>
    public sealed record LibraryView(
        IReadOnlyList<LibraryItemSummary> Items,
        int Total,
        int Enriched,
        int WithoutAsks,
        int WithQuotes,
        int Pending,
        DateTime? EnrichmentNewestUtc,
        DateTime IndexBuiltUtc,
        long Generation);
}

namespace Jellyfin.Plugin.Concierge.Api
{
    /// <summary>
    /// Redo one item, on a model of the caller's choosing.
    /// </summary>
    /// <remarks>
    /// Per item because the reasons are per item: a film whose dialogue was extracted
    /// from the wrong language track, or one the default model wrote nothing useful
    /// about. Rebuilding the library to fix one of those costs a few hundred items'
    /// worth of nothing.
    /// </remarks>
    /// <param name="ModelProfileId">
    /// Which profile to ask, or empty to use whatever the enrichment pass normally
    /// uses. Trying a better model on one stubborn item is most of the point.
    /// </param>
    /// <param name="Thinking">
    /// Inherit, On or Off for this one call. Thinking on a single item costs seconds
    /// nobody is waiting on, which is a very different trade from a whole build.
    /// </param>
    /// <param name="Enrichment">Whether to ask the model again.</param>
    /// <param name="Quotes">
    /// Whether to forget the extracted dialogue so the next extraction looks again.
    /// This is the one for a wrong-language subtitle: the media file has not changed,
    /// so nothing else will make the extractor reconsider it.
    /// </param>
    public sealed record ReindexRequest(
        string? ModelProfileId = null,
        Configuration.ThinkingMode Thinking = Configuration.ThinkingMode.Inherit,
        bool Enrichment = true,
        bool Quotes = false);

    /// <summary>What redoing one item did.</summary>
    /// <param name="Title">The item.</param>
    /// <param name="Enriched">Whether the model returned something usable.</param>
    /// <param name="Outcome">enriched, unknown-to-model, failed, or skipped.</param>
    /// <param name="Model">Which model answered.</param>
    /// <param name="Thinking">How thinking resolved, and which rule decided it.</param>
    /// <param name="CostUsd">What this one item cost.</param>
    /// <param name="Asks">How many asks came back.</param>
    /// <param name="Themes">How many themes came back.</param>
    /// <param name="PremiseChars">How much premise came back.</param>
    /// <param name="QuotesForgotten">Whether stored dialogue was discarded.</param>
    /// <param name="Note">What still has to happen for this to reach searches.</param>
    public sealed record ReindexResult(
        string Title,
        bool Enriched,
        string Outcome,
        string Model,
        string Thinking,
        decimal CostUsd,
        int Asks,
        int Themes,
        int PremiseChars,
        bool QuotesForgotten,
        string Note);
}
