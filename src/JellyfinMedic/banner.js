/* Jellyfin Medic: banners for admins on the home page, for serious problems (a damaged database, a
   full disk, a plugin that couldn't load), for a restart that's waiting to finish updates, and for
   what's new once Medic has been updated.
   Only admins see them. Dismissing one hides it on every device (it's kept on the server, per person),
   and both kinds can be turned off in Medic's settings. */
(function () {
    'use strict';
    var ID = 'jellyfin-medic-banners';
    var DISMISSED = 'jellyfin-medic-dismissed-alerts';
    var lastCheck = 0;
    var cached = [];

    function dismissed() {
        try { return JSON.parse(localStorage.getItem(DISMISSED) || '[]'); } catch (e) { return []; }
    }

    function dismiss(id) {
        try {
            var list = dismissed().filter(function (x) { return x !== id; });
            list.push(id);
            localStorage.setItem(DISMISSED, JSON.stringify(list.slice(-50)));
        } catch (e) { /* private window: it'll show again next time */ }
    }

    function el(tag, text, css) {
        var n = document.createElement(tag);
        if (text != null) n.textContent = text;
        if (css) n.style.cssText = css;
        return n;
    }

    function onHome() {
        return /#\/?(home|home\.html)?(\?|$)/.test(location.hash) || location.hash === '';
    }

    function render(alerts) {
        var old = document.getElementById(ID);
        if (old) old.remove();
        var hidden = dismissed();
        var show = (alerts || []).filter(function (a) { return hidden.indexOf(a.Id) < 0; });
        if (!show.length || !onHome()) return;

        var box = el('div', null, 'position:fixed;z-index:2147482000;top:64px;left:50%;transform:translateX(-50%);' +
            'width:min(720px,calc(100vw - 32px));display:flex;flex-direction:column;gap:8px;' +
            'font:14px/1.45 system-ui,-apple-system,"Segoe UI",Roboto,sans-serif;');
        box.id = ID;
        show.forEach(function (a) {
            var critical = a.Kind === 'critical';
            var whatsNew = a.Kind === 'whatsnew';
            var card = el('div', null, 'background:' + (critical ? '#3a1416' : '#16263a') + ';color:#f0f0f0;' +
                'border:1px solid ' + (critical ? '#ff5a5f' : '#00a4dc') + ';border-radius:10px;padding:12px 14px;' +
                'box-shadow:0 8px 24px rgba(0,0,0,.45);display:flex;gap:12px;align-items:flex-start;');
            card.setAttribute('role', critical ? 'alert' : 'status');
            var text = el('div', null, 'flex:1;min-width:0;');
            text.appendChild(el('strong', (critical ? '⚠ ' : '') + a.Title, 'display:block;margin-bottom:2px;'));
            text.appendChild(el('div', a.Detail, 'opacity:.9;overflow-wrap:anywhere;'));
            var off = el('a', whatsNew ? 'See all the changes in Medic \u2192 Settings' : 'Turn these banners off', 'color:#7cc0ff;font-size:12px;');
            off.href = '#/configurationpage?name=JellyfinMedic';
            text.appendChild(off);
            var close = el('button', '×', 'background:none;border:0;color:inherit;font-size:20px;line-height:1;cursor:pointer;padding:0 2px;');
            close.type = 'button';
            close.setAttribute('aria-label', 'Dismiss');
            close.addEventListener('click', function () {
                dismiss(a.Id);
                cached = cached.filter(function (x) { return x.Id !== a.Id; });
                card.remove();
                if (!box.children.length) box.remove();
                // Remembered on the server too, so other devices don't show it again.
                var api = window.ApiClient;
                if (api) api.ajax({ type: 'POST', url: api.getUrl('JellyfinMedic/Alerts/Dismiss', { id: a.Id }) }).catch(function () { /* this browser still remembers */ });
            });
            card.appendChild(text);
            card.appendChild(close);
            box.appendChild(card);
        });
        document.body.appendChild(box);
    }

    function check() {
        var api = window.ApiClient;
        if (!api || !api.accessToken || !api.accessToken()) return;
        if (Date.now() - lastCheck < 5 * 60 * 1000) { render(cached); return; }
        lastCheck = Date.now();
        api.getCurrentUser().then(function (user) {
            if (!user || !user.Policy || !user.Policy.IsAdministrator) { cached = []; return; }
            return api.getJSON(api.getUrl('JellyfinMedic/Alerts')).then(function (alerts) {
                cached = alerts || [];
                render(cached);
            });
        }).catch(function () { /* Medic not reachable: no banner */ });
    }

    window.addEventListener('hashchange', function () { setTimeout(check, 500); });
    document.addEventListener('viewshow', function () { setTimeout(check, 500); });
    var tries = 0;
    var wait = setInterval(function () {
        tries++;
        if ((window.ApiClient && window.ApiClient.accessToken && window.ApiClient.accessToken()) || tries > 60) {
            clearInterval(wait);
            check();
        }
    }, 1000);
    setInterval(check, 5 * 60 * 1000);
})();
