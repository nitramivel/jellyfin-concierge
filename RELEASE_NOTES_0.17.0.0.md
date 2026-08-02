# Concierge 0.17.0.0 — the settings you already had

## Per-pass models were always there

Concierge has been able to run a different model for every job since 0.1. Plan,
re-rank, enrichment and embeddings each have their own selector, and yours are
all sitting on "use the default profile" right now.

You asked for the feature because you could not find it, and that is a real
defect — the same one as the Gemini profile that would not stick. The capability
existed; the page hid it. *Which model runs which pass* was the **seventh** of
eleven headings on a single 1,600-line scroll, about two hundred lines below two
profile editors you rarely need to touch.

So: the page is tabbed now, and that section leads the Models tab.

**Search · Models · Index · Spending · Usage.** The tab you were last in is
remembered, so coming back returns you to your work rather than to the top of a
scroll.

## The pass table

*Which model runs which pass* is a table now — one row per job, read across:

| | |
|---|---|
| **Plan** | Reads the sentence for constraints. Only on queries that carry one. |
| **Re-rank** | Orders the shortlist and says why. Every paid search, and **99% of the wait** — judge a model here on speed as much as taste. |
| **Enrichment** | Writes what each item is about. Index build only, once per item ever. Nobody is waiting on it and it sets the ceiling for every search afterwards. **Spend here.** |
| **Embeddings** | Turns items and queries into vectors. **Changing this invalidates the index** — different model, different vector space. |

Each row says when it runs and what that costs you, because "which model should
this be" is unanswerable without knowing whether the pass runs once per library
or once per keystroke.

## Also

The page's opening paragraph has claimed since 0.1 that "there is no search box
in the Jellyfin client yet — that arrives in phase 2". It arrived in 0.10.0.0.
Fixed.

## Suggested setup for your install

You now have GPT 5.6 Luna and Gemini 3.6 Flash. A reasonable split, given the
measurements: **Flash on re-rank**, where all the latency is and the job is
mostly judgement about ordering, and **Luna on enrichment**, where it runs once
per item and quality is permanent. Try it and read the Usage tab — that is what
it is for.

329 tests.
