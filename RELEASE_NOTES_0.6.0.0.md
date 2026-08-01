# Concierge 0.6.0.0 — phase 2: it reads the sentence and orders the answer

Search now reads what you typed, extracts real constraints from it, and asks a
model to order the results and say why each one matched.

## What it fixes

Two of your own searches made the case better than any argument could:

| Query | Before | Why |
|---|---|---|
| `michael scott` | **Scott Pilgrim vs. the World** #1, The Office #2 | a title match outweighs a character name nobody indexed |
| `identity thefy isnt a joke` | **The Bourne Identity** #1 | the word "identity" dominates a Dwight Schrute quote |

The right answer was in the shortlist both times. Retrieval's job is recall;
this is precision, and a model looking at forty candidates has no difficulty
knowing which one is the Dwight quote.

## The pipeline now

1. **Route** — free. Titles you already know still never reach a model.
2. **Cache** — a repeat is free and instant.
3. **Plan** — one cheap call reads the sentence into what you are describing plus
   any real constraints. **Skipped automatically** when there is nothing to
   extract, so `dark and twisted` does not pay for one.
4. **Retrieve and fuse** — free, as always.
5. **Filter** — and it **fails open**: a filter that would leave fewer than a
   dozen candidates is demoted to a ranking signal rather than applied, because a
   small model's guess at "90s" should never be able to delete the right answer.
6. **Re-rank** — orders the shortlist and writes one clause per result saying
   what connects it to your search. It is told, at length, never to put a twist
   in that clause.

## What it costs you specifically

At `gpt-5.6-luna`'s rates, a paid search is about **$0.0017** — roughly
**$0.75 a month** at 50 searches a day with the router sending 30% to a model.
The plan's $6.30 estimate assumed Haiku-plus-Sonnet at list prices; your model is
much cheaper than that, and the settings page now shows this projection live, with
the router assumption stated next to it because that assumption moves the number
more than the model choice does.

Controls, all of which degrade rather than fail:

- **Monthly search budget**, default $5. At 85% the re-rank stops; at 100%
  searches fall back to free retrieval and say so on the page.
- **A separate index budget**, default $10. Sharing one pot would let a first
  index build exhaust the month's search budget on installation day.
- **Per-user rate limit**, default 30 paid searches an hour. Someone holding a
  key down in a search box is the cheapest way to spend a month in an afternoon.
- **Kill switches** for either pass. Both leave a working plugin.

The spend ledger is persisted, because a cap that resets on restart is not a cap.

## Still OpenAI, still swappable

Nothing here is tied to a provider. The plan and re-rank passes each pick their
own profile, so pointing one at Anthropic, Gemini or Grok is a dropdown — and
both new response shapes are implemented in both structured dialects, with
Gemini's `nullable` fields and OpenAI's union types kept deliberately apart.

## Not yet: the client search bar

Still the API and the settings page only. **Jellyfin Enhanced already owns your
search page**, including the Jellyseerr results that show what you could request
but do not own. Anything Concierge injects has to sit alongside that without
touching it, and getting that wrong would break a feature you use for one you are
still evaluating. The next release addresses it deliberately rather than
hopefully.

212 tests.

## Upgrading

Drop-in. No rebuild, no re-enrichment. Check the projection on the settings page
before your first search, and set the monthly budget to something you are
comfortable with.
