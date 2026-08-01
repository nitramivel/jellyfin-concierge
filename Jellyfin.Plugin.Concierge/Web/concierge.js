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
 */
(function () {
    'use strict';

    var CONTAINER_ID = 'concierge-results';
    // Jellyfin's own search waits 500ms because its requests are free. Concierge's
    // full path spends money, so it needs a genuinely settled query rather than a
    // slightly later copy of every prefix the native client searched. Enter still
    // runs immediately for somebody who has finished typing deliberately.
    var DEBOUNCE_MS = 2000;

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
        '#concierge-results .concierge-why{opacity:.72;font-size:.86em;white-space:normal;' +
        'padding:0 .4em;display:-webkit-box;-webkit-box-orient:vertical;' +
        '-webkit-line-clamp:2;overflow:hidden;min-height:2.4em;}' +
        '#concierge-results .concierge-card .cardText{text-align:left!important;' +
        'padding-left:0;padding-right:.4em;}' +
        '#concierge-results .concierge-note{opacity:.6;font-size:.7em;font-weight:400;}' +
        '#concierge-results .concierge-degraded{opacity:.65;font-size:.85em;' +
        'margin:.4em 0 0 .8em;}' +
        // A newer query is settling. Dimming beats blanking: emptying the row on
        // every keystroke left a hole for the two-second debounce plus six seconds
        // of query, which reads as broken rather than as busy.
        '#concierge-results.concierge-pending{opacity:.45;transition:opacity .15s;}' +
        '#concierge-results .concierge-stamp{position:absolute;right:.4em;bottom:.4em;' +
        'background:rgba(0,0,0,.72);color:#fff;border-radius:.25em;padding:.1em .4em;' +
        'font-size:.78em;}';

    function ensureStyles() {
        if (document.getElementById('concierge-styles')) {
            return;
        }

        var style = document.createElement('style');
        style.id = 'concierge-styles';
        style.textContent = STYLES;
        document.head.appendChild(style);
    }

    var timer = null;
    var lastQuery = '';
    var inFlight = null;
    var inFlightQuery = '';

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
        var before = page.querySelector('.jellyseerr-section');

        if (before && before.parentNode) {
            if (el.nextElementSibling !== before || el.parentNode !== before.parentNode) {
                before.parentNode.insertBefore(el, before);
            }

            return;
        }

        // Jellyfin's empty-search state is the reliable landmark when there are no
        // native Movies/Shows sections. Jellyfin Enhanced inserts "Discover on
        // Seerr" beside this node. Appending to #searchPage instead puts the row
        // outside the padded results layout, which collapses the cards into the
        // raw, full-width text list seen in the browser.
        var noResults = page.querySelector('.noItemsMessage');

        if (noResults && noResults.parentNode) {
            if (el.previousElementSibling !== noResults
                    || el.parentNode !== noResults.parentNode) {
                noResults.parentNode.insertBefore(el, noResults.nextSibling);
            }

            return;
        }

        // No Seerr section yet — sit after the native results instead of at the
        // end of the page, so we are in the right place before it arrives rather
        // than visibly jumping when it does.
        var after = lastNativeSection(page);

        if (after && after.parentNode) {
            if (el.previousElementSibling !== after) {
                after.parentNode.insertBefore(el, after.nextSibling);
            }

            return;
        }

        var results = page.querySelector(
            '.searchResults, [class*="searchResults"], .padded-top.padded-bottom-page');
        var parent = results || page;

        if (el.parentNode !== parent) {
            parent.appendChild(el);
        }
    }

    function lastNativeSection(page) {
        var sections = page.querySelectorAll(
            '.verticalSection:not(.jellyseerr-section):not(#' + CONTAINER_ID + ')');

        return sections.length ? sections[sections.length - 1] : null;
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
    function card(itemId, title, why, stamp) {
        var url = posterUrl(itemId);

        // Background image, not an <img>. ".cardImageContainer" and ".coveredImage"
        // exist in Jellyfin's stylesheet precisely to paint one — background-size:
        // cover, background-position:50%, background-clip:content-box — and both
        // the client's own cards and Jellyfin Enhanced's Seerr cards fill them this
        // way on this page. Using their path means their rules do the work.
        var image = 'background-color:rgba(127,127,127,.18);'
            + (url ? 'background-image:url(\'' + escapeHtml(url) + '\');' : '');
        var href = escapeHtml(itemLink(itemId));

        return '<div class="card overflowPortraitCard card-hoverable card-withuserdata concierge-card">'
            + '<div class="cardBox cardBox-bottompadded">'
            + '<div class="cardScalable">'
            + '<div class="cardPadder cardPadder-overflowPortrait"></div>'
            + '<a class="cardImageContainer coveredImage cardContent" href="' + href
            + '" style="' + image + '">'
            + (stamp ? '<div class="concierge-stamp">' + escapeHtml(stamp) + '</div>' : '')
            + '</a>'
            + '</div>'
            + '<div class="cardText cardTextCentered cardText-first">'
            + '<a is="emby-linkbutton" href="' + href + '" title="' + escapeHtml(title) + '">'
            + '<bdi>' + escapeHtml(title) + '</bdi></a></div>'
            + (why
                ? '<div class="cardText cardTextCentered concierge-why" title="'
                    + escapeHtml(why) + '">' + escapeHtml(why) + '</div>'
                : '')
            + '</div></div>';
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
        return (result.Hits || []).slice(0, 12).map(function (hit) {
            var title = hit.Name + (hit.Year ? ' (' + hit.Year + ')' : '');
            return card(hit.ItemId, title, hit.Why || '', null);
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

            return card(q.ItemId, q.Title, '\u201c' + (q.Line || '') + '\u201d', stamp);
        }).join('');

        return section('Said in\u2026', cards);
    }

    function render(result) {
        var el = container();
        if (!el) {
            return;
        }

        // Whatever we paint below is the answer to the query on screen, so the row
        // stops being stale the moment it lands.
        el.classList.remove('concierge-pending');

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
            el.innerHTML = '';
            return;
        }

        var html = '';

        if (hasHits) {
            html += section('Concierge matches', renderHits(result));
        }

        html += renderQuotes(result);

        if (result.Degraded) {
            html += '<div class="concierge-note concierge-degraded">'
                + escapeHtml(result.Degraded) + '</div>';
        }

        el.innerHTML = html;
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
            data: JSON.stringify({ Query: query, Limit: 12 }),
            contentType: 'application/json',
            dataType: 'json'
        }).then(function (result) {
            if (inFlight !== token) {
                return;
            }

            inFlight = null;
            inFlightQuery = '';
            log('result', query, result.Route, (result.Hits || []).length);
            render(result);
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
        }
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
        }
    }

    function runNow(value) {
        var query = (value || '').trim();

        clearTimeout(timer);

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
    var observer = new MutationObserver(function () { attach(); });

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
