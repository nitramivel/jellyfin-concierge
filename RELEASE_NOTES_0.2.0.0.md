# Concierge 0.2.0.0 — first installable release

Natural-language search over a Jellyfin library. This release is **phases 0 and
1**: the plugin, the model configuration, the index, and free hybrid retrieval.

**Nobody has run this against a live server yet.** It compiles against the real
10.11.11 assemblies and 114 tests pass, but no index has ever been built and no
search has ever been answered outside a unit test. Install it expecting to find
things.

## What works

- **Two model profile lists** — chat and embedding, each with its own provider,
  key, base URL and prices, and per-pass assignment so the cheap model reads your
  sentence and the good one enriches the index. Anthropic, OpenAI, Google, xAI
  Grok, and any OpenAI-compatible endpoint. Embeddings additionally support
  Voyage; Anthropic has no embeddings endpoint and is not offered.
- **A local index**, built by the `Build the Concierge search index` scheduled
  task. Keyed by a hash of each item's source text, so a nightly rebuild of an
  unchanged library costs nothing.
- **The enrichment pass** — one model call per batch of items at index time,
  asking how somebody who half-remembers a film would describe it. This is what
  makes *"the one where they kill the guy's dog"* find John Wick when its
  overview never mentions the dog.
- **Hybrid retrieval** — BM25 over weighted fields plus cosine similarity over
  the enriched documents, fused by reciprocal rank. Query-time retrieval calls no
  chat model and never will.
- **A router** that keeps title lookups on Jellyfin's own search, free and
  instant. `blade` costs nothing.
- **Era vocabulary**, so *"nostalgic 90s classics"* matches the decade lexically
  with no model in the loop at all.
- `POST /Concierge/Search`, plus a status panel and a search box on the plugin's
  settings page so you can try queries without curl.

## What does not work yet

- **No search box in the Jellyfin client.** Phase 2. For now, use the settings
  page or the API.
- **No re-ranking and no explanations.** Results come back in fused order with a
  crude reason ("matches both the words and the meaning"). The model-written
  *"matches: amnesia, revenge, non-linear structure"* line is phase 2.
- **Constraints are not honoured.** *"under two hours"*, *"I haven't seen"* and
  *"90s"*-as-a-hard-filter all need the plan pass, which is phase 2. Era language
  works as a ranking signal only.
- **No quote search.** Phase 3.

## Installing

Add this as a plugin repository:

```
https://raw.githubusercontent.com/nitramivel/jellyfin-concierge/main/manifest.json
```

Then install Concierge and restart. **Jellyfin restarts the host on plugin
install** — expect it.

## First run, in order

1. **Dashboard → Plugins → Concierge.** Add an embedding profile. The cheapest
   good option is a local one: run LM Studio or Ollama with `bge-m3`, choose the
   OpenAI-compatible provider, and set the base URL to
   `http://localhost:11434/v1`. Vectors then cost nothing and no library data
   leaves the house. The query/document prefixes fill themselves in for models
   known to need them — leave them alone unless you know why you are changing
   them.
2. **Add a chat profile** for enrichment, and point the *Enrichment* pass at it.
   This one costs money: roughly **$0.51 on Haiku-tier** or **$2.55 on Opus-tier**
   for ~200 films, once. It is the single highest-leverage spend in the plugin —
   it runs once and sets the ceiling on what any future search can find.
   Enrichment can be turned off, and the index will still build, but plot and
   mood searches will be markedly worse.
3. **Save**, then **Dashboard → Scheduled Tasks → Build the Concierge search
   index**. Watch the log: it reports items, vector rows, how many were enriched,
   and what it spent.
4. **Try a search** on the settings page. Suggested first queries:
   `dark and twisted`, `nostalgic 90s classics`, and something you half-remember
   the plot of. Then try `blade` and confirm it routes to Native and costs
   nothing.

## If something is wrong

The index is a cache and your library is read-only. Deleting the index restores
exactly the behaviour the server had before Concierge was installed, and nothing
in your library is ever written to. There is a delete button behind
`POST /Concierge/Index/Delete`, and removing `data/concierge/` by hand does the
same thing.

Note that the query log at `data/concierge/runs.json` records what was searched
for and by whom. That is deliberate and useful for debugging, and it is also a
log of what everyone in the house typed — worth a decision before anyone but you
uses this.
