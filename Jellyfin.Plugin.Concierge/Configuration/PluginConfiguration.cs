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

        /// <summary>Whether the plan pass may think. Inherit follows <see cref="EnableThinking"/>.</summary>
        /// <remarks>
        /// Per pass because the trade is opposite at the two ends. Reasoning tokens are
        /// billed as output and generated before the answer, so on a search they are
        /// pure latency — measured here at 39% of everything the re-rank generated —
        /// while on enrichment nobody is waiting and what it writes is the ceiling on
        /// every search afterwards.
        /// </remarks>
        public ThinkingMode PlanThinking { get; set; } = ThinkingMode.Inherit;

        /// <summary>Whether the re-rank pass may think. Inherit follows <see cref="EnableThinking"/>.</summary>
        /// <remarks>
        /// The one to leave off. Re-rank is ~99% of the time a search takes, and its
        /// duration is the tokens it writes.
        /// </remarks>
        public ThinkingMode RerankThinking { get; set; } = ThinkingMode.Inherit;

        /// <summary>Whether enrichment may think. Inherit follows <see cref="EnableThinking"/>.</summary>
        /// <remarks>
        /// The one worth turning on. It runs once per item during a scheduled build, so
        /// the cost is money and not waiting, and the answer is permanent.
        /// </remarks>
        public ThinkingMode EnrichmentThinking { get; set; } = ThinkingMode.Inherit;

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
        public int MaxAsksPerItem { get; set; } = 10;

        /// <summary>
        /// Gets or sets how many texts are embedded per request.
        /// </summary>
        public int EmbeddingBatchSize { get; set; } = 64;

        /// <summary>
        /// Gets or sets how many results a search returns.
        /// </summary>
        /// <remarks>
        /// 40 because that is the shortlist the re-rank pass takes, and the evaluation
        /// set reads recall@40 as its retrieval diagnostic: an item that never reaches
        /// this many candidates can never be recovered by any amount of prompt work
        /// downstream.
        /// </remarks>
        public int MaxResults { get; set; } = 40;

        // ── The language passes ──────────────────────────────────────────────────

        /// <summary>
        /// Gets or sets whether a model reads the sentence before retrieval runs.
        /// </summary>
        /// <remarks>
        /// A kill switch, and turning it off leaves a working plugin: retrieval uses
        /// the raw query and applies no filters. The pass is also skipped
        /// automatically when the router saw no constraint-like language, which saves
        /// the call on the most common Concierge query.
        /// </remarks>
        public bool EnablePlanPass { get; set; } = true;

        /// <summary>
        /// Gets or sets whether a model orders the shortlist and explains each match.
        /// </summary>
        /// <remarks>
        /// The pass that carries most of the quality, and the more expensive of the
        /// two — roughly five times the plan pass, because it sends the whole
        /// shortlist. Off leaves the fused order, which is slightly worse and free.
        /// </remarks>
        public bool EnableRerankPass { get; set; } = true;

        /// <summary>
        /// Gets or sets how many candidates go to the re-rank pass.
        /// </summary>
        /// <remarks>
        /// The single biggest lever on per-query cost and the second biggest on
        /// latency: this is almost all of the re-rank's input tokens. Lowered from
        /// 40 to 24 after measuring 8-22 second searches against a 2.5-second
        /// budget. Retrieval still returns 40 for the evaluation set's recall@40;
        /// this is only how many the model is asked to look at.
        /// </remarks>
        public int RerankShortlistSize { get; set; } = 40;

        /// <summary>
        /// The longest a match reason may be, in characters.
        /// </summary>
        /// <remarks>
        /// <b>This is the latency dial.</b> Re-rank latency is generated tokens and
        /// nothing else — measured at +0.937 correlation across 80 calls, at a flat
        /// ~166 tokens per second — and the reasons are almost all of the output. The
        /// prompt asked for eight words and got 609 tokens where 240 was warranted,
        /// because a limit stated in a rule list is a suggestion.
        /// <para>
        /// Stated in the response shape and enforced on the way out, so a model that
        /// writes an essay cannot make a card unreadable. Roughly: 60 characters is a
        /// clause, 120 is a sentence, and every 40 characters across a full row of
        /// results is about a tenth of a second of waiting.
        /// </para>
        /// </remarks>
        public int RerankWhyMaxChars { get; set; } = 60;

        /// <summary>
        /// How long the search box must be idle before Concierge spends anything, in
        /// milliseconds.
        /// </summary>
        /// <remarks>
        /// Jellyfin's own search waits 500 ms because its requests are free. This one
        /// costs money, so it waits for a query somebody has finished typing rather
        /// than a slightly later copy of every prefix. Measured effect of raising it
        /// to 2,000 ms: half-typed queries fell from 28 of 89 searches to 5 of 30.
        /// <para>
        /// The free preview is unaffected and still paints in about 250 ms, so a long
        /// wait here costs no responsiveness — only the moment the ranked order
        /// arrives. Enter always runs immediately.
        /// </para>
        /// </remarks>
        public int SearchDebounceMs { get; set; } = 2000;

        /// <summary>
        /// Whether to hide Jellyfin Enhanced's Jellyseerr icon so Concierge's can take
        /// that corner of the search box.
        /// </summary>
        /// <remarks>
        /// <b>The only place this plugin affects another's interface.</b> Done with a
        /// CSS rule rather than by touching their element: theirs still exists, its
        /// click handler still works, and it simply is not painted — so nothing of
        /// theirs can break and their re-creating it on every render does not fight us.
        /// <para>
        /// It is a setting because that icon is <em>their</em> Seerr-only filter
        /// toggle, and hiding it takes that control away. Anyone who wants it back
        /// should not need a release to get it.
        /// </para>
        /// </remarks>
        public bool HideJellyseerrIcon { get; set; } = true;

        /// <summary>
        /// How many of the ranked results get a reason written for them.
        /// </summary>
        /// <remarks>
        /// The ordering costs about six tokens an entry; a reason costs forty. The row
        /// shows eight cards before it scrolls, so writing reasons for all twenty
        /// spends most of the output on text nobody scrolls to. Zero means every
        /// result gets one.
        /// </remarks>
        public int RerankExplainCount { get; set; } = 8;

        // ── Spending ─────────────────────────────────────────────────────────────

        /// <summary>
        /// Gets or sets the monthly ceiling for <em>query</em> spend, in USD.
        /// 0 means uncapped.
        /// </summary>
        /// <remarks>
        /// This is the constraint that separates Concierge from its sibling. Curator
        /// spends on a schedule its owner controls; Concierge spends when someone
        /// types, which is unpredictable and can be triggered by anyone with an
        /// account on the server.
        /// <para>
        /// Reaching it never breaks search. At 85% the re-rank stops; at 100% queries
        /// fall back to free retrieval and say so.
        /// </para>
        /// </remarks>
        public decimal MonthlyBudgetUsd { get; set; } = 5m;

        /// <summary>
        /// Gets or sets the monthly ceiling for <em>index</em> spend, in USD.
        /// 0 means uncapped.
        /// </summary>
        /// <remarks>
        /// <b>Deliberately a separate pot from <see cref="MonthlyBudgetUsd"/>.</b> A
        /// full index build costs a couple of dollars, so sharing one budget would
        /// exhaust the month on the day someone installed the plugin and leave search
        /// degraded — the worst possible first impression, caused entirely by an
        /// accounting decision.
        /// </remarks>
        public decimal EnrichmentBudgetUsd { get; set; } = 10m;

        /// <summary>
        /// Gets or sets how many paid searches one user may make per hour.
        /// 0 means unlimited.
        /// </summary>
        /// <remarks>
        /// Someone holding a key down in a search box is the cheapest way to spend a
        /// month's budget in an afternoon. Reaching the limit degrades that user to
        /// free retrieval and leaves everyone else untouched.
        /// </remarks>
        public int PaidQueriesPerUserPerHour { get; set; } = 30;

        /// <summary>
        /// Gets or sets how many answered queries are remembered.
        /// </summary>
        /// <remarks>
        /// Repeats are free and instant. The same person retyping the same thing is
        /// the commonest search there is, and the cache is where that stops costing
        /// money. Invalidated wholesale by an index rebuild.
        /// </remarks>
        public int QueryCacheSize { get; set; } = 200;

        // ── Quote search ─────────────────────────────────────────────────────────

        /// <summary>
        /// Gets or sets whether dialogue is indexed and searchable.
        /// </summary>
        /// <remarks>
        /// Costs no money at all — only CPU, once, to read subtitles out of the files.
        /// It is also the only part of the plugin that works on films no model has
        /// heard of, because the text comes from the file rather than from a model's
        /// memory.
        /// </remarks>
        public bool EnableQuoteSearch { get; set; } = true;

        /// <summary>
        /// Gets or sets whether episodes have their dialogue indexed too.
        /// </summary>
        /// <remarks>
        /// Off by default, and the reason is size rather than taste: films are roughly
        /// 73,000 searchable windows and the full library is 850,000. Films finish in
        /// minutes; everything is an overnight job. Turning this on is also what would
        /// make a real full-text database the honest choice over the hand-rolled index.
        /// </remarks>
        public bool QuoteIncludeEpisodes { get; set; }

        /// <summary>
        /// Gets or sets the preferred subtitle language.
        /// </summary>
        public string SubtitleLanguage { get; set; } = "en";

        /// <summary>
        /// Gets or sets how many words a searchable dialogue window holds.
        /// </summary>
        /// <remarks>
        /// Windows overlap by half, so any phrase shorter than half of this is
        /// guaranteed to sit whole inside one of them. Changing it costs a reload but
        /// no re-extraction — only cleaned lines are stored, and windows are rebuilt
        /// from them.
        /// </remarks>
        public int QuoteWindowWords { get; set; } = 40;

        /// <summary>
        /// Gets or sets whether song lyrics are indexed alongside film dialogue.
        /// </summary>
        /// <remarks>
        /// Costs nothing and needs no extraction: Jellyfin already holds lyrics as
        /// parsed, time-stamped lines, so this is a read rather than an ffmpeg job.
        /// A matched lyric deep-links to the second it is sung, exactly as a quoted
        /// line does.
        /// </remarks>
        public bool EnableLyricSearch { get; set; } = true;

        // ── The query log ────────────────────────────────────────────────────────

        /// <summary>
        /// Gets or sets whether the text of each search is stored alongside its cost.
        /// </summary>
        /// <remarks>
        /// On by default, because a search you cannot see is a search you cannot
        /// debug — every diagnosis in this plugin's history started by reading what
        /// somebody actually typed.
        /// <para>
        /// <b>It is also a two-year record of what everyone in the house searched
        /// for</b>, which is a different thing from a cost log and deserves a separate
        /// decision. Turning this off keeps every number — timings, tokens, cost, the
        /// model used, which user — and drops only the words. Usage breakdowns are
        /// completely unaffected.
        /// </para>
        /// </remarks>
        public bool LogQueryText { get; set; } = true;
    }
}
