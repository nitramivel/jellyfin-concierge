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
    var DEBOUNCE_MS = 450;

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
        'padding:0 .4em;}' +
        '#concierge-results .concierge-note{opacity:.6;font-size:.7em;font-weight:400;}' +
        '#concierge-results .concierge-degraded{opacity:.65;font-size:.85em;' +
        'margin:.4em 0 0 .8em;}' +
        // Declared rather than inherited: the timestamp is positioned against the
        // poster, and if the client's own cardImageContainer ever stops being a
        // positioned ancestor the stamp would fly to the corner of the page.
        '#concierge-results .cardImageContainer{position:relative;}' +
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

    function posterUrl(itemId) {
        if (!window.ApiClient || !window.ApiClient.getImageUrl) {
            return '';
        }

        try {
            return window.ApiClient.getImageUrl(itemId, { type: 'Primary', maxHeight: 330 });
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
        var image = url
            ? 'background-image:url(\'' + escapeHtml(url) + '\');'
            : 'background:rgba(127,127,127,.18);';
        var href = escapeHtml(itemLink(itemId));

        return '<div class="card overflowPortraitCard card-hoverable concierge-card">'
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
                ? '<div class="cardText cardTextCentered concierge-why">' + escapeHtml(why) + '</div>'
                : '')
            + '</div></div>';
    }

    function section(heading, cards) {
        return '<h2 class="sectionTitle sectionTitle-cards padded-left padded-right'
            + ' concierge-heading">' + heading + '</h2>'
            + '<div class="itemsContainer padded-right vertical-wrap concierge-row">'
            + cards + '</div>';
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

        var hasHits = result.Hits && result.Hits.length;
        var hasQuotes = result.Quotes && result.Quotes.length;

        if (!hasHits && !hasQuotes) {
            // Nothing to add. Emptying our own container is fine \u2014 we made it.
            el.innerHTML = '';
            return;
        }

        var html = '';

        if (hasHits) {
            var label = result.Reranked
                ? 'Concierge'
                : 'Concierge <span class="concierge-note">(unranked)</span>';

            html += section(label, renderHits(result));
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

        if (inFlight) {
            // A newer query supersedes an older one. The old request is left to
            // land and be ignored rather than aborted, because aborting mid-flight
            // on some clients logs a console error that looks like a real fault.
            inFlight = null;
        }

        var token = {};
        inFlight = token;

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

            log('result', query, result.Route, (result.Hits || []).length);
            render(result);
        }, function (e) {
            if (inFlight !== token) {
                return;
            }

            // Never surface a failure on the page. Native results are already
            // there and correct, and an error banner under them would make a
            // working search look broken.
            log('failed', e);
        });
    }

    function onInput(value) {
        var query = (value || '').trim();

        if (query === lastQuery) {
            return;
        }

        lastQuery = query;
        clearTimeout(timer);

        if (query.length < MIN_QUERY_LENGTH) {
            var el = searchPage() && searchPage().querySelector('#' + CONTAINER_ID);
            if (el) {
                el.innerHTML = '';
            }

            return;
        }

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
