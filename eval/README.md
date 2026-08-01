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

```bash
python3 eval/run-eval.py --url http://192.168.1.9:8096 --key "$JELLYFIN_API_KEY"
```

`run-eval.py` reads `queries.md`, runs each labelled query against the live
plugin, and writes `results-phase1.md` itself. It talks to the same HTTP endpoint
a client would, so what it measures is what a user would actually get.

Each phase records its numbers in `results-<phase>.md` and **commits them**:

- **recall@40**, recall@5, recall@1, MRR
- cost per query (mean, p95)
- latency (mean, p95)
- router split — how many queries took the free path

**Read recall@40 separately from recall@1.** They fail for different reasons and
have different fixes. If the right film never reaches the top 40, the re-ranker
never sees it and no prompt work can recover it — that's retrieval, and the
lever is the enrichment pass. If it reaches the shortlist but lands at rank 12,
that's ranking, and the lever is the re-rank prompt. From the results page the
two look identical.

The first measurement to take, before anything else is tuned: **the same set run
against overviews alone, then against enriched documents.** That delta says
whether enrichment is carrying the feature or decorating it.

Phase 1's free path (BM25 + vectors + fusion, no model) is the baseline. Every
later phase is measured as a delta against it, and a phase that doesn't beat it
by enough to justify its cost per query is a phase that shipped the wrong thing.
