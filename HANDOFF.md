# Concierge — session handoff #2

Written 2026-08-01, replacing handoff #1 wholesale. **`CLAUDE.md` is picked up
automatically and is current** — it carries architecture, the hard rules, and how
to work in the repo. This file carries session state, live-server facts, and the
things that cost time to learn.

Read section 2 before doing anything. Three things handoff #1 asserted were
wrong, and two of them were load-bearing.

---

## 1. STATE

```
Released .......... v0.11.0.0 on GitHub, manifest.json pushed, MD5 verified
Installed ......... 0.10.0.0 on the server as of 11:00 — 0.11.0.0 NOT YET INSTALLED
main .............. level with origin, working tree clean
Tests ............. 292 passing, warnings-as-errors clean
Compiled against .. the real 10.11.11 assemblies
Index ............. generation 4 — 264 items, 2,550 rows, 254 enriched
Query log ......... data/concierge/queries/queries-2026-08.jsonl, 89 searches
Spend ............. $0.09 on queries, $0.09 on the one full index build
```

Phases 0, 1, 2 and 3a are built and shipped. Twelve releases, 0.2.0.0 → 0.11.0.0.

**1.0.0.0 was published and withdrawn** — release, tag and manifest entry deleted.
The number was picked for the milestone rather than the state of the product.
**The version stays 0.x until `eval/run-eval.py` produces a number and latency is
under the plan's 2.5 s ceiling.** Those two are the 1.0 gate, not the feature list.

---

## 2. WHAT HANDOFF #1 GOT WRONG

### 2.1 `/Search/Hints` was never ruled out — REOPENED

Handoff #1 said "zero `/Search/Hints` requests across three days of server logs"
and used it to close `PLAN.md` open question 6, which is what justified injecting
a script instead of serving results through the API.

**There are no request logs at all.** `config/logging.default.json` overrides
`Microsoft` to `Warning`, which suppresses ASP.NET request logging entirely —
225,821 log lines on 1 Aug, zero of them a request. Counting zero occurrences in
a log that cannot contain them is not evidence.

This decides third-party client support. Injection only ever reaches the web
client; **Swiftin, the Roku client and Wholphin get nothing.** If native clients
search via `/Search/Hints`, a hints provider reaches every client and is strictly
better architecture than injection.

To settle it, add to `config/logging.default.json` under `MinimumLevel.Override`:

```json
"Microsoft.AspNetCore.Hosting.Diagnostics": "Information"
```

restart, search once from Swiftin, read which endpoint it hits, then revert. The
owner has been asked and has not yet said go — **ask before touching their
server config.**

### 2.2 The "21-second unaccounted gap" does not exist

Handoff #1 made this the top priority: a 25,229 ms query with only ~3.9 s in
recorded calls. That reading was wrong — those call figures came from a different
record.

The actual record:

| query `gucci on my pickle` | | |
|---|---|---|
| embedding | 688 ms | |
| re-rank | **24,388 ms** | out 1,584, **thinking 1,537** |
| total | 25,229 ms | |

Across all 64 paid queries, **pipeline overhead outside model calls is a median
of 11 ms and a maximum of 196 ms.** There is no gap. Do not go instrumenting it.

The real finding is in the last column: re-rank latency tracks **thinking
tokens**. Typical queries spend 310–512 and take 5–9 s; the outlier spent 1,537
and took 24 s. Latency work belongs on the reasoning budget of the re-rank pass,
not on the pipeline.

### 2.3 The upgrade path silently did nothing

0.10.0.0 installed and loaded on the server while the browser went on running
1.0.0.0's script — the card rendering was in the plugin and invisible in the
client. The tag pointed at `/Concierge/client.js`, identical in every release,
and the response carried no cache headers at all.

Fixed in 0.10.1.0: the URL carries a hash of the script's contents, the response
carries `Cache-Control: no-cache` and an entity tag, and the index page is tagged
over the patched document rather than inheriting Jellyfin's on-disk tag (which
never changes when a plugin does). Jellyfin Enhanced versions its own injected
script the same way — `?v=12.0.0.0-…` — which is the tell that should have been
noticed first.

---

## 3. FIX FIRST, IN THIS ORDER

**1. Re-rank thinking tokens are the entire latency budget.** Median paid query
6,193 ms, p95 18,459 ms, ceiling per `PLAN.md` §8 is 2,500 ms. Overhead is 11 ms,
so every millisecond available is in that one call. Cap reasoning effort on the
re-rank pass and re-measure; if that is not enough, `PLAN.md` §8 prescribes
streaming — render the fused order at ~300 ms and re-order in place when the
re-rank lands. The client script is already structured for it.

**2. Typing burned money on prefixes — addressed in 0.11.0.0.** 28 of 89 logged
queries were a strict prefix of another logged query — `dark and tw`, `dark and
twsted` and `iron man`
all fired inside four seconds, each a full paid re-rank at ~$0.0014. That is
roughly a third of query spend buying answers to half-typed words. The 450 ms
debounce was too short for a pass that costs money and takes six seconds.
0.11.0.0 waits two seconds for the input to settle and lets Enter run immediately;
Enter cannot duplicate an identical request already in flight. Re-measure the
prefix share after it has real use rather than assuming the new interval is enough.

**3. The query log starts with a UTF-8 BOM.** `json.loads` on line 1 throws
`Unexpected UTF-8 BOM`; every reader needs `encoding="utf-8-sig"`. It is an
append-only file that is meant to be analysed later — fix the writer, and leave
existing files readable.

---

## 4. MEASURED — do not re-derive

**Query economics, 89 searches:** 64 paid, 25 free. $0.0911 total, $0.00142 mean
per paid query. Routes: Concierge 35, Native 23, Both 31. Prompt caching works —
`beatles` read 2,302 cached tokens and cost $0.00073 against a typical $0.00110.

**Index build, full, 263 items: $0.09 and ten minutes** on `gpt-5.6-luna`. The
plan estimated $0.51 on Haiku-tier, so the cost model is pessimistic, not
optimistic. A no-op rebuild is seconds.

**Ten items the enrichment model does not know**, all 2025–2026 releases:
Backrooms · Obsession · Widow's Bay · The Housemaid · Marty Supreme · Now You
See Me: Now You Don't · Pluribus · The Secret Life of Trees · TRON: Ares · and
one with no year, Million Dollar Nannies. It correctly declines rather than
inventing (hard rule 14). Those are findable by title and overview only —
**quote search is the fix for them and the reason phase 3 matters.**

**Jellyfin Enhanced 12.0.0.0, read out of its DLL** (`strings` on the embedded
JS — do this again rather than guessing):

- Its search section is `.jellyseerr-section`, classed
  `verticalSection emby-scroller-container`, holding an
  `itemsContainer padded-right vertical-wrap` of
  `card overflowPortraitCard` children. Concierge copies that exact combination,
  because it demonstrably renders on this client build.
- **It destroys and recreates that node on every keystroke** and re-positions
  itself after the last Movies/Shows section. Any anchor to it must be re-queried
  on every render; a held reference is a node already thrown away.
- It positions once per search, then its observer disconnects. There is no fight
  over the slot — Concierge lands seconds later and stays above it.
- Its Seerr-only filter hides `.verticalSection:not(.jellyseerr-section)`, so it
  hides the Concierge section too. That is correct behaviour, not a bug.

**Router findings, from real searches:**
- A short query naming nothing in the library is a description, not a title.
  `dark comedy`, `weed comedy` and `comedy` all returned nothing before the fix.
- A Native route only stands if keyword retrieval has a *dominant* winner.
  `michael scott` scored Scott Pilgrim 5.93 against The Office 5.55.
- `robots` ranks Love, Death & Robots first on keywords alone; `death love` ranks
  it first at 7.81. Both returned nothing until Native stopped meaning "answer
  nothing".

**237 library rows sit outside every configured folder** — the dead `/storage`
mount. Excluded from the index, correctly. With episodes excluded it is 36.

**Episodes: leave `IncludeEpisodes` off.** On, the library goes from 263 items to
5,338 and enrichment from 22 batches to 445 — about two hours. The model does not
know individual episodes, so you pay input on ~5,000 items for empty answers.

---

## 5. NEVER SEEN, NEVER RUN

**The poster cards have never been seen in a browser.** 0.11.0.0 is the current
build to verify: it can deliver them without stale-cache ambiguity and renders
them in Jellyfin's native horizontal scroller shape. Specifically unverified:

- Whether `overflowPortraitCard` / `cardPadder-overflowPortrait` size correctly
  in our section. If they were renamed, cards render as bare text and the fix is
  an aspect-ratio rule of our own.
- Whether the placement above `.jellyseerr-section` holds once Seerr re-renders.

**"Discover on Seerr" appears twice on the search page and nobody knows why.**
Ruled out: it is not a double script include (the served index has exactly one
Jellyfin Enhanced tag), and it is not Concierge — the script never creates,
clones, removes or replaces their nodes, which `ClientScriptTests` now enforces.
Reading their code, one function creates the section and the debounced handler
removes *all* of them before re-rendering, so a second should not survive. Needs
a browser. One line in the console on the search page settles it:

```js
[...document.querySelectorAll('.jellyseerr-section')].map(s =>
  [s.querySelector('h2')?.textContent.trim(), s.parentElement?.className, s.children.length])
```

**Quote search has never been exercised.** `data/concierge/quotes/` does not
exist. The extraction task, `SrtParser`, `CueCleaner`, `TrackSelector`,
`CueWindowing`, `PhraseIndex` and lyric indexing are built, unit-tested, and have
**never touched a real subtitle file.**

Run **Scheduled Tasks → Read dialogue for Concierge quote search**. Films first,
~140 items with usable text tracks, minutes not hours. Then:

1. Check the coverage panel. Expect ~66% of films to have text subtitles and ~28%
   image-only (measured 31 Jul, `PLAN.md` §6.2). Image-only is fixable for free
   by downloading an external English track.
2. Search `"one wish willow"` — it should find *Obsession*, a film the model has
   never heard of. That single result is the whole argument for phase 3.
3. Search a lyric. Lyrics go in the same index as dialogue.

---

## 6. OPEN — genuinely undecided

**Search quality is unmeasured.** `eval/queries.md` holds 40 queries and **no
expected answers**, because the library could not be read from this session (see
§7). `eval/run-eval.py` runs the whole set in one command once that column is
filled in. Until then every quality claim about this plugin is designed-for, not
demonstrated, and `eval/results-phase1.md` says so.

**Open question 6 is reopened** — see §2.1. It now also decides whether third-party
clients can ever be served.

**Open question 0 was never answered** — build this, or contribute the
differentiating parts upstream to
[Franciskid/jellyfin-plugin-ai-search](https://github.com/Franciskid/jellyfin-plugin-ai-search)
(`PLAN.md` §1.1, question at line 1277). The plan says decide before phase 2;
phase 2 shipped without it.

**Open question 5, privacy.** The query log retains two years, making it a
standing record of what everyone in the house searched for. `LogQueryText` keeps
every number and drops the words. Nobody has decided whether it should be on.
Note `UserId` is currently null on every entry — per-user breakdown does not work
yet.

**SQLite FTS5 for quotes.** The phrase index is hand-rolled, comfortable at
films-only (~73k windows) and uncomfortable at everything (~850k). Episode quote
indexing is where FTS5 becomes the honest answer — and that is a dependency, so
hard rule 13 says ask first.

---

## 7. ENVIRONMENT — and two traps that cost real time

```
Repo (canonical) .. /home/levi/jellyfin-concierge          on the NAS, 192.168.1.9
Repo (build) ...... clone locally; the NAS has .NET 9 but no shell from this session
Server ............ /home/levi/docker/jellyfin/            bind-mounted as /config
  plugins/Concierge_<version>/
  plugins/configurations/Jellyfin.Plugin.Concierge.xml     live config, API keys in plaintext
  config/logging.default.json                              Microsoft=Warning — no request logs
  data/concierge/                                          index, enrichment, vectors
  data/concierge/queries/queries-YYYY-MM.jsonl             query log, append-only, HAS A BOM
  data/concierge/runs/                                     one file per index build
  log/log_YYYYMMDD.log                                     server log
Server URL ........ http://192.168.1.9:8096                reachable with curl from here

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
Recovered by re-cloning; nothing was lost because everything was pushed.
**Do not use `--delete-excluded`.** The working sync is:

```bash
rsync -rlt --no-perms --no-owner --no-group \
  --exclude='.git/' --exclude='bin/' --exclude='obj/' --exclude='artifacts/' \
  "$LOCAL/" "$NAS/"
```

### Trap 2 — stored config beats code defaults

Curator's lesson, and it applies here. `RerankShortlistSize`, `MonthlyBudgetUsd`,
`EnableQuoteSearch` and every setting added after the owner last saved are
**absent** from the live config file and running on code defaults. Changing a
default in `PluginConfiguration.cs` does nothing to an install that has already
saved. Anything changed in code, the owner must also save on the settings page.

### Getting at the server from a workstation session

The NAS is reachable only through a GVFS SFTP mount at
`/run/user/1000/gvfs/sftp:host=192.168.1.9` — files but **no remote shell**, so
builds happen locally and source is synced across. The mount cannot store the
executable bit, so the NAS repo has `core.fileMode=false`; without it every shell
script shows as modified forever.

`curl` against `http://192.168.1.9:8096` works and is the fastest way to check
what the server is actually serving — headers included. That is how the caching
bug in §2.3 was found.

**Reading credentials and the Jellyfin database is blocked** by the sandbox. That
is why `eval/queries.md` has no expected answers: filling it in needs the library,
and the library needs either an API key from `data/jellyfin.db` or a query against
it. Both were correctly refused. Anyone with a shell on the NAS can fill it in.

---

## 8. IMMEDIATE NEXT STEPS

1. **Install 0.11.0.0** and reload the web client normally. Confirm the cards
   render and that the Jellyseerr rows are untouched. Its fingerprinted script
   URL removes the stale-client ambiguity from that check.
2. **Cap re-rank reasoning** and re-measure against the 2.5 s ceiling (§3.1).
3. **Stop paying for half-typed queries** (§3.2).
4. **Get an answer on `/Search/Hints`** (§2.1) — ask first. It decides whether
   this plugin can ever serve Swiftin, Roku and Wholphin.
5. **Run the dialogue extraction task** (§5). Largest never-executed surface.
6. **Fill in `eval/queries.md`** and run `eval/run-eval.py`. That turns "the
   results look about right" into a number, and it is the 1.0 gate.
