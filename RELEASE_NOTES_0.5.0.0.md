# Concierge 0.5.0.0 — the index already knew the answers

`robots` returned nothing. `death love` returned nothing. Both were the router's
fault, and the fix is the most important change since the index started working.

## A Native route no longer means "return nothing"

Running the real keyword index over the real library, offline, against the
queries that failed:

| Query | What the keyword index actually ranked | What you were shown |
|---|---|---|
| `robots` | **Love, Death & Robots** #1, **Mr. Robot** #7 | nothing |
| `death love` | **Love, Death & Robots** #1 (score 7.8, dominant) | nothing |

The answers were sitting in the index the whole time. The router decided both
queries looked like somebody typing a title, routed them to Jellyfin's own
search, and returned nothing at all — and Jellyfin's substring match cannot save
either one. "robots" does not occur inside "Mr. Robot", and "death love" is the
right words in the wrong order.

The mistake was conflating two different decisions. The plan's concern is
**money**: *"every one of those handed to an LLM is money burnt to produce a
worse answer than substring matching gives for free."* That is an argument about
model calls. Keyword retrieval is local, instant and free — there was never a
reason to skip it.

So Native now means *"spend nothing on this query"*, not *"do not answer it"*.
Keyword results always run. What Native still skips is the embedding call, since
a title lookup does not need semantic search and hard rule 2 says the native path
must never get slower. No network, no cost, and the answer appears.

## Mood words now have somewhere to land

`sexy` did not return *Fifty Shades of Grey* at all. Not a moderation problem and
not an enrichment problem — the enrichment is good, and `erotic` and `bdsm` each
rank that film **#1** on keywords alone.

The problem was that the word "sexy" appears nowhere in the item, so only the
semantic half could bridge it to "erotic" — and the only vector carrying "erotic"
was the document row, which averages a title, genres, cast, a full overview and a
premise. Seven words of tone were diluted into two hundred words about a college
newspaper and a helicopter.

Every enriched item now also gets a **vibe row**: a short vector of nothing but
its genres and themes. A mood query matches that instead of being averaged
against a plot summary. It costs one extra row per item and **no extra model
call** — the themes were already generated.

**This needs a re-index to take effect**, and it is nearly free: nothing has
changed about the items, so no enrichment runs and every existing vector is
reused. Only the ~250 new vibe rows are embedded. Seconds, and a fraction of a
cent.

## Also

- `VectorRowPlanner` moved to `Core/`, where deciding what the index can ever
  match belongs and can be tested.
- The query log records the top titles returned, so "10 results in 311ms" stops
  reading identically for a good answer and a useless one.
- `LiveIndexProbe` is a committed diagnostic: point `CONCIERGE_INDEX_DIR` at a
  copy of a live `docs.json` and `enrichment.json` and it reports exactly what the
  keyword half does with any query. Every number in this document came from it.

166 tests.

## Upgrading

Install, then **run the index task once** to pick up the vibe rows. No
enrichment, no meaningful cost.
