using System;
using System.Collections.Generic;
using System.Linq;

namespace Jellyfin.Plugin.Concierge.Core.Documents
{
    /// <summary>
    /// What a model that knows a film can say about it that the overview does not.
    /// </summary>
    /// <remarks>
    /// The reason this type exists is §5.2: overviews describe the <em>premise</em>,
    /// and people remember <em>moments</em>. John Wick's overview is "ex-hitman
    /// comes out of retirement to track down the gangsters that took everything from
    /// him", so "the one where they kill the guy's dog" — the single most memorable
    /// thing about it — matches nothing, semantically or lexically.
    /// <para>
    /// A cache, never library data. Deleting it degrades search and damages nothing
    /// (hard rule 6).
    /// </para>
    /// </remarks>
    /// <param name="Premise">What the overview should have said.</param>
    /// <param name="Moments">The images people actually remember.</param>
    /// <param name="Themes">
    /// Subject and <em>tone</em> — what it is about and what watching it feels like.
    /// This is the field a mood query lands on: "dark and twisted" has no plot to
    /// match against, so it matches here or nowhere.
    /// </param>
    /// <param name="Asks">
    /// How someone who half-remembers this would describe it. The heavy lifter:
    /// each one is embedded as its own row, so a user's fuzzy sentence is compared
    /// against other fuzzy sentences about the same film rather than against
    /// marketing copy.
    /// </param>
    /// <param name="Spoiler">Whether any of the above gives away the ending.</param>
    public sealed record Enrichment(
        string Premise,
        IReadOnlyList<string> Moments,
        IReadOnlyList<string> Themes,
        IReadOnlyList<string> Asks,
        bool Spoiler)
    {
        /// <summary>
        /// The enrichment of an item the model had nothing true to say about.
        /// </summary>
        /// <remarks>
        /// A real, storable value — not null-as-error. Hard rule 14: a model with
        /// nothing to say invents, and an invented <c>ask</c> is a permanent wrong
        /// answer that costs nothing to create and is invisible until somebody
        /// searches for it. Storing emptiness is the correct outcome for an obscure
        /// or brand-new title, and it stops the item being re-queued forever.
        /// </remarks>
        public static Enrichment Empty { get; } = new(string.Empty, [], [], [], false);

        /// <summary>
        /// Gets whether this enrichment carries nothing worth indexing.
        /// </summary>
        public bool IsEmpty =>
            string.IsNullOrWhiteSpace(Premise)
            && Moments.Count == 0
            && Themes.Count == 0
            && Asks.Count == 0;

        /// <summary>
        /// The text this enrichment contributes to the item's indexed document.
        /// </summary>
        /// <remarks>
        /// Everything, spoilers included — the index is allowed to know the twist,
        /// because "the one where he was dead the whole time" has to work. What must
        /// never happen is this text reaching a result card; see
        /// <see cref="DisplayPremise"/>.
        /// </remarks>
        /// <returns>The indexable text.</returns>
        public string RenderIndexText()
        {
            var parts = new List<string>(4);

            if (!string.IsNullOrWhiteSpace(Premise))
            {
                parts.Add(Premise);
            }

            foreach (var group in new[] { Themes, Moments, Asks })
            {
                if (group.Count > 0)
                {
                    parts.Add(string.Join(". ", group.Where(s => !string.IsNullOrWhiteSpace(s))));
                }
            }

            return string.Join(". ", parts);
        }

        /// <summary>
        /// The premise, but only when showing it would not ruin the film.
        /// </summary>
        /// <remarks>
        /// Hard rule 14's display half. <see cref="Moments"/> is never rendered at
        /// all — it is the field most likely to be the twist stated plainly.
        /// </remarks>
        /// <returns>The premise, or empty when this enrichment is spoiler-flagged.</returns>
        public string DisplayPremise() => Spoiler ? string.Empty : Premise;
    }

    /// <summary>
    /// One item's stored enrichment, tied to the document it was generated from.
    /// </summary>
    /// <remarks>
    /// <paramref name="SourceHash"/> is what stops the worst failure in §5.3: an
    /// enrichment describing the wrong film. A metadata refresh that rewrites an
    /// item's title and overview changes the source hash, and enrichment keyed to
    /// the old hash is discarded rather than silently kept.
    /// </remarks>
    /// <param name="ItemId">The item this describes.</param>
    /// <param name="SourceHash">The document hash this was generated from.</param>
    /// <param name="Enrichment">The enrichment itself, possibly empty.</param>
    /// <param name="GeneratedUtc">When it was generated.</param>
    /// <param name="RunId">
    /// The index build that produced this, or <see cref="Guid.Empty"/> for anything
    /// written before the tie existed.
    /// </param>
    /// <param name="Model">The model that wrote it, or empty when unrecorded.</param>
    /// <param name="CostUsd">Its share of the batch it was enriched in.</param>
    public sealed record StoredEnrichment(
        Guid ItemId,
        string SourceHash,
        Enrichment Enrichment,
        DateTime GeneratedUtc,

        /* Recorded here rather than left to the run files, because those are pruned:
         * after a dozen builds the run that produced an item is gone and the tie with
         * it. This is the durable half — what wrote this answer, when, and for how
         * much — and it survives the log it came from.
         *
         * Optional so that an enrichment.json written before this existed still
         * deserializes. Those entries report honestly as unknown rather than
         * inventing a run. */
        Guid RunId = default,
        string Model = "",
        decimal CostUsd = 0m);
}
