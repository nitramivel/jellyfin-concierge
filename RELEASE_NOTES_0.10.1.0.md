# Concierge 0.10.1.0 — the upgrade that silently did nothing

0.10.0.0 installed correctly, loaded correctly, and changed nothing you could
see. The poster cards were in the plugin; the browser was still running
1.0.0.0's script.

**Why.** The page asked for `/Concierge/client.js` — the same URL in every
release — and the server sent it with no cache headers at all. A browser that
had fetched it once had no reason to ever ask again, so the plugin upgraded
underneath a page that kept running the old client. Nothing logged, nothing
failed; the change just wasn't there.

**The fix.** The URL now carries a fingerprint of the script's own contents
(`/Concierge/client.js?v=…`), so it changes when and only when the script does —
an unchanged script still comes from cache across upgrades, a changed one cannot
be served stale. The response also carries `Cache-Control: no-cache` and an
entity tag, so a revalidation costs one small conditional request and answers
304 the rest of the time.

That is the same approach Jellyfin Enhanced takes for its own injected script,
which is a good sign it's the right one on this server.

**The index page needed the same treatment.** Jellyfin serves it with an entity
tag computed from the file on disk, which never changes when a plugin does — so
the browser would revalidate, get a 304, and go on using its cached copy of the
patched page, complete with whichever script URL it first saw. Concierge now
strips the validators from that request and tags the response it actually
produced, so a patched page is never stale either.

## Upgrading

Normal upgrade from 0.10.0.0 — install and reload. **No hard refresh needed,
this time or ever again.** That was the point.

If you're coming from the withdrawn 1.0.0.0, uninstall first and restart:
Jellyfin compares versions numerically and won't offer 0.10.x as an update.

288 tests, four of them specifically on this: the script URL must change when
the script does, the patched page must ask for that URL, a page already carrying
the tag must be left alone, and anything that isn't the client shell must be
untouched.
