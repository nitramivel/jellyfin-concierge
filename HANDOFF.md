# Concierge — session handoff #3

Written 2026-08-03, replacing the poster-issue handoff wholesale. **`CLAUDE.md`
is picked up automatically and is current** — architecture, hard rules, release
steps, the manifest rule. **`README.md` was rewritten this session** and is the
honest description of what exists. This file carries session state, live-server
facts, and the things that cost real time to learn.

---

## 1. STATE

```
Released .......... v0.28.0.0, manifest 5 entries, all verified installable
Installed ......... Concierge 0.27.0.0 — the owner installs from the catalogue
main .............. level with origin, working tree clean
Tests ............. 387, no network, warnings-as-errors clean
Index ............. generation 5 — 272 items, 2,574 rows, 256 enriched
Enrichment store .. 322 entries: 262 gpt-5.6-terra, 60 predating the run tie
Quote tracks ...... 6,328 extracted
Query log ......... 239 searches, $0.69 spent
```

**Two sessions have been working this repo concurrently.** Fetch and rebase
before every push; the only file that has ever collided is `manifest.json`, and
the fix is to keep both entries and never rewrite a published checksum.

---

## 2. THE BUG THAT MATTERS MOST

**A stray closing brace killed the entire client script from 0.18.0.0 to
0.23.0.0.** Five releases where the Jellyfin search box did nothing at all.

It looked healthy from the server the whole time: the query log kept filling up,
because the settings page has its own *Try a search* that does not use that
script. Every search recorded in that window came from there.

Every client test passed throughout, because they match strings and count
occurrences and none of that can see an unbalanced brace.

Now guarded three ways, and **keep all three**:

1. `TheScriptsBracketsBalance` walks the script with a stack and names the line a
   bracket was opened on against where it was wrongly closed.
2. `EveryFunctionItCallsIsOneItDefines` catches a deleted function whose call site
   survived — which has also happened here once.
3. **Before every publish**, the packaged DLL's embedded script is extracted and
   parsed with `esprima`. There is a venv at
   `<scratch>/jsenv` for this. A structural test cannot replace an actual parse.

---

## 3. MEASURED — do not re-derive

**Latency is generated tokens and nothing else.** Re-rank duration against tokens
written: **+0.937 across 80 calls**, at a flat **~166 tok/s**. Pipeline overhead
outside model calls: **11 ms median, 196 ms max**. There is no mystery gap; an
earlier handoff claimed one and was wrong.

**The free half answers in 0 ms** (median; 110 ms worst) over 35 measured
searches. That is why the preview exists and why nothing waits on the model.

**Query economics:** $0.0014 mean per paid query. Prompt caching works — one query
read 2,302 cached tokens and cost 34% less.

**Index build:** 263 items, **$0.09 and ten minutes** on `gpt-5.6-luna`. A no-op
rebuild is seconds.

**The model is the whole bill.** The same rebuild: $0.17 on gpt-5.6-luna
($0.2/$1.2 per M) against **$3.60** on claude-opus-5 ($5/$25). Opus was also
~8× slower per call. Both figures came out of the run log, which now breaks every
build down by model *with the rates billed*.

**Episodes are a different economy.** `IncludeEpisodes` on takes this library from
272 items to **5,270**. Measured on a real run: **45% came back unknown-to-model**
— it has never heard of *"Sow, Do You Like Them Apples"*. As of 0.27 they have
their own model and thinking profile; as of 0.26 they are named
`Adventure Time S6E13 — The Wand` in the prompt, so that 45% should improve.
**Nobody has measured whether it did.**

**Jellyfin caps every `<form>` at 54em** above 50em wide. The whole settings page
is one form; that was why the Models table looked cut off. Raised to 100em scoped
to the page id.

**Jellyfin Enhanced 12.0.0.0, read out of its DLL** (`strings` on the embedded JS
— do this again rather than guessing):

- Its search section is `.jellyseerr-section`. It **destroys and recreates that
  node on every keystroke**, so any anchor to it must be re-queried per render.
- Its search icon is `#jellyseerr-search-icon` at `right:10px; top:68%` — that 68%
  is for a 50px image with a shadow, and copying it for a round button puts it
  visibly low. Ours is centred.
- That icon is **their Seerr-only filter toggle**. Concierge hides it by a CSS
  rule, behind a setting, because hiding it takes that control away.

---

## 4. LIVE CONFIG — stored values beat code defaults

```
IncludeEpisodes ....... true      <- 5,270 items. Decide deliberately.
MaxAsksPerItem ........ 12        <- honoured as of 0.26; produced 8 before
RerankShortlistSize ... 35
EnableThinking ........ false     <- inert on OpenAI until 0.12
```

Anything changed in `PluginConfiguration.cs` does nothing to this install. The
owner must also save on the settings page.

---

## 5. OPEN — genuinely undecided

**Search quality is still unmeasured.** `eval/queries.md` holds 40 queries with
**no expected answers**, because filling them in needs the library and reading
`data/jellyfin.db` is blocked by the sandbox. `eval/run-eval.py` runs the set in
one command once that column exists. Until then every quality claim is
designed-for, not demonstrated. **This is the 1.0 gate**, with latency under
`PLAN.md`'s 2.5 s.

**The index is behind the enrichment, or was.** A cancelled build banks paid work
and stops before embedding, so searches keep using the previous answers. 0.23
added a banner for exactly this; check it before assuming a change took effect.

**50 episodes are enriched without series context**, from a run cancelled before
0.26 shipped. Banked, not re-charged, and carrying bare titles. Only a re-enrich
fixes them.

**`/Search/Hints` was never ruled out.** An earlier handoff said "zero requests in
three days of logs" — but `config/logging.default.json` sets `Microsoft` to
`Warning`, so **there are no request logs at all**. Counting zero occurrences in a
log that cannot contain them proves nothing. This decides whether Swiftin, Roku
and Wholphin can ever be served, since injection only ever reaches the web client.
To settle it, add `"Microsoft.AspNetCore.Hosting.Diagnostics": "Information"`,
restart, search from a native client, read the endpoint, revert. **Ask first — it
is the owner's server.**

**Open question 0** — build this or contribute upstream to
[Franciskid/jellyfin-plugin-ai-search](https://github.com/Franciskid/jellyfin-plugin-ai-search)
(`PLAN.md` §1.1) — was never answered.

**Privacy.** The query log retains two years. `LogQueryText` keeps the numbers and
drops the words; nobody has decided. `UserId` is null on every entry, so per-user
breakdown does not work yet.

---

## 6. NEVER SEEN IN A BROWSER

Everything shipped since 0.22 is first-run code: the Library tab, expandable build
history, per-item re-index, the subtitle picker, the episode tree, the relocated
progress bar, the search-box icon. The endpoints are covered by tests; the
rendering is not, and cannot be from here.

If something looks wrong it is far more likely layout than data.

---

## 7. ENVIRONMENT — and the traps

```
Repo (canonical) .. /home/levi/jellyfin-concierge          on the NAS, 192.168.1.9
Repo (build) ...... clone locally; no remote shell from this session
Server ............ /home/levi/docker/jellyfin/            bind-mounted as /config
  plugins/Concierge_<version>/
  plugins/configurations/Jellyfin.Plugin.Concierge.xml     live config, keys in plaintext
  config/logging.default.json                              Microsoft=Warning — no request logs
  data/concierge/                                          index, enrichment, vectors, quotes
  data/concierge/queries/queries-YYYY-MM.jsonl             append-only, HAS A UTF-8 BOM
  data/concierge/runs/run_*.json                           one per build, pruned
Server URL ........ http://192.168.1.9:8096                curl works and is the fastest check

export PATH="$HOME/.dotnet:$PATH"
dotnet test Jellyfin.Plugin.Concierge.sln -c Release
VERSION=x.y.z.0 CHANGELOG="..." ./build/release.sh
```

**Trap 1 — `rsync --delete-excluded` destroyed the NAS `.git`.** With
`--exclude='.git/'` it means "delete anything matching the excludes at the
destination". Never use it. The working sync is:

```bash
rsync -rlt --no-perms --no-owner --no-group \
  --exclude='.git/' --exclude='bin/' --exclude='obj/' --exclude='artifacts/' \
  "$LOCAL/" "$NAS/"
```

**Trap 2 — stored config beats code defaults.** See §4.

**Trap 3 — `git commit -m "…"` with quotes in the message silently fails** and the
rest of an `&&` chain runs anyway. That published a release tagged at the wrong
commit. **Always use `git commit -F -` with a heredoc.**

**Trap 4 — step detail keys are lifted into headline counters by name.** A step
sending `items` meaning "batch size" rewrote the library-wide count and the
progress panel read wrong for a whole build. The five reserved names are in
`IndexRunLogStore`.

**Trap 5 — `raw.githubusercontent.com` caches the manifest for ~5 minutes.** After
publishing, the catalogue lags. Verify against the GitHub API, not the raw URL.

**Reading credentials and `data/jellyfin.db` is blocked** by the sandbox. That is
why the eval set has no expected answers. Anyone with a shell on the NAS can fill
it in.

---

## 8. IMMEDIATE NEXT STEPS

1. **Install 0.28.0.0** and look at it. Seven releases of interface work have
   never been rendered.
2. **Decide about episodes.** On means 5,270 items; give them a cheap profile or
   turn them off. Then measure whether the series-context fix moved the 45%.
3. **Fill in `eval/queries.md`** and run `eval/run-eval.py`. It is the 1.0 gate
   and it has been open since phase 1.
4. **Get an answer on `/Search/Hints`** (§5) — ask first. It decides whether this
   plugin can ever serve anything but the web client.
5. Re-measure re-rank latency now that thinking is genuinely off and the reason
   length is capped. The arithmetic says ~1.2 s; nobody has confirmed it.
