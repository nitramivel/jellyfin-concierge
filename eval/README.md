# Evaluation set

Search quality is not assessable by vibes. Every prompt change feels like an
improvement on the query you had in mind when you made it, and quietly breaks
four others. This directory is the defence against that.

## The set

`queries.md` (to be written **before** phase 1 retrieval code) holds ~40 queries
against the owner's real library, each with a hand-labelled correct answer, in
four groups:

| Group | Example | Tests |
|---|---|---|
| **Plot recall** | *"guy loses his memory, covers himself in tattoos"* | semantic retrieval |
| **Vibe** | *"something funny but not stupid, for a Sunday"* | re-ranking, taste |
| **Constraints** | *"90s sci-fi under two hours I haven't seen"* | plan → filters |
| **Not-Concierge** | *"blade"*, *"the of"* | the router says native |

The fourth group matters as much as the first three. A router that sends
everything to a model is expensive and slow; one that sends nothing looks
broken.

## Running it

Each phase records its numbers in `results-<phase>.md` and **commits them**:

- recall@1, recall@5, MRR
- cost per query (mean, p95)
- latency (mean, p95)
- router split — how many queries took the free path

Phase 1's free path (BM25 + vectors + fusion, no model) is the baseline. Every
later phase is measured as a delta against it, and a phase that doesn't beat it
by enough to justify its cost per query is a phase that shipped the wrong thing.
