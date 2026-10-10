// A pretend Sonarr for the Medic Profiles tests: answers the API calls Medic Profiles makes with
// fixed data, and remembers what was sent to it.
import http from 'node:http';

export const received = [];

const indexers = [
    { id: 1, name: 'Good Indexer', protocol: 'torrent', enableRss: true, enableAutomaticSearch: true, enableInteractiveSearch: true },
    { id: 2, name: 'Broken Indexer', protocol: 'usenet', enableRss: true, enableAutomaticSearch: true, enableInteractiveSearch: true },
    { id: 3, name: 'Unused Indexer', protocol: 'torrent', enableRss: false, enableAutomaticSearch: false, enableInteractiveSearch: false }
];

const routes = {
    '/api/v3/system/status': () => ({ appName: 'Sonarr', version: '4.0.9.2244' }),
    '/api/v3/indexer': () => indexers,
    '/api/v3/indexerstatus': () => [{ indexerId: 2, initialFailure: new Date(Date.now() - 3 * 3600e3).toISOString(), mostRecentFailure: new Date().toISOString(), disabledTill: new Date(Date.now() + 3600e3).toISOString() }],
    '/api/v3/health': () => [{ source: 'IndexerStatusCheck', type: 'warning', message: 'Indexers unavailable due to failures: Broken Indexer' }],
    '/api/v3/queue': () => ({ page: 1, pageSize: 100, totalRecords: 0, records: [] }),
    '/api/v3/blocklist': () => ({ page: 1, pageSize: 100, totalRecords: 0, records: [] }),
    '/api/v3/qualityprofile': () => [],
    '/api/v3/customformat': () => [],
    '/api/v3/downloadclient': () => [{ id: 1, name: 'qBittorrent', implementation: 'QBittorrent', protocol: 'torrent', enable: true, fields: [{ name: 'host', value: 'qbit' }, { name: 'port', value: 8080 }] }]
};

export function start(port) {
    const server = http.createServer((req, res) => {
        let body = '';
        req.on('data', (c) => { body += c; });
        req.on('end', () => {
            const path = req.url.split('?')[0];
            received.push({ method: req.method, path, body, key: req.headers['x-api-key'] });
            const route = routes[path];
            if (req.headers['x-api-key'] !== 'test-key') { res.writeHead(401); res.end(); return; }
            res.writeHead(route ? 200 : 404, { 'Content-Type': 'application/json' });
            res.end(route ? JSON.stringify(route()) : '{}');
        });
    });
    return new Promise((resolve) => server.listen(port, '0.0.0.0', () => resolve(server)));
}

// A pretend qBittorrent Web UI: sign-in with admin/secret, and the "Excluded file names" preference.
export const qbit = { prefs: { excluded_file_names_enabled: false, excluded_file_names: '*.nfo' }, sets: [] };

export function startQbit(port) {
    const server = http.createServer((req, res) => {
        let body = '';
        req.on('data', (c) => { body += c; });
        req.on('end', () => {
            const path = req.url.split('?')[0];
            const signedIn = /SID=good/.test(req.headers.cookie || '');
            if (path === '/api/v2/auth/login') {
                const form = new URLSearchParams(body);
                if (form.get('username') === 'admin' && form.get('password') === 'secret') {
                    res.writeHead(200, { 'Set-Cookie': 'SID=good; HttpOnly; path=/' });
                    res.end('Ok.');
                } else {
                    res.writeHead(200); res.end('Fails.');
                }
                return;
            }
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
    return new Promise((resolve) => server.listen(port, '0.0.0.0', () => resolve(server)));
}
