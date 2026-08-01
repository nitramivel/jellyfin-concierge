# Phase 1 — results

**Status: not yet measured on the real library.** The numbers this file exists to
hold are still blank, and nothing downstream should be treated as validated until
they are filled in. What *is* recorded here is the mechanism evidence: the
retrieval stack running end to end over a fixture library, which says the
plumbing is right and says nothing about whether the plumbing is pointed at
anything good.

| | |
|---|---|
| Index built against the real library | ❌ no |
| Embedding model used | none — no provider configured, no key available |
| Enrichment model used | none — same |
| 40-query set run | ❌ no — `queries.md` has no expected answers yet |

## Why not

Three things were needed and none were available in the session that built
phase 1:

1. **An embedding provider.** No API key is configured, and no local server was
   listening on the usual Ollama or LM Studio ports on either the workstation or
   the NAS. Without one there are no vectors, so half of retrieval cannot run.
2. **An enrichment model.** Same key problem, and this one costs real money
   (~$0.51 on Haiku-tier for 213 films, ~$2.55 on Opus-tier). Not something to
   spend on the owner's behalf unasked.
3. **The library itself.** Reading it needed either an API key out of
   `data/jellyfin.db` or a query against a copy of that database; both were
   refused by the sandbox. So the expected-answer column in `queries.md` could
   not be filled either.

**The cheapest unblock is a local embedding model.** Start LM Studio or Ollama,
load `bge-m3` or `nomic-embed-text`, and point an embedding profile at
`http://localhost:11434/v1`. Vectors then cost nothing, nothing leaves the
house, and the free path — which is the entire phase-1 baseline — can be
measured without spending a penny. Enrichment still needs a paid model, and the
delta between "overviews only" and "enriched" is the single most useful number
in the project, so it is worth the couple of dollars once.

---

## What was measured: mechanism, on a fixture library

14 recognisable films with real overviews and hand-written enrichment of the
shape the real pass produces (`Jellyfin.Plugin.Concierge.Tests/FixtureLibrary.cs`),
run through the real `Bm25Index`, `VectorIndex` and `RankFusion`. The embedder is
a deterministic stand-in over a hand-built concept space, so **these results
prove the pipeline, not the model.**

| Query | Top 5 |
|---|---|
| `dark and twisted` | Oldboy, The Silence of the Lambs, Se7en, Fargo, Memento |
| `nostalgic 90s classics` | Jurassic Park, Clueless, Groundhog Day, The Big Lebowski, Se7en |
| `harrowing` | Se7en, Oldboy, The Silence of the Lambs, Blade Runner, Fargo |
| `something gentle and cosy for a rainy afternoon` | Paddington, Amélie, Groundhog Day, **Blade Runner**, The Big Lebowski |
| `the one where he tattoos the clues on himself` | **Memento**, Groundhog Day, Paddington, The Big Lebowski, The Silence of the Lambs |

Four things worth keeping from that table:

- **`nostalgic 90s classics` orders the decade by mood.** Seven of the fourteen
  fixtures are from the 90s, so the era half of the query does not discriminate
  much; what puts Jurassic Park above Se7en is `themes`. Se7en at rank 5 is
  correct behaviour, not a near-miss — it *is* a 90s classic, and it is not what
  anyone asking for "nostalgic" wants.
- **`harrowing` appears nowhere in the corpus.** Lexical search returns
  literally nothing for it. Every hit came through the vector half, which is the
  clearest demonstration available that the semantic half earns its place.
- **`the one where he tattoos the clues on himself` ranks Memento first**, and
  Memento's overview never mentions tattoos. That is the enrichment `asks`
  working exactly as §5.2 argues it will.
- **Blade Runner at rank 4 for "cosy rainy afternoon" is a real bug**, and it is
  instructive. "rainy" refers to the *viewer's* afternoon; Blade Runner's themes
  say `rain`. The lexical half cannot tell those apart and neither can a bag of
  concepts. This is precisely the class of error the phase-2 re-rank pass is
  supposed to clean up, and it belongs on the list of things to check has
  actually improved.

### Era vocabulary, measured with no model at all

`EraTokens` writes `1995 1990s 90s nineties` into each document, so era language
matches lexically:

- `90s`, `1990s` and `nineties` all return the same films, and only 90s films.
- This runs with **zero** vectors and **zero** model calls, which is what keeps
  era queries working when the budget is exhausted (hard rule 4).

It is a weaker thing than the phase-2 plan pass, which turns "90s" into a real
`[1990,1999]` filter that can be applied as a hard cut. It is not a replacement
for it.

---

## The table to fill in

Once an embedding profile exists and `queries.md` has its answers:

| Group | recall@40 | recall@5 | recall@1 | MRR | cost/query | p95 latency |
|---|---|---|---|---|---|---|
| Plot recall | | | | | | |
| Vibe | | | | | | |
| Constraints | | | | | | |
| **All** | | | | | | |

Router split: __ of 40 took the free native path (group 4 alone should account
for 8).

**And the measurement that matters most:** run the whole set twice, once with
`EnableEnrichment` off and once with it on. The delta between those two
recall@40 numbers is the answer to "is enrichment carrying this feature or
decorating it", and every cost decision in phase 2 depends on knowing it.
