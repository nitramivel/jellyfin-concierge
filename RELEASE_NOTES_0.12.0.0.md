# Concierge 0.12.0.0 — thinking you turned off, results you didn't ask for

## `EnableThinking` did nothing on OpenAI

Your configuration says `EnableThinking = false`. Every re-rank call was still
spending a median of **473 reasoning tokens**, 39% of everything it generated.

`LlmProviderFactory` built the OpenAI provider as
`CreateOpenAi(httpClient, model, apiKey, baseUrl)` — no thinking argument.
Anthropic and Google both received the setting and acted on it; the OpenAI path
took no such parameter and the request body never set `reasoning_effort`. The
provider read `reasoning_tokens` back out of the response and never controlled
it. The setting worked on two of three providers and was inert on the one you
use.

It now sends `reasoning_effort: "minimal"` when thinking is off, and **says
nothing at all when thinking is on** — so an install that works today cannot be
broken by a request shape it has never sent.

If your model doesn't accept the parameter, the first call fails, Concierge
notices, remembers, and re-sends without it. The cost of having asked is one
retry for the life of the process, not one per query, and the degraded state is
exactly today's behaviour.

**Why it matters:** latency here is generation, nothing else. Across 80 measured
calls, duration correlates with tokens generated at **+0.937**, at a flat
~166 tok/s. Median 1,119 tokens generated → 6.4 s. Removing thinking removes
about 39% of that.

## The result count is now an answer, not a setting

Every search returned exactly twelve. "beatles" has nine good answers in your
library and "im your freaky nicki" has one; padding both to twelve fills the
difference with whatever ranked tenth, and there is no way to tell the padding
from the answer.

The re-rank pass is already asked which ones it would *actually show*. That
count is now what you get — floored at 3, so a degenerate reply naming one item
still leaves something beside it, and capped by what the caller asked for. When
the re-rank doesn't run there is no judgement to honour and the configured
maximum stands, because the fused order is a ranking, not an opinion about where
the good answers stop.

The prompt now says so plainly: *returning four is a better answer than padding
to twenty.*

## More candidates considered

`RerankShortlistSize` default goes from 24 to **40**, and the model may place up
to 20 of them.

**This one needs you.** Your install has `25` saved, and a stored value always
beats a code default — open the Concierge settings, set the shortlist size to 40,
and save. Nothing else in this release needs touching.

## Card text matches the native rows

Title tight under the poster, year beneath it at 86% in Jellyfin's own
`.cardText-secondary`, then the match reason below that. The year is no longer
jammed into the title.

The centring class is gone too. `.cardText` is already left-aligned for
left-to-right layouts, so adding `cardTextCentered` and then overriding it with
`text-align:left !important` was us arguing with the stylesheet about something
it had right — the same mistake that hid the posters for four releases.

On a quote card the timestamp takes the year's slot, which is where that kind of
secondary detail belongs.

308 tests.
