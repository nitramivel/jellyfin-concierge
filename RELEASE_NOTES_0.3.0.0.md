# Concierge 0.3.0.0 — run logging and checkpointing

Everything in this release comes out of the first real index build. It worked —
263 items, 250 enriched, 2,263 vector rows, **$0.09**, ten minutes — but two
things about it were worse than they needed to be, and both are fixed here.

## Enrichment checkpoints now

**0.2.0.0 saved enrichment only after the entire pass finished.** Cancelling a
build — a completely ordinary thing to do on realising episodes were switched on
— threw away every model call already billed for.

Enrichment now saves every five batches, and once more on the way out of a
cancellation. Stopping a build keeps everything paid for, and the next run
resumes on the document hash it already used rather than starting over. The
rescue save deliberately does not honour the cancellation token that stopped the
run, because a rescue that cancels itself is not a rescue.

Pinned by seven tests, including the one that matters: cancel mid-pass, and the
completed batches are still there.

## A real run log, per build

One JSON document per build under `data/concierge/runs/`, holding:

- every **step** — scan, enrichment plan, each checkpoint, embedding plan, write
- every **model call**, *including the ones that failed*, with tokens, cache
  reads and writes, thinking tokens, duration, outcome, and what that single call
  cost. Failures are the entries worth having: a batch that truncates has still
  been paid for, and a log that records only successes makes a run look cheaper
  and healthier than it was.
- a preview of each prompt and, more usefully, each **response** — where
  enrichment quality is actually visible
- every **item that came out unenriched, by title, with a reason**:
  `unknown-to-model`, `omitted`, `batch-failed`, `truncated`. "3 failed" is
  unactionable; three titles with a reason each is a bug report.
- **totals summed per call**, never derived by multiplying aggregate tokens by
  one rate — a build that enriches on one model and embeds on another has two
  prices in it.

Twenty-five runs are kept. The recorder never throws: a build that spent money
must not be lost to a logging fault.

## You can see it happening

- **The server log gets a heartbeat** every ten batches with running counts and
  cost. Previously a long pass was fifteen minutes of silence between "starting"
  and "finished", which is indistinguishable from a hang.
- **The settings page grows a build panel** — a live progress bar with items
  enriched and money spent so far while a build runs, and the history with cost,
  timing and outcome once it finishes.
- Cost now logs as `$0.0900` rather than `$"0.0900"`.

## New endpoints

| | |
|---|---|
| `GET /Concierge/Index/Runs` | build history, newest first |
| `GET /Concierge/Index/Current` | the build in flight, from memory, or null |
| `GET /Concierge/Index/Runs/{id}` | one build's whole record |

## Worth knowing from that first run

- **Leave "Index individual episodes" off.** With it on, this library goes from
  263 items to 5,338 and from 22 batches to 445 — about two hours instead of ten
  minutes. The model does not know individual TV episodes, so it correctly
  declines to invent them and you pay for ~5,000 empty answers.
- **237 rows were excluded** as sitting outside every configured library folder.
  Those are the dead `/storage` mount left behind by a remount. They look real in
  Jellyfin and play back as nothing, and they are never indexed.
- **Ten items came back `unknown-to-model`** and are now stored as such, so
  future runs will not pay to ask again. Three failed outright — with this
  release you can see which three and why.

## Upgrading

Drop-in. The index, its enrichment and its vectors are unchanged and are not
rebuilt. Existing enrichment stays valid, so upgrading costs nothing.
