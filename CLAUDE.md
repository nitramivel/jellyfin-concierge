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

**Phase 0 — nothing built yet.** The repo currently holds the plan only. The
first code to write is the plugin skeleton and the model profile system
(`PLAN.md` §3.3 and §9 phase 0).

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

The twelve invariants live in **`PLAN.md` §11** and are not repeated here.
The three that get broken first, by anyone moving fast:

1. **The model never sees Jellyfin GUIDs** — batch-local integer indexes only.
2. **Native search never gets slower or worse.** Concierge is additive. If it is
   broken, out of budget, or the provider is down, the user gets exactly the
   search they have today.
3. **Retrieval is free.** No model call ever goes in `Core/Retrieval`.

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
