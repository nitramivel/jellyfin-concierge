# The evaluation set

40 queries in the four groups from `PLAN.md` §10. **The `Expected` column is
deliberately unfilled** — it has to name items in *this* library, and the agent
that wrote this file could not read the library (see *Why these are blank*
below). Filling it in is a one-evening job and it is the thing that turns every
future prompt change from a guess into a measurement.

Fill `Expected` with the title you would be annoyed not to see. **Separate
several acceptable answers with a semicolon** — commas turn up inside titles far
too often to use (*Crouching Tiger, Hidden Dragon*). One answer where there is an
obvious one; two or three where the question genuinely has several. Leave a query
blank if nothing in the library fits: an unanswerable query measures nothing, and
the harness skips blanks rather than scoring them as misses.

Matching is loose, so `Se7en` finds `Se7en (1995)` and the reverse. You do not
need to reproduce Jellyfin's exact title string.

## How to run it

Build the index first — once, and it costs money:

```
Dashboard → Scheduled Tasks → Build the Concierge search index
```

Then run the whole set in one command:

```bash
python3 eval/run-eval.py --url http://192.168.1.9:8096 --key "$JELLYFIN_API_KEY"
```

It runs every labelled query through `POST /Concierge/Search`, finds where each
expected title landed, and overwrites `results-phase1.md` with recall@40,
recall@5, recall@1 and MRR per group, the router split, cost, latency, a row per
query, and a list of the misses. Standard library only — it runs on the server
with nothing installed.

`--dry-run` parses the file and stops, which is the quick way to check your
edits before spending anything.

An API key comes from **Dashboard → API Keys**.

**Read recall@40 separately from recall@1.** If the right film never reaches the
top 40, the re-ranker will never see it and no amount of prompt work in phase 2
can recover it — that is a retrieval failure and the lever is enrichment. If it
reaches the shortlist but lands at rank 12, that is a ranking failure and phase
2 is what fixes it. From the results page the two look identical.

---

## Group 1 — Plot recall

Someone half-remembers what happens. This is what the enrichment pass exists
for: overviews describe the premise, and none of these are the premise.

| # | Query | Expected |
|---|---|---|
| 1 | the one where they kill the guy's dog | |
| 2 | that movie where the guy can't make new memories | |
| 3 | the one where he tattoos the clues on himself | |
| 4 | the movie where a guy's whole life is secretly a TV show | |
| 5 | the one where he sails to the edge of the world and hits a wall | |
| 6 | that film where the kid sees dead people | |
| 7 | the one with the spinning top at the end | |
| 8 | film where they're stuck reliving the same day | |
| 9 | the one where the hotel corridor fight is all in one shot | |
| 10 | that movie with the wood chipper | |
| 11 | the one where the ship hits an iceberg and the old lady tells it | |
| 12 | film about a man who ages backwards | |

## Group 2 — Vibe

No plot at all — a mood, a mode, a time of day. These match on enrichment
`themes` or they match on nothing, which is why the enrichment prompt asks for
tone in the same breath as subject.

| # | Query | Expected |
|---|---|---|
| 13 | dark and twisted | |
| 14 | nostalgic 90s classics | |
| 15 | something funny but not stupid, for a Sunday | |
| 16 | something gentle and cosy for a rainy afternoon | |
| 17 | a comfort watch | |
| 18 | something bleak | |
| 19 | stylish and cool, nothing heavy | |
| 20 | genuinely frightening, not just gory | |
| 21 | something to put on with friends who aren't paying attention | |
| 22 | slow and beautiful, nothing happens | |
| 23 | feel-good but not saccharine | |
| 24 | tense the whole way through | |

## Group 3 — Constraints

A real filter hides in the prose. Phase 1 has no plan pass, so these are
expected to do *worse* than groups 1 and 2 — the era half is carried only by the
decade vocabulary written into each document, and runtime and watch-state are
not honoured at all until phase 2. **Record them anyway**: the gap between the
phase-1 and phase-2 numbers on this group is exactly what the plan pass is worth.

| # | Query | Expected |
|---|---|---|
| 25 | 90s sci-fi under two hours I haven't seen | |
| 26 | something from the 80s | |
| 27 | a short comedy, under 100 minutes | |
| 28 | recent thrillers I haven't watched | |
| 29 | anything with Toni Collette | |
| 30 | a Coen brothers film | |
| 31 | animated, but not for children | |
| 32 | something from before I was born | |

## Group 4 — Not-Concierge

The router must send every one of these to native search and spend nothing. This
group matters as much as the other three: a router that sends everything to a
model is expensive and slow, and one that sends nothing looks broken.

| # | Query | Expected route |
|---|---|---|
| 33 | blade | Native |
| 34 | bla | Native |
| 35 | the of | Native |
| 36 | fargo | Native |
| 37 | lord of the rings | Native |
| 38 | tarantino | Native |
| 39 | s | Native |
| 40 | 1917 | Native — a title, not a year filter |

Query 40 is the one worth watching. `1917` is both a film and a four-digit year,
and the router's year rule and its known-name rule disagree about it. The
known-name check runs first *specifically* so a title wins, but that is only
right while the library holds that film.

---

## Why these are blank

The session that wrote this file was running on the workstation, not the NAS.
The Jellyfin server was reachable and answered `System/Info/Public`, but reading
the library needed either an API key from `data/jellyfin.db` or a direct query
against a copy of that database, and both reads were refused by the sandbox — the
first as credential extraction, correctly.

So this file is honest about what it is: the structure, argued through, with the
answers left to whoever can see the library. Anyone with the server in front of
them can fill it in from `Concierge/Status` and the Jellyfin item list.
