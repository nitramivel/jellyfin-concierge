# Concierge 0.4.0.0 — the router was wrong about short queries

All of this comes from the first session of real searches. The good news first:
five description-shaped queries all returned results in **310–390ms** for
effectively nothing, which is comfortably inside the latency budget the design
was aiming at.

The bad news was three queries that returned nothing at all.

## `dark comedy` now works

`dark comedy`, `weed comedy` and `comedy` were all routed to Jellyfin's own
search and came back empty. The rule that did it read *"two words or fewer, no
function words → somebody is typing a title"*, which is right for `blade` and
`fargo` and completely wrong for `dark comedy`.

The fix uses something the router already knew: whether any word in the query
names anything in your library. `blade` does. `dark comedy` does not — so it is
not a title lookup, it is a two-word description, and it now goes to **both**
paths. Jellyfin's search still answers instantly and for free; Concierge adds
semantic results for one embedding call costing a rounding error.

`blade runner` is still two words and still a title, and still goes straight to
native. That case is pinned by a test, along with 33 others — the router now has
the query table the plan asked for, including every query typed on 1 August.

## The settings search box shows what you would really get

When the router sent a query to Jellyfin's own search, the box showed an empty
result and a sentence explaining that native had already answered it. Native's
answer was not on screen, so a correctly-routed title lookup looked exactly like
a failure.

The box now calls Jellyfin's search itself and shows both halves, labelled. A
`Both` route shows Concierge's results *and* Jellyfin's underneath.

## The query log records what came back

`10 results in 311ms` reads identically for a perfect search and a useless one,
and by the time anyone wonders which it was the results are gone. The log now
keeps the top five titles per query.

## Still not in the client search bar

Expected, and it is phase 2's headline. Concierge has no way into the Jellyfin
client's own search box yet — that needs the File Transformation injection layer,
and until it lands the settings page and the API are the only way in.

## Upgrading

Drop-in. No rebuild, no re-enrichment, no cost. The index is untouched.
