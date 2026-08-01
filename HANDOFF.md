# Concierge — session handoff #1

Written 2026-08-01, at the end of the session that built the whole plugin from
the plan. **`CLAUDE.md` is picked up automatically and is current** — it carries
architecture, the hard rules, and how to work in the repo. This file carries only
session state, live-server facts, and the things that cost time to learn.

---

## STATE

```
Released .......... v0.10.0.0 on GitHub, manifest.json pushed
Deployed .......... the owner installs from the catalogue
main .............. level with origin, working tree clean
Tests ............. 278 passing, no network, warnings-as-errors clean
Compiled against .. the real 10.11.11 assemblies
Index ............. generation 4 — 264 items, 2,550 rows, 254 enriched
Query log ......... data/concierge/queries/queries-2026-08.jsonl, 68 searches
Spend ............. data/concierge/spend.json, query spend only so far
```

Phases 0, 1, 2 and 3a are built and shipped. Nine releases, 0.2.0.0 → 0.10.0.0,
each with notes in `RELEASE_NOTES_*.md`.

**1.0.0.0 was published and withdrawn** — release, tag and manifest entry all
deleted, republished as 0.10.0.0. The number was picked for the milestone rather
than the state of the product: the eval set is unfilled, quality is unmeasured,
and a search takes 25 seconds. **The version stays 0.x until `eval/run-eval.py`
has produced a number and latency is under the plan's 2.5 s ceiling.** Those two
things are the 1.0 gate, not the feature list.

---

## THE ONE THING TO FIX FIRST

**Searches take 22–25 seconds and nobody knows where the time goes.**

Measured on the live server at 10:02:09 UTC, query `"gucci on my pickle"`,
running 1.0.0.0 which already contains the latency work:

| | |
|---|---|
| Total wall clock | **25,229 ms** |
| Embedding call | 557 ms |
| Re-rank call | 3,380 ms (in 2,305 / out 287) |
| **Unaccounted** | **~21,000 ms** |

The 0.8.0.0 latency work **did what it was meant to** — re-rank output is down
to 287 tokens and the call itself is 3.4 seconds. The problem is elsewhere, and
it is roughly five times larger than the part that was optimised.

What is already ruled out:

- Not the plan pass. It was correctly skipped (4 tokens, no constraint words),
  and there are no "plan pass failed" warnings in the log.
- Not retries. Those would appear inside the recorded call duration.
- Not native queries — those return in 0–1 ms.

Leading suspects, in order: the index load in `GetIndexAsync` after a rebuild
invalidates the cache (`vectors.bin` is 14 MB read one row at a time across a
container bind mount), or something in retrieval scaling worse than expected.

**Do not guess at this. Instrument it.** Add per-stage timings to
`QueryRunRecord` — index load, lexical, vector, fuse, filter, re-rank — and read
one slow query back. The query log already exists and is append-only, so this is
a small change that will answer the question in one search.

Latency target is 1.2 s, ceiling 2.5 s (`PLAN.md` §8). Everything else about the
plugin works; this is what makes it feel broken.

---

## MEASURED — do not re-derive

**Index build, full, 263 items: $0.09 and ten minutes** on `gpt-5.6-luna`. The
plan estimated $0.51 on Haiku-tier, so the cost model is pessimistic, not
optimistic. A no-op rebuild is seconds.

**Ten items the enrichment model does not know**, all 2025–2026 releases:
Backrooms · Obsession · Widow's Bay · The Housemaid · Marty Supreme · Now You
See Me: Now You Don't · Pluribus · The Secret Life of Trees · TRON: Ares · and
one with no year, Million Dollar Nannies. It correctly declines rather than
inventing (hard rule 14). Those films are findable by title and overview only —
**quote search is the fix for them and the reason phase 3 matters.**

**`/Search/Hints` is a dead end.** Zero requests across three days of server
logs; the web client fetches `/Items` with a search term. Jellyfin Enhanced
does not use it either — zero references, no `ISearchEngine`. `PLAN.md` open
question 6 is closed: injection was the only route to the search bar.

**Jellyfin Enhanced owns the search page.** It injects via `IStartupFilter`
middleware and namespaces everything `je-` / `jellyseerr-`, creating its own
`.jellyseerr-section` containers. Jellyseerr lives at `http://192.168.1.9:5055`
with `JellyseerrShowSearchResults = true`. Concierge coexists by the same
discipline — only ever touching nodes it created. **If the Seerr section ever
shifts or disappears, that rule was broken and it is Concierge's fault.**

**Router findings, from real searches:**
- A short query naming nothing in the library is a description, not a title.
  `dark comedy`, `weed comedy` and `comedy` all returned nothing before this
  was fixed.
- A Native route only stands if keyword retrieval has a *dominant* winner.
  `michael scott` scored Scott Pilgrim 5.93 against The Office 5.55 — a coin
  toss the router was treating as certainty.
- `robots` ranks Love, Death & Robots first and Mr. Robot seventh on keywords
  alone; `death love` ranks Love, Death & Robots first at 7.81. Both returned
  nothing until Native stopped meaning "answer nothing".

**237 library rows sit outside every configured folder** — the dead `/storage`
mount. They are excluded from the index and always should be. With episodes
excluded it is 36.

**Episodes: leave `IncludeEpisodes` off.** On, the library goes from 263 items
to 5,338 and enrichment from 22 batches to 445 — about two hours. The model does
not know individual episodes, so you pay input on ~5,000 items for empty
answers. This is not a budget decision.

---

## NEVER RUN — the largest untested surface

**Quote search has never been exercised.** `data/concierge/quotes/` does not
exist. The extraction task, `SrtParser`, `CueCleaner`, `TrackSelector`,
`CueWindowing`, `PhraseIndex` and lyric indexing are all built and unit-tested
and have **never touched a real subtitle file.**

Run **Scheduled Tasks → Read dialogue for Concierge quote search**. Films first,
~140 items with usable text tracks, minutes not hours. Then:

1. Check the coverage panel. Expect ~66% of films to have text subtitles and
   ~28% to be image-only (measured 31 Jul, `PLAN.md` §6.2). Image-only is fixable
   for free by downloading an external English track.
2. Search `"one wish willow"` — it should find *Obsession*, a film the model has
   never heard of. That single result is the whole argument for phase 3.
3. Search a lyric. Lyrics go in the same index as dialogue.

**The search bar injection has been seen once, and it was wrong.** It rendered —
so injection, serving and the endpoint all work — but as a plain text list sitting
below four rows of Jellyseerr discovery. 0.10.0.0 rewrites it as poster cards and
moves it above the Seerr sections, and **that rewrite has not been seen in a
browser at all.** Specifically unverified:

- Whether the card classes (`overflowPortraitCard`, `cardPadder-overflowPortrait`)
  still exist in 10.11.11's web client. If they were renamed, the cards render as
  bare text and the fix is a `.concierge-card` aspect-ratio rule of our own.
- Whether the Jellyseerr selector in `position()` matches. The three patterns are
  guesses from the plugin's naming convention, not from reading its DOM. If none
  match we fall back to appending — the old bad placement, silently.

Hard-refresh with Ctrl+Shift+R after installing; the browser caches the index
page and the script tag will not be there otherwise.

---

## OPEN — genuinely undecided

**Search quality is unmeasured.** `eval/queries.md` holds 40 queries and **no
expected answers**, because the library could not be read from this session (see
Environment). `eval/run-eval.py` runs the whole set in one command once that
column is filled in. Until then every quality claim about this plugin is
designed-for, not demonstrated. `eval/results-phase1.md` says so explicitly.

**Open question 0 was never answered** — build this, or contribute the
differentiating parts upstream to `jellyfin-plugin-ai-search`. The plan says to
decide before phase 2, and phase 2 shipped without it. Still worth answering
before phase 4.

**Open question 5, privacy.** The query log now retains two years, which makes
it a standing record of what everyone in the house searched for. `LogQueryText`
lets you keep every number and drop the words, but nobody has decided whether it
should be on.

**SQLite FTS5 for quotes.** The phrase index is hand-rolled, which is comfortable
at films-only (~73k windows) and uncomfortable at everything (~850k). Turning on
episode quote indexing is the point at which FTS5 becomes the honest answer, and
that is a dependency — hard rule 13 says ask first.

**Streaming results.** If the latency investigation above does not get under
2.5 s, `PLAN.md` §8 prescribes rendering the fused order at ~300 ms and
re-ordering in place when the re-rank lands. The client script is already
structured to allow it.

---

## ENVIRONMENT — and two traps that cost real time

```
Repo (canonical) .. /home/levi/jellyfin-concierge          on the NAS, 192.168.1.9
Repo (build) ...... clone locally; the NAS has .NET 9 but no shell from this session
Server ............ /home/levi/docker/jellyfin/            bind-mounted as /config
  plugins/Concierge_<version>/
  plugins/configurations/Jellyfin.Plugin.Concierge.xml     live config, API keys in plaintext
  data/concierge/                                          index, enrichment, vectors
  data/concierge/queries/queries-YYYY-MM.jsonl             the query log, append-only
  data/concierge/runs/                                     one file per index build
  log/log_YYYYMMDD.log                                     server log

export PATH="$HOME/.dotnet:$PATH"                          required in every shell
dotnet test Jellyfin.Plugin.Concierge.sln -c Release
VERSION=x.y.z.0 CHANGELOG="..." ./build/release.sh
gh release create vX artifacts/concierge_X.zip --notes-file RELEASE_NOTES_X.md
```

**ALWAYS verify the published asset's MD5 against manifest.json afterwards.**
Re-zipping changes the checksum and breaks catalogue installs.

### Trap 1 — `rsync --delete-excluded` destroyed the NAS `.git`

Syncing with `--exclude='.git/'` **and** `--delete-excluded` means "delete
anything matching the excludes at the destination". It removed the repository.
Recovered by re-cloning from GitHub, and nothing was lost because everything was
pushed. **Do not use `--delete-excluded`.** The working sync is:

```bash
rsync -rlt --no-perms --no-owner --no-group \
  --exclude='.git/' --exclude='bin/' --exclude='obj/' --exclude='artifacts/' \
  "$LOCAL/" "$NAS/"
```

### Trap 2 — stored config beats code defaults

Curator's lesson, and it applies here. `RerankShortlistSize`, `MonthlyBudgetUsd`,
`EnableQuoteSearch` and every other setting added after the owner last saved are
**absent** from the live config file and running on code defaults. Changing a
default in `PluginConfiguration.cs` does nothing to an install that has already
saved. Anything changed in code, the owner must also save on the settings page.

### Getting at the server from a workstation session

The NAS is reachable only through a GVFS SFTP mount at
`/run/user/1000/gvfs/sftp:host=192.168.1.9`. It gives files but **no remote
shell**, so builds happen locally and the source is synced across. The mount
cannot store the executable bit, so the NAS repo has `core.fileMode=false` set —
without it, every shell script shows as modified forever.

**Reading credentials and the Jellyfin database is blocked** by the sandbox. That
is why `eval/queries.md` has no expected answers: filling it in needs the library,
and the library needs either an API key from `data/jellyfin.db` or a query against
it. Both were correctly refused. Anyone with a shell on the NAS can fill it in
directly.

---

## IMMEDIATE NEXT STEPS

1. **Instrument the query pipeline** and find the missing 21 seconds. Nothing
   else matters as much — everything works and it feels broken.
2. **Run the dialogue extraction task** and confirm quote search works against
   real files. It is the largest never-executed surface in the plugin.
3. **Hard-refresh the web client** and confirm the search bar section renders,
   and that the Jellyseerr section is untouched.
4. **Fill in `eval/queries.md`** and run `eval/run-eval.py`. That turns "the
   results look about right" into a number, and it is the gate the plan puts in
   front of phase 4.
5. Decide on `LogQueryText`, now that retention is two years.
