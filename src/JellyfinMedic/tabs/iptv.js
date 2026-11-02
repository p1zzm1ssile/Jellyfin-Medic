// The Medic page's IPTV tab. medic.html loads this when the tab is first opened (see tabScript there), and calls the
// function below once with the page's shared helpers; it returns what the page calls on this tab.
(window.JellyfinMedicTabs = window.JellyfinMedicTabs || {}).iptv = function (m) {
    'use strict';
    const $ = m.$;
    const el = m.el;
    const pick = m.pick;

    // The last analysis, kept by the server until Jellyfin restarts.
    let iptv = null;

    function renderIptv() {
        const box = $('md-iptv');
        box.innerHTML = '';
        if (!iptv) {
            box.appendChild(el('p', 'so-muted so-small', 'Not analysed since Jellyfin last started.'));
            return;
        }
        box.appendChild(el('p', 'so-muted so-small', 'Analysed ' + m.fmtDateTime(pick(iptv, 'GeneratedUtc')) + '. "Watched" is the most items in that group watched by any one person.'));
        const libs = pick(iptv, 'Libraries') || [];
        if (libs.length === 0) box.appendChild(el('p', 'so-muted', 'No IPTV (.strm) libraries were found.'));
        libs.forEach(function (lib) {
            const panel = el('div', 'so-panel');
            panel.appendChild(el('h3', null, pick(lib, 'Name') + ': ' + Number(pick(lib, 'Items') || 0).toLocaleString('en-GB') + ' items'));
            const table = el('table', 'so-table');
            const hr = el('tr');
            ['Genre', 'Items', 'Watched', 'Share of library'].forEach(function (h) { hr.appendChild(el('th', null, h)); });
            const thead = el('thead');
            thead.appendChild(hr);
            table.appendChild(thead);
            const body = el('tbody');
            (pick(lib, 'Genres') || []).forEach(function (g) {
                const tr = el('tr');
                tr.appendChild(el('td', null, pick(g, 'Name')));
                tr.appendChild(el('td', null, Number(pick(g, 'Items') || 0).toLocaleString('en-GB')));
                const w = pick(g, 'Watched') || 0;
                tr.appendChild(el('td', w === 0 && pick(g, 'Items') >= 500 ? 'md-warn-text' : null, Number(w).toLocaleString('en-GB')));
                const share = el('td');
                const bar = el('div', 'md-bar');
                const fill = el('i');
                fill.style.width = Math.round((pick(g, 'Share') || 0) * 100) + '%';
                bar.appendChild(fill);
                share.appendChild(bar);
                tr.appendChild(share);
                body.appendChild(tr);
            });
            table.appendChild(body);
            const wrap = el('div', 'so-scroll');
            wrap.appendChild(table);
            panel.appendChild(wrap);
            const countries = (pick(lib, 'Countries') || []).map(function (c) { return pick(c, 'Name') + ' ' + Math.round((pick(c, 'Share') || 0) * 100) + '%'; });
            if (countries.length) panel.appendChild(el('p', 'so-small so-muted', 'Where they come from (sampled): ' + countries.join(', ')));
            box.appendChild(panel);
        });

        const ch = pick(iptv, 'Channels') || {};
        const panel = el('div', 'so-panel');
        panel.appendChild(el('h3', null, 'Live channels'));
        if (!pick(ch, 'Total')) {
            panel.appendChild(el('p', 'so-muted', 'No live TV channels found.'));
        } else {
            const dl = el('dl', 'so-kv');
            function row(k, v) { dl.appendChild(el('dt', null, k)); dl.appendChild(el('dd', null, v)); }
            row('Channels', Number(pick(ch, 'Total')).toLocaleString('en-GB'));
            row('Shown more than once', Number(pick(ch, 'DuplicateGroups') || 0).toLocaleString('en-GB') + ' channels, ' + Number(pick(ch, 'ExtraCopies') || 0).toLocaleString('en-GB') + ' extra copies');
            row('Regional versions', Number(pick(ch, 'Regional') || 0).toLocaleString('en-GB'));
            panel.appendChild(dl);
            const ex = pick(ch, 'Examples') || [];
            if (ex.length) panel.appendChild(el('p', 'so-small so-muted', 'For example: ' + ex.join(', ')));
            const dups = pick(ch, 'Duplicates') || [];
            if (dups.length) {
                // Every channel with copies, and the name of each copy, so you can pick which to keep.
                const details = el('details', 'md-dups');
                details.appendChild(el('summary', null, 'Show all ' + dups.length.toLocaleString('en-GB') + ' channels with copies'));
                const filter = el('input', 'so-input');
                filter.type = 'search';
                filter.placeholder = 'Filter channels';
                filter.setAttribute('aria-label', 'Filter channels');
                details.appendChild(filter);
                const list = el('ul', 'md-dup-list');
                dups.forEach(function (d) {
                    const li = el('li');
                    li.appendChild(el('strong', null, pick(d, 'Name') + ' (' + (pick(d, 'Versions') || []).length + ')'));
                    li.appendChild(el('span', 'so-small so-muted', (pick(d, 'Versions') || []).join(' · ')));
                    li.dataset.text = (pick(d, 'Name') + ' ' + (pick(d, 'Versions') || []).join(' ')).toLowerCase();
                    list.appendChild(li);
                });
                details.appendChild(list);
                filter.addEventListener('input', function () {
                    const q = filter.value.trim().toLowerCase();
                    list.querySelectorAll('li').forEach(function (li) { li.hidden = q !== '' && li.dataset.text.indexOf(q) < 0; });
                });
                panel.appendChild(details);
                panel.appendChild(el('p', 'md-note', 'Keep one version of each (usually the best quality your TVs can play) and hide or remove the rest in your IPTV plugin or provider.'));
            }
        }
        box.appendChild(panel);
    }

    $('md-run-iptv').addEventListener('click', async function () {
        if (m.isTesting()) return;
        const btn = $('md-run-iptv');
        btn.disabled = true;
        $('md-iptv').innerHTML = '';
        $('md-iptv').appendChild(el('p', 'so-muted', 'Analysing your IPTV libraries… this can take a minute on a big catalogue.'));
        try {
            iptv = await m.postJson('/JellyfinMedic/Iptv');
            renderIptv();
        } catch (err) {
            console.error('Jellyfin Medic: IPTV analysis failed', err);
            $('md-iptv').innerHTML = '';
            $('md-iptv').appendChild(el('p', 'md-bad-text', 'The analysis couldn\'t run. If a test is running, wait for it to finish.'));
        } finally {
            btn.disabled = false;
        }
    });

    return {
        load: async function () {
            if (!iptv) { iptv = await m.getJson('/JellyfinMedic/Iptv'); }
            renderIptv();
        },
        redrawTimes: function () { if (iptv) renderIptv(); }
    };
};
