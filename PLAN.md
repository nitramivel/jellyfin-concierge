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
│   │   └── FieldWeights.cs        # title vs. cast vs. overview, one place
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
│   │   ├── CueParser.cs           # SRT/VTT/ASS → cues
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
called in this step, ever.

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

### 5.2 Staleness

`DocumentHash` is a hash of the rendered document text. An item whose hash is
unchanged is never re-embedded. This is what makes a nightly rebuild cost
approximately nothing and a metadata refresh cost only the items that actually
changed. Same pattern as Curator's `SummaryPlan`, same reason.

### 5.3 Storage

`data/concierge/`:

| File | Contents |
|---|---|
| `vectors.bin` | packed float32 (or int8, subtitles), row-major |
| `docs.json` | row → `{ itemId, hash, fields }` |
| `lexical.json` | BM25 postings + doc lengths |
| `state.json` | index generation, model + dims used, last build |
| `runs/*.json` | one file per query: plan, candidates, prompts, cost |

Atomic temp-file-then-rename on every write. **The embedding model and its
dimensionality are recorded in `state.json`, and changing either invalidates the
whole index** — vectors from two models are not comparable and mixing them
produces silently garbage rankings, which is the worst failure mode available
here because nothing errors.

### 5.4 Cost of building it

300 items × ~250 tokens = 75k tokens. On `text-embedding-3-small`
(~$0.02/1M) that is **$0.0015** for a full build, and near zero for incremental
ones. Even a 10k-item library is ~$0.05 once. Embedding is not where the money
goes; per-query LLM calls are.

---

## 6. Quotes (phase 3)

The headline feature, and the one with a real engineering cost.

**Source.** External `.srt`/`.vtt`/`.ass` beside the media, plus embedded text
subtitle streams. Read via `ISubtitleManager`/`MediaStreams` where they're text;
forced/SDH tracks are preferred *against* — they carry sound descriptions. One
language, configurable, default English.

> ⚠️ **To verify.** Whether extraction can be done without ffmpeg shelling out,
> what the Subtitle Extract plugin already leaves on disk (it's installed on the
> owner's server and may make this nearly free), and whether image-based subs
> (PGS/VobSub) are simply out of scope. Assume they are: OCR is a different
> project.

**Windowing.** Cues are 1-2 seconds and useless alone. `CueWindowing` merges
into ~40-word windows with 50% overlap, each keeping the start timestamp of its
first cue. A 2-hour film ≈ 1,500 cues ≈ 300 windows.

**Retrieval, in two stages, and the order is the point:**

- **3a — BM25 only, no embeddings.** Most quote searches are near-verbatim:
  people remember the words. Lexical search over subtitle windows nails those,
  costs nothing to build, adds no storage beyond postings, and can ship long
  before the vector half. *Do this first and it may be enough.*
- **3b — vectors, for paraphrase.** *"the one where he says he could have been
  a contender"* is a paraphrase and needs embeddings. But 300 films × 300
  windows = 90k vectors, which at 1536×float32 is **550MB** — unshippable.
  Fix: int8 scalar quantization (**138MB**) plus float32 re-scoring of the top
  500, or Matryoshka truncation to 512 dims. `Core/Retrieval/Quantization`
  exists for this. Quote-vector indexing stays **opt-in per library**.

**Result shape.** A quote hit returns the item, the timestamp, and the
surrounding lines, and the client deep-links to playback at that position minus
5 seconds. That is the moment this plugin justifies itself.

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
- `CueParser`, `CueWindowing`, subtitle discovery.
- 3a: BM25 over windows. 3b: quantized vectors, opt-in.
- Timestamped results, deep-link to playback position.
- **Done when:** 20 known quotes from the owner's library resolve to the right
  film *and* the right minute.

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

Metrics: recall@1, recall@5, MRR, cost/query, p95 latency. Recorded in
`eval/results-<phase>.md` and **committed**, so a prompt change that improves
one query and quietly breaks four is visible instead of anecdotal.

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
9. **The embedding model and dimensionality are part of the index's identity.**
   Recorded in `state.json`; changing either invalidates the whole index. Mixed
   vectors don't error — they just rank garbage.
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

---

## 12. Open questions

Ordered by how much a wrong answer costs.

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
3. **Do subtitles exist for enough of the library to make phase 3 real?** Count
   before building. Below ~60% coverage, quote search is a feature that fails
   most of the time it's tried, which is worse than not having it.
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
