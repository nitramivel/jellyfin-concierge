# Concierge 0.11.4.0 — the posters were being hidden by one line of our own CSS

Four releases chased blank posters through markup, image APIs and authentication.
None of it was the cause. This was:

```css
#concierge-results .cardImageContainer { position: relative; }
```

Jellyfin's `.cardContent` — the element our poster anchor is — is
`position:absolute` with `contain:strict` and `height:100%`, inset to fill
`.cardScalable`, whose height comes entirely from the sibling `.cardPadder`. That
one rule of ours was more specific, so it put the box back into normal flow,
where a percentage height has no definite parent to resolve against and
`contain:size` makes the answer zero. The card kept its full portrait height from
the padder while the image area collapsed to nothing — a correctly sized row of
correctly sized cards with a blank space where the poster goes.

It was added in 0.10.0.0 to give the quote timestamp a positioned ancestor.
`.cardContent` was already `position:absolute`, which is a positioned ancestor.
The rule was never needed and did nothing but harm.

## Also fixed

**The access token is out of the image URLs.** 0.11.3.0 added it after a bare
image request answered 403 — but that request used an all-zeros GUID, so the 403
was Jellyfin refusing an item that does not exist, not refusing an anonymous
caller. Measured against a real item on this server:

```
/Items/e910fc1406cb2b9717a41c6b70d67265/Images/Primary?maxHeight=330
  -> 200 image/jpeg, 62,115 bytes
```

Jellyfin's image routes allow anonymous access by design. Putting a token in a
`src` attribute leaks it into the DOM, the referrer, and every proxy log in
between, to buy nothing.

**Posters are painted as background images again.** `.cardImageContainer` and
`.coveredImage` are background-image classes in Jellyfin's own stylesheet —
`background-size:cover`, `background-position:50%`, `background-clip:content-box`.
Both the client's own cards and Jellyfin Enhanced's Seerr cards fill them that
way. Using their path means their rules do the work instead of ours competing.

**The heading sits where every other heading sits.** The hand-picked `2.5vw`
override is gone; the row uses Jellyfin's own `padded-left`, which is 3.3%. It
now lines up with *Discover on Seerr* and with the native rows exactly.

**A settling query dims the row instead of emptying it.** Between the two-second
debounce and a five-to-nine-second search, blanking on every keystroke left the
section empty for the best part of ten seconds — which reads as broken rather
than busy. It now fades to 45% while the newer answer is on its way, and is only
emptied when the query drops below three characters, where no answer is coming.

## The lesson, written down

Every one of these was our CSS or our URL fighting something Jellyfin already
does correctly. The tests now assert the *absence* of the overrides rather than
the presence of a workaround, so the next attempt to out-clever the client's own
stylesheet fails in CI instead of in a browser four releases later.

297 tests.
