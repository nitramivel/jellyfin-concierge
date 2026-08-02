# Concierge development guide

Concierge is a natural-language search plugin for Jellyfin. It combines local
keyword and semantic retrieval with optional model planning, re-ranking, and
index-time enrichment. It also indexes subtitle dialogue and lyrics for quoted
line searches.

Read [PLAN.md](PLAN.md) for the architecture, invariants, cost model, and design
history. This file is the practical repository guide.

## Ground truth

- Target Jellyfin: `10.11.11`.
- Target framework: `net9.0`.
- There is no local Jellyfin server on this workstation.
- Compile against the pinned real Jellyfin ABI and verify with the offline test
  suite.
- Do not make live LLM or embedding calls from tests.
- The plugin has run against a real library, but the labelled quality evaluation
  is still incomplete. Do not claim measured search quality from fixture tests.
- Keep episode enrichment off unless the owner explicitly accepts the much
  larger model workload.

## Commands

The .NET 9 SDK is installed per-user and is not on `PATH` by default.

```bash
export PATH="$HOME/.dotnet:$PATH"

dotnet build Jellyfin.Plugin.Concierge.sln -c Release --no-restore
dotnet test Jellyfin.Plugin.Concierge.sln -c Release --no-restore
git diff --check
./build/package.sh
```

Tests and builds must not restore from the network. Build treats warnings as
errors.

## Architecture

Keep pure decisions in `Core/` and Jellyfin, network, filesystem, and lifecycle
wiring in `Services/`.

- `Core/Documents`: document creation, enrichment parsing, hashes, vector rows.
- `Core/Retrieval`: BM25, vector scoring, tokenization, and rank fusion.
- `Core/Query`: routing, planning, normalization, and filters.
- `Core/Ranking`: re-rank prompt and response handling.
- `Core/Subtitles`: subtitle cleaning, windows, tracks, and phrase search.
- `Services/Indexing`: scanning, enrichment, embeddings, persistence, tasks.
- `Services/Quotes`: extraction and persisted quote/lyric indexes.
- `Services/Llm` and `Services/Embeddings`: provider adapters and factories.
- `Services/Runs` and `Services/Budget`: diagnostics, usage, and spending.
- `Web/concierge.js`: additive Jellyfin Web integration.
- `Configuration/configPage.html`: admin UI.

Prefer moving testable decisions into `Core/` over embedding them in service
orchestration.

## Invariants most likely to regress

1. Models never see Jellyfin GUIDs. Use batch-local integer indexes.
2. Native search remains additive and available. Provider, index, and budget
   failures degrade instead of breaking the search page.
3. Money is spent only on planning, re-ranking, and index-time enrichment.
4. The plugin never writes to library items.
5. The injected client only removes or rewrites DOM it owns. Concierge mode may
   hide remembered native sections with its own class and must restore exactly
   those sections.
6. Newer queries must prevent stale responses from repainting the page.
7. Escape every server or library value inserted into markup.
8. Never put access tokens in image URLs, logs, screenshots, fixtures, or docs.
9. Cost is computed from each call's actual provider/profile rates.

The complete invariant list is in `PLAN.md` §11.

## Index lifecycle

The normal scheduled build reuses enrichment by source hash and vectors by
source text. That is what makes an unchanged nightly run free.

The admin **Regenerate index** action is intentionally different: it marks a
one-shot request and queues the existing Jellyfin scheduled task. The next task
ignores cached enrichment and vectors, but does not invalidate the active search
index until a replacement has been written successfully. Preserve its
single-flight and cost-warning behavior.

Changing embedding models already makes old vectors unusable because vector
spaces are not interchangeable. Changing an enrichment model does not change a
document source hash, so a full regeneration is the explicit way to regenerate
that paid enrichment.

## Web-client rules

The client script is injected rather than bundled with Jellyfin Web. Treat
Jellyfin globals and DOM structure as an integration surface that must be
verified against `10.11.11`.

- Preserve native and third-party rows.
- Keep title-like searches on the native route.
- Keep the free preview independent from the settled paid request.
- Enter runs the paid request immediately; typing uses the configured debounce.
- Respect `prefers-reduced-motion`.
- Do not reintroduce CSS overrides for layout Jellyfin already owns.

Structural client tests are useful regression guards, but they cannot prove a
poster renders or a browser request succeeds. Visual/network defects require
browser evidence from the deployed server.

## Testing

- Provider tests use stub `HttpMessageHandler` responses.
- Pipeline tests use stub `ILlmProvider` and `IEmbeddingProvider` factories.
- Keep orchestration dependent on interfaces so it remains testable.
- Treat [eval/queries.md](eval/queries.md), once labelled, as the quality test.
- Read recall@40 separately from recall@1: the former diagnoses retrieval and
  the latter ranking.

## Releasing

Do not infer release authorization from an implementation request.

When explicitly asked to publish:

1. Start from a reviewed, intentional worktree and choose the next version.
2. Run the complete tests and `git diff --check`.
3. Run `build/release.sh` with `VERSION` and `CHANGELOG`.
4. Commit the code and exact generated `manifest.json` entry.
5. Tag `v<VERSION>` and push the commit and tag.
6. Create the GitHub release and upload the exact generated zip.
7. Download the published asset once and verify its checksum against the
   manifest.

The plugin files must be at the zip root. Re-zipping changes the checksum.
Release notes belong in `manifest.json` and the GitHub release, not standalone
`RELEASE_NOTES_*.md` files.

### Keep the manifest short

`manifest.json` is a catalogue, not a changelog. It reached twenty-four entries
once and was cut back to five, each folding in the releases below it — which is
honest because plugin builds are cumulative: installing `0.13.0.0` really does
give you `0.11.0.0` through `0.13.0.0`.

The kept entries are an upgrade ladder, not a sample. Each one is somewhere a
person might deliberately want to stop: the current build, the last before a
risky feature, the first where something started working.

**When cutting a release, decide whether it earns an entry or folds into the top
one.** Adding to the top by reflex is how it got to twenty-four. Rewriting the
top entry's changelog and checksum for a superseding build is usually right;
a new entry is for a version somebody might want to pin to.

**Never rewrite a kept entry's `checksum`, `sourceUrl`, `targetAbi` or
`timestamp`.** Catalogue installs verify the MD5, so changing any of them breaks
installation for whoever is on that version. Only `changelog` is editable after
publication. Verify every kept entry against its published asset after editing.

Folding an entry out of the manifest does not delete its GitHub release or tag,
and should not.

Plugin GUID: `361b0830-e7c9-460a-b116-0164adec76dd`.

## Dependencies and prior art

Add no runtime dependency without asking. Brute-force retrieval is sufficient
at the intended library sizes and avoids a vector database or ML runtime.

`jellyfin-plugin-ai-search` is useful prior art but is GPL-3.0. Study patterns;
do not copy code unless this project deliberately adopts a compatible license.
The sibling `/home/levi/jellyfin-curator` contains additional hard-won Jellyfin
integration and release lessons.
