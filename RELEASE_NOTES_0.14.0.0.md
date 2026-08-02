# Concierge 0.14.0.0 — the length dial, and the profile that wouldn't stick

## Gemini kept switching back — here's what was happening

Nothing was failing to save. Your Gemini 3.6 Flash profile is on disk, complete,
and has been the whole time.

The page has **two** dropdowns and only said what one of them did:

- **the profile selector** at the top picks which profile you are *editing*
- **"Default profile"** further down picks which profile Concierge actually
  *calls*

Only the second is persisted. On reload the editor reopens on whichever profile
is the default — so choosing Gemini, saving, and coming back to find GPT looked
exactly like the save had been thrown away, when nothing had been asked to
change. Your `DefaultModelProfileId` still pointed at GPT 5.6 Luna, so searches
were still going to OpenAI.

Two fixes:

- Every profile dropdown now marks the one in use: **"GPT 5.6 Luna — in use"**.
- The editor has a **"Use this one"** button that points the default at whatever
  you have open. It reads **"In use"** and is disabled when it already is.

So picking Gemini and pressing *Use this one* now does what picking Gemini looked
like it did. Still needs Save — nothing on that page writes without it.

## Match reasons are now a dial, and it is *the* latency dial

Re-rank latency is generated tokens and nothing else — +0.937 correlation across
80 measured calls, at a flat ~166 tokens per second. The reasons are almost all
of that output: 609 tokens at the median where 240 was warranted, because "at
most eight words" stated in the middle of a rule list is a suggestion.

Two new settings:

**Longest match reason (characters)** — default 60. Stated in the response shape
the model is looking at *while it writes*, not three bullets earlier, and
enforced on the way out so a model that writes an essay cannot make a card
unreadable. Roughly: 60 characters is a clause, 120 is a sentence, and every 40
characters across a full row is about a tenth of a second of waiting.

**Results that get a reason** — default 8. Putting a result in order costs about
six tokens; explaining it costs about forty. Your row shows eight cards before it
scrolls, so explaining all twenty spends most of the wait on text nobody scrolls
to. Set 0 to explain everything.

Expected effect at the defaults: output 609 → roughly 200 tokens, so the ranked
answer lands in about 1.2 s instead of 3.9 s. **That is arithmetic, not a
measurement** — the log will say whether the model actually obeys a limit it is
now told twice.

## What I did not do

I did not shorten the system prompt, which is what you might have expected from
"shorten the prompt". It is 421 tokens of a 3,160-token request — 13% — and input
is not what you wait on. Halving it would save about $0.0004 a query and no
measurable time, while every rule in it is load-bearing: never spoil an ending,
order rather than select, judge by what was meant rather than word overlap. Those
failures are expensive and invisible. Not a good trade.

322 tests.
