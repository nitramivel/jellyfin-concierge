# Concierge 0.8.0.0 — lyrics, and the two things that were wrong

## Song lyrics are searchable

Type a line from a song and find the track — and the second it is sung.

Lyrics go into the **same index as film dialogue**, on purpose: a remembered line
is a remembered line, and you should not have to know whether it was spoken or
sung before you can search for it.

This costs nothing and needs no extraction. Jellyfin already holds lyrics as
parsed, time-stamped lines, so indexing them is a read rather than an ffmpeg job
— unlike subtitles, which take seconds to a minute per file. They are cleaned
with the same rules, which strips the `[Chorus]` and `(x2)` markers lyric files
carry and would otherwise match on. Unsynced lyric files are indexed too; the
song is findable, it just cannot be seeked to.

Run the same task: **Scheduled Tasks → Read dialogue for Concierge quote search.**

## `michael scott` finally returns The Office

It ranked *Scott Pilgrim vs. the World* first for two releases, and the reason
was that the router never let the re-ranker see it. Both words name *something*
in the library, so it was treated as a title lookup and answered from keywords
alone.

The measured scores were Scott Pilgrim **5.93** against The Office **5.55** — a
7% edge, which is a coin toss dressed as an answer.

§4.2's third native rule was the missing piece: a Native route claims keyword
retrieval already knows what was meant, so if its top hit is **not a clear
winner**, that claim was wrong and the query gets the full pipeline. Real title
lookups still cost nothing — `fargo` and `blade runner` produce runaway winners
and stay free.

## Searches should be several times faster

Paid searches were taking **8–22 seconds** against a 2.5-second budget. Almost
all of it was the re-rank model emitting forty entries one token at a time.

Three changes, all of them output-side:

- The model is now asked for its **best 12**, not all of them. This is safe by
  construction — the parser already treats the answer as a preference, so
  anything omitted keeps the position retrieval gave it, which is exactly the
  answer that would have been served anyway.
- Explanations are capped at **eight words**, and the prompt says why: every word
  is time somebody is waiting.
- The shortlist dropped from 40 to **24**, and `moments` is no longer sent at
  all. The premise already says what happens, moments were the longest field, and
  they were also the likeliest place for a twist to leak into an explanation.

Retrieval still returns 40 internally, so the evaluation set's recall@40 is
unchanged. This only changes how many the model is asked to look at.

If it is still over budget, the remaining fix is the one §8 names: stream the
fused order at ~300ms and re-order in place when the re-rank lands. That needs a
client, and it is the natural next step.

## Also

- Every new setting is now carried through a per-request result limit, which
  previously reset them to defaults on any search that asked for a specific count.

267 tests.

## Upgrading

Drop-in. Re-run the dialogue task to pick up lyrics — it skips everything already
read, so it costs only the songs.
