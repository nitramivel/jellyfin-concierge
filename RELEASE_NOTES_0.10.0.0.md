# Concierge 0.10.0.0 — in the search bar, as cards, above the requests

**This replaces 1.0.0.0, which has been withdrawn.** That version number was
wrong: it was picked for the milestone — results reached the search bar — when
the plan's own gate says otherwise. The eval set has no expected answers filled
in, search quality has never been measured, and a search still takes about
twenty-five seconds. A 1.0 claims done and good; this is neither yet. Back to
0.x until the numbers say otherwise.

Because Jellyfin compares versions numerically, it will not offer this as an
update over 1.0.0.0. **Uninstall Concierge, restart, then install 0.10.0.0 from
the catalogue.** Your index, enrichment, config and query log all live under
`data/concierge/` and survive that untouched.

## Search from Jellyfin's own search box

Type as normal. Native results appear instantly and unchanged; Concierge's row
fills in below after a moment, each poster carrying the reason it matched. Quote
a line in quotation marks and a **Said in…** row appears with the minute.

Three or more characters, and it waits 450 ms after you stop typing — so a title
lookup settles without ever asking, and holding a key down costs nothing.

## What changed since the withdrawn build

**Results are poster cards now.** They were a plain text list — the same markup
the settings page uses, which is right for a settings page and wrong for a page
made entirely of posters. It read as bolted on, because it was.

**They sit above the Jellyseerr rows, not below four of them.** Concierge results
are things you *own*. Putting them underneath a stack of things you don't was
exactly backwards. The position is re-checked on every render, because those
sections arrive asynchronously — set it once and Seerr lands afterwards and
jumps back on top.

This still keeps the rule the injection was built on: **the script only ever
touches nodes it created.** Moving our own node above theirs reads their markup
to find a landmark and modifies none of it. If the Jellyseerr section ever
shifts or disappears, that rule was broken and it is Concierge's fault.

## Why injection at all

`PLAN.md`'s open question 6 asked whether a plugin could supply results through
`/Search/Hints` instead of injecting anything. That would have been strictly
better — no DOM work, no risk to anything else on the page, mobile apps for
free. Two findings closed it:

- **Jellyfin Enhanced doesn't use it.** Zero references in its assembly, no
  `ISearchEngine`. Its Jellyseerr results are client-side DOM work on
  `#searchPage`.
- **Neither does the web client.** Three days of server logs contain **zero**
  `/Search/Hints` requests — the search page fetches `/Items` with a search term.
  A plugin supplying hints would never be called by the surface you actually use.

Failure stays silent by design. If the search request fails, nothing appears:
native results are already on screen and correct, and an error banner under them
would make a working search look broken. The injection itself is one `<script>`
tag before `</body>`, and every failure path serves the original page untouched.

278 tests.

## Upgrading

Uninstall, restart, install 0.10.0.0, then **hard-refresh the web client**
(Ctrl+Shift+R) — the browser caches the index page, and without it the script tag
won't be there yet.
