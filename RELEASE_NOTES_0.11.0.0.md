# Concierge 0.11.0.0 — one search page, native-shaped rows

Concierge results now use the same horizontal row structure as Jellyfin 10.11's
own search sections. Posters scroll sideways instead of wrapping into a wall,
match explanations stay to two lines, and the full explanation remains available
on hover.

The row is additive only when it has something different to add. A query the
router decides is a native title lookup no longer gets a second copy of the same
film in a Concierge section. Natural-language and ambiguous queries still get
**Concierge matches**, and dialogue hits keep their separate **Said in…** row.

## Fewer searches for unfinished words

Jellyfin's native search waits 500 ms because its requests are free. Concierge
used to follow only 450 ms behind it, even though a full query can spend money.
That bought answers to prefixes such as `dark and tw` while the next letters were
already being typed.

Concierge now waits two seconds for the input to settle. Pressing Enter runs it
immediately when you know the query is finished. Pressing Enter after the timer
has already started the same request does not start a duplicate.

Changing the text also clears the old Concierge row and invalidates its response
immediately, so a slow answer for the previous query cannot appear under the new
words.

## Compatibility and upgrading

The client still writes only into `#concierge-results`; Jellyfin's rows and
Jellyfin Enhanced's Jellyseerr sections are never cleared, replaced, or reordered.
The Concierge section remains after the native library results and before the
Jellyseerr request rows.

Normal upgrade from 0.10.1.0. Reload the web client after installing; the
fingerprinted script URL makes a hard refresh unnecessary. No index rebuild and
no configuration changes are required.

292 tests passing, including checks for the native horizontal row shape, Native
route suppression, settled-query timing, Enter handling, duplicate in-flight
requests, and stale-result invalidation.
