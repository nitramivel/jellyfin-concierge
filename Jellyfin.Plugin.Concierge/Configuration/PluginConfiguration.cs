using System;
using MediaBrowser.Model.Plugins;

namespace Jellyfin.Plugin.Concierge.Configuration
{
    /// <summary>
    /// Plugin configuration.
    /// </summary>
    /// <remarks>
    /// The index is NOT stored here — it lives under the plugin data directory
    /// behind the index store, because it is a cache and this file is settings
    /// (hard rule 6: deleting the index restores exactly the previous behaviour).
    /// <para>
    /// <b>There are no legacy single-provider scalars here, and none may ever be
    /// added.</b> Concierge starts life with the profile lists already in place, so
    /// it never has to migrate an install that predates them. This matters because
    /// the trap is one-way: <see cref="System.Xml.Serialization.XmlSerializer"/>
    /// silently drops elements it has no property for, so a scalar that ships once
    /// can never be removed — deleting it would throw away the API key of every
    /// install that upgrades before it next opens the config page. Not shipping
    /// them is the only move that stays free.
    /// </para>
    /// </remarks>
    public class PluginConfiguration : BasePluginConfiguration
    {
        /// <summary>
        /// Gets or sets the saved chat model profiles — each a provider, a model,
        /// its own API key, and its own prices. See <see cref="ModelProfile"/>.
        /// </summary>
        public ModelProfile[] ModelProfiles { get; set; } = Array.Empty<ModelProfile>();

        /// <summary>
        /// Gets or sets the id of the profile used by any pass that has not been
        /// given one of its own.
        /// </summary>
        public string DefaultModelProfileId { get; set; } = string.Empty;

        /// <summary>
        /// Gets or sets the saved embedding profiles. A parallel list to
        /// <see cref="ModelProfiles"/>, never the same one — see
        /// <see cref="EmbeddingProfile"/>.
        /// </summary>
        public EmbeddingProfile[] EmbeddingProfiles { get; set; } = Array.Empty<EmbeddingProfile>();

        /// <summary>
        /// Gets or sets the id of the embedding profile used when
        /// <see cref="EmbeddingProfileId"/> is blank.
        /// </summary>
        public string DefaultEmbeddingProfileId { get; set; } = string.Empty;

        // ── Per-pass model assignment ────────────────────────────────────────────
        //
        // Blank is a REAL VALUE meaning "follow the default profile", not "unset".
        // An install that has configured nothing but a single default profile must
        // run every pass sensibly, and it does.
        //
        // The split between the two per-query passes and the index-time one is the
        // economic argument of the whole design: the per-query passes run forever
        // and should be as cheap as quality allows, while enrichment runs once per
        // item and sets the recall ceiling for every search after it.

        /// <summary>
        /// Gets or sets the profile that reads a sentence into a search plan. Runs
        /// per query, wants small and fast — Haiku-tier. Blank uses
        /// <see cref="DefaultModelProfileId"/>.
        /// </summary>
        public string PlanModelProfileId { get; set; } = string.Empty;

        /// <summary>
        /// Gets or sets the profile that orders the shortlist and explains each
        /// match. Runs per query; this is where quality shows — Sonnet-tier. Blank
        /// uses <see cref="DefaultModelProfileId"/>.
        /// </summary>
        public string RerankModelProfileId { get; set; } = string.Empty;

        /// <summary>
        /// Gets or sets the profile that writes each item's enrichment at index
        /// time. Runs <em>once</em> per item and should be the best model
        /// affordable: it decides what a fuzzy sentence can ever match against.
        /// Blank uses <see cref="DefaultModelProfileId"/>.
        /// </summary>
        public string EnrichmentModelProfileId { get; set; } = string.Empty;

        /// <summary>
        /// Gets or sets the embedding profile used for both documents and queries.
        /// Blank uses <see cref="DefaultEmbeddingProfileId"/>.
        /// </summary>
        public string EmbeddingProfileId { get; set; } = string.Empty;

        // ── Call shape ───────────────────────────────────────────────────────────

        /// <summary>
        /// Gets or sets the default for <see cref="ModelProfile.Thinking"/> when a
        /// profile inherits.
        /// </summary>
        /// <remarks>
        /// Off by default, which is the opposite of Curator's choice and correct for
        /// this plugin: two of Concierge's three paid passes sit inside a 2.5-second
        /// search budget, and thinking competes with the answer for one output cap.
        /// Turn it on for enrichment, where the run is a background task and quality
        /// is the only thing that matters.
        /// </remarks>
        public bool EnableThinking { get; set; }

        /// <summary>
        /// Gets or sets the output-token cap applied to a single model call.
        /// </summary>
        /// <remarks>
        /// Caps thinking and the visible answer together on every provider that
        /// reports them separately, which is how a response gets truncated mid-JSON.
        /// </remarks>
        public int MaxOutputTokens { get; set; } = 8000;

        // ── The index ────────────────────────────────────────────────────────────

        /// <summary>
        /// Gets or sets whether individual episodes are indexed alongside their series.
        /// </summary>
        /// <remarks>
        /// Off by default. Episodes are where specific plots actually live, but they
        /// multiply the index by roughly 15x and a results list can drown a series in
        /// its own episodes.
        /// </remarks>
        public bool IncludeEpisodes { get; set; }

        /// <summary>
        /// Gets or sets whether the index-time enrichment pass runs.
        /// </summary>
        /// <remarks>
        /// On by default, and it is what makes plot and mood recall work at all. With
        /// it off the index holds only what the library already knew, and searches
        /// fall back to matching against marketing copy — which is precisely the
        /// failure this plugin exists to fix. It is a setting rather than a fixed step
        /// because on a 10,000-item library the cost stops being pocket change.
        /// </remarks>
        public bool EnableEnrichment { get; set; } = true;

        /// <summary>
        /// Gets or sets how many items go into one enrichment call.
        /// </summary>
        /// <remarks>
        /// The real constraint is the output cap, not the input: each item asks for a
        /// premise, several moments, themes and up to ten phrasings, so a large batch
        /// truncates mid-JSON and loses everything after the cut.
        /// </remarks>
        public int EnrichmentBatchSize { get; set; } = 12;

        /// <summary>
        /// Gets or sets how many generated phrasings are kept per item.
        /// </summary>
        /// <remarks>
        /// Each one becomes a vector row, so this multiplies the index's memory
        /// directly — it is the first lever to turn down on a large library.
        /// </remarks>
        public int MaxAsksPerItem { get; set; } = 8;

        /// <summary>
        /// Gets or sets how many texts are embedded per request.
        /// </summary>
        public int EmbeddingBatchSize { get; set; } = 64;

        /// <summary>
        /// Gets or sets how many results a search returns.
        /// </summary>
        /// <remarks>
        /// 40 because that is the shortlist the re-rank pass will take in phase 2, and
        /// the evaluation set reads recall@40 as its retrieval diagnostic: an item
        /// that never reaches this many candidates can never be recovered by any
        /// amount of prompt work downstream.
        /// </remarks>
        public int MaxResults { get; set; } = 40;
    }
}
