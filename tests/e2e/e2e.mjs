// End-to-end tests against a real Jellyfin started by run.sh.
// Sets the server up through its own API, checks every plugin loads and answers, then opens each
// plugin page in a real browser and fails on script errors.
import { createRequire } from 'node:module';

const require = createRequire(import.meta.url);
const { chromium } = require(process.env.PLAYWRIGHT_MODULE || 'playwright');

const JF = process.env.JF || 'http://127.0.0.1:8096';
const AUTH = 'MediaBrowser Client="e2e", Device="e2e", DeviceId="medic-e2e", Version="1.0"';
const sleep = (ms) => new Promise((r) => setTimeout(r, ms));

let token = '';
let passed = 0;
let failed = 0;
const only = process.argv.slice(2);

async function api(method, path, body, raw) {
    const headers = { Authorization: AUTH + (token ? `, Token="${token}"` : '') };
    if (body !== undefined) headers['Content-Type'] = 'application/json';
    const res = await fetch(JF + path, { method, headers, body: body === undefined ? undefined : JSON.stringify(body) });
    if (raw) return res;
    const text = await res.text();
    let json = null;
    try { json = text ? JSON.parse(text) : null; } catch { /* not JSON */ }
    return { status: res.status, json, text };
}

async function check(name, fn) {
    if (only.length && !only.some((o) => name.toLowerCase().includes(o.toLowerCase()))) return;
    try {
        const detail = await fn();
        passed++;
        console.log(`PASS  ${name}${detail ? '  (' + detail + ')' : ''}`);
    } catch (e) {
        failed++;
        console.log(`FAIL  ${name}  -> ${e.message}`);
    }
}

function expect(cond, message) { if (!cond) throw new Error(message); }

async function ok(path) {
    const r = await api('GET', path);
    expect(r.status === 200, `${path} returned ${r.status} ${r.text.slice(0, 200)}`);
    return r.json;
}

// ---------- Server setup ----------

async function setup() {
    for (let i = 0; i < 60; i++) {
        const r = await api('GET', '/Startup/Configuration').catch(() => null);
        if (r && r.status === 200) break;
        await sleep(1000);
    }
    await api('POST', '/Startup/Configuration', { UICulture: 'en-US', MetadataCountryCode: 'GB', PreferredMetadataLanguage: 'en' });
    await api('GET', '/Startup/User');
    await api('POST', '/Startup/User', { Name: 'admin', Password: 'admin' });
    await api('POST', '/Startup/RemoteAccess', { EnableRemoteAccess: true, EnableAutomaticPortMapping: false });
    await api('POST', '/Startup/Complete');

    const login = await api('POST', '/Users/AuthenticateByName', { Username: 'admin', Pw: 'admin' });
    expect(login.status === 200, 'admin sign-in failed: ' + login.status);
    token = login.json.AccessToken;

    // Films only from the local NFO files, so nothing reaches the internet.
    await api('POST', '/Library/VirtualFolders?name=Movies&collectionType=movies&refreshLibrary=false', {
        LibraryOptions: {
            PathInfos: [{ Path: '/media/movies' }],
            MetadataSavers: [], DisabledLocalMetadataReaders: [], TypeOptions: [{ Type: 'Movie', MetadataFetchers: [], ImageFetchers: [] }],
            EnableRealtimeMonitor: false, EnableChapterImageExtraction: false, EnableTrickplayImageExtraction: false
        }
    });
    await api('POST', '/Library/Refresh');
    let films = 0;
    for (let i = 0; i < 90 && films < 12; i++) {
        await sleep(2000);
        const r = await api('GET', '/Items?Recursive=true&IncludeItemTypes=Movie');
        films = r.json?.TotalRecordCount || 0;
    }
    return { userId: login.json.User.Id, serverId: login.json.ServerId, films };
}

// ---------- Tests ----------

const ctx = await setup();

await check('library scanned', async () => {
    expect(ctx.films >= 12, `found ${ctx.films} films`);
    return `${ctx.films} films`;
});

await check('all three plugins loaded', async () => {
    const plugins = await ok('/Plugins');
    const names = plugins.map((p) => p.Name);
    for (const want of ['Jellyfin Medic', 'Medic Picks', 'Medic Profiles']) {
        const p = plugins.find((x) => x.Name === want);
        expect(p, `${want} missing (have ${names.join(', ')})`);
        expect(p.Status === 'Active', `${want} is ${p.Status}`);
    }
});

const medicGets = [
    '/JellyfinMedic/Report', '/JellyfinMedic/Now', '/JellyfinMedic/LoadGuard', '/JellyfinMedic/MediaReport',
    '/JellyfinMedic/Tracks/Scan', '/JellyfinMedic/Tracks/Progress', '/JellyfinMedic/Version', '/JellyfinMedic/Iptv',
    '/JellyfinMedic/MedicSettings', '/JellyfinMedic/Plugins', '/JellyfinMedic/Usage', '/JellyfinMedic/TranscodeLog',
    '/JellyfinMedic/Weekly', '/JellyfinMedic/Cleanup', '/JellyfinMedic/JellyfinSettings',
    '/JellyfinMedic/Schedule/GetCalendar', '/JellyfinMedic/Schedule/GetRunHistory', '/JellyfinMedic/Schedule/PreviewScheduleDiff',
    '/JellyfinMedic/Schedule/ListBackups', '/MedicPicks/Admin/Status', '/MedicProfiles/Status', '/MedicProfiles/History'
];
for (const path of medicGets) {
    await check(`GET ${path}`, async () => { await ok(path); });
}

await check('schedule plan uses quarter-hour slots', async () => {
    const cal = await ok('/JellyfinMedic/Schedule/GetCalendar');
    expect(cal.AvoidedSlots?.length === 96, 'expected 96 avoided slots, got ' + cal.AvoidedSlots?.length);
    expect(cal.Days[0].Busy.length === 96, 'expected 96 busy slots');
});

await check('avoid window saves in 15-minute steps', async () => {
    const s = await ok('/JellyfinMedic/MedicSettings');
    const saved = await api('POST', '/JellyfinMedic/MedicSettings', { ...s, AvoidEnabled: true, AvoidStartMinute: 18 * 60 + 45, AvoidEndMinute: 22 * 60 + 15 });
    expect(saved.status === 200, 'save failed ' + saved.status);
    const back = await ok('/JellyfinMedic/MedicSettings');
    expect(back.AvoidStartMinute === 1125 && back.AvoidEndMinute === 1335, `got ${back.AvoidStartMinute}-${back.AvoidEndMinute}`);
    const cal = await ok('/JellyfinMedic/Schedule/GetCalendar');
    expect(cal.AvoidedSlots.filter(Boolean).length === 14, 'expected 14 avoided slots');
    await api('POST', '/JellyfinMedic/MedicSettings', { ...s, AvoidEnabled: false });
});

await check('Medic Picks builds picks from watch history', async () => {
    const items = (await ok(`/Items?Recursive=true&IncludeItemTypes=Movie&SortBy=SortName&UserId=${ctx.userId}`)).Items;
    for (const item of items.slice(0, 4)) await api('POST', `/UserPlayedItems/${item.Id}?userId=${ctx.userId}`);
    const tasks = await ok('/ScheduledTasks');
    const build = tasks.find((t) => t.Key === 'MedicPicksBuild' || /personal picks/i.test(t.Name));
    expect(build, 'Build personal picks task missing');
    await api('POST', `/ScheduledTasks/Running/${build.Id}`);
    let me = null;
    for (let i = 0; i < 60; i++) {
        await sleep(1000);
        me = (await api('GET', '/MedicPicks/Me')).json;
        const n = me?.inLibrary?.length ?? 0;
        if (n > 0) return `${n} picks`;
    }
    throw new Error('no picks after a minute: ' + JSON.stringify(me).slice(0, 300));
});

await check('My picks page is served with no-cache', async () => {
    const res = await api('GET', '/MedicPicks/Page', undefined, true);
    expect(res.status === 200, 'status ' + res.status);
    expect((res.headers.get('cache-control') || '').includes('no-cache'), 'cache-control ' + res.headers.get('cache-control'));
});

// ---------- Pages in a real browser ----------

const browser = await chromium.launch({ executablePath: process.env.CHROMIUM || undefined });
const page = await browser.newPage();
const errors = [];
page.on('pageerror', (e) => errors.push(e.message + ' @ ' + String(e.stack || '').split('\n').slice(1, 3).join(' ').trim()));
// Sign in through the real login form; Jellyfin ties tokens to the browser's device ID.
await page.goto(JF + '/web/index.html#/login');
await page.waitForSelector('#txtManualName, input[type="text"]', { timeout: 60000 });
await page.fill('#txtManualName', 'admin');
await page.fill('#txtManualPassword', 'admin');
await page.click('.manualLoginForm button[type="submit"]');
await page.waitForURL(/home/, { timeout: 60000 });

// Opens a plugin page, then clicks through each of its tabs, failing on any script error.
async function openPage(name, hash, selector, tabs) {
    await check(`${name} page and its tabs open without script errors`, async () => {
        errors.length = 0;
        await page.goto(JF + '/web/index.html#' + hash);
        await page.waitForSelector(selector, { timeout: 30000 });
        await sleep(2000);
        // Close Medic's "What's new" box if it's showing.
        const gotIt = page.locator('#md-whatsnew-close');
        if (await gotIt.isVisible().catch(() => false)) await gotIt.click();
        let count = 0;
        if (tabs) {
            const buttons = await page.locator(selector.split(' ')[0] + ' ' + tabs).all();
            for (const b of buttons) {
                if (!(await b.isVisible())) continue;
                await b.click();
                await sleep(1500);
                count++;
            }
        }
        // Jellyfin's own web app reports CancelledError when navigating away cancels its requests.
        const ours = errors.filter((e) => !/ResizeObserver|^CancelledError/i.test(e));
        expect(ours.length === 0, ours.join(' | '));
        return count ? `${count} tabs` : '';
    });
}

await openPage('Jellyfin Medic', '/configurationpage?name=JellyfinMedic', '#JellyfinMedicPage .so-tab', '.so-tab');
await openPage('Medic Profiles', '/configurationpage?name=MedicProfiles', '#MedicProfilesPage .mp-tab', '.mp-tab');
await openPage('Medic Picks settings', '/configurationpage?name=Medic%20Picks', 'form');

await check('My picks page opens signed in', async () => {
    errors.length = 0;
    await page.goto(JF + '/MedicPicks/Page');
    await page.waitForLoadState('networkidle');
    await sleep(2000);
    expect(errors.length === 0, errors.join(' | '));
});

await browser.close();
console.log(`${passed}/${passed + failed} passed`);
process.exit(failed ? 1 : 0);
