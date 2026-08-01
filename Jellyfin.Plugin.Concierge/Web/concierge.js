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
 * So: every element here carries a "concierge-" prefix, everything is appended,
 * and nothing outside our own container is ever cleared, replaced, or reordered.
 * If our section is missing we add it; we never assume it is the only thing there.
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
        '#concierge-results .concierge-heading{font-size:1.2em;margin:0 0 .4em;}' +
        '#concierge-results .concierge-note{opacity:.65;font-size:.85em;font-weight:400;}' +
        '#concierge-results .concierge-degraded{margin-top:.5em;}' +
        '#concierge-results .concierge-list{display:flex;flex-direction:column;}' +
        '#concierge-results .concierge-hit{display:block;padding:.5em .2em;text-decoration:none;' +
        'color:inherit;border-bottom:1px solid rgba(127,127,127,.18);}' +
        '#concierge-results .concierge-hit:hover{background:rgba(127,127,127,.12);}' +
        '#concierge-results .concierge-title{font-weight:600;}' +
        '#concierge-results .concierge-year{opacity:.6;}' +
        '#concierge-results .concierge-why{display:block;font-size:.86em;opacity:.72;margin-top:.15em;}';

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

        var existing = page.querySelector('#' + CONTAINER_ID);
        if (existing) {
            return existing;
        }

        var el = document.createElement('div');
        el.id = CONTAINER_ID;
        el.className = 'concierge-section verticalSection';
        el.style.margin = '1.5em 0';

        // Appended to the end of the page, after whatever else is already there.
        // Deliberately not inserted at a computed position: that would depend on
        // the order other plugins add their own sections, which is not ours to
        // reason about.
        page.appendChild(el);
        return el;
    }

    function itemLink(hit) {
        return '#/details?id=' + encodeURIComponent(hit.ItemId);
    }

    function renderHits(result) {
        var rows = (result.Hits || []).slice(0, 12).map(function (hit) {
            var year = hit.Year ? ' <span class="concierge-year">(' + escapeHtml(String(hit.Year)) + ')</span>' : '';
            return '<a class="concierge-hit" href="' + escapeHtml(itemLink(hit)) + '">'
                + '<span class="concierge-title">' + escapeHtml(hit.Name) + '</span>' + year
                + '<span class="concierge-why">' + escapeHtml(hit.Why || '') + '</span>'
                + '</a>';
        }).join('');

        return rows;
    }

    function renderQuotes(result) {
        if (!result.Quotes || !result.Quotes.length) {
            return '';
        }

        var rows = result.Quotes.slice(0, 6).map(function (q) {
            var at = Math.floor((q.Position || 0) / 10000000);
            var mins = Math.floor(at / 60);
            var secs = at % 60;
            var stamp = mins + ':' + (secs < 10 ? '0' : '') + secs;

            // Deep-link five seconds before the line so playback starts on the
            // run-up rather than mid-word.
            var href = '#/details?id=' + encodeURIComponent(q.ItemId);

            return '<a class="concierge-hit" href="' + escapeHtml(href) + '">'
                + '<span class="concierge-title">' + escapeHtml(q.Title) + '</span>'
                + ' <span class="concierge-year">' + escapeHtml(stamp) + '</span>'
                + '<span class="concierge-why">&ldquo;' + escapeHtml(q.Line) + '&rdquo;</span>'
                + '</a>';
        }).join('');

        return '<h2 class="concierge-heading">Said in&hellip;</h2><div class="concierge-list">' + rows + '</div>';
    }

    function render(result) {
        var el = container();
        if (!el) {
            return;
        }

        var hasHits = result.Hits && result.Hits.length;
        var hasQuotes = result.Quotes && result.Quotes.length;

        if (!hasHits && !hasQuotes) {
            // Nothing to add. Emptying our own container is fine — we made it.
            el.innerHTML = '';
            return;
        }

        var html = '';

        if (hasHits) {
            var label = result.Reranked ? 'Concierge' : 'Concierge <span class="concierge-note">(unranked)</span>';
            html += '<h2 class="concierge-heading">' + label + '</h2>'
                + '<div class="concierge-list">' + renderHits(result) + '</div>';
        }

        html += renderQuotes(result);

        if (result.Degraded) {
            html += '<div class="concierge-note concierge-degraded">' + escapeHtml(result.Degraded) + '</div>';
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
