// The Medic page's Media report tab. medic.html loads this when the tab is first opened (see tabScript there), and calls
// the function below once with the page's shared helpers; it returns what the page calls on this tab.
(window.JellyfinMedicTabs = window.JellyfinMedicTabs || {}).media = function (m) {
    'use strict';
    const $ = m.$;
    const el = m.el;
    const pick = m.pick;
    const tracksSize = m.tracksSize;
    const fmtShort = m.fmtShort;
    const fmtDate = m.fmtDate;
    const txReason = m.txReason;

    let mediaStatus = null;
    let mediaTimer = null;
    const HW_LABEL = { nvenc: 'NVIDIA NVENC', qsv: 'Intel QSV', vaapi: 'VAAPI', amf: 'AMD AMF', videotoolbox: 'Apple VideoToolbox', rkmpp: 'Rockchip MPP', v4l2m2m: 'V4L2' };

    function mediaTile(head, big, note) {
        const t = el('section', 'md-tile');
        t.appendChild(el('h3', 'md-tile-head', head));
        t.appendChild(el('p', 'md-big', big));
        if (note) t.appendChild(el('p', 'so-muted so-small', note));
        return t;
    }

    function mediaRows(tbody, rows, total, withBar) {
        tbody.innerHTML = '';
        (rows || []).forEach(function (r) {
            const tr = el('tr');
            const bytes = pick(r, 'Bytes') || 0;
            tr.appendChild(el('td', null, pick(r, 'Name')));
            tr.appendChild(el('td', null, (pick(r, 'Files') || 0).toLocaleString('en-GB')));
            tr.appendChild(el('td', null, tracksSize(bytes)));
            if (withBar) {
                const td = el('td');
                const bar = el('div', 'md-bar');
                const fill = el('i');
                fill.style.width = (total > 0 ? Math.max(1, Math.round(100 * bytes / total)) : 0) + '%';
                bar.appendChild(fill);
                td.appendChild(bar);
                tr.appendChild(td);
            }
            tbody.appendChild(tr);
        });
    }

    function renderMedia() {
        const st = mediaStatus || {};
        const running = !!pick(st, 'Running');
        const r = pick(st, 'Report');
        $('md-media-build').disabled = running;
        $('md-media-build').textContent = r ? 'Rebuild report' : 'Build report';

        if (running) {
            const total = pick(st, 'Total') || 0, done = pick(st, 'Done') || 0;
            $('md-media-status').textContent = total ? 'Reading ' + done.toLocaleString('en-GB') + ' of ' + total.toLocaleString('en-GB') + ' items…' : 'Gathering your library…';
        } else if (r) {
            const when = new Date(pick(r, 'GeneratedUtc'));
            $('md-media-status').textContent = 'Built ' + fmtShort(when) + ' · ' + (pick(r, 'Files') || 0).toLocaleString('en-GB') + ' video files · ' + tracksSize(pick(r, 'Bytes') || 0);
        } else {
            $('md-media-status').textContent = 'No report yet. Building one reads every video file Jellyfin knows about; on a large library it takes a minute or two.';
        }

        if (r) {
            $('md-media-body').hidden = false;
            const sv = pick(r, 'Savings') || {};
            const hw = String(pick(r, 'HardwareAcceleration') || 'none').toLowerCase();
            const tiles = $('md-media-tiles');
            tiles.innerHTML = '';
            tiles.appendChild(mediaTile('Video files', (pick(r, 'Files') || 0).toLocaleString('en-GB'), tracksSize(pick(r, 'Bytes') || 0) + ' in total'));
            tiles.appendChild(mediaTile('Could convert', (pick(sv, 'CandidateFiles') || 0).toLocaleString('en-GB') + ' files',
                tracksSize(pick(sv, 'CandidateBytes') || 0) + ' in H.264 or older formats that HEVC stores more efficiently'));
            tiles.appendChild(mediaTile('Could free with a GPU', 'About ' + tracksSize(pick(sv, 'GpuBytes') || 0),
                HW_LABEL[hw] ? 'Using your ' + HW_LABEL[hw] + ' encoder. Fast.' : 'No GPU encoder is set up in Jellyfin yet.'));
            tiles.appendChild(mediaTile('Could free with software', 'About ' + tracksSize(pick(sv, 'SoftwareBytes') || 0), 'Slow x265 encoding on the CPU. Smaller files, many times longer.'));

            const big = $('md-media-big');
            big.innerHTML = '';
            const list = pick(r, 'BiggestSavings') || [];
            if (list.length === 0) {
                const tr = el('tr');
                const td = el('td', 'so-muted', 'Nothing to convert: your video is already in efficient formats.');
                td.colSpan = 6;
                tr.appendChild(td);
                big.appendChild(tr);
            }
            list.forEach(function (f) {
                const tr = el('tr');
                const title = el('td');
                title.appendChild(el('div', null, pick(f, 'Title')));
                title.appendChild(el('div', 'so-muted so-small', pick(f, 'FileName')));
                tr.appendChild(title);
                tr.appendChild(el('td', null, pick(f, 'Codec')));
                tr.appendChild(el('td', null, pick(f, 'Resolution')));
                tr.appendChild(el('td', null, pick(f, 'Mbps') ? pick(f, 'Mbps') + ' Mbps' : '–'));
                tr.appendChild(el('td', null, tracksSize(pick(f, 'Bytes') || 0)));
                tr.appendChild(el('td', null, 'about ' + tracksSize(pick(f, 'GpuSavingBytes') || 0)));
                big.appendChild(tr);
            });

            const total = pick(r, 'Bytes') || 0;
            mediaRows($('md-media-codecs'), pick(r, 'Codecs'), total, true);
            mediaRows($('md-media-res'), pick(r, 'Resolutions'), total, false);
            mediaRows($('md-media-hdr'), pick(r, 'DynamicRange'), total, false);
            mediaRows($('md-media-ext'), pick(r, 'Containers'), total, false);

            const c = pick(r, 'Compatibility') || {};
            const compat = $('md-media-compat');
            compat.innerHTML = '';
            const items = [
                [pick(c, 'DtsOrTrueHdOnly'), 'have only DTS or TrueHD audio', 'Browsers and many TVs can\'t play these, so Jellyfin converts the audio as it plays. That\'s a light job, but adding a stereo AAC or AC-3 track avoids it.'],
                [pick(c, 'ImageSubtitles'), 'have picture-based subtitles (PGS or VobSub)', 'When someone turns these on, Jellyfin has to burn them into the picture, which means a full video transcode. Text subtitles such as SRT don\'t.'],
                [pick(c, 'OldContainers'), 'use older file types (AVI, WMV, MPEG and similar)', 'Modern apps often can\'t play these directly. Remuxing to MKV is quick and lossless, but it changes the file name.'],
                [pick(c, 'Hdr'), 'are HDR or Dolby Vision', 'Screens without HDR need tone mapping, which needs a GPU to be smooth.']
            ];
            let shown = 0;
            items.forEach(function (it) {
                if (!it[0]) return;
                shown++;
                const card = el('div', 'so-card sev-Tip');
                card.appendChild(el('h4', null, it[0].toLocaleString('en-GB') + ' files ' + it[1]));
                card.appendChild(el('p', 'so-why', it[2]));
                compat.appendChild(card);
            });
            if (shown === 0) compat.appendChild(el('p', 'so-muted', 'Nothing here is likely to force a transcode.'));
        } else {
            $('md-media-body').hidden = true;
        }

        // Transcode log from playback
        const tx = pick(st, 'Transcodes') || {};
        const since = pick(tx, 'SinceUtc');
        const top = pick(tx, 'Top') || [];
        const sinceText = since ? fmtDate(since, true) : null;
        if (top.length === 0) {
            $('md-media-tx-note').textContent = sinceText
                ? 'Nothing has been caught transcoding since ' + sinceText + '. Medic checks playback every 5 minutes, so this fills in as people watch.'
                : 'Medic records this from its 5-minute playback check. It starts filling in once people watch something.';
            $('md-media-tx-wrap').hidden = true;
        } else {
            const totals = pick(tx, 'ReasonTotals') || {};
            const topReasons = Object.keys(totals).sort(function (a, b) { return totals[b] - totals[a]; }).slice(0, 3).map(txReason);
            $('md-media-tx-note').textContent = (pick(tx, 'Files') || top.length) + ' files caught transcoding since ' + sinceText + '. Most common reasons: ' + topReasons.join('; ') + '. Listed by how often they were caught; each catch is roughly 5 minutes of playback.';
            const body = $('md-media-tx');
            body.innerHTML = '';
            top.forEach(function (e) {
                const tr = el('tr');
                tr.appendChild(el('td', null, pick(e, 'Title')));
                const reasons = pick(e, 'Reasons') || {};
                tr.appendChild(el('td', null, Object.keys(reasons).sort(function (a, b) { return reasons[b] - reasons[a]; }).map(txReason).join('; ')));
                const n = pick(e, 'Samples') || 0;
                tr.appendChild(el('td', null, n === 1 ? 'once' : n + ' times'));
                tr.appendChild(el('td', 'so-muted', (pick(e, 'Clients') || []).join(', ')));
                body.appendChild(tr);
            });
            $('md-media-tx-wrap').hidden = false;
        }
    }

    function stop() { clearTimeout(mediaTimer); }

    async function loadMedia() {
        clearTimeout(mediaTimer);
        try {
            mediaStatus = await m.getJson('/JellyfinMedic/MediaReport');
            renderMedia();
            if (pick(mediaStatus, 'Running')) {
                clearTimeout(mediaTimer);
                if (m.isShowing('media')) mediaTimer = setTimeout(function () { if (m.isShowing('media')) loadMedia(); }, 2000);
            }
        } catch (e) {
            $('md-media-status').textContent = 'Couldn\'t load the media report. Check the Jellyfin log for Jellyfin Medic errors.';
        }
    }

    $('md-media-build').addEventListener('click', async function () {
        $('md-media-build').disabled = true;
        try {
            mediaStatus = await m.postJson('/JellyfinMedic/MediaReport/Build');
            renderMedia();
        } catch (e) { /* the poll below shows the state */ }
        loadMedia();
    });

    return {
        load: loadMedia,
        stop: stop,
        redrawTimes: function () { if (mediaStatus) renderMedia(); }
    };
};
