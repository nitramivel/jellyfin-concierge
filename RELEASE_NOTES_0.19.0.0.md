# Concierge 0.19.0.0 — run logs that answer the question

Your 2 August rebuild cost $0.40 in five minutes and was cancelled after 30 of
269 items. The log recorded that faithfully. What it did not say was that the
model had changed from `gpt-5.6-luna` to `claude-opus-5`, that the rates had gone
from $0.2/$1.2 per million to **$5/$25**, or that finishing would have been about
**$3.60 and 36 minutes**. Working that out meant reading four files by hand.

Every one of those is now in the run file.

## Broken down by model

```json
"ByModel": [{
  "Provider": "Anthropic", "Model": "claude-opus-5", "Pass": "enrichment",
  "Calls": 3, "Items": 30, "OutputTokens": 14816, "DurationMs": 238658,
  "CostUsd": 0.400255,
  "InputCostPerMillion": 5, "OutputCostPerMillion": 25
}]
```

One row per model per pass, dearest first, **carrying the rates it was billed at**
— a total nobody can check by hand is a total nobody trusts. A run that enriches
on one model and embeds on another now shows two rows instead of one lump, which
matters more now that 0.17 makes it easy to point each pass somewhere different.

The run list shows the model names inline, so the expensive run is identifiable
without opening anything.

## What each item actually got

```json
{"Title": "Hereditary", "Year": 2018, "Batch": 3, "Outcome": "enriched",
 "PremiseChars": 180, "Moments": 3, "Themes": 5, "Asks": 8,
 "Spoiler": true, "CostUsd": 0.0133}
{"Title": "Backrooms", "Year": 2026, "Batch": 1, "Outcome": "unknown-to-model",
 "PremiseChars": 0, "Moments": 0, "Themes": 0, "Asks": 0,
 "Spoiler": false, "CostUsd": 0.0133}
```

Counts and lengths, not the text — the full answer is already in the enrichment
store and copying it here would make the log a second index. But
`"PremiseChars": 0, "Asks": 0` is exactly what a model paid for nothing looks
like, and no aggregate makes that visible. Outcomes are `enriched`,
`unknown-to-model`, `omitted`, `truncated` or `batch-failed`.

Per-item cost is its batch's cost divided by the batch — a share rather than a
measurement, since items are billed together. Recorded anyway, because it is the
number that makes two models comparable.

## Where an unfinished run was heading

```json
"Projection": {
  "ItemsDone": 30, "ItemsRemaining": 239,
  "CostSoFarUsd": 0.400255,
  "ProjectedTotalCostUsd": 3.5889,
  "ProjectedTotalMs": 2140000
}
```

Both numbers, because "$3.59" and "36 minutes" are different reasons to stop.
Shown on the run list as *"$3.59 to finish at this rate"*.

## It survives the run not finishing

The rollup and the projection are recomputed on **every flush**, not in a
completion handler. The runs worth reading are the ones that stopped, and a
summary that only exists on success is a summary that is absent exactly when it
is wanted — including when the process is killed outright.

There is also now a step per enrichment batch carrying the running total, so a
slow run can be watched rather than guessed at.

338 tests, including the 2 August run reproduced as a fixture.
