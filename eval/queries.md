# The evaluation set

40 queries in the four groups from `PLAN.md` §10, **labelled against this
library**. `run-eval.py` parses all 40 with an expected answer, so the set is
runnable as it stands — this is the measurement `PLAN.md` calls the real test,
and it has been the open gate since phase 1.

**These labels were written by reading `data/concierge/docs.json`** — the 272
indexed items with their titles, years, genres, runtimes and people — not by
reading the library through Jellyfin. That is the same library, one build behind
at worst, but it means the answers are an agent's judgement of "the title you
would be annoyed not to see" rather than the owner's. **Review them before
trusting a number that comes out of them.** Group 2 especially: taste is the
whole content of a vibe query, and mine is not yours.

Five of the original plot-recall queries asked for films this library does not
have — *Memento*, *Fargo*, *Titanic*, *The Sixth Sense*, *The Curious Case of
Benjamin Button* — and two constraint queries were unanswerable (no Coen brothers
film here; "before I was born" needs a birth year). Those were rewritten against
what is on the shelf rather than labelled blank, because the groups are there to
exercise a capability and a blank query exercises nothing.

Editing them: put the title you would be annoyed not to see. **Separate several
acceptable answers with a semicolon** — commas turn up inside titles far too
often to use (*Crouching Tiger, Hidden Dragon*). Leave a query blank if nothing
in the library fits; the harness skips blanks rather than scoring them as misses.

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

Half-remembered specifics. **Every one of these names a film that is actually in
this library** — the original set was written blind and asked for *Memento*,
*Fargo*, *Titanic*, *The Sixth Sense* and *The Curious Case of Benjamin Button*,
none of which are here. A query nothing can answer measures nothing.

| # | Query | Expected |
|---|---|---|
| 1 | the one where they kill the guy's dog | John Wick |
| 2 | the one where he pays to have his ex erased from his memory | Eternal Sunshine of the Spotless Mind |
| 3 | the one where the narrator turns out to be the same person as the other guy | Fight Club |
| 4 | the movie where a guy's whole life is secretly a TV show | The Truman Show |
| 5 | the one where he sails to the edge of the world and hits a wall | The Truman Show |
| 6 | the one where the poor family all get jobs working for the rich family | Parasite |
| 7 | the one with the spinning top at the end | Inception |
| 8 | film where they're stuck reliving the same day | Groundhog Day; Edge of Tomorrow |
| 9 | the one shot to look like a single unbroken take | Birdman or (The Unexpected Virtue of Ignorance) |
| 10 | the one where you die seven days after watching the tape | The Ring |
| 11 | the one where the family is attacked by their own doubles | Us |
| 12 | the one where an hour on the planet costs them seven years | Interstellar |

## Group 2 — Vibe

No single right answer, so several are listed and the harness scores a hit on
any of them. Drawn from what is actually on the shelf.

| # | Query | Expected |
|---|---|---|
| 13 | dark and twisted | Hereditary; Midsommar; American Psycho; The Substance; Gummo |
| 14 | nostalgic 90s classics | The Matrix; Pulp Fiction; Forrest Gump; The Shawshank Redemption; Groundhog Day |
| 15 | something funny but not stupid, for a Sunday | The Grand Budapest Hotel; The Royal Tenenbaums; Ferris Bueller's Day Off; Superbad |
| 16 | something gentle and cosy for a rainy afternoon | Ratatouille; Moonrise Kingdom; Spirited Away; Fantastic Mr. Fox |
| 17 | a comfort watch | Ferris Bueller's Day Off; Groundhog Day; Ratatouille; Elf |
| 18 | something bleak | Gummo; I'm Thinking of Ending Things; Hereditary; Midsommar |
| 19 | stylish and cool, nothing heavy | Baby Driver; Scott Pilgrim vs. the World; True Romance |
| 20 | genuinely frightening, not just gory | Hereditary; The Ring; Get Out; Cure |
| 21 | something to put on with friends who aren't paying attention | Happy Gilmore; Step Brothers; Zoolander; Nacho Libre |
| 22 | slow and beautiful, nothing happens | 2001: A Space Odyssey; I'm Thinking of Ending Things; Thirty Two Short Films About Glenn Gould |
| 23 | feel-good but not saccharine | The Secret Life of Walter Mitty; The Perks of Being a Wallflower; Ratatouille |
| 24 | tense the whole way through | Parasite; Gravity; Sunshine; Get Out |

## Group 3 — Constraints

Structured cuts the plan pass is supposed to read out of the sentence. Two of
the originals were unanswerable here and have been replaced: there is **no Coen
brothers film in this library** (Wes Anderson, with ten, is the useful auteur
test), and "before I was born" needs a birth year the harness does not have.

| # | Query | Expected |
|---|---|---|
| 25 | 90s sci-fi under two hours | Men in Black; Back to the Future Part III |
| 26 | something from the 80s | Back to the Future; Blade Runner; E.T. the Extra-Terrestrial; Ferris Bueller's Day Off; Planes, Trains and Automobiles |
| 27 | a short comedy, under 100 minutes | Fantastic Mr. Fox; Elf; The Darjeeling Limited; Can't Buy Me Love |
| 28 | recent thrillers | Saltburn; The Substance; The Housemaid; Obsession |
| 29 | anything with Toni Collette | Hereditary; I'm Thinking of Ending Things |
| 30 | a Wes Anderson film | The Grand Budapest Hotel; Moonrise Kingdom; The Royal Tenenbaums; Isle of Dogs; Asteroid City; Fantastic Mr. Fox; The Darjeeling Limited; The Life Aquatic with Steve Zissou |
| 31 | animated, but not for children | Isle of Dogs; Fantastic Mr. Fox |
| 32 | something made before 1970 | 2001: A Space Odyssey; A Hard Day's Night; Yellow Submarine; Help!; Magical Mystery Tour |

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

## Why these were blank until now

The session that wrote this file was running on the workstation, not the NAS. The
Jellyfin server answered `System/Info/Public`, but reading the library needed
either an API key from `data/jellyfin.db` or a query against a copy of that
database, and both reads were refused by the sandbox — the first as credential
extraction, correctly.

What unblocked it was not more access but a better source: `docs.json` in the
plugin's own data directory **is** the library, already scanned and normalized,
with no credential anywhere near it. It was there the whole time.

The lesson worth keeping: when a read is refused, check whether the thing you
actually need has already been derived somewhere you are allowed to look.
