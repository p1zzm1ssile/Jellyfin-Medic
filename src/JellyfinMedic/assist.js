/* Jellyfin Medic: shows the issue you're fixing in a small box you can move, on the page Medic sent you to.
   Medic saves the issue in this browser when you click one of its "Where" links. Nothing is sent anywhere,
   and the box only appears for 30 minutes after a click, until you close it. */
(function () {
    'use strict';
    var KEY = 'jellyfin-medic-assist';
    var POS = 'jellyfin-medic-assist-pos';

    function read() {
        try {
            var a = JSON.parse(localStorage.getItem(KEY) || 'null');
            if (!a || !a.title || Date.now() - (a.at || 0) > 30 * 60 * 1000) {
                localStorage.removeItem(KEY);
                return null;
            }
            return a;
        } catch (e) {
            return null;
        }
    }

    function el(tag, text, css) {
        var n = document.createElement(tag);
        if (text != null) n.textContent = text;
        if (css) n.style.cssText = css;
        return n;
    }

    function show(a) {
        if (document.getElementById(KEY)) return;

        var box = el('div', null,
            'position:fixed;z-index:2147483000;right:20px;bottom:20px;width:min(380px,calc(100vw - 32px));' +
            'background:#1c1f26;color:#e8e8e8;border:1px solid #00a4dc;border-radius:10px;' +
            'box-shadow:0 8px 28px rgba(0,0,0,.5);font:14px/1.45 system-ui,-apple-system,"Segoe UI",Roboto,sans-serif;');
        box.id = KEY;
        box.setAttribute('role', 'dialog');
        box.setAttribute('aria-label', 'Jellyfin Medic: the issue you are fixing');

        var head = el('div', null,
            'display:flex;align-items:center;gap:8px;padding:8px 10px;background:#00a4dc;color:#fff;' +
            'border-radius:9px 9px 0 0;cursor:move;user-select:none;touch-action:none;');
        head.appendChild(el('strong', 'Jellyfin Medic', 'flex:1;font-size:13px;letter-spacing:.02em'));

        function button(label, title) {
            var b = el('button', label,
                'background:transparent;border:0;color:#fff;font-size:18px;line-height:1;cursor:pointer;padding:2px 6px;border-radius:4px;');
            b.type = 'button';
            b.title = title;
            b.setAttribute('aria-label', title);
            return b;
        }

        var min = button('–', 'Minimise');
        var close = button('×', 'Close');
        head.appendChild(min);
        head.appendChild(close);
        box.appendChild(head);

        var body = el('div', null, 'padding:12px 14px 14px;max-height:50vh;overflow:auto;');
        body.appendChild(el('div', a.title, 'font-weight:600;font-size:15px;margin-bottom:8px;'));

        function row(label, value, color) {
            if (!value) return;
            var r = el('div', null, 'margin:4px 0;');
            r.appendChild(el('span', label + ' ', 'color:#9aa3b2;'));
            r.appendChild(el('span', value, color ? 'color:' + color + ';' : ''));
            body.appendChild(r);
        }

        row('Now:', a.current, '#ffb37a');
        row('Suggested:', a.suggested, '#7fe0a4');
        if (a.why) body.appendChild(el('p', a.why, 'margin:8px 0 0;color:#c8ccd4;'));
        if (a.where) body.appendChild(el('p', 'Where: ' + a.where, 'margin:8px 0 0;color:#9aa3b2;font-size:12px;'));
        box.appendChild(body);

        min.addEventListener('click', function () {
            var hidden = body.style.display === 'none';
            body.style.display = hidden ? '' : 'none';
            min.textContent = hidden ? '–' : '+';
            min.title = hidden ? 'Minimise' : 'Show';
        });
        close.addEventListener('click', function () {
            try { localStorage.removeItem(KEY); } catch (e) { /* ignore */ }
            box.remove();
        });

        // Restore the last position, kept inside the window.
        function place(x, y) {
            var w = box.offsetWidth, h = box.offsetHeight;
            x = Math.max(4, Math.min(x, window.innerWidth - w - 4));
            y = Math.max(4, Math.min(y, window.innerHeight - h - 4));
            box.style.left = x + 'px';
            box.style.top = y + 'px';
            box.style.right = 'auto';
            box.style.bottom = 'auto';
        }

        document.body.appendChild(box);
        try {
            var saved = JSON.parse(localStorage.getItem(POS) || 'null');
            if (saved) place(saved.x, saved.y);
        } catch (e) { /* default corner */ }

        // Drag by the header (mouse or touch).
        var drag = null;
        head.addEventListener('pointerdown', function (e) {
            if (e.target.tagName === 'BUTTON') return;
            var r = box.getBoundingClientRect();
            drag = { dx: e.clientX - r.left, dy: e.clientY - r.top };
            head.setPointerCapture(e.pointerId);
        });
        head.addEventListener('pointermove', function (e) {
            if (drag) place(e.clientX - drag.dx, e.clientY - drag.dy);
        });
        head.addEventListener('pointerup', function () {
            if (!drag) return;
            drag = null;
            var r = box.getBoundingClientRect();
            try { localStorage.setItem(POS, JSON.stringify({ x: r.left, y: r.top })); } catch (e) { /* ignore */ }
        });
    }

    function start() {
        var a = read();
        if (a) show(a);
    }

    if (document.readyState === 'loading') {
        document.addEventListener('DOMContentLoaded', start);
    } else {
        start();
    }
})();
