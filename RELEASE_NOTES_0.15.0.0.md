# Concierge 0.15.0.0 — the row shows its work

Three states, in the order you see them:

**Skeletons.** The moment a query is worth answering, the row fills with
card-shaped placeholders — same markup as real cards, so nothing jumps when the
real ones arrive. On this library the free answer beats them by about a tenth of
a second, so mostly you will not see these; they exist so a slow moment looks
like a row getting ready rather than a row that is not there.

**A light sweep.** While the model ranks, a diagonal highlight travels across the
posters and three dots count in the heading. One animation drives both the
skeletons and the shimmer over real posters, so the row has a single rhythm
rather than two things blinking out of step.

**Cards slide.** When the ranked answer lands, every card animates from where the
free answer had put it to where the ranking puts it. This is the only moment in
the plugin where the model's work is visible as work: keyword retrieval was
already right about *what* matched, and what arrives is a better opinion about
the order. Showing that as movement says so more honestly than swapping the row
and hoping somebody noticed.

The slide measures, re-renders, measures again, and puts each card back where it
started with a transform before releasing it — so a row of twenty posters
reorders by animating one compositor property, not by touching layout twice.

## Restraint

The sweep stops when the search stops. A row still shimmering over a finished
answer claims the model is thinking when it has already replied, which makes a
fast search look like a hung one — so it is cleared on all three endings:
answered, answered-with-nothing, and abandoned.

`prefers-reduced-motion: reduce` turns all of it off. The information lives in the
dimming and the "ranking…" label; the animation is only how it is delivered, and
it is never the only copy of the state.

325 tests.
