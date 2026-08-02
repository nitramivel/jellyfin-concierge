/*
 * Concierge — client script.
 *
 * THE ONE RULE: this script only ever touches DOM nodes it created itself.
 *
 * The Jellyfin search page is shared property. On this server Jellyfin Enhanced
 * already owns parts of it, including the Jellyseerr section showing what you
 * could request but do not own. Two scripts re-rendering the same container is
 * how one silently wipes the other, and breaking a feature somebody uses for one
 * they are still evaluating is a bad trade.
 *
 * So: every element here carries a "concierge-" prefix, and nothing outside our
 * own container is ever cleared, replaced, or reordered. We move our own node to
 * sit above theirs and read theirs only to find the landmark. If our section is
 * missing we add it; we never assume it is the only thing there.
 *
 * THE ONE EXCEPTION is Concierge mode, which the owner turns on deliberately: it
 * adds a class of OURS to the library's own sections so they can be hidden, and
 * removes it again from exactly those nodes. See applyMode. Nothing else in this
 * file writes to a node it did not create, and the tests hold that line.
 */
(function () {
    'use strict';

    var CONTAINER_ID = 'concierge-results';
    var TOGGLE_ID = 'concierge-toggle';
    var MODE_KEY = 'concierge-mode';

    /* Concierge mode.
     *
     * THE ONE RULE IS RELAXED HERE, DELIBERATELY, AND ONLY HERE. Everywhere else
     * this script touches nothing it did not create. Concierge mode adds one class
     * of ours to the library's own sections so they can be hidden, which is a
     * decision the owner made rather than something that crept in.
     *
     * Three things keep it honest:
     *   - The class is OURS ("concierge-hidden"), never Jellyfin Enhanced's
     *     "section-hidden". Sharing their class would mean their Seerr-only filter
     *     un-hiding our sections and ours un-hiding theirs.
     *   - Every element hidden is remembered, and only those are restored. We never
     *     un-hide something we did not hide.
     *   - Nothing is hidden unless our row actually has something in it, so a failed
     *     search can never leave an empty page behind.
     *
     * The Jellyseerr rows stay visible on purpose: a search that finds nothing you
     * own should still offer what you could request, without leaving the mode. */
    var hiddenSections = [];
    // Jellyfin's own search waits 500ms because its requests are free. Concierge's
    // full path spends money, so it needs a genuinely settled query rather than a
    // slightly later copy of every prefix the native client searched. Enter still
    // runs immediately for somebody who has finished typing deliberately.
    var DEBOUNCE_MS = 2000;

    // The free half of the pipeline — keyword retrieval, no embedding, no model —
    // answers in about a millisecond. Measured on this library: 0 ms median, 110 ms
    // worst, against 6.4 s for the full path. There is no reason to look at an empty
    // row for six seconds when a real answer already exists, so the preview fires
    // almost immediately and the paid answer replaces it when it lands.
    var PREVIEW_MS = 250;

    // Long enough that a title lookup is settled before we ask. Native results
    // render on their own timeline regardless — hard rule 2 — so this delay costs
    // the user nothing, it only avoids asking about half-typed words.
    var MIN_QUERY_LENGTH = 3;

    /* Styles are injected by the script rather than shipped as a stylesheet, so
     * there is one file to serve and no second request. Everything is scoped under
     * our own prefix and inherits the theme's colours rather than declaring its
     * own, so it looks native in whichever theme is active. */
    var STYLES =
        // Card size, spacing and hover all come from the client's own card rules.
        // Only what is genuinely ours is declared here: the reason line, the
        // timestamp badge, and the degraded note.
        // The reason line is the only card text that is ours rather than Jellyfin's.
        // ".cardText" is nowrap with an ellipsis, which is right for a title and
        // wrong for a sentence, so this is the one place we override — clamped to
        // two lines, with a floor so a short reason and a long one leave their cards
        // the same height.
        '#concierge-results .concierge-why{opacity:.72;font-size:.82em;' +
        'white-space:normal;display:-webkit-box;-webkit-box-orient:vertical;' +
        '-webkit-line-clamp:2;overflow:hidden;min-height:2.3em;}' +
        '#concierge-results .concierge-note{opacity:.6;font-size:.7em;font-weight:400;}' +
        '#concierge-results .concierge-degraded{opacity:.65;font-size:.85em;' +
        'margin:.4em 0 0 .8em;}' +
        // A newer query is settling. Dimming beats blanking: emptying the row on
        // every keystroke left a hole for the two-second debounce plus six seconds
        // of query, which reads as broken rather than as busy.
        '#concierge-results.concierge-pending{opacity:.55;transition:opacity .15s;}' +

        // The light sweep that says work is happening. One animation drives both the
        // skeleton cards and the shimmer over real posters, so there is a single
        // rhythm on the row rather than two things blinking out of step.
        '@keyframes concierge-sweep{0%{transform:translateX(-120%);}' +
        '100%{transform:translateX(220%);}}' +
        '#concierge-results .concierge-card .cardScalable{overflow:hidden;}' +
        '#concierge-results.concierge-working .concierge-card .cardScalable::after{' +
        'content:"";position:absolute;inset:0;z-index:2;pointer-events:none;' +
        'background:linear-gradient(105deg,transparent 35%,rgba(255,255,255,.16) 50%,' +
        'transparent 65%);animation:concierge-sweep 1.5s ease-in-out infinite;}' +

        // Skeletons for the moment before even the free answer is back. A row that
        // is visibly getting ready reads better than a row that is not there.
        '#concierge-results .concierge-skeleton .cardImageContainer{' +
        'background:rgba(127,127,127,.18);}' +
        '#concierge-results .concierge-skeleton .cardText{height:.9em;margin:.35em .4em;' +
        'border-radius:.2em;background:rgba(127,127,127,.18);}' +
        '#concierge-results .concierge-skeleton .cardText-secondary{width:35%;}' +
        '#concierge-results .concierge-skeleton .concierge-why{width:75%;}' +

        // Three dots, counting, in the heading. Small enough to be a status and not
        // a decoration.
        '@keyframes concierge-blink{0%,80%,100%{opacity:.25;}40%{opacity:1;}}' +
        '#concierge-results .concierge-dots span{animation:concierge-blink 1.4s infinite;}' +
        '#concierge-results .concierge-status.is-idle{display:none;}' +
        '#concierge-results .concierge-dots span:nth-child(2){animation-delay:.2s;}' +
        '#concierge-results .concierge-dots span:nth-child(3){animation-delay:.4s;}' +

        // Cards slide to their ranked positions rather than teleporting.
        '#concierge-results .concierge-card{will-change:transform;}' +

        // Anyone who has asked their system to stop moving things gets the states
        // without the motion. The information is in the dimming and the label; the
        // animation is only how it is delivered.
        '@media(prefers-reduced-motion:reduce){' +
        '#concierge-results.concierge-working .concierge-card .cardScalable::after,' +
        '#concierge-results .concierge-dots span{animation:none;}' +
        '#concierge-results .concierge-card{transition:none!important;}}' +
        '#concierge-results .concierge-stamp{position:absolute;right:.4em;bottom:.4em;' +
        'background:rgba(0,0,0,.72);color:#fff;border-radius:.25em;padding:.1em .4em;' +
        'font-size:.78em;}';

    var MODE_STYLES =
        '.concierge-hidden{display:none!important;}' +

        // The chip. Shaped like the client's own pill controls rather than invented,
        // and it states which mode it is IN, not which one it would switch to.
        '#concierge-toggle{display:inline-flex;align-items:center;gap:.4em;' +
        'margin:0 0 0 .6em;padding:.32em .8em;border:1px solid currentColor;' +
        'border-radius:1.2em;background:transparent;color:inherit;cursor:pointer;' +
        'font:inherit;font-size:.86em;opacity:.62;vertical-align:middle;' +
        'transition:opacity .15s,background-color .15s;}' +
        '#concierge-toggle:hover{opacity:.9;}' +
        '#concierge-toggle[aria-pressed="true"]{opacity:1;' +
        'background:rgba(127,127,127,.22);}' +

        // Full mode: the row stops being a row. A grid that fills the page is the
        // whole reason for taking it over — a horizontal strip in a page with
        // nothing else in it would just be a strip with white space under it.
        '#concierge-results.concierge-full .concierge-scroller{overflow:visible;}' +
        '#concierge-results.concierge-full .concierge-row{display:grid;' +
        'grid-template-columns:repeat(auto-fill,minmax(10.5em,1fr));gap:.2em 0;' +
        'overflow:visible;}' +
        '#concierge-results.concierge-full .concierge-card{width:auto;}' +

        // Room to say why properly, now that there is room.
        '#concierge-results.concierge-full .concierge-why{-webkit-line-clamp:4;' +
        'min-height:0;font-size:.84em;}' +
        '#concierge-results.concierge-full .concierge-heading{font-size:1.4em;}';

    function ensureStyles() {
        if (document.getElementById('concierge-styles')) {
            return;
        }

        var style = document.createElement('style');
        style.id = 'concierge-styles';
        style.textContent = STYLES + MODE_STYLES;
        document.head.appendChild(style);
    }

    var timer = null;
    var previewTimer = null;
    var lastQuery = '';
    var inFlight = null;
    var inFlightQuery = '';

    // The query whose full answer is on screen. A preview must never overwrite the
    // better answer to the same query — the requests are independent and the cheap
    // one can land second.
    var settledQuery = '';

    function log() {
        if (window.ConciergeDebug) {
            console.log.apply(console, ['[Concierge]'].concat([].slice.call(arguments)));
        }
    }

    function escapeHtml(value) {
        return String(value === null || value === undefined ? '' : value)
            .replace(/&/g, '&amp;').replace(/</g, '&lt;').replace(/>/g, '&gt;')
            .replace(/"/g, '&quot;');
    }

    function searchPage() {
        // ":not(.hide)" matters: Jellyfin keeps pages in the DOM and hides them,
        // so without it we would find a stale search page on every other view.
        return document.querySelector('#searchPage:not(.hide)');
    }

    /* Our container, created once and reused. Never removed by us — the client
     * disposes the page and takes it along, which is the correct lifetime. */
    function container() {
        var page = searchPage();
        if (!page) {
            return null;
        }

        var el = page.querySelector('#' + CONTAINER_ID);

        if (!el) {
            el = document.createElement('div');
            el.id = CONTAINER_ID;
            // This is the exact outer shape used by Jellyfin Enhanced's working
            // "Discover on Seerr" search row. The scroller-container class is not
            // ornamental: Jellyfin scopes part of the card-row layout through it.
            el.className = 'verticalSection emby-scroller-container concierge-section';
        }

        position(el, page);
        return el;
    }

    /* Concierge results are things you OWN, so they belong with the rest of your
     * library and above anything offering things you do not. Appending to the end
     * of the page put them below four rows of Jellyseerr discovery, which is
     * exactly backwards.
     *
     * The landmark is ".jellyseerr-section" — read out of Jellyfin Enhanced 12.0.0.0
     * rather than guessed, along with the fact that it REMOVES and recreates that
     * node on every keystroke and re-positions itself after the last Movies/Shows
     * section. So the anchor is re-queried on every render; holding a reference to
     * it would mean holding a node that has already been thrown away.
     *
     * This still obeys the one rule: insertBefore moves OUR node, and reads theirs
     * only to find a landmark. Nothing of theirs is modified.
     *
     * There is no fight over the slot. They position once per search, then their
     * observer disconnects; ours runs on every render and lands ~20s later, so we
     * settle above them and stay there. */
    function position(el, page) {
        // Above every result row, always: ours, then the library's, then Seerr's.
        //
        // Concierge answers the question that was actually asked — a description
        // rather than a substring — so burying it under the rows that matched on
        // spelling makes the reader scroll past the worse answer to reach the better
        // one. Native results are still right there, unchanged and one row down.
        //
        // Still only OUR node moves. The others are read to find a landmark and are
        // never touched, which is what the tests hold.
        var first = firstResultSection(page, el);

        if (first && first.parentNode) {
            if (el.nextElementSibling !== first || el.parentNode !== first.parentNode) {
                first.parentNode.insertBefore(el, first);
            }

            return;
        }

        // Nothing matched natively. Above the "no results" line rather than below it:
        // Concierge may well have found something, and an answer underneath a notice
        // saying there is no answer reads as a contradiction.
        var noResults = page.querySelector('.noItemsMessage');

        if (noResults && noResults.parentNode) {
            if (el.nextElementSibling !== noResults || el.parentNode !== noResults.parentNode) {
                noResults.parentNode.insertBefore(el, noResults);
            }

            return;
        }

        var results = page.querySelector(
            '.searchResults, [class*="searchResults"], .padded-top.padded-bottom-page');
        var parent = results || page;

        if (el.parentNode !== parent || parent.firstElementChild !== el) {
            parent.insertBefore(el, parent.firstChild);
        }
    }

    // The first result row on the page, whoever owns it — ours excluded, or we would
    // spend every render trying to insert ourselves before ourselves.
    function firstResultSection(page, el) {
        var sections = page.querySelectorAll('.verticalSection');

        for (var i = 0; i < sections.length; i++) {
            if (sections[i] !== el) {
                return sections[i];
            }
        }

        return null;
    }

    function modeOn() {
        try {
            return window.localStorage.getItem(MODE_KEY) === '1';
        } catch (e) {
            // Private browsing, or storage disabled. The mode simply does not
            // persist; it must not take the search box down with it.
            return false;
        }
    }

    function setMode(on) {
        try {
            window.localStorage.setItem(MODE_KEY, on ? '1' : '0');
        } catch (e) { /* see above */ }
    }

    /* Hide the library's own rows, remembering exactly which ones.
     *
     * Only ever called with something already in our row, so a search that fails or
     * returns nothing cannot leave a page with nothing on it. Jellyseerr is skipped
     * by name: those are things you could request, which is still worth seeing when
     * the library has nothing. */
    function applyMode(page) {
        var el = page.querySelector('#' + CONTAINER_ID);
        var showing = !!el && el.innerHTML !== '';
        var wanted = modeOn() && showing;

        if (el) {
            el.classList.toggle('concierge-full', wanted);
        }

        if (!wanted) {
            restoreSections();
            return;
        }

        Array.prototype.forEach.call(page.querySelectorAll('.verticalSection'), function (s) {
            if (s.id === CONTAINER_ID
                || s.classList.contains('jellyseerr-section')
                || s.classList.contains('concierge-hidden')) {
                return;
            }

            s.classList.add('concierge-hidden');
            hiddenSections.push(s);
        });
    }

    // Only what we hid, and only our class. Anything else on the page that is
    // hidden was hidden by somebody else and is not ours to reveal.
    function restoreSections() {
        for (var i = 0; i < hiddenSections.length; i++) {
            hiddenSections[i].classList.remove('concierge-hidden');
        }

        hiddenSections = [];
    }

    function toggle(page) {
        var fields = page.querySelector('.searchFields .inputContainer')
            || page.querySelector('.searchFields')
            || page;

        if (page.querySelector('#' + TOGGLE_ID)) {
            return;
        }

        var button = document.createElement('button');
        button.id = TOGGLE_ID;
        button.type = 'button';
        button.title = 'Show only what Concierge found in your library';
        button.setAttribute('aria-pressed', modeOn() ? 'true' : 'false');
        button.textContent = '\u2726 Concierge';

        button.addEventListener('click', function () {
            var next = !modeOn();
            setMode(next);
            button.setAttribute('aria-pressed', next ? 'true' : 'false');

            var current = searchPage();
            if (current) {
                applyMode(current);
            }
        });

        fields.appendChild(button);
    }

    function itemLink(id) {
        return '#/details?id=' + encodeURIComponent(id);
    }

    /* No access token in the URL.
     *
     * An earlier build appended one after a bare image request answered 403 — but
     * that request used an all-zeros GUID, so the 403 was Jellyfin refusing an item
     * that does not exist, not refusing an anonymous caller. Measured against a
     * real item on this server:
     *
     *   /Items/e910fc1406cb2b9717a41c6b70d67265/Images/Primary?maxHeight=330
     *     -> 200 image/jpeg, 62,115 bytes
     *
     * Jellyfin's image routes allow anonymous access by design, which is why an
     * <img> can load one at all. Putting the token in a src attribute would leak it
     * into the DOM, referrers and every proxy log between here and the browser, to
     * buy nothing. */
    function posterUrl(itemId) {
        if (!window.ApiClient) {
            return '';
        }

        try {
            // Jellyfin 10.11's own card builder uses getScaledImageUrl. Prefer the
            // same API, retaining getImageUrl only for older web clients which do
            // not expose the scaled helper.
            var getUrl = window.ApiClient.getScaledImageUrl || window.ApiClient.getImageUrl;

            return getUrl
                ? getUrl.call(window.ApiClient, itemId, { type: 'Primary', maxHeight: 600 })
                : '';
        } catch (e) {
            return '';
        }
    }

    /* Jellyfin's own card markup rather than a list. The search page is made of
     * poster cards, and a bare text list on it reads as something bolted on —
     * which is what the owner saw.
     *
     * This class combination is copied from Jellyfin Enhanced's Jellyseerr cards,
     * which demonstrably render correctly on this exact client build — a proven
     * combination beats one invented from reading the CSS. Sizing, spacing and
     * hover come from the client's own rules, so these track the theme instead of
     * drifting from it. The classes are the client's; we only ever put them on
     * elements we created ourselves. */
    function card(itemId, title, subtitle, why, stamp) {
        var url = posterUrl(itemId);

        // Background image, not an <img>. ".cardImageContainer" and ".coveredImage"
        // exist in Jellyfin's stylesheet precisely to paint one — background-size:
        // cover, background-position:50%, background-clip:content-box — and both
        // the client's own cards and Jellyfin Enhanced's Seerr cards fill them this
        // way on this page. Using their path means their rules do the work.
        var image = 'background-color:rgba(127,127,127,.18);'
            + (url ? 'background-image:url(\'' + escapeHtml(url) + '\');' : '');
        var href = escapeHtml(itemLink(itemId));

        return '<div data-concierge-id="' + escapeHtml(itemId) + '"'
            + ' class="card overflowPortraitCard card-hoverable card-withuserdata concierge-card">'
            + '<div class="cardBox cardBox-bottompadded">'
            + '<div class="cardScalable">'
            + '<div class="cardPadder cardPadder-overflowPortrait"></div>'
            + '<a class="cardImageContainer coveredImage cardContent" href="' + href
            + '" style="' + image + '">'
            + (stamp ? '<div class="concierge-stamp">' + escapeHtml(stamp) + '</div>' : '')
            + '</a>'
            + '</div>'
            // Three lines, in the order the native Movies and Shows rows use them:
            // title tight under the poster, then the year at 86% in
            // ".cardText-secondary", then — ours alone — why it matched. No
            // "cardTextCentered": ".cardText" is already left-aligned for ltr, and
            // adding the centring class only to override it again with !important
            // was us arguing with the stylesheet over an alignment it had right.
            + '<div class="cardText cardText-first"><bdi>' + escapeHtml(title) + '</bdi></div>'
            + (subtitle
                ? '<div class="cardText cardText-secondary"><bdi>'
                    + escapeHtml(subtitle) + '</bdi></div>'
                : '')
            + (why
                ? '<div class="cardText concierge-why" title="'
                    + escapeHtml(why) + '">' + escapeHtml(why) + '</div>'
                : '')
            + '</div></div>';
    }

    /* A card-shaped placeholder. Same markup as a real card so the row does not
     * change height or spacing when the real ones arrive — a layout that jumps at
     * the moment the answer lands undoes the point of showing anything early. */
    function skeletonCards(count) {
        var one = '<div class="card overflowPortraitCard concierge-card concierge-skeleton">'
            + '<div class="cardBox cardBox-bottompadded"><div class="cardScalable">'
            + '<div class="cardPadder cardPadder-overflowPortrait"></div>'
            + '<div class="cardImageContainer coveredImage cardContent"></div>'
            + '</div>'
            + '<div class="cardText cardText-first"></div>'
            + '<div class="cardText cardText-secondary"></div>'
            + '<div class="cardText concierge-why"></div>'
            + '</div></div>';

        var all = '';
        for (var i = 0; i < count; i++) {
            all += one;
        }

        return all;
    }

    /* The status line, in the heading.
     *
     * Always present, even when empty, so that changing it later is a text edit on a
     * node we already own rather than a re-render of the row. That matters: the
     * status has to change while results are on screen, and re-rendering to say
     * "searching" would throw away the cards the reader is currently looking at.
     *
     * It names the stage rather than just animating. "searching" and "ranking" are
     * different waits — one is free and about to end, the other is the model — and a
     * row that says which is a row you can decide to stop waiting on. */
    function statusLabel(text) {
        return ' <span class="concierge-note concierge-status' + (text ? '' : ' is-idle') + '">'
            + '<span class="concierge-statustext">' + escapeHtml(text || '') + '</span>'
            + '<span class="concierge-dots"><span>.</span><span>.</span><span>.</span></span>'
            + '</span>';
    }

    // Text, not markup. The status changes while cards are on screen, so it is set
    // by writing to a text node we already own — no innerHTML anywhere but our own
    // container, which is the rule the whole script is held to.
    function setStatus(text) {
        var el = ourSection();
        var slot = el && el.querySelector('.concierge-status');

        if (!slot) {
            return;
        }

        slot.querySelector('.concierge-statustext').textContent = text || '';
        slot.classList.toggle('is-idle', !text);
    }

    function section(heading, cards) {
        // Match Jellyfin 10.11's SearchResultsRow rather than merely borrowing its
        // card classes. One ranked result set is one horizontal row: it does not
        // grow into a poster wall that pushes the native results off the screen.
        return '<h2 class="sectionTitle sectionTitle-cards focuscontainer-x padded-left padded-right'
            + ' concierge-heading">' + heading + '</h2>'
            + '<div is="emby-scroller" data-horizontal="true" data-centerfocus="card"'
            + ' class="padded-top-focusscale padded-bottom-focusscale emby-scroller'
            + ' concierge-scroller">'
            + '<div is="emby-itemscontainer"'
            + ' class="focuscontainer-x itemsContainer scrollSlider concierge-row">'
            + cards + '</div></div>';
    }

    function renderHits(result) {
        return (result.Hits || []).map(function (hit) {
            return card(
                hit.ItemId,
                hit.Name,
                hit.Year ? String(hit.Year) : '',
                hit.Why || '',
                null);
        }).join('');
    }

    function renderQuotes(result) {
        if (!result.Quotes || !result.Quotes.length) {
            return '';
        }

        var cards = result.Quotes.slice(0, 8).map(function (q) {
            var at = Math.floor((q.Position || 0) / 10000000);
            var mins = Math.floor(at / 60);
            var secs = at % 60;
            var stamp = mins + ':' + (secs < 10 ? '0' : '') + secs;

            // The timestamp takes the year's slot: on a quote card it is the piece of
            // secondary information that belongs directly under the title.
            return card(q.ItemId, q.Title, stamp, '\u201c' + (q.Line || '') + '\u201d', stamp);
        }).join('');

        return section('Said in\u2026', cards);
    }

    /* Cards slide from where they were to where the ranking put them.
     *
     * Measure every card, let the row be replaced, measure again, then put each card
     * back where it started with a transform and release it on the next frame. The
     * browser animates one property it can do on the compositor, so a row of twenty
     * posters reorders without touching layout twice.
     *
     * This is the only moment in the plugin where the model's work is visible as
     * work: the free answer was already right about WHAT matched, and what arrives
     * is a better opinion about the order. Showing that as movement says so more
     * honestly than replacing the row and hoping somebody noticed. */
    function positionsOf(el) {
        var map = {};

        Array.prototype.forEach.call(
            el.querySelectorAll('.concierge-card[data-concierge-id]'),
            function (card) {
                map[card.getAttribute('data-concierge-id')] = card.getBoundingClientRect().left;
            });

        return map;
    }

    function slideFrom(el, before) {
        if (window.matchMedia && window.matchMedia('(prefers-reduced-motion: reduce)').matches) {
            return;
        }

        var moved = [];

        Array.prototype.forEach.call(
            el.querySelectorAll('.concierge-card[data-concierge-id]'),
            function (card) {
                var was = before[card.getAttribute('data-concierge-id')];
                if (was === undefined) {
                    return;
                }

                var delta = was - card.getBoundingClientRect().left;
                if (Math.abs(delta) < 2) {
                    return;
                }

                card.style.transition = 'none';
                card.style.transform = 'translateX(' + delta + 'px)';
                moved.push(card);
            });

        if (!moved.length) {
            return;
        }

        // Two frames: one for the browser to accept the starting offset, one to let
        // the transition it is about to gain actually have something to run from.
        requestAnimationFrame(function () {
            requestAnimationFrame(function () {
                moved.forEach(function (card) {
                    card.style.transition = 'transform .38s cubic-bezier(.2,.7,.3,1)';
                    card.style.transform = '';
                });
            });
        });
    }

    function render(result, provisional) {
        var el = container();
        if (!el) {
            return;
        }

        // A preview stays dimmed and sweeping: it IS provisional, and both are the
        // page's word for "a better answer is on its way". The full answer clears
        // them, and the cards slide from wherever the preview had put them.
        el.classList.toggle('concierge-pending', !!provisional);
        el.classList.toggle('concierge-working', !!provisional);

        var before = positionsOf(el);

        // The router has established that native substring search is the right
        // answer. Showing the free Concierge retrieval beside it would duplicate
        // the same title and make the additive path look noisier, not smarter.
        if (result.Route === 'Native' && !(result.Quotes && result.Quotes.length)) {
            el.innerHTML = '';
            return;
        }

        var hasHits = result.Route !== 'Native' && result.Hits && result.Hits.length;
        var hasQuotes = result.Quotes && result.Quotes.length;

        if (!hasHits && !hasQuotes) {
            // Nothing to add. Emptying our own container is fine \u2014 we made it.
            // The working class goes too: a row still sweeping over an empty answer
            // claims work is happening when it has finished and found nothing.
            el.innerHTML = '';
            el.classList.remove('concierge-working');
            restoreSections();
            return;
        }

        var html = '';

        if (hasHits) {
            html += section(
                'Matches' + statusLabel(provisional ? 'ranking' : ''),
                renderHits(result));
        }

        html += renderQuotes(result);

        if (result.Degraded) {
            html += '<div class="concierge-note concierge-degraded">'
                + escapeHtml(result.Degraded) + '</div>';
        }

        el.innerHTML = html;
        slideFrom(el, before);
        applyMode(searchPage());
    }

    /* The row before there is anything to put in it.
     *
     * Only reached when the free answer has not come back yet, which on this library
     * is under a tenth of a second — but a search that starts by showing something
     * getting ready reads better than one that starts with nothing and then has a row
     * appear out of it. */
    function renderWaiting() {
        var el = container();
        if (!el || el.innerHTML !== '') {
            return;
        }

        el.classList.add('concierge-working');
        el.classList.remove('concierge-pending');
        el.innerHTML = section('Matches' + statusLabel('searching'), skeletonCards(7));
        applyMode(searchPage());
    }

    /* The free answer, on its own.
     *
     * Fired on its own short timer rather than chained off anything, because the two
     * requests are independent: this one is keyword-only and returns in about a
     * millisecond, the paid one takes seconds. It only ever paints when nothing
     * better is already showing for the same query. */
    function runPreview(query) {
        if (!window.ApiClient || !window.ApiClient.ajax || settledQuery === query) {
            return;
        }

        window.ApiClient.ajax({
            type: 'POST',
            url: window.ApiClient.getUrl('Concierge/Search'),
            data: JSON.stringify({ Query: query, Limit: 20, Preview: true }),
            contentType: 'application/json',
            dataType: 'json'
        }).then(function (result) {
            // Three ways this is already stale: the text moved on, the full answer
            // beat us to it, or the full answer for this very query has landed.
            if (lastQuery !== query || settledQuery === query) {
                return;
            }

            log('preview', query, (result.Hits || []).length);
            render(result, true);
        }, function () { /* A preview that fails costs nothing and says nothing. */ });
    }

    function run(query) {
        if (!window.ApiClient || !window.ApiClient.ajax) {
            return;
        }

        // Enter is an immediate shortcut, but pressing it after the automatic
        // settle timer fired must not buy the same answer twice.
        if (inFlight && inFlightQuery === query) {
            return;
        }

        if (inFlight) {
            // A newer query supersedes an older one. The old request is left to
            // land and be ignored rather than aborted, because aborting mid-flight
            // on some clients logs a console error that looks like a real fault.
            inFlight = null;
        }

        var token = {};
        inFlight = token;
        inFlightQuery = query;

        window.ApiClient.ajax({
            type: 'POST',
            url: window.ApiClient.getUrl('Concierge/Search'),
            data: JSON.stringify({ Query: query, Limit: 20 }),
            contentType: 'application/json',
            dataType: 'json'
        }).then(function (result) {
            if (inFlight !== token) {
                return;
            }

            inFlight = null;
            inFlightQuery = '';
            settledQuery = query;
            log('result', query, result.Route, (result.Hits || []).length);
            render(result, false);
        }, function (e) {
            if (inFlight !== token) {
                return;
            }

            inFlight = null;
            inFlightQuery = '';
            // Never surface a failure on the page. Native results are already
            // there and correct, and an error banner under them would make a
            // working search look broken.
            log('failed', e);
        });
    }

    function clearResults() {
        var el = ourSection();

        if (el) {
            el.innerHTML = '';
            el.classList.remove('concierge-pending');
            el.classList.remove('concierge-working');
        }

        // Nothing of ours on screen means nothing of theirs should be hidden for it.
        restoreSections();
    }

    function ourSection() {
        var page = searchPage();

        return page ? page.querySelector('#' + CONTAINER_ID) : null;
    }

    /* The query on screen has moved on but an answer for it is still seconds away.
     *
     * Blanking the row here was the obvious thing and it was wrong: between the
     * two-second settle and a five-to-nine-second search, the section was empty for
     * the best part of ten seconds after every keystroke, which reads as broken
     * rather than as busy. Dimming says the same thing — this is stale — without
     * taking away the answer the reader already has. */
    function markPending() {
        var el = ourSection();

        if (el && el.innerHTML !== '') {
            el.classList.add('concierge-pending');
            el.classList.add('concierge-working');
            setStatus('searching');
        }
    }

    function runNow(value) {
        var query = (value || '').trim();

        clearTimeout(timer);
        clearTimeout(previewTimer);

        if (query.length < MIN_QUERY_LENGTH) {
            return;
        }

        lastQuery = query;
        run(query);
    }

    function onInput(value) {
        var query = (value || '').trim();

        if (query === lastQuery) {
            return;
        }

        lastQuery = query;
        clearTimeout(timer);
        clearTimeout(previewTimer);

        // The text on screen now names a different query. An earlier request may
        // still finish, but its token is invalid immediately so it can never paint
        // stale cards while the new debounce is running.
        inFlight = null;
        inFlightQuery = '';

        if (query.length < MIN_QUERY_LENGTH) {
            // Below the threshold nothing is coming, so there is nothing to be
            // pending about — take the row away rather than leaving it dimmed.
            clearResults();
            return;
        }

        markPending();
        renderWaiting();

        previewTimer = setTimeout(function () { runPreview(query); }, PREVIEW_MS);
        timer = setTimeout(function () { run(query); }, DEBOUNCE_MS);
    }

    /* The search input is created and destroyed as the SPA routes around, so we
     * attach to whichever one is present rather than holding a reference. */
    function attach() {
        var page = searchPage();
        if (!page) {
            return;
        }

        var input = page.querySelector('#searchTextInput, .searchfields-txtSearch, input[type="search"]');
        if (!input || input.dataset.conciergeBound === '1') {
            return;
        }

        toggle(page);
        input.dataset.conciergeBound = '1';
        input.addEventListener('input', function (e) { onInput(e.target.value); });
        input.addEventListener('keydown', function (e) {
            if (e.key === 'Enter') {
                runNow(e.target.value);
            }
        });

        log('bound to the search input');

        if (input.value) {
            onInput(input.value);
        }
    }

    /* A MutationObserver rather than a route hook: Jellyfin's client has no stable
     * public event for "the search view is ready", and observing is inert when
     * nothing changes. It only ever reads. */
    var observer = new MutationObserver(function () {
        attach();

        // Routing away disposes the page, and with it every node we hid. Dropping
        // the list here means a stale reference can never be un-hidden onto a page
        // that has moved on.
        if (!searchPage() && hiddenSections.length) {
            hiddenSections = [];
        }
    });

    function start() {
        ensureStyles();
        observer.observe(document.body, { childList: true, subtree: true });
        attach();
        log('ready');
    }

    if (document.readyState === 'loading') {
        document.addEventListener('DOMContentLoaded', start);
    } else {
        start();
    }
})();
