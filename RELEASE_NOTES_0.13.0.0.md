# Concierge 0.13.0.0 — results in about a millisecond

## What was actually slow

Measured across 99 searches and 185 model calls on this server:

| | |
|---|---|
| Pipeline overhead outside model calls | **11 ms** median, 196 ms worst |
| Embedding call | 297 ms |
| Re-rank call | **6,366 ms** median, 24,388 ms worst |
| Correlation of call duration with tokens generated | **+0.937** |
| Generation rate | a flat ~166 tok/s |

Latency is one thing: the number of tokens the re-rank model writes. Not the
network, not retrieval, not the index, not the vectors.

And the free half of the pipeline — keyword retrieval, no embedding, no model —
already answers in **0 ms at the median and 110 ms at the worst**, measured over
the 35 searches that took that path.

So the way to make a search fast is not to make the model faster. It is to stop
making you wait for it.

## Two phases

**250 ms in, the free answer appears.** Keyword retrieval, dimmed, headed
*Concierge matches (ranking…)*. It costs nothing and is never written to the
query log — it fires on every keystroke, and burying the record of what searches
cost under rows that cost nothing would make that record useless.

**When the ranked answer lands it replaces it**, undimmed, in the model's order,
with the reason each one matched.

The two requests are independent and the cheap one can land second, so the
preview checks whether the full answer for that same query has already arrived
and refuses to paint over it. Without that, the row would visibly get *worse* a
second after it got better.

A preview also never upgrades itself into a paid query. The dominant-winner rule
— which promotes a Native route to the full pipeline when keyword scores are too
close to trust — is correct for a real search and wrong for one made every
250 ms, so it does not fire on a preview.

## What this means end to end

| | before | now |
|---|---|---|
| Something real on screen | 6.4 s | **~250 ms** |
| Ranked answer | 6.4 s | 3.9 s (thinking off, 0.12.0.0) |
| Cost of the preview | — | nothing |

The ranked answer has not got faster in this release; it stopped being the thing
standing between you and a result.

## Also

`Preview` is a normal field on the search request, so anything that can call the
API gets the same behaviour — including whatever eventually reaches the native
clients.

315 tests. The new ones assert the free path literally: the embedding factory and
the model factory both throw on use, so a preview that reaches either fails the
build rather than quietly charging for a keystroke.
