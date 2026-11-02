// A pretend Sonarr for the Medic Profiles tests: answers the API calls Medic Profiles makes with
// fixed data, and remembers what was sent to it.
import http from 'node:http';

// Only on the address Jellyfin's container reaches this machine by (run.sh passes it), not on every network.
const HOST = process.env.FAKE_HOST || '127.0.0.1';

export const received = [];

// How many entries the download queue has; a test sets it and puts it back to 0.
export const queue = { total: 0 };

function queuePage(url) {
    const page = Number(url.searchParams.get('page') || 1);
    const size = Number(url.searchParams.get('pageSize') || 100);
    const records = [];
    for (let i = (page - 1) * size; i < Math.min(queue.total, page * size); i++) {
        const episode = (i % 99) + 1;
        records.push({
            id: i + 1, downloadId: 'download-' + i, title: `Fake.Show.S01E${String(episode).padStart(2, '0')}.1080p`,
            status: 'downloading', trackedDownloadState: 'downloading', size: 1000, sizeleft: 500,
            added: new Date().toISOString(), seriesId: 1, series: { title: 'Fake Show' },
            episode: { seasonNumber: 1, episodeNumber: episode, title: 'An Episode' }
        });
    }
    return { page, pageSize: size, totalRecords: queue.total, records };
}

// A finished download Sonarr wouldn't import on its own, for the import-by-hand check: one file is fine,
// the other Sonarr rejected. A check sets stuckImport.on and puts it back to false.
export const stuckImport = { on: false };
const STUCK_DOWNLOAD = 'download-stuck-import';

function withStuckImport(page) {
    if (!stuckImport.on || page.page !== 1) return page;
    const stuck = {
        id: 9001, downloadId: STUCK_DOWNLOAD, title: 'Fake.Show.S02E01.1080p', status: 'completed', trackedDownloadState: 'importBlocked',
        size: 1000, sizeleft: 0, added: new Date().toISOString(), seriesId: 1, series: { title: 'Fake Show' },
        episode: { seasonNumber: 2, episodeNumber: 1, title: 'Another Episode' },
        statusMessages: [{ title: 'Fake.Show.S02E01.1080p', messages: ['One or more episodes expected in this release were not imported'] }]
    };
    return { ...page, totalRecords: page.totalRecords + 1, records: [...page.records, stuck] };
}

function manualImport(url) {
    if (!stuckImport.on || url.searchParams.get('downloadId') !== STUCK_DOWNLOAD) return [];
    const file = (name, rejections) => ({
        path: '/downloads/Fake.Show.S02E01.1080p/' + name, relativePath: name, size: 500, seasonNumber: 2,
        quality: { quality: { id: 9, name: 'HDTV-1080p' } }, languages: [{ id: 1, name: 'English' }],
        series: { id: 1, title: 'Fake Show' }, episodes: [{ id: 201, seasonNumber: 2, episodeNumber: 1, title: 'Another Episode' }], rejections
    });
    return [file('Fake.Show.S02E01.1080p.mkv', []), file('Fake.Show.S02E01.1080p.sample.mkv', [{ reason: 'Sample', type: 'permanent' }])];
}

// How many releases the blocklist holds; a test sets it above 100 and puts it back to 0.
export const blocklist = { total: 0 };

function blocklistPage(url) {
    const size = Number(url.searchParams.get('pageSize') || 20);
    const records = [];
    for (let i = 0; i < Math.min(blocklist.total, size); i++) {
        records.push({
            id: i + 1, seriesId: 1, series: { title: 'Fake Show' }, sourceTitle: `Fake.Show.S01E${String((i % 99) + 1).padStart(2, '0')}.720p`,
            date: new Date(Date.now() - i * 60000).toISOString(), quality: { quality: { name: 'HDTV-720p' } }, indexer: 'Good Indexer', message: 'Manually blocked'
        });
    }
    return { page: 1, pageSize: size, totalRecords: blocklist.total, records };
}

const indexers = [
    { id: 1, name: 'Good Indexer', protocol: 'torrent', enableRss: true, enableAutomaticSearch: true, enableInteractiveSearch: true },
    { id: 2, name: 'Broken Indexer', protocol: 'usenet', enableRss: true, enableAutomaticSearch: true, enableInteractiveSearch: true },
    { id: 3, name: 'Unused Indexer', protocol: 'torrent', enableRss: false, enableAutomaticSearch: false, enableInteractiveSearch: false }
];

const routes = {
    '/api/v3/system/status': () => ({ appName: 'Sonarr', version: '4.0.9.2244' }),
    '/api/v3/indexer': () => indexers,
    '/api/v3/health': () => [{ source: 'IndexerStatusCheck', type: 'warning', message: 'Indexers unavailable due to failures: Broken Indexer' }],
    '/api/v3/queue': (url) => withStuckImport(queuePage(url)),
    '/api/v3/queue/bulk': () => ({}),
    '/api/v3/blocklist': blocklistPage,
    '/api/v3/manualimport': manualImport,
    '/api/v3/qualityprofile': () => [],
    '/api/v3/customformat': () => [],
    '/api/v3/downloadclient': () => [{ id: 1, name: 'qBittorrent', implementation: 'QBittorrent', protocol: 'torrent', enable: true, fields: [{ name: 'host', value: 'qbit' }, { name: 'port', value: 8080 }] }]
};

export function start(port) {
    const server = http.createServer((req, res) => {
        let body = '';
        req.on('data', (c) => { body += c; });
        req.on('end', () => {
            const url = new URL(req.url, 'http://fake');
            const path = url.pathname;
            received.push({ method: req.method, path, query: url.search, body, key: req.headers['x-api-key'] });
            const route = routes[path];
            if (req.headers['x-api-key'] !== 'test-key') { res.writeHead(401); res.end(); return; }
            res.writeHead(route ? 200 : 404, { 'Content-Type': 'application/json' });
            res.end(route ? JSON.stringify(route(url)) : '{}');
        });
    });
    return new Promise((resolve) => server.listen(port, HOST, () => resolve(server)));
}

// A pretend Radarr with one finished download it refused to import: its one file is matched to a film
// but rejected. It remembers the commands posted to it.
export const radarr = { commands: [] };

const radarrFilm = { id: 7, title: 'Fake Film', year: 2001 };

const radarrRoutes = {
    '/api/v3/system/status': () => ({ appName: 'Radarr', version: '5.14.0.9383' }),
    '/api/v3/queue': () => ({
        page: 1, pageSize: 100, totalRecords: 1, records: [{
            id: 1, downloadId: 'film-download', title: 'Fake.Film.2001.1080p', status: 'completed', trackedDownloadState: 'importBlocked',
            size: 1000, sizeleft: 0, added: new Date().toISOString(), movieId: radarrFilm.id, movie: radarrFilm
        }]
    }),
    '/api/v3/manualimport': () => [{
        path: '/downloads/Fake.Film.2001.1080p/Fake.Film.2001.1080p.mkv', relativePath: 'Fake.Film.2001.1080p.mkv', size: 1000,
        quality: { quality: { id: 7, name: 'Bluray-1080p' } }, languages: [{ id: 1, name: 'English' }],
        movie: radarrFilm, rejections: [{ reason: 'Not an upgrade for existing movie file', type: 'permanent' }]
    }],
    '/api/v3/movie': () => [radarrFilm]
};

export function startRadarr(port) {
    const server = http.createServer((req, res) => {
        let body = '';
        req.on('data', (c) => { body += c; });
        req.on('end', () => {
            const path = req.url.split('?')[0];
            if (req.headers['x-api-key'] !== 'test-key') { res.writeHead(401); res.end(); return; }
            res.setHeader('Content-Type', 'application/json');
            if (req.method === 'POST' && path === '/api/v3/command') {
                radarr.commands.push(JSON.parse(body));
                res.end(JSON.stringify({ id: radarr.commands.length, name: 'ManualImport' }));
                return;
            }
            const route = req.method === 'GET' && radarrRoutes[path];
            res.writeHead(route ? 200 : 404);
            res.end(route ? JSON.stringify(route()) : '{}');
        });
    });
    return new Promise((resolve) => server.listen(port, HOST, () => resolve(server)));
}

// A pretend qBittorrent Web UI: sign-in with admin/secret, and the "Excluded file names" preference.
// v52 answers sign-ins the way qBittorrent 5.2 does (204 with no text, 401 for a wrong password, a
// QBT_SID_<port> cookie); banned answers 403, as qBittorrent does after too many wrong passwords.
export const qbit = { prefs: { excluded_file_names_enabled: false, excluded_file_names: '*.nfo' }, sets: [], logins: 0, failedLogins: 0, v52: false, banned: false };

export function startQbit(port) {
    const server = http.createServer((req, res) => {
        let body = '';
        req.on('data', (c) => { body += c; });
        req.on('end', () => {
            const path = req.url.split('?')[0];
            const signedIn = /SID(_\d+)?=good/.test(req.headers.cookie || '');
            if (path === '/api/v2/auth/login') {
                const form = new URLSearchParams(body);
                qbit.logins++;
                if (qbit.banned) {
                    res.writeHead(403); res.end('Your IP address has been banned after too many failed authentication attempts.');
                } else if (form.get('username') === 'admin' && form.get('password') === 'secret') {
                    if (qbit.v52) {
                        res.writeHead(204, { 'Set-Cookie': `QBT_SID_${port}=good; HttpOnly; SameSite=Strict; path=/` });
                        res.end();
                    } else {
                        res.writeHead(200, { 'Set-Cookie': 'SID=good; HttpOnly; path=/' });
                        res.end('Ok.');
                    }
                } else {
                    qbit.failedLogins++;
                    if (qbit.v52) { res.writeHead(401); res.end('Unauthorized'); } else { res.writeHead(200); res.end('Fails.'); }
                }
                return;
            }
            if (path === '/api/v2/auth/logout') { res.writeHead(200); res.end(); return; }
            if (!signedIn) { res.writeHead(403); res.end('Forbidden'); return; }
            if (path === '/api/v2/app/preferences') {
                res.writeHead(200, { 'Content-Type': 'application/json' });
                res.end(JSON.stringify(qbit.prefs));
                return;
            }
            if (path === '/api/v2/app/setPreferences') {
                const json = JSON.parse(new URLSearchParams(body).get('json'));
                qbit.sets.push(json);
                Object.assign(qbit.prefs, json);
                res.writeHead(200); res.end();
                return;
            }
            res.writeHead(404); res.end();
        });
    });
    return new Promise((resolve) => server.listen(port, HOST, () => resolve(server)));
}

// A pretend Seerr: a Jellyfin account is the Seerr user a check lists in accounts (by its Jellyfin id
// without dashes), and Seerr user 42 otherwise. It remembers which Seerr user each approve or decline was
// made as (X-API-User), and every new request: its body and the user it was made as. Its request list ignores
// ?requestedBy= on purpose and always holds one request by user 42 and one by user 7, plus any a check adds
// to extraRequests.
export const seerr = { decisions: [], extraRequests: [], accounts: {}, created: [] };

const requests = [
    { id: 11, status: 1, type: 'movie', createdAt: '2026-10-01T10:00:00.000Z', media: { tmdbId: 550, mediaType: 'movie', status: 2 }, requestedBy: { id: 42 } },
    { id: 12, status: 1, type: 'movie', createdAt: '2026-10-02T10:00:00.000Z', media: { tmdbId: 551, mediaType: 'movie', status: 2 }, requestedBy: { id: 7 } }
];

export function startSeerr(port) {
    const server = http.createServer((req, res) => {
        let body = '';
        req.on('data', (c) => { body += c; });
        req.on('end', () => answerSeerr(req, res, body));
    });
    return new Promise((resolve) => server.listen(port, HOST, () => resolve(server)));
}

function answerSeerr(req, res, body) {
    const path = req.url.split('?')[0];
    if (req.headers['x-api-key'] !== 'e2e-seerr-key') { res.writeHead(401); res.end(); return; }
    res.setHeader('Content-Type', 'application/json');
    if (req.method === 'GET' && path.startsWith('/api/v1/user/jellyfin/')) {
        const jellyfinId = path.slice('/api/v1/user/jellyfin/'.length).replace(/-/g, '').toLowerCase();
        res.end(JSON.stringify({ id: seerr.accounts[jellyfinId] ?? 42, displayName: 'admin' }));
        return;
    }
    if (req.method === 'POST' && path === '/api/v1/request') {
        let json = null;
        try { json = JSON.parse(body); } catch { /* recorded as null, so a check sees it wasn't JSON */ }
        seerr.created.push({ body: json, asUser: req.headers['x-api-user'] ?? null, contentType: req.headers['content-type'] ?? null });
        res.writeHead(201);
        res.end(JSON.stringify({ id: 100 + seerr.created.length, status: 1, type: json?.mediaType }));
        return;
    }
    if (req.method === 'GET' && path === '/api/v1/request') {
        const results = [...requests, ...seerr.extraRequests];
        res.end(JSON.stringify({ pageInfo: { results: results.length }, results }));
        return;
    }
    const title = path.match(/^\/api\/v1\/movie\/(\d+)$/);
    if (req.method === 'GET' && title) {
        res.end(JSON.stringify({ id: Number(title[1]), title: 'Film ' + title[1], releaseDate: '2000-01-01' }));
        return;
    }
    const decision = path.match(/^\/api\/v1\/request\/(\d+)\/(approve|decline)$/);
    if (req.method === 'POST' && decision) {
        seerr.decisions.push({ id: Number(decision[1]), action: decision[2], asUser: req.headers['x-api-user'] ?? null });
        res.end(JSON.stringify({ id: Number(decision[1]), status: decision[2] === 'approve' ? 2 : 3 }));
        return;
    }
    res.writeHead(404); res.end('{}');
}
