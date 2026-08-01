# Jellyfin Concierge handoff: search posters still do not render

Date: 2026-08-01  
Repository: `https://github.com/nitramivel/jellyfin-concierge`  
Branch: `main`  
Latest release: `v0.11.3.0`  
Latest code commit before this handoff: `022ea48`

## Read this first

The remaining issue is visual and has **not** been solved:

- Concierge search results now occupy a correctly sized horizontal poster row.
- Titles and explanations render at the bottom of each card.
- The entire poster area remains blank for every Concierge result.
- The owner also still considers the title/heading inset slightly too far right.
- Jellyfin Enhanced's adjacent **Discover on Seerr** row renders poster images normally.

Do not ship another speculative image-markup change. The next step must be to inspect
one real generated Concierge image URL and its browser network response.

## Current deployed state is confirmed

The server is `https://levithepirate.com` and runs Jellyfin `10.11.11`. The search
screen also has Jellyfin Enhanced `12.0.0.0`.

After the owner reported that `0.11.3.0` still had no images, the live client was
downloaded directly:

```bash
curl -fsS https://levithepirate.com/Concierge/client.js -o /tmp/concierge-live.js
sha256sum /tmp/concierge-live.js Jellyfin.Plugin.Concierge/Web/concierge.js
```

Both hashes were:

```text
373775d1e323c4f893b367f0e98f82872b3a3e14160f4e228ee86b32019f7f38
```

The live response had:

```text
cache-control: no-cache
etag: "373775d1e323c4f8"
cf-cache-status: DYNAMIC
```

The live script contains `getScaledImageUrl`, the `<img class="concierge-poster">`
element, and `options.api_key = token`. Therefore this is **not** explained by the
browser or server still serving a pre-`0.11.3.0` script.

Never record or paste the real `api_key` while debugging.

## Screenshot

`/home/levi/Pasted image.png` shows the established symptom:

- `Concierge matches` is a horizontal row.
- The row reserves full portrait-card height.
- Only titles and the two-line explanations appear.
- `Discover on Seerr` immediately below it has working posters.

That file predates the final token change, but the owner explicitly confirmed the
same missing-poster result after installing `0.11.3.0`.

## Release history for this issue

### `v0.11.0.0` — commit `2e7687e`

Changed results to native-looking horizontal rows. The first installed result still
looked like a raw, full-width text list.

### `v0.11.1.0` — commit `15055e1`

Copied the proven section/scroller shape and empty-state placement from Jellyfin
Enhanced's `Discover on Seerr` implementation:

- outer `verticalSection emby-scroller-container`
- inner `emby-scroller`
- `focuscontainer-x itemsContainer scrollSlider`
- insertion beside `.noItemsMessage` when native search is empty
- insertion before `.jellyseerr-section` when that section exists

This fixed the row structure. It did not produce posters.

### `v0.11.2.0` — commit `c438111`

- Switched from `ApiClient.getImageUrl` to Jellyfin 10.11's
  `ApiClient.getScaledImageUrl`.
- Replaced background-only rendering with a real
  `<img class="concierge-poster" src="...">`.
- Added scoped `width:100%`, `height:100%`, and `object-fit:cover`.
- Left-aligned card text.

The owner still saw no posters.

### `v0.11.3.0` — commit `022ea48`

A public request to a Jellyfin item-image route without authentication returned 403:

```bash
curl -sS -o /dev/null -w '%{http_code}\n' \
  'https://levithepirate.com/Items/00000000000000000000000000000000/Images/Primary?maxHeight=600'
# 403
```

The code was changed to append `ApiClient.accessToken()` as the `api_key` option in
the image URL. Card text padding was reduced to zero and heading padding to `2.5vw`.

The owner still sees no posters. The 403 observation only proved that an anonymous
request is rejected; it did **not** prove that lack of a query token was the actual
failure affecting the browser. Treat the earlier conclusion as disproven.

## Relevant code

Primary client file:

```text
Jellyfin.Plugin.Concierge/Web/concierge.js
```

Important areas in the current file:

- styles and alignment: lines 36-57
- placement relative to Seerr/no-results: lines 133-183
- `posterUrl`: lines 193-222
- card markup: lines 234-259
- row markup: lines 262-273
- result mapping: lines 276 onward

The search API does **not** return image metadata. Its hit type is:

```csharp
public sealed record SearchHit(
    Guid ItemId,
    string Name,
    int? Year,
    double Score,
    int? LexicalRank,
    int? VectorRank,
    string Why);
```

See `Jellyfin.Plugin.Concierge/Services/SearchService.cs:37`.

This omission is now the strongest architectural suspect. Jellyfin's native card
builder works with a `BaseItemDto`, checks `ImageTags.Primary`, and includes that tag
when it calls `getScaledImageUrl`. Concierge guesses an image URL from only an item
ID and never verifies that the current user can retrieve that item or that a primary
image exists.

Structural client tests are in:

```text
Jellyfin.Plugin.Concierge.Tests/ClientScriptTests.cs
```

They currently verify strings and safety invariants only. They cannot establish that
a URL returns an image or that the rendered card looks correct.

## Required next diagnostic

Use browser developer tools on one failing result. Do not infer this from source.

1. Inspect one `<img class="concierge-poster">`.
2. Confirm whether it has a non-empty `src`.
3. Confirm the URL path contains the result's real Jellyfin item ID.
4. Confirm an `api_key` query parameter exists, without copying its value anywhere.
5. In Network, record only:
   - HTTP status
   - response `Content-Type`
   - response length
   - whether the request was blocked by CSP, mixed content, or the browser
6. If the response is 200 with an image, inspect computed dimensions and stacking for:
   - `.concierge-poster`
   - `.cardImageContainer`
   - `.cardScalable`
7. If `src` is empty, evaluate only the booleans/types—not secrets:

   ```js
   typeof ApiClient
   typeof ApiClient.getScaledImageUrl
   typeof ApiClient.accessToken
   Boolean(ApiClient.accessToken && ApiClient.accessToken())
   ```

8. Compare the failing real URL's route/query keys with a working native Jellyfin
   poster request from another page on the same server.

The next session should ask the owner for those status/type observations if it cannot
access the authenticated browser itself. Do not ask for or expose the token.

## Recommended implementation direction after diagnosis

The likely robust fix is to hydrate ranked hits with Jellyfin's real item DTOs before
building cards:

1. Collect the Concierge hit IDs in rank order.
2. Make one authenticated client request with `ApiClient.getItems(currentUserId, ...Ids...)`.
3. Map the returned DTOs back to the ranked hits.
4. Use each DTO's `ImageTags.Primary` in `getScaledImageUrl`.
5. Preserve Concierge's `Why` text and rank order.
6. Drop any item the current user cannot retrieve. This also closes the current gap
   where the search index can name an item without confirming user visibility.

If normal image URLs still cannot load but authenticated API requests succeed, fetch
each image through `ApiClient.ajax`, convert the response blob to an object URL, and
revoke object URLs whenever the row is cleared. Guard asynchronous image responses
with the same stale-query token used by search results.

Using Jellyfin's native card builder would be preferable if its module can be loaded
reliably from an injected non-bundled script. Do not assume that module is a stable
global; verify it on this exact 10.11.11 client first.

## Alignment note

The owner still reports that the title is slightly too far right. It is ambiguous
whether “title” means the `Concierge matches` heading or each result title. Current
CSS already sets:

```css
#concierge-results .concierge-heading { padding-left: 2.5vw !important; }
#concierge-results .concierge-card .cardText {
    text-align: left !important;
    padding-left: 0;
}
```

Inspect computed box positions rather than reducing both again. Compare `left` for
the Concierge heading, first card, result title, and the corresponding Seerr nodes.

## Invariants to preserve

- Concierge only writes to DOM nodes it created.
- Never clear, remove, replace, or rewrite Jellyfin/Seerr containers.
- Concierge results remain additive to native results.
- Native title routes do not get duplicate Concierge rows.
- A newer query must prevent an older response from repainting the page.
- Paid searches retain the settled-query debounce; Enter remains immediate.
- Escape every server/library value interpolated into markup.
- Do not leak access tokens into logs, test output, screenshots, or handoffs.

## Verification and release workflow

Run:

```bash
/home/levi/.dotnet/dotnet test Jellyfin.Plugin.Concierge.sln -c Release --no-restore
git diff --check
```

Current baseline: **294 tests passed**.

For a real fix, use the next bug-fix version (`0.11.4.0`), run `build/release.sh`, verify
the zip MD5 equals the manifest checksum, commit, tag, push, create the GitHub release,
and download the published asset once to verify its checksum. Do not create a separate
`RELEASE_NOTES_*.md`; release notes belong in `manifest.json` and the GitHub release.

## Repository state at handoff creation

Before adding this file, `main` was clean and synchronized with `origin/main` at
`022ea48`. This handoff itself should be committed and pushed, but it should not create
a plugin release because it changes no shipped code.
