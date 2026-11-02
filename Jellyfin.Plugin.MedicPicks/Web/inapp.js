/* Medic Picks: opens My picks as a page inside Jellyfin's web app.
   The web app draws every menu link to open in a new tab or window (and the apps hand it to something outside
   Jellyfin), so a click on the My picks link is taken first, here, and goes to #/mypicks in the same app. That isn't
   one of the web app's own pages, so it shows its "Page not found" page there, inside its own frame and under its top
   bar; this fills that page with the picks, drawn with the Home page's markup so they follow the person's theme.
   When the page can't be filled, the full picks page opens in the same window, never a new one.
   Added to the web app's page by Web/MenuLink.cs. */
(function () {
    'use strict';
    if (window.MedicPicksInApp) return;
    window.MedicPicksInApp = true;

    var ROUTE = '#/mypicks';
    var MOUNT_ID = 'medicPicksInApp';
    var NAME_KEY = 'medic-picks-link-name';
    // How long the web app may take to show its page for the route before the full page opens instead.
    var MOUNT_WAIT_MS = 8000;

    function onRoute() {
        return location.hash === ROUTE || location.hash.indexOf(ROUTE + '?') === 0;
    }

    function isPicksLink(a) {
        if (!a || !a.href) return false;
        try {
            var url = new URL(a.href, location.href);
            return url.origin === location.origin && /\/MedicPicks\/Page\/?$/i.test(url.pathname);
        } catch (e) {
            return false;
        }
    }

    function fullPageUrl() {
        var api = window.ApiClient;
        return api && api.getUrl ? api.getUrl('MedicPicks/Page') : '../MedicPicks/Page';
    }

    function openFullPage() {
        location.replace(fullPageUrl());
    }

    function linkName() {
        try { return sessionStorage.getItem(NAME_KEY) || 'My picks'; } catch (e) { return 'My picks'; }
    }

    // Taken in the capture phase, before the web app's own handlers, which would open a new tab or window. A click
    // that asks for a new tab (the middle button, or Ctrl, Cmd or Shift held) is left to open the full page there.
    document.addEventListener('click', function (e) {
        if (e.defaultPrevented || e.button !== 0 || e.ctrlKey || e.metaKey || e.shiftKey || e.altKey) return;
        var a = e.target && e.target.closest ? e.target.closest('a[href]') : null;
        if (!isPicksLink(a)) return;
        e.preventDefault();
        e.stopImmediatePropagation();
        // The link's own words, without its icon, whose name is written out as text.
        var words = a.cloneNode(true);
        words.querySelectorAll('.material-icons, .MuiButton-icon, .MuiListItemIcon-root, [aria-hidden="true"]').forEach(function (n) { n.remove(); });
        var name = (words.textContent || '').trim();
        try { if (name) sessionStorage.setItem(NAME_KEY, name); } catch (err) { /* keeps "My picks" */ }
        closeMenus();
        if (onRoute()) mount();
        else location.hash = ROUTE;
    }, true);

    // The side menu stays open when its own click handler doesn't run, so it's closed here: the legacy drawer by
    // its handle's own script, the newer one by its backdrop.
    function closeMenus() {
        var backdrop = document.querySelector('.MuiDrawer-root .MuiBackdrop-root');
        if (backdrop) backdrop.click();
        var drawer = document.querySelector('.mainDrawer');
        if (drawer && drawer.classList.contains('drawer-open') && document.querySelector('.mainDrawerButton')) {
            document.querySelector('.mainDrawerButton').click();
        }
    }

    function el(tag, cls, text) {
        var node = document.createElement(tag);
        if (cls) node.className = cls;
        if (text != null) node.textContent = text;
        return node;
    }

    function bdiLine(cls, text, title) {
        var line = el('div', 'cardText cardTextCentered ' + cls);
        line.appendChild(el('bdi', null, text));
        if (title) line.title = title;
        return line;
    }

    // A card as the web app's library pages draw one: a portrait poster with its title and a second line under it.
    function card(opts) {
        var c = el('div', 'card portraitCard card-hoverable');
        var box = el('div', 'cardBox cardBox-bottompadded');
        var scalable = el('div', 'cardScalable');
        scalable.appendChild(el('div', 'cardPadder cardPadder-portrait'));
        var image = el(opts.href ? 'a' : 'div', 'cardImageContainer coveredImage cardContent defaultCardBackground defaultCardBackground1');
        if (opts.href) {
            image.href = opts.href;
            if (opts.newTab) { image.target = '_blank'; image.rel = 'noopener'; }
        }
        image.setAttribute('aria-label', opts.title + (opts.year ? ' (' + opts.year + ')' : ''));
        // The title stands in for a poster until it loads, and for good when there's none.
        var stand = el('div', 'cardText cardDefaultText', opts.title);
        stand.style.fontSize = '1.2em';
        image.appendChild(stand);
        if (opts.image) {
            var probe = new Image();
            probe.onload = function () {
                image.style.backgroundImage = 'url("' + opts.image.replace(/"/g, '%22') + '")';
                stand.remove();
            };
            probe.src = opts.image;
        }
        scalable.appendChild(image);
        box.appendChild(scalable);
        box.appendChild(bdiLine('cardText-first', opts.title));
        box.appendChild(bdiLine('cardText-secondary', opts.second || (opts.year ? String(opts.year) : ' '), opts.second));
        c.appendChild(box);
        return c;
    }

    // A section as the Home page draws one, with its cards wrapping as on a library page, so every pick can be
    // reached without sideways scrolling.
    function row(title, note, cards) {
        var section = el('div', 'verticalSection medicPicksRow');
        section.appendChild(el('h2', 'sectionTitle sectionTitle-cards padded-left', title));
        if (note) section.appendChild(el('p', 'fieldDescription padded-left padded-right', note));
        var items = el('div', 'itemsContainer vertical-wrap padded-left padded-right');
        cards.forEach(function (c) { items.appendChild(c); });
        section.appendChild(items);
        return section;
    }

    function libraryCards(list, serverId) {
        return list.map(function (pick) {
            var api = window.ApiClient;
            return card({
                title: pick.name,
                year: pick.year,
                second: pick.reason || (pick.year ? String(pick.year) : ''),
                href: '#/details?id=' + encodeURIComponent(pick.itemId) + (serverId ? '&serverId=' + encodeURIComponent(serverId) : ''),
                image: api.getUrl('Items/' + encodeURIComponent(pick.itemId) + '/Images/Primary', { fillWidth: 320, quality: 90 })
            });
        });
    }

    function discoverCards(list, data) {
        return list.map(function (pick) {
            var type = pick.mediaType === 'tv' ? 'tv' : 'movie';
            // Requests are made on the full page for now; a link goes where the full page's button would.
            var href = data.requestMode === 'link' && data.seerrUrl
                ? data.seerrUrl + '/' + type + '/' + encodeURIComponent(pick.tmdbId)
                : data.requestMode === 'direct' ? null
                    : 'https://www.themoviedb.org/' + type + '/' + encodeURIComponent(pick.tmdbId);
            return card({
                title: pick.title,
                year: pick.year,
                second: pick.becauseOf ? 'Because you watched ' + pick.becauseOf : '',
                href: href,
                newTab: !!href,
                image: pick.posterPath ? 'https://image.tmdb.org/t/p/w342' + pick.posterPath : null
            });
        });
    }

    function header(root, data) {
        var top = el('div', 'padded-left padded-right medicPicksTop');
        top.appendChild(el('h1', 'sectionTitle', linkName()));
        if (data && data.generatedUtc) {
            var when = new Date(data.generatedUtc);
            top.appendChild(el('p', 'fieldDescription', 'Chosen from what you’ve been watching. Updated ' +
                when.toLocaleString(undefined, { day: 'numeric', month: 'short', hour: '2-digit', minute: '2-digit' }) + '.'));
        }
        var full = el('a', 'emby-button raised button-alt medicPicksFull', 'Choices, hiding and requests');
        full.href = fullPageUrl();
        top.appendChild(full);
        root.appendChild(top);
    }

    function render(root, data) {
        root.textContent = '';
        header(root, data);
        var serverId = window.ApiClient && window.ApiClient.serverId ? window.ApiClient.serverId() : null;
        var lib = data.inLibrary || [];
        var linked = data.linked || [];
        var disc = data.discover || [];
        if (lib.length) {
            root.appendChild(row('On your server', data.playlistName
                ? 'Ready to watch now. They’re also in your playlist “' + data.playlistName + '”.'
                : 'Ready to watch now.', libraryCards(lib, serverId)));
        }
        if (linked.length) root.appendChild(row('Linked to what you’ve watched', null, libraryCards(linked, serverId)));
        if (disc.length) {
            root.appendChild(row('Not on the server yet', data.requestMode === 'direct'
                ? 'To ask for one, open Choices, hiding and requests.'
                : null, discoverCards(disc, data)));
        }
        if (!lib.length && !linked.length && !disc.length) {
            root.appendChild(el('p', 'padded-left padded-right medicPicksNote',
                data.note || 'Nothing to suggest yet. Picks are built overnight from what you watch.'));
        }
    }

    function showFailure(root) {
        root.textContent = '';
        header(root, null);
        root.appendChild(el('p', 'padded-left padded-right medicPicksNote',
            'Your picks couldn’t load. Try again in a moment, or open the full page.'));
    }

    function setTitle() {
        var name = linkName();
        document.title = name;
        document.querySelectorAll('.pageTitle').forEach(function (t) {
            if (/not found/i.test(t.textContent)) t.textContent = name;
        });
    }

    var waitingSince = 0;
    var waitTimer = null;

    // Fills the web app's "Page not found" page while the address is #/mypicks. Called again when the page is
    // drawn anew; a page that already has the picks is left alone.
    function mount() {
        if (!onRoute()) { waitingSince = 0; return; }
        var page = document.querySelector('#fallbackPage:not(.hide)');
        var api = window.ApiClient;
        if (!page || !api || !api.accessToken || !api.accessToken()) {
            if (!waitingSince) waitingSince = Date.now();
            if (Date.now() - waitingSince > MOUNT_WAIT_MS) { waitingSince = 0; openFullPage(); return; }
            clearTimeout(waitTimer);
            waitTimer = setTimeout(mount, 250);
            return;
        }
        waitingSince = 0;
        setTitle();
        if (page.querySelector('#' + MOUNT_ID)) return;
        // The web app's own message stays in the page, hidden, so its own code finds what it drew.
        Array.prototype.forEach.call(page.children, function (child) { child.hidden = true; });
        var root = el('div', 'medicPicksInApp padded-bottom-page');
        root.id = MOUNT_ID;
        root.setAttribute('aria-live', 'polite');
        root.appendChild(el('p', 'padded-left padded-right', 'Loading your picks…'));
        page.appendChild(root);
        api.getJSON(api.getUrl('MedicPicks/Me')).then(function (data) {
            if (root.isConnected) render(root, data || {});
        }).catch(function (err) {
            console.error('Medic Picks: couldn\'t load the picks', err);
            if (root.isConnected) showFailure(root);
        });
    }

    window.addEventListener('hashchange', mount);
    // The web app draws the page after the address changes, and again on its own (after signing in, say).
    new MutationObserver(function () {
        if (onRoute() && !document.getElementById(MOUNT_ID)) mount();
    }).observe(document.documentElement, { childList: true, subtree: true });
    if (onRoute()) mount();
})();
