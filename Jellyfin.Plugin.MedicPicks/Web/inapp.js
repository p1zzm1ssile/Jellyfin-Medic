/* Medic Picks: opens My picks as a page inside Jellyfin's web app.
   The web app draws every menu link to open in a new tab or window (and the apps hand it to something outside
   Jellyfin), so a click on the My picks link is taken first, here, and goes to #/mypicks in the same app. That isn't
   one of the web app's own pages, so it shows its "Page not found" page there, inside its own frame and under its top
   bar; this fills that page with the picks and their controls, drawn with the web app's own markup so they follow
   the person's theme. When the page can't be filled, the full picks page opens in the same window, never a new one.
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
    // Request states that are still on their way, so My requests keeps itself current while any is shown.
    var MOVING = ['downloading', 'partial', 'queued', 'searching', 'pending', 'trouble'];

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
        if (!isPicksLink(a) || a.closest('#' + MOUNT_ID)) return;
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

    // ---- Talking to the server, with the web app's own sign-in ----

    function api() { return window.ApiClient; }

    function getJson(path) {
        return api().getJSON(api().getUrl('MedicPicks/' + path));
    }

    function postJson(path, body) {
        return api().ajax({
            type: 'POST',
            url: api().getUrl('MedicPicks/' + path),
            data: JSON.stringify(body || {}),
            contentType: 'application/json',
            dataType: 'text'
        }).then(function (text) { return text ? JSON.parse(text) : null; });
    }

    // ---- Drawing, with the web app's markup ----

    // A customised element (emby-button and the like) as the web app makes them, so they're drawn as its own. The web
    // app's custom elements library takes the element's type as a plain string here.
    function el(tag, cls, text, is) {
        var node;
        try { node = is ? document.createElement(tag, is) : document.createElement(tag); } catch (e) { node = document.createElement(tag); }
        if (is && node.getAttribute('is') !== is) node.setAttribute('is', is);
        if (cls) node.className = cls;
        if (text != null) node.textContent = text;
        return node;
    }

    function button(text, cls) {
        var b = el('button', 'emby-button ' + (cls || 'button-flat'), null, 'emby-button');
        b.type = 'button';
        b.appendChild(el('span', null, text));
        return b;
    }

    function setText(b, text) {
        var span = b.querySelector('span');
        (span || b).textContent = text;
    }

    function bdiLine(cls, text, title) {
        var line = el('div', 'cardText cardTextCentered ' + cls);
        line.appendChild(el('bdi', null, text));
        if (title) line.title = title;
        return line;
    }

    function posterBox(target, url, title) {
        // The title stands in for a poster until it loads, and for good when there's none.
        var stand = el('div', 'cardText cardDefaultText', title);
        stand.style.fontSize = '1.2em';
        target.appendChild(stand);
        if (!url) return;
        var probe = new Image();
        probe.onload = function () {
            target.style.backgroundImage = 'url("' + url.replace(/"/g, '%22') + '")';
            stand.remove();
        };
        probe.src = url;
    }

    // A card as the web app's Home page draws one in a row: a portrait poster with its title, a second line, and any
    // controls under it.
    function card(opts) {
        var c = el('div', 'card overflowPortraitCard card-hoverable');
        var box = el('div', 'cardBox cardBox-bottompadded');
        var scalable = el('div', 'cardScalable');
        scalable.appendChild(el('div', 'cardPadder cardPadder-overflowPortrait'));
        var image = el(opts.href ? 'a' : 'div', 'cardImageContainer coveredImage cardContent defaultCardBackground defaultCardBackground1');
        if (opts.href) {
            image.href = opts.href;
            if (opts.newTab) { image.target = '_blank'; image.rel = 'noopener'; }
        }
        image.setAttribute('aria-label', opts.title + (opts.year ? ' (' + opts.year + ')' : ''));
        posterBox(image, opts.image, opts.title);
        scalable.appendChild(image);
        box.appendChild(scalable);
        box.appendChild(bdiLine('cardText-first', opts.title));
        box.appendChild(bdiLine('cardText-secondary', opts.second || (opts.year ? String(opts.year) : ' '), opts.second));
        (opts.controls || []).forEach(function (control) {
            var line = el('div', 'cardText cardTextCentered medicPicksControl');
            line.appendChild(control);
            box.appendChild(line);
        });
        c.appendChild(box);
        return c;
    }

    // A row as the Home page draws one: a title, then cards that scroll sideways.
    function section(title, note) {
        var s = el('div', 'verticalSection emby-scroller-container medicPicksRow');
        var head = el('div', 'sectionTitleContainer medicPicksHead');
        head.appendChild(el('h2', 'sectionTitle sectionTitle-cards padded-left', title));
        s.arrows = arrowButtons();
        head.appendChild(s.arrows.wrap);
        s.appendChild(head);
        var p = el('p', 'fieldDescription padded-left padded-right');
        if (note) p.textContent = note;
        p.hidden = !note;
        s.appendChild(p);
        s.note = p;
        return s;
    }

    // A row that scrolls sideways, with the Home page's arrow buttons beside the row's title. The browser's own
    // scrolling, so a mouse wheel, a touch screen and a TV remote's focus all move it.
    function cardList(cards, s) {
        var scroller = el('div', 'padded-top-focusscale padded-bottom-focusscale medicPicksScroller');
        var items = el('div', 'itemsContainer focuscontainer-x');
        cards.forEach(function (c) { items.appendChild(c); });
        scroller.appendChild(items);
        var buttons = s && s.arrows;
        if (buttons) {
            var update = function () {
                var room = scroller.scrollWidth - scroller.clientWidth;
                buttons.wrap.hidden = room <= 2;
                buttons.left.disabled = scroller.scrollLeft <= 2;
                buttons.right.disabled = scroller.scrollLeft >= room - 2;
            };
            buttons.left.onclick = function () { scroller.scrollBy({ left: -scroller.clientWidth * 0.8, behavior: 'smooth' }); };
            buttons.right.onclick = function () { scroller.scrollBy({ left: scroller.clientWidth * 0.8, behavior: 'smooth' }); };
            scroller.addEventListener('scroll', update, { passive: true });
            window.addEventListener('resize', update);
            requestAnimationFrame(update);
        }
        return scroller;
    }

    function arrowButtons() {
        var wrap = el('div', 'emby-scrollbuttons padded-right medicPicksArrows');
        function arrow(direction, title, icon) {
            var b = el('button', 'emby-scrollbuttons-button paper-icon-button-light', null, 'paper-icon-button-light');
            b.type = 'button';
            b.title = title;
            b.setAttribute('data-direction', direction);
            var span = el('span', 'material-icons ' + icon);
            span.setAttribute('aria-hidden', 'true');
            b.appendChild(span);
            wrap.appendChild(b);
            return b;
        }
        var left = arrow('left', 'Previous', 'chevron_left');
        var right = arrow('right', 'Next', 'chevron_right');
        wrap.hidden = true;
        return { wrap: wrap, left: left, right: right };
    }

    // Picks in rows by why they were picked, as the Home page's "Because you watched" rows: each reason shared by at
    // least `least` picks gets a row of its own, in the order of its best pick, and the rest share the first row.
    function byReason(list, least) {
        var groups = [];
        var index = {};
        list.forEach(function (pick) {
            var reason = pick.reason || '';
            if (!(reason in index)) { index[reason] = groups.length; groups.push({ reason: reason, picks: [] }); }
            groups[index[reason]].picks.push(pick);
        });
        var rest = [];
        var rows = [];
        groups.forEach(function (g) {
            if (g.reason && g.picks.length >= least) rows.push(g);
            else rest = rest.concat(g.picks);
        });
        return { rest: rest, rows: rows };
    }

    function rowTitle(reason) {
        return /^From /.test(reason) ? 'Because you watched ' + reason.slice(5) : reason;
    }

    function serverId() {
        return api() && api().serverId ? api().serverId() : null;
    }

    function detailsHref(itemId) {
        var id = serverId();
        return '#/details?id=' + encodeURIComponent(itemId) + (id ? '&serverId=' + encodeURIComponent(id) : '');
    }

    function fmtUpdated(iso) {
        return new Date(iso).toLocaleString(undefined, { day: 'numeric', month: 'short', hour: '2-digit', minute: '2-digit' });
    }

    function formatEta(iso) {
        var when = new Date(iso);
        var mins = Math.round((when - new Date()) / 60000);
        if (!(mins > 0)) return 'should be ready any moment';
        var sameDay = when.toDateString() === new Date().toDateString();
        var at = sameDay
            ? when.toLocaleTimeString(undefined, { hour: '2-digit', minute: '2-digit' })
            : when.toLocaleString(undefined, { weekday: 'short', hour: '2-digit', minute: '2-digit' });
        var left = mins < 60 ? mins + ' min' : mins < 48 * 60 ? Math.round(mins / 60) + ' h' : Math.round(mins / 1440) + ' days';
        return 'ready around ' + at + ' (about ' + left + ')';
    }

    // ---- The page ----

    // Everything one visit to #/mypicks draws and keeps, so a visit that has ended stops polling.
    function PicksPage(root) {
        var state = { data: null, admin: false, hidden: 0, requestsTimer: null, busy: false };

        var top = el('div', 'padded-left padded-right medicPicksTop');
        var heading = el('h1', 'sectionTitle', linkName());
        var updated = el('p', 'fieldDescription');
        top.appendChild(heading);
        top.appendChild(updated);

        // Choices, as on the full page, folded away so the picks come first.
        var choices = el('details', 'medicPicksChoices');
        choices.appendChild(el('summary', 'sectionTitle sectionTitle-cards', 'Your choices'));
        var form = el('form', 'medicPicksForm');
        var kind = selectField(form, 'Show', [['all', 'Films and series'], ['movies', 'Films only'], ['series', 'Series only']]);
        var count = selectField(form, 'How many', []);
        var dub = checkboxField(form, 'Dubbed audio only');
        var genresBox = el('details', 'medicPicksGenres');
        var genresSummary = el('summary', null, 'Genres: any');
        genresBox.appendChild(genresSummary);
        var genreList = el('div', 'medicPicksGenreList');
        var seasonalHeading = el('p', 'fieldDescription', 'Seasonal');
        var seasonalList = el('div', 'medicPicksGenreList');
        var clearGenres = button('Clear genres');
        genresBox.appendChild(genreList);
        genresBox.appendChild(seasonalHeading);
        genresBox.appendChild(seasonalList);
        genresBox.appendChild(clearGenres);
        form.appendChild(genresBox);
        var actions = el('div', 'medicPicksActions');
        var apply = el('button', 'emby-button raised button-submit', null, 'emby-button');
        apply.type = 'submit';
        apply.appendChild(el('span', null, 'Update my picks'));
        var more = button('Show me different ones');
        actions.appendChild(apply);
        actions.appendChild(more);
        form.appendChild(actions);
        var note = el('p', 'fieldDescription medicPicksNote');
        note.setAttribute('aria-live', 'polite');
        form.appendChild(note);
        var fewNote = el('p', 'fieldDescription');
        fewNote.hidden = true;
        form.appendChild(fewNote);
        var hiddenNote = el('p', 'fieldDescription');
        var hiddenText = el('span');
        var unhide = button('Show them again');
        hiddenNote.appendChild(hiddenText);
        hiddenNote.appendChild(unhide);
        hiddenNote.hidden = true;
        form.appendChild(hiddenNote);
        choices.appendChild(form);
        top.appendChild(choices);

        var full = el('a', 'button-link medicPicksFull', 'Open the full page');
        full.href = fullPageUrl();
        top.appendChild(full);

        var approvals = section('Waiting for your approval', 'Requests from people on this server that Seerr is holding for an admin.');
        var requests = section('My requests', null);
        var library = section('On your server', null);
        var libraryRows = el('div');
        var linked = section('More linked to what you’ve watched', null);
        var linkedRows = el('div');
        var discover = section('Not on the server yet', null);
        var message = el('p', 'padded-left padded-right medicPicksMessage');
        [approvals, requests, library, linked, discover].forEach(function (s) { s.hidden = true; });
        message.hidden = true;

        root.textContent = '';
        [top, approvals, requests, message, library, libraryRows, linkedRows, linked, discover].forEach(function (n) { root.appendChild(n); });

        function alive() { return root.isConnected && onRoute(); }

        function selectField(parent, label, options) {
            var wrap = el('div', 'selectContainer');
            var lab = el('label', 'selectLabel', label);
            var select = el('select', 'emby-select-withcolor emby-select');
            select.id = 'medicPicks' + label.replace(/\W/g, '');
            lab.htmlFor = select.id;
            options.forEach(function (o) {
                var opt = el('option', null, o[1]);
                opt.value = o[0];
                select.appendChild(opt);
            });
            wrap.appendChild(lab);
            wrap.appendChild(select);
            parent.appendChild(wrap);
            return select;
        }

        function checkbox(label, onChange) {
            var lab = el('label', 'emby-checkbox-label');
            var box = el('input', 'emby-checkbox', null, 'emby-checkbox');
            box.type = 'checkbox';
            if (onChange) box.addEventListener('change', onChange);
            lab.appendChild(box);
            lab.appendChild(el('span', 'checkboxLabel', label));
            return { label: lab, box: box };
        }

        function checkboxField(parent, label) {
            var wrap = el('div', 'checkboxContainer');
            var c = checkbox(label);
            wrap.appendChild(c.label);
            parent.appendChild(wrap);
            return c.box;
        }

        function dubNote(data) {
            return 'Dubbed audio only: on your server, only titles with ' + (data.dubLanguage || 'English') +
                ' audio. Titles not on the server yet that may not have it are marked.';
        }

        function chosenGenres() {
            return Array.prototype.slice.call(genresBox.querySelectorAll('input:checked')).map(function (b) { return b.value; });
        }

        function genreSummaryText() {
            var g = chosenGenres();
            genresSummary.textContent = g.length === 0 ? 'Genres: any' : 'Genres: ' + (g.length <= 3 ? g.join(', ') : g.length + ' chosen');
        }

        function updateHiddenNote() {
            hiddenText.textContent = state.hidden === 1
                ? 'You’ve ignored 1 title, so it won’t be recommended again. '
                : 'You’ve ignored ' + state.hidden + ' titles, so they won’t be recommended again. ';
            hiddenNote.hidden = state.hidden === 0;
        }

        function fillChoices(data) {
            var prefs = data.preferences || {};
            kind.value = prefs.kind || 'all';
            dub.checked = !!prefs.dubbedOnly;
            var options = data.countChoices || [5, 10, 15, 20, 25, 30];
            var n = prefs.count || data.defaultCount || 20;
            if (options.indexOf(n) < 0) {
                // The server's default isn't one of the choices: the nearest one.
                n = options.reduce(function (a, b) { return Math.abs(b - n) < Math.abs(a - n) ? b : a; });
            }
            count.textContent = '';
            options.forEach(function (v) {
                var o = el('option', null, String(v));
                o.value = String(v);
                count.appendChild(o);
            });
            count.value = String(n);

            var lower = function (list) { return list.map(function (x) { return x.toLowerCase(); }); };
            var chosen = lower(prefs.genres || []);
            var seasonal = (data.seasonalGenres || []).slice();
            var genres = (data.availableGenres || []).slice();
            // A saved choice that's in neither list is kept, so it can still be unticked.
            (prefs.genres || []).forEach(function (g) {
                if (lower(genres).indexOf(g.toLowerCase()) < 0 && lower(seasonal).indexOf(g.toLowerCase()) < 0) genres.push(g);
            });
            [[genreList, genres], [seasonalList, seasonal]].forEach(function (pair) {
                pair[0].textContent = '';
                pair[1].forEach(function (g) {
                    var c = checkbox(g, genreSummaryText);
                    c.box.value = g;
                    c.box.checked = chosen.indexOf(g.toLowerCase()) >= 0;
                    pair[0].appendChild(c.label);
                });
            });
            seasonalHeading.hidden = seasonal.length === 0;
            genresBox.hidden = genres.length + seasonal.length === 0;
            genreSummaryText();
            state.hidden = (prefs.hiddenItems || []).length + (prefs.hiddenTmdb || []).length;
            updateHiddenNote();
            if (!state.busy) note.textContent = prefs.dubbedOnly ? dubNote(data) : '';
        }

        // "Ignore": the title goes straight away, and is never recommended again (until "Show them again").
        function ignoreButton(cardNode, body) {
            var b = button('Ignore');
            b.title = 'Don’t recommend this again';
            b.addEventListener('click', function () {
                b.disabled = true;
                postJson('Me/Hide', body).then(function () {
                    var row = cardNode.closest('.medicPicksRow');
                    cardNode.remove();
                    if (row && !row.querySelector('.card')) row.hidden = true;
                    state.hidden++;
                    updateHiddenNote();
                }).catch(function () {
                    b.disabled = false;
                    setText(b, 'Couldn’t ignore it. Try again');
                });
            });
            return b;
        }

        function requestButton(pick, type) {
            var b = button('Request', 'raised button-submit');
            var msg = el('div', 'fieldDescription');
            msg.setAttribute('aria-live', 'polite');
            b.addEventListener('click', function () {
                b.disabled = true;
                setText(b, 'Requesting…');
                msg.textContent = '';
                postJson('Request', { tmdbId: pick.tmdbId, mediaType: type }).then(function (r) {
                    var st = r && r.status;
                    if (st === 'approved' || st === 'requested' || st === 'exists') {
                        setText(b, st === 'exists' ? 'Already requested' : 'Requested ✓');
                        msg.textContent = st === 'exists' ? '' : (r.message || '');
                        refreshRequests();
                    } else {
                        b.disabled = false;
                        setText(b, 'Request');
                        msg.textContent = (r && r.message) || 'Couldn’t request it just now.';
                    }
                }).catch(function () {
                    b.disabled = false;
                    setText(b, 'Request');
                    msg.textContent = 'Couldn’t request it just now.';
                });
            });
            var wrap = el('div');
            wrap.appendChild(b);
            wrap.appendChild(msg);
            return wrap;
        }

        // In a row named after their reason, the cards show the year instead of repeating it.
        function libraryCards(list, inReasonRow) {
            return list.map(function (pick) {
                var c = card({
                    title: pick.name,
                    year: pick.year,
                    second: inReasonRow ? (pick.year ? String(pick.year) : '') : (pick.reason || (pick.year ? String(pick.year) : '')),
                    href: detailsHref(pick.itemId),
                    image: api().getUrl('Items/' + encodeURIComponent(pick.itemId) + '/Images/Primary', { fillWidth: 320, quality: 90 })
                });
                c.querySelector('.cardBox').appendChild(controlLine(ignoreButton(c, { itemId: pick.itemId })));
                return c;
            });
        }

        function discoverCards(list, data) {
            var prefs = data.preferences || {};
            return list.map(function (pick) {
                var type = pick.mediaType === 'tv' ? 'tv' : 'movie';
                var href = data.requestMode === 'link' && data.seerrUrl
                    ? data.seerrUrl + '/' + type + '/' + encodeURIComponent(pick.tmdbId)
                    : 'https://www.themoviedb.org/' + type + '/' + encodeURIComponent(pick.tmdbId);
                var second = pick.becauseOf ? 'Because you watched ' + pick.becauseOf : '';
                if (prefs.dubbedOnly && pick.originalLanguage && pick.originalLanguage !== data.dubLanguageCode) {
                    second = (data.dubLanguage || 'English') + ' audio not confirmed' + (second ? ' · ' + second : '');
                }
                var c = card({
                    title: pick.title,
                    year: pick.year,
                    second: second,
                    href: href,
                    newTab: true,
                    image: pick.posterPath ? 'https://image.tmdb.org/t/p/w342' + pick.posterPath : null
                });
                if (pick.overview) c.querySelector('.cardImageContainer').title = pick.overview;
                var box = c.querySelector('.cardBox');
                if (data.requestMode === 'direct') box.appendChild(controlLine(requestButton(pick, type)));
                else if (data.requestMode === 'link' && data.seerrUrl) {
                    var a = el('a', 'button-link', 'Request in Seerr');
                    a.href = href;
                    a.target = '_blank';
                    a.rel = 'noopener';
                    box.appendChild(controlLine(a));
                }
                box.appendChild(controlLine(ignoreButton(c, { tmdbId: pick.tmdbId, mediaType: type })));
                return c;
            });
        }

        function controlLine(control) {
            var line = el('div', 'cardText cardTextCentered medicPicksControl');
            line.appendChild(control);
            return line;
        }

        function fill(s, cards) {
            var old = s.querySelector('.medicPicksScroller');
            if (old) old.remove();
            s.appendChild(cardList(cards, s));
            s.hidden = cards.length === 0;
        }

        // The rows for one kind of pick: the first row (`first`) for the picks that share no reason with others, then
        // a row for each reason, placed in `extra`.
        function fillGrouped(first, extra, list, least) {
            var grouped = byReason(list, least);
            fill(first, libraryCards(grouped.rest, false));
            extra.textContent = '';
            grouped.rows.forEach(function (g) {
                var s = section(rowTitle(g.reason), null);
                fill(s, libraryCards(g.picks, true));
                extra.appendChild(s);
            });
        }

        function render(data) {
            state.data = data;
            updated.textContent = data.generatedUtc
                ? 'Chosen from what you’ve been watching. Updated ' + fmtUpdated(data.generatedUtc) + '.'
                : 'Chosen from what you’ve been watching. They’re rebuilt every night.';
            fillChoices(data);
            var lib = data.inLibrary || [];
            var link = data.linked || [];
            var disc = data.discover || [];
            var prefs = data.preferences || {};
            var wanted = prefs.count || data.defaultCount || 0;
            fewNote.hidden = !(lib.length && wanted && lib.length < wanted);
            fewNote.textContent = lib.length === 1
                ? 'Only 1 title on the server matches these choices. Choose more genres, or films and series, to see more.'
                : 'Only ' + lib.length + ' titles on the server match these choices. Choose more genres, or films and series, to see more.';

            library.note.textContent = data.playlistName
                ? 'Ready to watch now. They’re also in your playlist “' + data.playlistName + '”, which you’ll find in every Jellyfin app.'
                : 'Ready to watch now.';
            library.note.hidden = false;
            fillGrouped(library, libraryRows, lib, 3);
            fillGrouped(linked, linkedRows, link, 2);
            // With nothing left for the first row, its note moves to the first reason row.
            if (library.hidden && libraryRows.firstChild) {
                libraryRows.firstChild.note.textContent = library.note.textContent;
                libraryRows.firstChild.note.hidden = false;
            }
            discover.note.textContent = data.requestMode === 'direct'
                ? 'Press Request and it goes straight to Seerr under your own account, so your usual request limits apply.'
                : data.requestMode === 'link'
                    ? 'Ask for any of these in Seerr, and they’ll be added once they’re approved.'
                    : 'Titles you might like that aren’t here. Mention any you’d want to whoever runs this server.';
            discover.note.hidden = false;
            fill(discover, discoverCards(disc, data));

            message.hidden = !!(lib.length || link.length || disc.length);
            message.textContent = data.note || 'Nothing to suggest yet. Picks are built overnight from what you watch.';
        }

        // Saving the choices rebuilds this person's picks straight away. Asks until the server says the rebuild has
        // finished (it saves part-way through, so a new time alone isn't enough), for up to 3 minutes. A rebuild that
        // works always saves a new time, so a finished one without it failed.
        function rebuildAndWait(send, busyText) {
            var buttons = [apply, more, unhide];
            var setBusy = function (busy) { state.busy = busy; buttons.forEach(function (b) { b.disabled = busy; }); };
            setBusy(true);
            note.textContent = busyText;
            var before = state.data && state.data.generatedUtc || null;
            var started = Date.now();
            send().then(function () {
                (function poll() {
                    if (!alive()) return;
                    var secs = Math.round((Date.now() - started) / 1000);
                    getJson('Me').then(function (data) {
                        if (!alive()) return;
                        var done = !data.rebuilding;
                        if (done || secs >= 180) {
                            setBusy(false);
                            render(data);
                            var n = (data.inLibrary || []).length + (data.linked || []).length + (data.discover || []).length;
                            note.textContent = !done
                                ? 'Still working on your picks. They’ll appear when you open this page again.'
                                : (data.generatedUtc || null) !== before
                                    ? 'Updated just now: ' + n + (n === 1 ? ' pick.' : ' picks.')
                                    : 'Couldn’t update your picks just now, so they’re as they were.';
                        } else {
                            note.textContent = busyText + (secs >= 5 ? ' (' + secs + 's)' : '');
                            setTimeout(poll, 1500);
                        }
                    }).catch(function () {
                        setBusy(false);
                        note.textContent = 'Couldn’t check on your picks just now. Open this page again to see them.';
                    });
                })();
            }).catch(function () {
                setBusy(false);
                note.textContent = 'Couldn’t save that just now. Try again.';
            });
        }

        form.addEventListener('submit', function (e) {
            e.preventDefault();
            var body = { kind: kind.value, count: parseInt(count.value, 10) || 0, dubbedOnly: dub.checked, genres: chosenGenres() };
            rebuildAndWait(function () { return postJson('Me/Preferences', body); }, 'Updating your picks…');
        });
        more.addEventListener('click', function () {
            rebuildAndWait(function () { return postJson('Me/More', {}); }, 'Finding different ones…');
        });
        unhide.addEventListener('click', function () {
            rebuildAndWait(function () { return postJson('Me/Unhide', {}); }, 'Bringing ignored titles back…');
        });
        clearGenres.addEventListener('click', function () {
            genresBox.querySelectorAll('input').forEach(function (b) { b.checked = false; });
            genreSummaryText();
        });
        dub.addEventListener('change', function () {
            note.textContent = dub.checked && state.data ? dubNote(state.data) : '';
        });

        // ---- Requests ----

        function requestRow(r) {
            var c = card({
                title: r.title + (r.is4k ? ' · 4K' : ''),
                year: r.year,
                second: r.stateText,
                href: r.playItemId ? detailsHref(r.playItemId) : null,
                image: r.posterPath ? 'https://image.tmdb.org/t/p/w342' + r.posterPath : null
            });
            c.setAttribute('data-state', r.state);
            var extra = [];
            var moving = r.state === 'downloading' || r.state === 'partial';
            if (r.detail) extra.push(r.detail);
            if (r.progress != null && moving) extra.push(r.progress + '%');
            if (r.etaUtc && moving) extra.push(formatEta(r.etaUtc));
            if (r.requestedBy) extra.push('asked for by ' + r.requestedBy);
            var box = c.querySelector('.cardBox');
            if (extra.length) box.appendChild(bdiLine('cardText-secondary', extra.join(' · '), extra.join(' · ')));
            if (r.progress != null && moving) {
                var bar = el('progress', 'medicPicksProgress');
                bar.max = 100;
                bar.value = r.progress;
                bar.setAttribute('aria-label', 'Downloaded');
                bar.style.width = '100%';
                box.appendChild(controlLine(bar));
            }
            if (r.playItemId) {
                var play = el('a', 'button-link', 'Play');
                play.href = detailsHref(r.playItemId);
                box.appendChild(controlLine(play));
            }
            return c;
        }

        function renderRequests(data) {
            if (!data || !data.enabled) { requests.hidden = true; return false; }
            requests.note.textContent = !data.linked
                ? 'Sign in to Seerr once with your Jellyfin login, and your requests will show here.'
                : !data.reachable
                    ? 'Couldn’t reach Seerr just now, so your requests can’t be shown. This page tries again by itself.'
                    : 'What you’ve asked for and where it’s got to. This updates by itself while anything is downloading.';
            requests.note.hidden = false;
            var rows = data.requests || [];
            fill(requests, rows.map(requestRow));
            // Shown when there's something to show, or something to explain.
            requests.hidden = rows.length === 0 && data.linked && data.reachable;
            return !data.reachable || rows.some(function (r) { return MOVING.indexOf(r.state) >= 0; });
        }

        function approvalButtons(r, c) {
            var msg = el('div', 'fieldDescription');
            msg.setAttribute('aria-live', 'polite');
            var both = [];
            function decide(text, verb) {
                var b = button(text, verb === 'Approve' ? 'raised button-submit' : 'raised');
                b.addEventListener('click', function () {
                    both.forEach(function (x) { x.disabled = true; });
                    msg.textContent = verb === 'Approve' ? 'Approving…' : 'Declining…';
                    postJson('Admin/Requests/' + encodeURIComponent(r.id) + '/' + verb, {}).then(function (res) {
                        if (res && res.ok) {
                            c.remove();
                            if (!approvals.querySelector('.card')) approvals.hidden = true;
                            refreshRequests();
                        } else {
                            msg.textContent = (res && res.message) || 'That didn’t work. Try again.';
                            both.forEach(function (x) { x.disabled = false; });
                        }
                    }).catch(function () {
                        msg.textContent = 'That didn’t work. Try again.';
                        both.forEach(function (x) { x.disabled = false; });
                    });
                });
                both.push(b);
                return b;
            }
            var box = c.querySelector('.cardBox');
            box.appendChild(controlLine(decide('Approve', 'Approve')));
            box.appendChild(controlLine(decide('Decline', 'Decline')));
            box.appendChild(controlLine(msg));
        }

        function renderApprovals(data) {
            var rows = (data && data.enabled && data.requests) || [];
            fill(approvals, rows.map(function (r) {
                var c = requestRow(r);
                approvalButtons(r, c);
                return c;
            }));
            return rows.length > 0;
        }

        function refreshRequests() {
            clearTimeout(state.requestsTimer);
            if (!alive()) return Promise.resolve();
            var mine = getJson('Me/Requests').then(renderRequests).catch(function () { return false; });
            var theirs = state.admin
                ? getJson('Admin/Requests').then(renderApprovals).catch(function () { return false; })
                : Promise.resolve(false);
            return Promise.all([mine, theirs]).then(function (moving) {
                // Kept current while something's on its way, and only while this page is open and shown.
                if ((moving[0] || moving[1]) && alive()) {
                    state.requestsTimer = setTimeout(function again() {
                        if (!alive()) return;
                        if (document.hidden) { state.requestsTimer = setTimeout(again, 5000); return; }
                        refreshRequests();
                    }, 60000);
                }
            });
        }

        message.hidden = false;
        message.textContent = 'Loading your picks…';
        getJson('Me').then(function (data) {
            if (root.isConnected) render(data || {});
        }).catch(function (err) {
            console.error('Medic Picks: couldn\'t load the picks', err);
            if (!root.isConnected) return;
            message.hidden = false;
            message.textContent = 'Your picks couldn’t load. Try again in a moment, or open the full page.';
            choices.hidden = true;
        });
        Promise.resolve(api().getCurrentUser ? api().getCurrentUser() : null).then(function (user) {
            state.admin = !!(user && user.Policy && user.Policy.IsAdministrator);
        }).catch(function () { /* not an admin, as far as this page knows */ }).then(refreshRequests);
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
        var client = api();
        if (!page || !client || !client.accessToken || !client.accessToken()) {
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
        page.appendChild(root);
        addStyles();
        PicksPage(root);
    }

    // The few things the web app's own classes don't lay out.
    function addStyles() {
        if (document.getElementById('medicPicksStyles')) return;
        var style = el('style');
        style.id = 'medicPicksStyles';
        style.textContent = [
            '#medicPicksInApp .medicPicksChoices { margin: .5em 0 1em; }',
            '#medicPicksInApp .medicPicksChoices > summary { cursor: pointer; margin: 0; }',
            '#medicPicksInApp .medicPicksForm { max-width: 40em; padding-top: .5em; }',
            '#medicPicksInApp .medicPicksGenres { margin: .5em 0 1em; }',
            '#medicPicksInApp .medicPicksGenres > summary { cursor: pointer; padding: .3em 0; }',
            '#medicPicksInApp .medicPicksGenreList { display: flex; flex-wrap: wrap; gap: 0 1.5em; }',
            '#medicPicksInApp .medicPicksGenreList .emby-checkbox-label { width: auto; }',
            '#medicPicksInApp .medicPicksActions { display: flex; flex-wrap: wrap; gap: .5em; align-items: center; }',
            '#medicPicksInApp .medicPicksActions .emby-button { margin: 0; }',
            '#medicPicksInApp .medicPicksControl { white-space: normal; padding: .1em 0; }',
            '#medicPicksInApp .medicPicksControl .emby-button { margin: 0; padding: .3em .6em; font-size: .9em; }',
            '#medicPicksInApp .medicPicksFull { display: inline-block; margin-bottom: 1em; }',
            '#medicPicksInApp .medicPicksScroller { overflow-x: auto; overflow-y: hidden; scroll-behavior: smooth; scrollbar-width: thin; }',
            '#medicPicksInApp .medicPicksScroller > .itemsContainer { white-space: nowrap; padding-left: 3.3%; padding-right: 3.3%; }',
            '#medicPicksInApp .medicPicksHead { display: flex; align-items: center; justify-content: space-between; }',
            '#medicPicksInApp .medicPicksHead .sectionTitle { margin-bottom: 0; }',
            '#medicPicksInApp .medicPicksArrows { display: flex; flex-shrink: 0; }',
            '#medicPicksInApp .medicPicksArrows[hidden] { display: none; }'
        ].join('\n');
        document.head.appendChild(style);
    }

    window.addEventListener('hashchange', mount);
    // The web app draws the page after the address changes, and again on its own (after signing in, say).
    new MutationObserver(function () {
        if (onRoute() && !document.getElementById(MOUNT_ID)) mount();
    }).observe(document.documentElement, { childList: true, subtree: true });
    if (onRoute()) mount();
})();
