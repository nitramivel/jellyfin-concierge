# Concierge — Jellyfin plugin

Natural-language search over a Jellyfin library. Reads a sentence, retrieves
against a local hybrid index (keyword + embeddings), re-ranks with a model, and
returns items with an explanation of why each matched. Later: spoken dialogue,
with timestamps.

**[`PLAN.md`](PLAN.md) is the design document and the source of truth for
architecture, phases, hard rules, and open questions. Read it before writing
code.** This file covers only how to work in the repo.

**Scope discipline:** Concierge turns a sentence into the right items. It is not
a rules engine (SmartLists), not a recommender (Curator, its sibling), and never
writes to the library. Reject feature requests that amount to "add a filter UI."

## Status

**Phases 0 and 1 are built and the index has been built for real once.**
121 tests green, warnings as errors, compiling against the real 10.11.11 ABI.

First real build, 1 Aug 2026: **263 items, 250 enriched, 10 unknown to the
model, 3 failed, 2,263 vector rows, $0.09, ten minutes** on `gpt-5.6-luna`. The
plan estimated ~$0.51 on Haiku-tier for a library this size, so the cost model
is if anything pessimistic.

**Two things that run taught us, both now fixed in 0.3.0.0:** enrichment saved
nothing until the whole pass finished, so cancelling threw away paid-for work;
and a long pass logged nothing between "starting" and "finished". Enrichment now
checkpoints every five batches and the run log records every call, its cost, and
every item that came out unenriched with a reason.

**Leave `IncludeEpisodes` off.** On, this library goes from 263 items to 5,338
and from 22 batches to 445. The model does not know individual episodes, so it
correctly declines to invent them and you pay for ~5,000 empty answers.

What exists: the plugin skeleton and both profile lists (phase 0); documents,
hashing, the enrichment pass, BM25, vectors, fusion, the router, the index
store, the daily build task and `POST /Concierge/Search` (phase 1).

**What is still unproven is search quality.** The index exists, but the
40-query evaluation set in `eval/queries.md` has no expected answers yet, so
nobody has measured recall against this library. `eval/results-phase1.md`
records what *was* measured — the retrieval stack over a fixture library with a
stand-in embedder — and is explicit that this proves the pipeline rather than
the model. **Do not treat any quality claim about this plugin as established
until that set is filled in and run.**

The first real measurement to take is the one `eval/README.md` names: the same
set with `EnableEnrichment` off, then on. That delta says whether enrichment is
carrying the feature or decorating it, and every phase-2 cost decision depends
on the answer.

**Open question 0 is still open** (`PLAN.md` §12): whether to build this at all
or contribute the differentiating parts upstream to `jellyfin-plugin-ai-search`.
Phase 0 and phase 1 are worth building either way; **phase 2 is where duplicated
effort would land**, so the answer is owed before phase 2 starts, not before the
next commit.

**Open question 5 now has code attached** — `QueryRunRecord` stores the query
text and the user id, because that is what makes a bad result diagnosable, and
it is also a log of what everyone in the house searched for. Decide whether that
log is admin-visible per user, anonymized, or dropped, before anyone but the
owner uses this.

## Development commands

The .NET 9 SDK is installed per-user and is **not on `PATH` by default**:

```bash
export PATH="$HOME/.dotnet:$PATH"     # required first, in every shell

dotnet build Jellyfin.Plugin.Concierge.sln -c Release
dotnet test  Jellyfin.Plugin.Concierge.sln -c Release   # no network, ever
./build/package.sh                                       # artifacts/Concierge_<version>/
VERSION=0.1.0.0 CHANGELOG="..." ./build/release.sh       # zip + manifest.json entry
```

Ubuntu's apt only carries SDK 8 and 10; 9 came from `dot.net/v1/dotnet-install.sh`
into `~/.dotnet`. Target framework is **net9.0** — Jellyfin 10.11.x runs on
.NET 9, *not* .NET 8. Build treats warnings as errors.

There is no local Jellyfin server on the dev machine. Verification is
`dotnet test` plus compiling against the real 10.11 ABI; anything requiring a
server is verified by the owner installing a release.

## Releasing

`build/release.sh` builds the zip (plugin files at zip **root**), computes the
MD5 Jellyfin verifies on install, and inserts the version into `manifest.json`.
Then create a GitHub release tagged `v<VERSION>` and upload **that exact zip** —
rebuilding or re-zipping changes the checksum and breaks catalogue installs.
Users add
`https://raw.githubusercontent.com/nitramivel/jellyfin-concierge/main/manifest.json`
as a plugin repository.

Plugin GUID: `361b0830-e7c9-460a-b116-0164adec76dd`

## Architecture

**The Core/Services split is the main architectural rule**, carried from
Curator. Anything decidable without a server belongs in `Core/` as a pure
function so it can be tested; `Services/` wires those decisions to Jellyfin, the
network, and the disk.

That split does more work here than it did in Curator, because almost everything
interesting in retrieval is pure: BM25 scoring, cosine similarity, rank fusion,
query routing, filter application, index staleness, budget arithmetic. If a
retrieval bug appears in service code, the first question is whether the logic
can move to `Core/` and be pinned by a test.

Full layout in `PLAN.md` §3.1.

## Hard rules

The fourteen invariants live in **`PLAN.md` §11** and are not repeated here.
The three that get broken first, by anyone moving fast:

1. **The model never sees Jellyfin GUIDs** — batch-local integer indexes only.
2. **Native search never gets slower or worse.** Concierge is additive. If it is
   broken, out of budget, or the provider is down, the user gets exactly the
   search they have today.
3. **Money is spent in exactly three named places** — the plan pass, the re-rank
   pass, and index-time enrichment. Query-time retrieval is free, and no model
   call ever goes in `Core/Retrieval`.

## Prior art — read it first

[Franciskid/jellyfin-plugin-ai-search](https://github.com/Franciskid/jellyfin-plugin-ai-search)
already implements much of phases 1-2: local embedding index, ~40 candidates,
chat model picks from the shortlist and explains, injected client script,
OpenAI-compatible endpoints. It reached rule 1 independently. **`PLAN.md` §1.1
covers what to adopt from it and where Concierge genuinely differs.**

It is **GPL-3.0**: read it for patterns and API usage, never copy code unless
Concierge becomes GPL-3.0 too. Same rule Curator applies to SmartLists.

## Relationship to Curator

`/home/levi/jellyfin-curator` is the sibling plugin, running live on a 10.11.11
server, and its `CLAUDE.md` is a long list of Jellyfin facts learned the
expensive way — the host restarting on plugin install, series watch data not
living where you'd expect, config-page idioms, structured-output schema traps.
**Read it before debugging anything Jellyfin-shaped.**

Concierge ports Curator's provider stack, model profile system, run logging, and
release scripts. It **shares no runtime state and takes no dependency** in
either direction. When porting, port the tests too.

## Testing

- **No live LLM or embedding calls in tests.** Providers through a stub
  `HttpMessageHandler`; the pipeline through stub `ILlmProvider` /
  `IEmbeddingProvider`. Orchestration takes the *interface* factories — that
  seam is the only thing that makes end-to-end pipeline tests possible.
- **The evaluation set is the real test.** ~40 hand-labelled queries against a
  real library, results committed to `eval/results-<phase>.md`. Search quality
  is not assessable by vibes: every prompt change feels like an improvement on
  the query you had in mind when you made it. See `PLAN.md` §10.

## Dependencies

Target: **none at runtime** beyond `Jellyfin.Controller` / `Jellyfin.Model`,
xUnit in tests. No vector database, no ANN library, no tokenizer package, no ML
runtime — brute force is fast enough at these library sizes, and every one of
those is a support burden the owner carries alone. Ask before adding anything.
