# Concierge 1.0.0.0 — it's in the search bar

Search from Jellyfin's own search box. Concierge results appear underneath the
native ones, with the reason each matched, and quoted lines show the film and the
minute.

## What I checked before building it

`PLAN.md`'s open question 6 asked whether a plugin could supply results through
`/Search/Hints` instead of injecting anything. That would have been strictly
better architecture — no DOM manipulation, no risk to anything else on the page,
and mobile apps for free. **Two findings closed it:**

- **Jellyfin Enhanced doesn't use it.** Zero references in its assembly, no
  `ISearchEngine`. Its Jellyseerr results are client-side DOM work on
  `#searchPage`.
- **Neither does the web client.** Three days of server logs contain **zero**
  `/Search/Hints` requests — the search page fetches `/Items` with a search term.
  A plugin supplying hints would never be called by the surface you actually use.

So injection, done the way the evidence pointed.

## Built to coexist, not to win

Your search page already has an owner. Jellyfin Enhanced adds the Jellyseerr
section that shows what you could request but don't have, and two scripts
re-rendering the same container is exactly how one silently erases the other.

**The rule this script is built on: it only ever touches nodes it created.**
Everything carries a `concierge-` prefix, everything is appended, and nothing
outside its own container is cleared, replaced or reordered. It appends to the
end of the page rather than computing a position, because a computed position
depends on what other plugins did first — which isn't ours to reason about.

Failure is silent by design. If the search request fails, nothing appears: native
results are already on screen and correct, and an error banner under them would
make a working search look broken.

The injection itself is one `<script>` tag before `</body>`, and every failure
path serves the original page untouched. A plugin that could break the page it
patches would take the whole web client down with it.

## Using it

Type into Jellyfin's search box as normal. Native results appear instantly and
unchanged; Concierge's section fills in below after a moment. Quote a line in
quotation marks and a **Said in…** section appears with timestamps.

Three or more characters, and it waits 450ms after you stop typing — so a title
lookup settles without ever asking, and holding a key down costs nothing.

## Also

- The client script is served from the plugin itself, so there's no second plugin
  to install and no ordering agreement with anything else patching web files.
- Styles inherit the theme's colours rather than declaring their own, so it looks
  native in whichever theme you're using.

278 tests.

## Upgrading

Drop-in, then **hard-refresh the web client** (Ctrl+Shift+R) — the browser caches
the index page, and without it the script tag won't be there yet.
