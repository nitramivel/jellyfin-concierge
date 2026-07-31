# Concierge — build plan

A Jellyfin plugin that replaces substring search with a natural-language one:
*"that 90s movie where the guy can't make new memories and covers himself in
tattoos"* should return **Memento**, and *"I'm walking here!"* should return
**Midnight Cowboy** at 01:04:12.

This document is the execution plan. It is written to be argued with — every
number in it is a guess until measured, and the places where a guess would be
expensive to get wrong are called out as such.

---

## 1. Scope

Concierge does one thing: **turn a sentence into the right items in this
library.**

In scope:

- Natural-language queries over titles, people, genres, plot, tone, and (later)
  spoken dialogue.
- Structured constraints expressed in prose — *"under two hours"*, *"from the
  90s"*, *"that I haven't seen"* — resolved against real Jellyfin fields.
- A result list that explains itself: one line per hit saying why it matched.

Out of scope, deliberately:

- **A rules engine or a filter UI.** [SmartLists](https://github.com/jyourstone/jellyfin-smartlists-plugin)
  does that, and Jellyfin's own filter panel does the simple half.
- **Recommendations and browsing rows.** That is
  [Curator](https://github.com/nitramivel/jellyfin-curator). Concierge answers a
  question the user asked; Curator answers one they didn't. They may share a
  server and should share nothing else at runtime.
- **Metadata correction, scraping, or writing anything back to the library.**
  Concierge is read-only over the library, permanently. See rule 6.

The relationship to Curator is worth stating precisely because it will tempt us:
the two plugins solve different problems with overlapping machinery. Copy the
proven parts (provider stack, run logging, config-page idioms, release script),
share no state, and take no dependency in either direction.

---

### 1.1 Prior art — `jellyfin-plugin-ai-search`

[Franciskid/jellyfin-plugin-ai-search](https://github.com/Franciskid/jellyfin-plugin-ai-search)
(GPL-3.0, created 5 Jul 2026, actively developed, ~3.4k lines of C#) already
does a substantial part of what phases 1-2 of this plan describe, and it does it
well. Read it before writing any of them.

**It independently arrived at the same core architecture**, which is strong
evidence the architecture is right: a local embedding index over item documents,
~40 candidates retrieved per query, a chat model that picks from the shortlist
and explains its picks, injected client script, any OpenAI-compatible endpoint,
nightly incremental re-embedding. It even landed on the same `MaxRetrieve = 40`.

Most striking, from its `PromptBuilder`:

> *"The model only ever picks by index from the CANDIDATES list, which keeps it
> from inventing titles that are not in the library."*

That is rule 1, reached independently. Two projects converging on it separately
is about as good as design validation gets.

**Four things it does that this plan had wrong or missing**, all adopted:

1. **Asymmetric embedding prefixes** (`EmbeddingQueryPrefix` /
   `EmbeddingDocumentPrefix`). `bge-m3`, E5, and `nomic-embed-text` are trained
   with distinct prefixes for queries versus passages, and omitting them
   degrades retrieval **silently** — nothing errors, results are just quietly
   worse. This plan had no notion of it. See §5.1.
2. **Patching `index.html` directly** as the no-dependency injection path —
   backed up, idempotent, removed on plugin stop. See §7.
3. **The index refuses to load when built by a different embedding model**
   (`IsUsable(target.Model)`), which is exactly rule 9 — independently confirmed
   as a real hazard rather than a theoretical one.
4. **Retrieval scoped to `allowedIds`** so per-user visibility is enforced
   during scoring rather than filtered afterwards. Correct for multi-user
   servers and cheaper besides.

**Where this plan genuinely differs** — these are the reasons to build Concierge
rather than file issues against that project:

| | ai-search | Concierge |
|---|---|---|
| Document source | raw overview + genres + cast | **+ enrichment / generated `asks`** (§5.2) |
| Lexical retrieval | none — pure vector | **BM25 + RRF** (§4.4-4.5) |
| Free path for short queries | none; every search calls a model | **router** (§4.2) |
| Structured filters from prose | none | year / runtime / watch-state (§4.3) |
| Cost governance | none | budget, cache, rate limit, profiles (§8) |
| Model configuration | one flat model | **Curator's profile system** (§3.3) |
| Dialogue / quotes | none | §6 |
| Quality measurement | none in repo | evaluation set (§10) |

The two biggest are **enrichment** and **quotes**. Pure-vector search over raw
overviews is precisely the configuration whose failure §5.2 demonstrates on John
Wick, and no Jellyfin plugin currently searches dialogue at all.

**Licensing: it is GPL-3.0.** Read it for patterns and API usage; **never copy
code** unless Concierge is itself GPL-3.0, which is a decision the owner has not
made. Same discipline Curator applies to SmartLists.

**A fair question worth answering before phase 2, not after:** given the
overlap, is contributing enrichment and hybrid retrieval upstream a better use
of effort than reimplementing them? The honest case for building separately
rests on quotes, the cost model, and the profile system — not on the parts that
already exist and work.

---

## 2. Why the current search fails

Jellyfin's search is a **substring match with ranking**. `/Search/Hints` and
`/Items?searchTerm=` match the term against name fields (and, depending on
options, a few others); relevance ordering is roughly exact → starts-with →
contains. That design is correct for what it is — instant, free, deterministic,
and it does the thing people do most, which is type four letters of a title they
already know.

It fails on three query shapes, and they are the entire reason this plugin
exists:

| Query | Why it returns nothing |
|---|---|
| *"movie about a guy who loses his memory"* | No title contains that string. Overviews aren't searched by default, and even when they are, it's still substring — "loses his memory" doesn't appear in the text that describes it. |
| *"90s sci-fi under two hours I haven't watched"* | Three constraints in prose, zero of them a substring of anything. |
| *"I'm walking here"* | The text lives in a subtitle file the search index has never read. |

The important consequence: **Concierge is not a better ranker bolted onto the
same index.** It needs its own index, over content Jellyfin does not index, plus
a way to read intent out of a sentence.

> ⚠️ **To verify against 10.11.11 before writing retrieval code.** The exact
> fields `SearchService` matches, whether `SearchTerm` on `InternalItemsQuery`
> reaches `Overview`, and what `IncludeItemTypes` filtering costs. Read the
> server source; do not trust this table. It shapes the router in §4.2, which is
> the difference between a free query and a paid one.

---

## 3. Architecture

Two halves, on completely different clocks.

```
INDEX (background, incremental, ~daily)      QUERY (foreground, per search, <2.5s)
─────────────────────────────────────        ──────────────────────────────────────
library items                                 user's sentence
   ↓ ItemDocument                                ↓ QueryRouter        ← free, pure
   ↓ hash → what changed?                        ↓ SearchPlan         ← 1 cheap call
   ↓ embed changed docs only                     ↓ BM25 + vector + filters ← free
   ↓ vectors + BM25 postings                     ↓ RankFusion         ← free, pure
subtitle cues → windows → same                   ↓ Reranker           ← 1 call, top-N
                                                 ↓ item GUIDs → DTOs
```

The **Core/Services split from Curator is carried over unchanged** and matters
more here, because almost everything interesting in retrieval is decidable
without a server: BM25 scoring, vector similarity, rank fusion, query routing,
filter application, index staleness. All of it belongs in `Core/` as pure
functions with tests. `Services/` does exactly three things Core cannot: talk to
Jellyfin, talk to the network, and touch the disk.

### 3.1 Project layout

```text
Jellyfin.Plugin.Concierge/
├── Core/                          # Pure. No Jellyfin services, no IO, fully tested.
│   ├── Documents/
│   │   ├── ItemDocument.cs        # BaseItem projection → the indexed text
│   │   ├── DocumentHash.cs        # staleness key; what makes rebuilds free
│   │   ├── FieldWeights.cs        # title vs. cast vs. overview, one place
│   │   ├── EnrichmentPromptBuilder.cs  # "how would someone half-remember this?"
│   │   ├── EnrichmentParser.cs    # must accept "I don't know this one"
│   │   └── Enrichment.cs          # premise, moments, themes, asks, spoiler flag
│   ├── Retrieval/
│   │   ├── Bm25Index.cs           # postings + scoring, in-memory, no deps
│   │   ├── Tokenizer.cs           # fold case/diacritics, split, stem-lite
│   │   ├── VectorIndex.cs         # cosine over a packed float array
│   │   ├── Quantization.cs        # float32 ↔ int8, for the subtitle index
│   │   └── RankFusion.cs          # reciprocal rank fusion of N ranked lists
│   ├── Query/
│   │   ├── QueryRouter.cs         # is this even a natural-language query?
│   │   ├── SearchPlan.cs          # the parsed intent (filters + semantic text)
│   │   ├── PlanPromptBuilder.cs
│   │   ├── PlanParser.cs
│   │   └── FilterApplication.cs   # plan filters → candidate predicate, fail-open
│   ├── Ranking/
│   │   ├── RerankPromptBuilder.cs
│   │   ├── RerankParser.cs        # preference over the shortlist, never a replacement
│   │   └── ResultExplanation.cs
│   ├── Subtitles/
│   │   ├── SrtParser.cs           # SRT → cues (Jellyfin converts everything to SRT)
│   │   ├── CueCleaner.cs          # strip SDH, formatting, speaker prefixes
│   │   ├── TrackSelector.cs       # MediaStream[] → the one track to index
│   │   ├── CueWindowing.cs        # cues → overlapping searchable windows
│   │   └── QuoteMatch.cs          # window hit → item + timestamp + context
│   ├── Llm/                       # ported from Curator: Batcher, JsonResponse,
│   │   ├── ModelProfiles.cs       #   chat profile list: normalize + resolve
│   │   └── EmbeddingProfiles.cs   #   the parallel list for embedding profiles
│   └── Budget/
│       ├── SpendLedger.cs         # pure arithmetic over recorded calls
│       └── BudgetDecision.cs      # allow / degrade / refuse — pure
├── Services/                      # Jellyfin, network, disk.
│   ├── SearchService.cs           # the end-to-end query; every entry point calls it
│   ├── Indexing/
│   │   ├── IndexBuildTask.cs      # IScheduledTask, daily
│   │   ├── ItemIndexer.cs         # scan → documents → embeddings → store
│   │   ├── SubtitleIndexer.cs     # phase 3
│   │   └── IIndexStore.cs / IndexStore.cs
│   ├── Llm/                       # ported: ILlmProvider + Anthropic/Google/
│   │                              #   OpenAI/Grok/compatible, TransientHttpRetry,
│   │                              #   ILlmProviderFactory
│   ├── Embeddings/
│   │   ├── IEmbeddingProvider.cs  # separate from ILlmProvider — different shape
│   │   ├── OpenAiCompatibleEmbeddings.cs   # covers OpenAI, Ollama, LM Studio, vLLM
│   │   ├── GoogleEmbeddings.cs
│   │   └── VoyageEmbeddings.cs
│   ├── Library/LibraryScanner.cs  # ported and trimmed
│   ├── Cache/QueryCache.cs        # normalized query → results, invalidated by index gen
│   └── Runs/                      # ported: per-query log, one file, capped
├── Api/ConciergeController.cs     # /Concierge/Search, /Status, /Reindex
├── Web/concierge.js               # injected into the client via File Transformation
└── Configuration/                 # PluginConfiguration + configPage.html
```

### 3.2 Why not the obvious simpler thing

**"Just send the whole library to the model with the question."** This is what
Curator does, and for a 300-item library it would genuinely work — 300 items at
~120 tokens each is ~36k tokens, cached, and the model is very good at this.
Rejected as the *architecture*, kept as a fallback, for four reasons:

1. It costs a full-library call per search. Curator spends that weekly;
   a search box spends it per keystroke-burst.
2. Latency. 36k tokens of prefill plus generation is 3-8 seconds. A search box
   has about 2.5 seconds before it feels broken.
3. It does not scale past the owner's library. A 10k-item library is 1.2M
   tokens and simply cannot be done this way.
4. It cannot do quotes at all — subtitles are 100× the volume of metadata.

The hybrid pipeline costs one small call and one medium call, is bounded in the
library's size, and degrades to *free* when the budget is gone. But **build the
whole-library fallback anyway** (§9, phase 1): it is ~50 lines, it is the
quality ceiling to measure the real pipeline against, and on a small library
with a cheap model it may honestly be the better answer.

### 3.3 The model and provider system — Curator's, unchanged

This is settled, not a design question. Curator's profile system is ported whole
and its rules carry with it. Concierge has *more* passes than Curator, not
fewer, so the reasons that system exists apply harder here.

**`ModelProfile` is the unit of "how to call a model."** One profile carries
provider, model id, API key, base URL, **its own prices**, and whether it
thinks:

```csharp
sealed class ModelProfile
{
    string  Id;                       // stable GUID — what passes reference
    string  Name;                     // what the config page shows
    LlmProvider Provider;             // Anthropic | Google | OpenAi | Grok | OpenAiCompatible
    string  Model;
    string  ApiKey;
    string  BaseUrl;
    decimal InputCostPerMillion;
    decimal OutputCostPerMillion;
    decimal CachedInputCostPerMillion; // blank → half of this profile's input price
    bool    Thinking;
}
```

Pricing lives **on the profile**, not on the configuration, for the reason
Curator found: a list you switch between turns "remember to change the prices
when you change provider" from an occasional mistake into the normal case. Rule
10 says the cost line must be right, and a shared price block cannot be. The
cached rate falls back to half of *its own* input rate — never another
profile's.

`Core/Llm/ModelProfiles` normalizes the list on every read: assigns ids, fills
defaults, and folds pre-profile scalars into one profile the first time it sees
an empty list.

**The pass assignments.** Concierge has three, where Curator has three:

| Setting | Pass | Wants |
|---|---|---|
| `PlanModelProfileId` | sentence → `SearchPlan` | small, fast, structured-output capable |
| `RerankModelProfileId` | order the shortlist + explain | mid-tier; this is where quality shows |
| `EmbeddingProfileId` | documents and queries → vectors | an embedding model (separate list, below) |

Blank means *follow the default profile* on every one of them — blank is a real
value, not unset, and an install that has configured nothing must behave
sensibly. Same convention as Curator's config page.

**Three things this breaks if done casually** — all three are Curator's rule 17,
and all three are cheaper to obey than to rediscover:

- **Resolve every pass from one `ModelProfiles.Normalize` result**, via the
  `Resolve(NormalizedProfiles, id)` overload. Normalizing per resolve is not
  idempotent on a pre-profile-list install: the migrated profile is synthesized
  afresh each call *with a new id*, so two resolves of one profile compare as
  two — by reference and by id — and the query builds two identical providers
  and reports itself as mixed.
- **Sum the query total from per-call costs**, never recompute it from aggregate
  token totals. No single rate can price a two-model query. The run-log call
  record takes optional per-pass pricing, falling back to the query's.
- **Attribute output to the model that produced it.** The run log names every
  pass's model; one model reported for a two-model query is how a bug report
  gets read wrongly.

**Legacy scalars are not dead code.** If any pre-profile-list scalars ever ship
(`Provider`, `Model`, `ApiKey`, `BaseUrl`, the `*CostPerMillion` set), they must
not be deleted later: `XmlSerializer` silently drops elements it has no property
for, so removing them throws away the API key of every install that upgrades
before it next opens the config page. Migration runs exactly once — only on an
empty list — and the config page blanks the scalars on the next save.
Concierge starts life with the profile list already in place, so the *right*
move is to never ship the scalars at all; if that holds, this paragraph is a
warning about a trap we designed around rather than a rule we have to obey.

**Embeddings get a parallel list, not the same one.** `EmbeddingProfile` follows
identical discipline — stable id, name, provider, model, key, base URL,
normalize-on-read, referenced by id — but carries `Dimensions` and a single
input price, and carries no output price, no thinking, no cached rate. Reusing
`ModelProfile` would mean four fields that are meaningless on every embedding
profile and a `Thinking` checkbox on a thing that cannot think. Two types, one
pattern, and `Core/Llm/EmbeddingProfiles.Normalize` is a near-mirror of its
sibling.

`OpenAiCompatible` is what makes the local option free: Ollama, LM Studio, and
vLLM all serve `/v1/embeddings`, so a local embedding model is a profile with a
base URL and no new code.

**`ILlmProviderFactory` and `IEmbeddingProviderFactory` are interfaces, and
orchestration takes the interfaces.** Not the concrete factories. That seam is
the only thing that makes the pipeline testable end to end against canned
responses (rule 5) — with a concrete type there is nothing to substitute and
only whatever pure logic can be lifted out gets tested. Both factories resolve
`Thinking` internally so no call site can bypass it.

---

## 4. The query pipeline in detail

### 4.1 Normalize and cache

Lowercase, collapse whitespace, strip trailing punctuation. Cache key is
`(normalizedQuery, userId, indexGeneration)`. `indexGeneration` bumps on every
index write, so a re-index invalidates every cached answer without a sweep.
`userId` is in the key because watch-state filters (*"that I haven't seen"*)
make results per-user.

Cache hits must be free and instant, and the cache is where repeated searches
(the same person retyping the same thing) stop costing money. Bounded LRU,
persisted, small.

### 4.2 Route — the single most important cost decision

`Core/Query/QueryRouter` is pure and decides, without spending anything, whether
a query needs Concierge at all.

Most searches are not natural language. They are `bla`, `blade`, `blade run` —
somebody typing a title they already know. Every one of those handed to an LLM
is money burnt to produce a worse answer than substring matching gives for free.

The router sends a query to the **native path** when any of:

- It is short (≤ 2 tokens) and contains no function words.
- It is a prefix of an indexed title, person, or studio name — checked against
  the BM25 dictionary, which is free.
- Lexical retrieval already returns a hit with a dominant score (a clear
  winner, not a flat distribution).

It sends to the **Concierge path** when the query looks like language: ≥ 4
tokens, or contains function words (*who, where, about, with, like, that, from,
under, without*), or contains a temporal/numeric constraint, or is quoted (which
means *quote search*, phase 3).

Everything ambiguous goes to **both**, concurrently, with native results
rendering immediately and Concierge results merging in when they arrive. This is
rule 2 made concrete: the fast path never waits for the slow one.

Get this wrong toward "always Concierge" and the plugin is expensive and
sluggish. Get it wrong toward "rarely Concierge" and the plugin appears not to
work. It is pure logic, so **pin it with a table of ~60 real queries** — write
that table before writing the router.

### 4.3 Plan — one cheap call

The LLM's *first* job is not to find things. It is to read the sentence:

```jsonc
{
  "semantic": "amnesiac man investigating his wife's murder using tattoos and photographs",
  "filters": {
    "types":    ["Movie"],
    "years":    [1990, 1999],
    "genres":   [],
    "people":   [],
    "runtimeMaxMinutes": null,
    "watchState": "any"          // any | unwatched | watched | favorite
  },
  "quote": null                   // set when the user is quoting dialogue
}
```

Notes that matter:

- **The plan is a hypothesis, not a command.** Every filter is applied as a
  *ranking boost first* and a hard cut only when it leaves enough candidates
  (§4.5). A model that decides *"90s"* means `[1990,1999]` and excludes a 1989
  film the user meant is worse than no filter at all.
- Structured output where the provider supports it (`ResponseShape.SearchPlan`).
  Carry Curator's hard-won rule: **schema and prompt must request exactly the
  same fields**, in both provider dialects, or the model writes a missing field
  into the previous string.
- This runs on its own model profile (`PlanModelProfileId`), and it should be a
  small fast model. This is the pass where Haiku/Flash-tier is not a compromise
  but the right answer.
- **It is skippable.** If the router saw no constraint-like language, go
  straight to retrieval with the raw query as `semantic` and no filters. Saves
  the call and ~400ms on the most common Concierge query.

### 4.4 Retrieve — free, parallel, three ways

**Lexical (BM25).** ~250 lines of pure C#, no dependency. Indexes the item
document with per-field weighting: title ×4, original title ×3, people ×2,
genres/tags/studio ×1.5, overview ×1. Catches everything semantic search is bad
at — proper nouns, rare names, exact titles, actor names. Non-negotiable: a pure
vector system fails embarrassingly on *"the one with Toni Collette"*.

**Vector.** Cosine similarity of the query embedding against a packed
`float[itemCount * dims]`. Brute force. For a 10k-item library at 1536 dims
that is 61MB and one pass is ~15ms — an ANN index (HNSW) is a dependency and a
correctness risk bought to solve a problem this plugin does not have. Revisit
only if someone shows up with a 200k-item library.

**Filters.** Applied over the candidate set from `Core/Query/FilterApplication`.

All three run concurrently. Retrieval is free and stays free — no model is
called in this step, ever. The enrichment pass (§5.2) is what it retrieves
*over*; its cost was paid once at index time.

**Collapse before fusing.** An item owns several vector rows — its document and
each generated `ask` — so the vector search returns rows, not items. Reduce to
one hit per item, taking its best-scoring row, *before* handing anything to
fusion. Skip this and one thoroughly enriched film takes eight of the top ten
slots.

### 4.5 Fuse

`Core/Retrieval/RankFusion` — reciprocal rank fusion, `score = Σ 1/(k + rank)`
with k=60. Chosen over weighted score blending because BM25 scores and cosine
similarities are not on comparable scales and any weighting between them is a
magic number that will be wrong for the next library. RRF uses only ranks, has
one parameter, and is hard to make badly wrong.

Filter handling, in order:
1. Score the union of lexical and vector candidates.
2. Apply hard filters. If ≥ 12 candidates survive → keep the cut.
3. If fewer survive, **discard the cut and demote instead** — the filtered-out
   items drop in rank but remain reachable. Fail open, always. An empty result
   page is the failure mode that makes people stop using a search box.

Take the top 40 into re-ranking.

### 4.6 Re-rank — one call, indexes only

The shortlist goes to the model as batch-local integer indexes with their
documents. **The model never sees a Jellyfin GUID** (rule 1, carried from
Curator verbatim, and it is what makes it structurally impossible to return an
item the user doesn't own).

It returns an ordering plus one short sentence per item saying *why* — which is
the feature, not decoration. "Matches: amnesia, revenge, non-linear structure"
under a poster is what makes a semantic result trustworthy instead of spooky.

`RerankParser` treats the answer as a **preference over the shortlist**:
anything the model omits, repeats, or invents leaves the fused order in place
for those items. Dropping an index here would silently delete a correct result
from somebody's search. Curator learned this on its recommendation parser; there
is no reason to relearn it.

Re-ranking is skipped entirely when the budget says so, and the fused order is
served. Slightly worse, still good, free.

---

## 5. The index

### 5.1 Item documents

One per movie/series/episode (episodes optional — they multiply the index by
~15× and mostly duplicate the series' semantics; default off).

```
title · original title · year · type · genres · tags · studios ·
top 8 cast + directors/writers · official rating · runtime · overview
```

Overview is the whole thing, not Curator's 300-character cut — truncating before
embedding stores a compression of the first paragraph forever with nothing
downstream able to tell. (Curator hit exactly this with condensed summaries.)

**Asymmetric prefixes are not optional.** `bge-m3`, the E5 family, and
`nomic-embed-text` are trained with a marker distinguishing a query from a
passage — typically `query: ` and `passage: ` — and using the wrong one, or
neither, degrades retrieval **with no error and no symptom** beyond results
being quietly worse. `EmbeddingProfile` therefore carries `QueryPrefix` and
`DocumentPrefix`, defaulted per known model and overridable, and the pair is
recorded in `state.json` alongside the model: changing a prefix invalidates the
index exactly as changing the model does, because every stored vector was
written under the old one. Adopted from `jellyfin-plugin-ai-search`, which
carries both settings (§1.1).

### 5.2 Enrichment — the pass that makes plot recall actually work

**This is the most important section in the plan and it was missing from the
first draft.**

Measured on the owner's library (31 Jul 2026): all 213 films have an overview,
median ~252 characters / ~50 words. So the raw material exists. The problem is
what it *says*.

Take the canonical query — *"that movie where the guy is the subject of a live
TV show"*:

> **The Truman Show (1998).** *"In a picture-perfect seaside town, an insurance
> salesman begins to realize that his entire existence may be staged and
> observed by a vast unseen audience as part of a long-running real-time reality
> TV show."*

That one is a slam dunk. "guy" ↔ "insurance salesman", "subject of" ↔ "staged
and observed by a vast unseen audience", "live TV show" ↔ "real-time reality TV
show". No shared keywords beyond *TV* and *show*, which is precisely why the
vector half exists — BM25 alone would rank Groundhog Day ("a cynical TV
weatherman") right alongside it.

Now the failure, from the same library:

> **John Wick (2014).** *"Ex-hitman John Wick comes out of retirement to track
> down the gangsters that took everything from him."*

*"the one where they kill the guy's dog"* — the single most memorable thing
about that film — **misses completely**. Nothing in that sentence is in the
overview, semantically or lexically.

The pattern generalizes: **overviews describe the premise; people remember
moments, images, and specifics.** Oppenheimer's overview is a Wikipedia stub
about the Manhattan Project. Ready Player One's doesn't mention the 80s. This
isn't bad metadata — it's what marketing copy is *for*, and it's spoiler-averse
on purpose.

**The fix is one LLM pass at index time, and it is cheap because it happens
once.** Same shape as Curator's summary distillation — the mirror image of it,
in fact. Curator *condenses* overviews to get at tone; Concierge *expands* them
to get at searchability.

For each item, one call produces:

```jsonc
{
  "premise":  "…",                    // what the overview should have said
  "moments":  ["…"],                  // the images people actually remember
  "themes":   ["identity", "surveillance", "…"],
  "asks":     [                       // ← the important one
    "the movie where a guy's whole life is secretly a TV show",
    "the one where he sails to the edge of the world and hits a wall",
    "film about a man who doesn't know he's on camera"
  ],
  "spoiler":  true                    // does any of this give away the ending?
}
```

`asks` is doing the heavy lifting, and it's the technique that makes this work:
**generate the queries, not just the document.** Ask the model *"how would
someone who half-remembers this film describe it to a friend?"*, store 6-10 such
phrasings, embed each one, and index them pointing back at the item. Now the
user's fuzzy sentence is being matched against *other fuzzy sentences about the
same film* rather than against marketing copy — which is a far easier matching
problem, and it is how *"the one where they kill the guy's dog"* finds John Wick.

This is a known IR technique (doc2query / query generation); it is unusually
well-suited here because the corpus is small, static, and famous.

**Why this is affordable:** 213 films × one call ≈ **$0.05-0.20 total, once**,
on a cheap model. Incremental after that — a new film costs one call. It adds
~9 vectors per item instead of 1, which for the owner's library is ~1,900
vectors, i.e. nothing. Compare that to paying for retrieval quality on every
single query forever.

**Three things this must get right:**

- **Spoilers are indexed but never displayed.** *"the one where Bruce Willis is
  dead the whole time"* has to work, so the twist must be in the index. It must
  not appear in the result card. Store enrichment fields separately from
  display fields, flag them, and render only what's safe.
- **The model's world knowledge is the point, and it's also the risk.** This
  pass works because the model has seen these films. For an obscure or brand-new
  title it will have nothing, and a model with nothing to say **invents**. The
  prompt must permit "I don't know this one" and the parser must accept an empty
  enrichment rather than storing a hallucinated plot. An invented `asks` entry is
  worse than none: it is a permanent wrong answer sitting in the index.
- **Enrichment is a cache, exactly like Curator's summaries.** Stored beside the
  index, never written back to the library, and deleting it degrades search
  without damaging anything (rule 6).

**One reframing this makes obvious, and it should shape the whole pipeline:**

> Retrieval's job is **recall** — get the right item into the shortlist.
> Re-ranking's job is **precision** — and the re-ranker already knows these
> films.

A model looking at 40 candidates knows perfectly well which one is the dog
movie. It doesn't need the overview to say so. So every failure of the kind
above is a *recall* failure, not a reasoning failure, and enrichment exists
purely to fix recall. That is also why §10's evaluation set must measure
**recall@40 separately from recall@1** — they fail for different reasons and
have different fixes.

### 5.3 Staleness

`DocumentHash` is a hash of the rendered document text. An item whose hash is
unchanged is never re-embedded, and enrichment is keyed the same way. This is
what makes a nightly rebuild cost approximately nothing and a metadata refresh
cost only the items that actually changed. Same pattern as Curator's
`SummaryPlan`, same reason — and the same trap: hash the *source* text, so a
metadata refresh can never leave an enrichment describing the wrong film.

### 5.4 Storage

`data/concierge/`:

| File | Contents |
|---|---|
| `vectors.bin` | packed float32 (or int8, subtitles), row-major |
| `docs.json` | row → `{ itemId, hash, fields, vectorRows[] }` |
| `enrichment.json` | per item: premise, moments, themes, `asks`, spoiler flag |
| `lexical.json` | BM25 postings + doc lengths |
| `state.json` | index generation, model + dims used, last build |
| `runs/*.json` | one file per query: plan, candidates, prompts, cost |

One item now owns **several** vector rows — its document plus each `ask` — so
`docs.json` maps item → rows and retrieval collapses multiple hits on one item
down to its best-scoring row before fusion. Forgetting to collapse would let a
well-enriched film occupy eight of the top ten slots.

Atomic temp-file-then-rename on every write. **The embedding model and its
dimensionality are recorded in `state.json`, and changing either invalidates the
whole index** — vectors from two models are not comparable and mixing them
produces silently garbage rankings, which is the worst failure mode available
here because nothing errors.

### 5.5 Cost of building it

| | Owner's library (213 films) | 10k-item library |
|---|---|---|
| Embedding documents + `asks` | ~$0.005 | ~$0.25 |
| **Enrichment pass** (1 cheap call/item) | **~$0.05-0.20** | ~$3-10 |
| **Total, once** | **under $0.25** | under $10 |

Incremental thereafter — a newly added film costs one call and nine embeddings.

Embedding is not where the money goes. Enrichment is a real but one-time cost,
and it buys recall on **every future query**, which is the trade this whole
design is built around: pay once at index time so the per-query path can stay
small and cheap.

---

## 6. Quotes (phase 3)

The headline feature, and the one with a real engineering cost. Everything in
this section is measured against the owner's library or verified against the
10.11.11 assembly — the numbers are real, not estimates.

### 6.1 Where the text comes from

**Verified.** `MediaBrowser.Controller.MediaEncoding.ISubtitleEncoder` is in the
`Jellyfin.Controller` package Concierge already references. **No new
dependency, no shelling out to ffmpeg ourselves:**

```csharp
Task<Stream> GetSubtitles(
    BaseItem item, string mediaSourceId, int subtitleStreamIndex,
    string outputFormat,            // ← always "srt"
    long startTimeTicks, long endTimeTicks,
    bool preserveOriginalTimestamps, CancellationToken ct);

Task ExtractAllExtractableSubtitles(MediaSourceInfo source, CancellationToken ct);
```

**Always request `"srt"`.** Jellyfin converts ASS/SSA, `mov_text`, WebVTT and
external files into it for us, so Concierge parses exactly one format forever.
This deletes most of what `Core/Subtitles/CueParser` was going to be — the
multi-format parser in §3.1 is now an SRT parser and a text cleaner.

Track selection comes off `IMediaSourceManager.GetMediaStreams(Guid itemId)`,
which reads the database and touches no disk. `MediaStream` carries exactly the
predicates needed — all verified present: `IsTextSubtitleStream`,
`IsExtractableSubtitleStream`, `IsExternal`, `IsForced`, `IsHearingImpaired`,
`Language`, `Codec`, `Index`, `Path`.

Preference order for choosing one track per item:

1. `IsTextSubtitleStream` — non-negotiable, image subs can't be read.
2. Language match (default `en`).
3. **Reject `IsForced`.** A forced track only carries foreign-language lines —
   a few dozen cues for a whole film. Indexing one looks like success and
   produces a film that can never be found by anything anyone actually says.
4. Prefer not `IsHearingImpaired`, but **take SDH if it's the only text track**
   and strip its annotations (§6.3). SDH is better than nothing by a wide margin.
5. External over embedded, only because it skips extraction entirely.

### 6.2 Coverage — measured on the owner's library

Counted from `jellyfin.db`, 31 Jul 2026:

| | Movies (213) | Episodes (5,276) |
|---|---|---|
| **Text subtitles, English** | **140 (66%)** | **3,882 (74%)** |
| Image-only (PGS/VobSub) | 60 (28%) | 1,046 (20%) |
| No subtitles at all | 8 (4%) | 289 (5%) |

**This clears the viability bar** (open question 3 asked for ~60%; the answer is
66% and 74%). Quote search is worth building.

The 28% image-only gap is real but not our problem to solve with OCR — that's a
dependency, a GPU, and an error rate. The cheap fix is external subtitle
download (OpenSubtitles, which Jellyfin already does), which converts image-only
items into text ones for free. **Concierge's job is to make the gap visible**:
the Quotes tab reports coverage per library and names the items it cannot index,
so the owner can fix the 60 films if they care to.

Note also: only 29 external `.srt` files exist on disk for 225 movie files.
Essentially all of this text is **embedded**, so extraction is the main cost,
not parsing.

### 6.3 Cleaning

Subtitle text is not prose, and indexing it raw poisons the results:

- HTML/ASS formatting: `<i>`, `{\an8}`, `{\pos(...)}`.
- SDH annotations: `[door creaks]`, `(SIRENS WAILING)`, `♪ music ♪` — these are
  descriptions, not dialogue, and they match vibe queries wrongly and loudly.
- Speaker prefixes: leading `- `, and `VINCENT:` in caps.
- Duplicate consecutive cues (common in rips), and credits/subtitle-author
  spam at the head and tail of the file (`Subtitles by …`, `OpenSubtitles`).

Strip all of it. Keep the raw text alongside the cleaned text for display —
users should see the line as it appeared, and search the line as it means.

### 6.4 Volume — the real constraint is memory, not money

Movies average ~1,300 cues; episodes ~500. At 66%/74% coverage that is roughly
**182k cues across films and 1.94M across episodes** — 2.1M cues, ~850k windows
after merging into ~40-word windows at 50% overlap.

Embedding all of it costs about **$0.94** once, and films alone about **$0.08**.
Cost is not the constraint here and the earlier draft was wrong to imply it was.
Storage is:

| Scope | float32 ×1536 | int8 ×1536 | int8 ×512 |
|---|---|---|---|
| Films only (73k windows) | 448 MB | 112 MB | **37 MB** |
| Everything (850k windows) | 5.2 GB | 1.3 GB | 437 MB |

**Films at 512-dim int8 fit comfortably; the full library does not.** That
settles two defaults: quote vectors are films-first, and episodes are opt-in per
series rather than per library — nobody needs 780k vectors of a sitcom to find
one line of it.

### 6.5 Retrieval, in three stages, and the order is the whole design

**Stage 1 — phrase search. No embeddings, no model, no cost.**
People quote *verbatim*: "I'm walking here", "You can't handle the truth". Exact
and near-exact phrase matching answers those perfectly, and it is the cheapest
thing in the entire plugin. It should ship long before anything semantic, and it
may well be enough.

The design fork worth deciding early is what backs the index:

- **SQLite FTS5** — disk-backed, native phrase queries (`"i'm walking here"`),
  BM25 built in, ~40% of source size on disk, no RAM ceiling, and it handles
  2.1M cues without complaint. It is a **dependency** (rule 13 says ask), but
  Playback Reporting ships SQLite into this very server, so the pattern is
  established and the risk is low. *This is my recommendation.*
- **Hand-rolled positional index** — no dependency, consistent with the item
  index, and fine for the 73k film windows. It gets uncomfortable at 850k and
  it means writing phrase matching, tokenization, and on-disk postings by hand.

Films-only makes the hand-rolled option viable. Wanting episodes makes FTS5 the
honest answer.

**Stage 2 — fuzzy, for misremembered quotes.** People quote wrong: *"Luke, I am
your father"* is not a line in any Star Wars film. Character-trigram similarity
over the phrase index catches near-misses for free, before spending anything.

**Stage 3 — semantic, for genuine paraphrase.** *"the one where he says he could
have been a contender"* needs embeddings, at films-only/512-int8 (§6.4).

**And one trick that outperforms all of stage 3 for pennies:** when the query is
a *description* of a line rather than the line itself, have the plan pass
**generate two or three literal phrasings** of what the character probably said,
and run those through stage 1. *"the one where the guy says he's not smart but
he knows what love is"* → `"I'm not a smart man"` → exact hit. This is HyDE
applied to dialogue, it costs one small call already in the pipeline, and it
turns paraphrase into a phrase-search problem instead of a vector problem.

### 6.6 Extraction is the expensive part — treat it as such

`GetSubtitles` on an **embedded** stream shells out to ffmpeg internally, takes
seconds to a minute per file, and writes into Jellyfin's subtitle cache. Across
~4,000 items that is hours of CPU and gigabytes of cache. Therefore:

- Extraction is a **throttled background task with a persistent cursor**, never
  part of a query, never part of the item index build. Concurrency 1-2. It must
  survive restart and resume, because it will be interrupted (installing any
  plugin tears the host down mid-task — Curator learned this expensively).
- **Staleness key is (stream index, path, file size, mtime)**, so a re-index is
  free and a re-encoded file re-extracts.
- The **Subtitle Extract plugin is already installed on this server but idle**
  (`ExtractionDuringLibraryScan=false`, no libraries selected). Enabling it
  front-loads the whole job outside Concierge. If it has run, external `.srt`
  files exist and Concierge's work drops to parsing. Detect and prefer that.
- Do the **films first, always.** 140 items is a job that finishes in minutes and
  makes the feature demonstrable; 3,882 episodes is an overnight job.

### 6.7 Result shape

A quote hit returns the item, the matched line, the surrounding two or three
lines as context, and the timestamp — and the client deep-links to playback at
that position minus 5 seconds.

One honest caveat to surface in the UI: **external subtitle files are sometimes
out of sync** with the file they sit beside. Embedded tracks are reliable;
externals can be seconds off, and a deep link to the wrong moment is the most
visible way this feature can fail. Prefer embedded timestamps when both exist.

### 6.8 Scripts

Screenplays are a different source with a different trade, and they are **not
phase 3**.

Against: they are not time-aligned, so they cannot deep-link without extra work;
coverage is poor and skewed to famous films; and the sources are legally grey in
a way subtitles sitting in the user's own files are not.

For: a script contains what subtitles structurally cannot — **scene
descriptions, action, and speaker attribution**. *"the scene where they walk
through the kitchen in one shot"* is in a script and in no subtitle file.

If it is ever wanted, the tractable version is: align script dialogue to the
subtitle cues by longest-common-subsequence over normalized lines, which
recovers both timestamps *and* speaker names for the script's dialogue. That
would let Concierge answer *"where does Vincent say…"* — speaker attribution is
the real prize, and alignment is how you get it without trusting the script's
own timing.

Cheaper interim: SDH tracks sometimes carry `VINCENT:` prefixes, and the
re-ranker can infer the speaker from context for the handful of results a user
actually sees. Do that before touching scripts.

---

## 7. Delivery surfaces

**1. The API.** `POST /Concierge/Search { query, limit }` → ranked items with
explanations. User-authenticated (not admin-only — every user searches). This is
the real interface; everything else is a client of it. Ship it first and it's
usable from a bookmark or a script before any UI exists.

**2. The web client.** Jellyfin plugins cannot replace the search page from the
server side, so this goes through **File Transformation** (already installed on
the owner's server; it's how Jellyfin Enhanced does it) — register a
transformation on `index.html` that injects `Web/concierge.js`.

The script hooks the search view and:
- lets native results render untouched and immediately;
- fires Concierge on submit/Enter or after a ~600ms idle on a query the router
  likes — **never per keystroke**;
- renders Concierge results in their own labelled section above/below with the
  match explanations, and a spinner that never blocks the native list.

File Transformation is a **soft dependency**: detect it by assembly probe, and
if it's missing, log one clear line and carry on. The API still works. Never
throw out of injection. (Curator's integrations follow this rule and it has paid
for itself.)

**Fallback when File Transformation is absent: patch `index.html` directly**,
the way `jellyfin-plugin-ai-search` does — write a marked `<script>` block into
the web root at startup, back the original up, make it idempotent, and remove it
on plugin stop. It needs no dependency and it is genuinely simpler. The costs
are real though: the web root is read-only in some container setups, a server
update overwrites it, and recreating the container reverts it. So it is the
fallback, not the default — and it must degrade to "the API still works" when
the write fails, which is exactly how that project handles it.

> ⚠️ **To verify.** File Transformation's exact registration payload
> (`FilenamePattern` + callback assembly/class/method, per its controller), and
> whether the injected script survives a client update. Read the plugin's own
> source; the DLL on the server confirms the shape but not the contract.

**3. The config page.** Tabs, following Curator's conventions exactly (no ES6
template literals, `class="emby-input"`, JSON bodies in `data` not `content`,
escape everything interpolated):

- **Model** — the profile list and the per-pass pickers (plan / rerank), built
  exactly like Curator's: an in-memory `modelProfiles` + `activeProfileId`, an
  editor showing one profile at a time, and `captureProfileEditor()` called
  before *anything* that changes the selection or the outgoing profile's edits
  are lost. `normalizeProfiles()` is a hand-mirror of
  `Core/Llm/ModelProfiles.Normalize` — change one, change both. Every picker
  that lists profiles is rebuilt by `renderProfiles()`; miss one and a rename
  two rows up leaves a picker showing a name the profile no longer has, silently
  saving the wrong id. Option order in the provider `<select>` is load-bearing
  (stored numeric enums fall back to index matching): change labels freely,
  never reorder.
- **Embeddings** — the parallel `EmbeddingProfile` list with the same editor
  discipline, plus dimensions and base URL. Ollama lives here. Changing the
  model or dimensions warns that it invalidates the index (rule 9).
- **Index** — what's indexed, episodes on/off, build now, index status.
- **Search** — router thresholds, result count, re-rank on/off.
- **Budget** — monthly cap, per-user rate limit, what happens at the cap.
- **Quotes** — subtitle indexing, language, vector mode.
- **Queries** — the run log: what was asked, what was planned, what it cost.

---

## 8. Cost and latency governance

This is the design constraint that separates Concierge from Curator. Curator
spends money on a schedule the owner controls. Concierge spends it when someone
types — which is unbounded, unpredictable, and can be triggered by a stranger
with an account.

**Per-query budget, measured in advance:**

| Step | Tokens (est.) | Cheap model | Mid model |
|---|---|---|---|
| Plan | 600 in / 120 out | ~$0.0003 | ~$0.002 |
| Rerank (40 items) | 3,500 in / 600 out | ~$0.0015 | ~$0.012 |
| Embed query | 20 in | ~$0.0000004 | — |
| **Total** | | **~$0.002** | **~$0.014** |

At 50 searches/day that's $3/month cheap, $21/month mid. **Both passes default
to the cheap profile**, and the config page must show the projected monthly
number next to the model picker, because that is the number people actually
decide on.

**Controls, all of which degrade rather than fail:**

1. **Router** — most queries never reach a model. Biggest lever by far.
2. **Cache** — repeats are free.
3. **Monthly cap** (`MonthlyBudgetUsd`). At the cap, Concierge falls back to
   free retrieval (BM25 + vector + fusion, no plan, no re-rank). It still
   works. It never returns an error and never silently switches off.
4. **Per-user rate limit** — N paid queries per hour, per user.
5. **Kill switches** — re-rank off, plan off; both leave a working plugin.

**Latency budget**, which is a hard product constraint:

| | Target | Ceiling |
|---|---|---|
| Native results visible | 0ms (untouched) | — |
| Cached Concierge | 50ms | 200ms |
| Free path (no LLM) | 150ms | 400ms |
| Full path | 1.2s | 2.5s |

Plan and re-rank cannot be parallelized (the second needs the first), so the
full path is two sequential round-trips. If measurement puts that over 2.5s on a
cheap model, the answer is to stream: render the fused order at ~300ms and
re-order in place when the re-rank lands. Design the client for that from the
start, even if phase 2 doesn't use it.

---

## 9. Phases

Each phase ends in something installable and useful on its own.

### Phase 0 — skeleton *(the plugin exists)*
- Solution, `net9.0`, `Jellyfin.Controller`/`Model` 10.11.11, warnings as errors.
- `Plugin.cs`, GUID `361b0830-e7c9-460a-b116-0164adec76dd`, `ServiceRegistrator`,
  empty config page, `build/package.sh` + `build/release.sh`, `manifest.json`.
- Port from Curator: `ILlmProvider` + the five providers, `TransientHttpRetry`,
  `ILlmProviderFactory`, `ModelProfiles`, `JsonResponse`, run log,
  `LibraryScanner`.
- Build the profile system in full from day one (§3.3) — both lists, both
  factories, the Model tab, per-pass ids. It is the one part where retrofitting
  costs more than building it before there is anything to call.
- **Done when:** it installs on the live server, loads, shows a config page, and
  a saved profile round-trips through `Normalize` unchanged.

### Phase 1 — retrieval, no UI *(the hard part, and it's free)*
- `ItemDocument`, `DocumentHash`, `IndexStore`, `IndexBuildTask`.
- **The enrichment pass (§5.2)** — it is index-time, not query-time, so it
  belongs here despite calling a model. Build the baseline both ways: overviews
  only, then enriched. The delta between those two numbers is the single most
  useful measurement in the project.
- `IEmbeddingProvider` + OpenAI-compatible implementation (covers OpenAI,
  Ollama, LM Studio in one class).
- `Bm25Index`, `VectorIndex`, `RankFusion`, `QueryRouter`.
- `POST /Concierge/Search` returning fused results, **no LLM in the query path**.
- The whole-library-in-one-prompt fallback (§3.2), behind a flag, as the quality
  yardstick.
- **Done when:** a 40-query evaluation set (§10) runs against the owner's real
  library and the free path's numbers are written down. Everything after this is
  measured against that baseline.

### Phase 2 — language *(the plugin's actual promise)*
- `SearchPlan` + prompt/parser, `ResponseShape.SearchPlan` in both structured
  providers.
- `FilterApplication` with fail-open demotion.
- Re-rank pass + explanations, `RerankParser` as preference-not-replacement.
- `QueryCache`, `SpendLedger`, budget degradation, rate limiting.
- Web injection via File Transformation.
- **Done when:** the same 40 queries beat the phase-1 baseline, with cost and
  p95 latency recorded per query.

### Phase 3 — quotes *(the reason anyone will install it)*
- `TrackSelector`, `SrtParser`, `CueCleaner`, `CueWindowing` — all pure, all
  testable against a handful of real subtitle files committed as fixtures.
- Throttled, resumable extraction task over `ISubtitleEncoder` (§6.6).
  **Films first** — 140 items, minutes, demonstrable.
- 3a: phrase search only, no embeddings, no model. 3b: literal-phrasing
  generation in the plan pass. 3c: quantized vectors, films-only, opt-in.
- Timestamped results, deep-link to playback minus 5s.
- Coverage reporting in the Quotes tab, naming what can't be indexed.
- **Done when:** 20 known quotes from the owner's library resolve to the right
  film *and* the right minute — and phase 3a alone is measured before 3c is
  written, because it may make 3c unnecessary.

### Phase 4 — durability
- Health check (pure, shy — copy Curator's discipline: a panel that cries wolf
  gets ignored). Findings: index stale, embedding model changed under the index,
  budget exhausted, subtitle coverage collapsed.
- Per-user personalization of ordering; "more like this".
- Multi-language, `/Search/Hints` shim if it turns out to be reachable.

---

## 10. Testing and evaluation

**Unit.** xUnit, no network, ever. Every provider through a stub
`HttpMessageHandler`; the pipeline through a stub `ILlmProvider` and a stub
`IEmbeddingProvider`. Orchestration takes `ILlmProviderFactory`, never the
concrete type — that interface is the only seam that makes end-to-end pipeline
tests possible, and Curator's summary-retry tests are the proof of what it buys.

**The evaluation set is the real test, and it needs to exist before phase 1
retrieval is written.** ~40 queries against the owner's actual library, each
with a hand-labelled correct answer, in four groups:

1. **Plot recall** — "guy loses his memory, tattoos" → Memento.
2. **Vibe** — "something funny but not stupid, for a Sunday".
3. **Constraints** — "90s sci-fi under two hours I haven't seen".
4. **Should-not-be-Concierge** — "blade", "the of" — router says native.

Metrics: **recall@40, recall@5, recall@1**, MRR, cost/query, p95 latency.
Recorded in `eval/results-<phase>.md` and **committed**, so a prompt change that
improves one query and quietly breaks four is visible instead of anecdotal.

**Recall@40 is the diagnostic that tells you what to fix**, and it must be read
separately from the rest (§5.2). If the right film isn't in the top 40, the
re-ranker never sees it and no amount of prompt work will recover it — that is a
retrieval problem, and the lever is enrichment. If it *is* in the top 40 but
lands at rank 12, that is a ranking problem and the lever is the re-rank prompt.
Two different failures that look identical from the results page.

This is the thing most likely to be skipped and the thing most likely to decide
whether the plugin is good. Search quality is not assessable by vibes — every
change feels like an improvement on the query you were thinking about when you
made it.

---

## 11. Hard rules

Invariants, not preferences. Rules 1, 5, 6, 7, 10, 12 and 13 are carried from
Curator, where each was learned expensively; the rest are specific to search.

1. **The model never sees Jellyfin GUIDs.** Prompts use batch-local integer
   indexes; the parser discards anything outside `0..n-1` and maps survivors
   back. Structurally impossible to return an item the user doesn't own.
2. **Native search must never get slower or worse.** Concierge is additive. The
   native list renders on its own timeline and is never blocked, replaced, or
   delayed by anything here. If Concierge is broken, misconfigured, out of
   budget, or the model is down, the user gets exactly the search they have
   today.
3. **Retrieval is free; only planning and re-ranking cost money.** No model call
   ever goes in `Core/Retrieval`. Any feature that would put one there needs a
   deliberate decision, not a refactor.
4. **Every paid path has a free degradation, and it is never an error.** Budget
   exhausted, provider down, key wrong, rate limited — all of these serve fused
   retrieval results. A search box that returns an error message is a broken
   search box.
5. **No live LLM or embedding calls in tests.**
6. **The index is a cache and the library is read-only.** Deleting
   `data/concierge/` restores exactly the previous behaviour. Nothing is ever
   written back to items, and any UI offering to clear the index says so plainly.
7. **The re-ranker states a preference over the shortlist; it never replaces
   it.** Omitted, repeated, or invented indexes leave the fused order in place
   for those items. Never let a model delete a correct result.
8. **Filters fail open.** A structured filter that would empty the results is
   demoted to a ranking signal instead. Fewer than 12 survivors means the cut
   was wrong, not the library.
9. **The embedding model, its dimensionality, and its query/document prefixes
   are part of the index's identity.** Recorded in `state.json`; changing any of
   them invalidates the whole index. Mixed vectors don't error — they just rank
   garbage. `jellyfin-plugin-ai-search` enforces the model half of this with
   `IsUsable(model)` and refuses to load a mismatched index, which is the right
   shape: refuse, don't degrade.
10. **Log tokens and estimated cost for every query.** Cache reads are charged,
    not free; a provider reporting cached tokens inside its input count has them
    subtracted before costing, or the total understates by ~25%.
11. **The router is pure and pinned by a table of real queries.** It is the
    single biggest lever on both cost and perceived quality, and it must be
    changeable without fear.
12. **A model profile is the unit of "how to call a model", and every pass
    resolves from one `Normalize` result.** Prices live on the profile because a
    list you switch between makes a shared price block wrong by default.
    Per-pass ids may be blank, and blank means *the default profile*, not unset.
    Query totals are summed from per-call costs — no single rate can price a
    two-model query — and the run log names the model behind every pass.
13. **Ask before adding dependencies.** Target: none beyond the Jellyfin
    packages at runtime, xUnit in tests. No vector database, no ANN library, no
    tokenizer package, no ML runtime — brute force is fast enough at these sizes
    and every one of those is a support burden the owner would carry alone.
    **One open exception:** SQLite/FTS5 for the subtitle index (§6.5), which is
    a real decision with a real case behind it, not a drift.
14. **Enrichment may not invent, and enrichment is never displayed raw.** The
    index-time pass (§5.2) works because the model knows these films; for one it
    doesn't know, it must be allowed to say so and the parser must store nothing
    rather than a plausible fiction. A hallucinated `ask` is a permanent wrong
    answer that costs nothing to create and is invisible until someone searches
    for it. Separately: enrichment carries spoilers on purpose, so result cards
    render only display fields — never `moments`, never a spoiler-flagged
    premise.

---

## 12. Open questions

Ordered by how much a wrong answer costs.

0. **Build, or contribute to `jellyfin-plugin-ai-search`?** (§1.1) That project
   already implements roughly phases 1-2, is GPL-3.0, and is actively developed.
   The differentiators that survive the comparison are enrichment, hybrid
   retrieval, cost governance, the profile system, and quotes — the last of
   which nothing in the ecosystem does. Answer this before phase 2, because
   phase 2 is where the duplicated effort actually lands; phase 1's index and
   evaluation harness are worth building either way.

1. **How good is the free path, really?** If BM25 + vectors + fusion answers 80%
   of the evaluation set, phase 2's re-rank is a polish step and the whole cost
   model relaxes. If it answers 40%, the LLM passes are load-bearing and the
   budget work in §8 becomes urgent. **This is measurable in phase 1 and should
   be measured before anything in phase 2 is designed further.**
2. **Can the web client's search actually be hooked cleanly?** The injection
   mechanism is proven (Jellyfin Enhanced does it), but hooking *the search view
   specifically* — surviving client updates and Jellyfin's SPA routing — is
   unverified. If it's fragile, the fallback is a dedicated Concierge page plus a
   keyboard shortcut, which is worse but entirely under our control.
3. ~~**Do subtitles exist for enough of the library?**~~ **Answered, measured
   (§6.2): 66% of films and 74% of episodes have English text subtitles.** Above
   the viability bar. What replaces it: **does quote search index episodes at
   all?** 850k windows against 73k is the difference between a hand-rolled index
   and a SQLite FTS5 dependency (§6.5), and that decision is owed before phase 3
   starts.
4. **Episodes in the index?** They're where dialogue and specific plots actually
   live, but 15× the vectors and a results list that can drown a series in its
   own episodes. Probably: series indexed by default, episodes opt-in, and
   quote hits roll up to the series with the episode named.
5. **Multi-user privacy.** Watch-state filters make results per-user. Query logs
   record what people searched for. Decide early whether the run log is
   admin-visible per user or anonymized — retrofitting privacy is painful.
6. **Does anything in Jellyfin let us serve `/Search/Hints` directly?** If a
   plugin can supply hints, the injection layer becomes unnecessary and every
   client — including mobile apps — gets natural-language search for free. This
   would be a strictly better architecture. Worth two hours of source reading
   before phase 2 commits to injection.

---

## 13. Release

Identical to Curator's, which is proven:

```bash
export PATH="$HOME/.dotnet:$PATH"
dotnet build Jellyfin.Plugin.Concierge.sln -c Release
dotnet test  Jellyfin.Plugin.Concierge.sln -c Release
VERSION=0.1.0.0 CHANGELOG="..." ./build/release.sh
```

Then a GitHub release tagged `v<VERSION>` with **that exact zip** uploaded —
rebuilding changes the MD5 and breaks catalogue installs. Users add
`https://raw.githubusercontent.com/nitramivel/jellyfin-concierge/main/manifest.json`
as a plugin repository.

Plugin files sit at the **zip root**; the server creates
`plugins/Concierge_<version>/` itself.
