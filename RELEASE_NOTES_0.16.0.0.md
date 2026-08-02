# Concierge 0.16.0.0 — Concierge mode

A **✦ Concierge** chip next to the search box. Off, nothing changes: one row among
the others, above the Seerr rows, exactly as before. On, the library's own Movies
and Shows rows step aside and Concierge gets the page — a grid that fills the
width, bigger posters, and room for the reason to be a sentence instead of two
clamped lines.

**Discover on Seerr stays.** A search that finds nothing you own should still
offer what you could request, without making you leave the mode to see it.

The setting sticks across navigation and reloads. Toggle off and the page comes
back exactly as it was.

## The one rule, and where it now bends

This script has been built on a single rule since the first release: *it only
touches nodes it created*. That is why the Jellyseerr rows have never broken
while three other things about this plugin have.

Concierge mode is the one exception, and it is deliberate rather than accidental.
Three things keep it honest:

- **It hides with a class of ours**, `concierge-hidden` — never Jellyfin
  Enhanced's `section-hidden`. Sharing that class would mean its Seerr-only
  filter un-hiding our sections and ours un-hiding its.
- **Every hidden section is remembered**, and only those are restored. The
  restore walks the list it recorded, never a selector, so it can never sweep up
  something hidden by somebody else.
- **Nothing is hidden unless Concierge actually has something to show.** A failed
  search, an empty answer, or a query below three characters restores the page
  first. There is no sequence that leaves you looking at nothing.

All four are tests, not intentions.

## Also in this release line

If you have not seen 0.15.0.0 yet, it is where the row learned to show its work:
skeleton cards while the free answer loads, a light sweep across the posters
while the model ranks, and cards that slide into their ranked positions when the
answer lands. Concierge mode makes all of that considerably more visible, because
now it is the only thing on the page.

329 tests.
