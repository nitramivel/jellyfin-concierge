# Concierge 0.18.0.0 — Matches, at the top, saying what it is doing

## It is called Matches now

Not "Concierge matches". The row is on your search page under a heading you
already know is Concierge — saying so twice was branding, not information.

## It sits at the top

Above the library's own Movies and Shows rows, not just above Seerr. Concierge
answers the question you actually asked — a description rather than a substring —
so putting it under the rows that matched on spelling made you scroll past the
worse answer to reach the better one. The native results are still right there,
unchanged, one row down.

It anchors on the **first** result row rather than on any particular plugin's, so
a new row from something else installed later cannot end up above it. And on a
search where Jellyfin found nothing, the row now sits *above* the "no results"
line rather than below it — an answer underneath a notice saying there is no
answer reads as a contradiction.

## The status says which wait you are in

Where the "ranking…" dots were, the row now names the stage:

- **searching…** — the free keyword answer is on its way, about 250 ms
- **ranking…** — that answer is on screen and the model is reordering it
- *(nothing)* — done

Those are different waits. One is free and about to end; the other is the model
and costs money. A row that says which is a row you can decide to stop waiting
on.

The status is written as text into a node that is always there, so it can change
while cards are on screen. Re-rendering the heading to say "searching" would have
thrown away the results you were looking at, which is the opposite of the point.

## The wait before spending is yours to set

**Wait before spending (milliseconds)**, on the Spending tab. Default 2,000.

Jellyfin's own search waits 500 ms because its requests are free. This one costs
money, so it waits for a query you have finished typing rather than a slightly
later copy of every prefix — raising it to 2,000 ms cut half-typed searches from
28 of 89 down to 5 of 30 on this server.

**Lowering it does not make the row faster**, and raising it does not make it
slower: the free preview fills the row in about 250 ms either way. This setting
only moves the moment the *ranked* order arrives. Enter always searches
immediately.

The value is substituted into the client script as it is served, and the script's
URL carries a hash of what was actually served — so changing the setting changes
the URL and your browser cannot keep the old number. Same mechanism that stopped
0.10.0.0 shipping invisibly.

332 tests.
