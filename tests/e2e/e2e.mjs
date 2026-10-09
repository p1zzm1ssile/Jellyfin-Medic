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

await check('storage: library drives and growing folders are listed', async () => {
    let specs = (await ok('/JellyfinMedic/Report')).Specs || [];
    const drive = specs.find((x) => /^Drive for .*Movies/.test(x.Label));
    expect(drive, 'no library drive line: ' + specs.filter((x) => x.Group === 'Storage').map((x) => x.Label).join(', '));
    expect(/free of/.test(drive.Value), drive.Value);
    await sleep(3000); // folder sizes are measured in the background
    specs = (await ok('/JellyfinMedic/Report')).Specs || [];
    const meta = specs.find((x) => x.Label === 'Metadata folder');
    expect(meta && !/measuring/.test(meta.Value), 'metadata size: ' + JSON.stringify(meta));
    return drive.Value;
});

await check('GPU/CPU advice: a slow software preset and full-frame trickplay are flagged', async () => {
    const enc = await ok('/System/Configuration/encoding');
    const cfg = await ok('/System/Configuration');
    await api('POST', '/System/Configuration/encoding', { ...enc, EncoderPreset: 'slower' });
    await api('POST', '/System/Configuration', { ...cfg, TrickplayOptions: { ...cfg.TrickplayOptions, EnableKeyFrameOnlyExtraction: false } });
    const titles = ((await ok('/JellyfinMedic/Report')).Findings || []).map((f) => f.Title);
    await api('POST', '/System/Configuration/encoding', enc);
    await api('POST', '/System/Configuration', cfg);
    expect(titles.includes('Software transcodes use a slow preset'), 'no preset finding: ' + titles.join(' | '));
    expect(titles.includes('Trickplay reads every frame'), 'no trickplay finding');
});

await check('theme checks: late @import, raw GitHub address and unbalanced braces', async () => {
    const branding = await ok('/System/Configuration/branding');
    const css = '.tweak { color: red; }\n@import url("https://raw.githubusercontent.com/example/theme/main/theme.css");\n.broken { color: blue;';
    const saved = await api('POST', '/System/Configuration/branding', { ...branding, CustomCss: css });
    expect(saved.status < 300, 'saving branding ' + saved.status);
    const titles = ((await ok('/JellyfinMedic/Report')).Findings || []).filter((f) => f.Area === 'Themes').map((f) => f.Title);
    await api('POST', '/System/Configuration/branding', branding);
    for (const want of ['A theme import in your custom CSS is ignored', 'A theme is loaded from raw.githubusercontent.com', 'Your custom CSS has unbalanced braces']) {
        expect(titles.includes(want), `missing "${want}": ${titles.join(' | ')}`);
    }
    return titles.length + ' theme findings';
});

await check('schedule plan uses quarter-hour slots', async () => {
    const cal = await ok('/JellyfinMedic/Schedule/GetCalendar');
    expect(cal.AvoidedSlots?.length === 96, 'expected 96 avoided slots, got ' + cal.AvoidedSlots?.length);
    expect(cal.Days[0].Busy.length === 96, 'expected 96 busy slots');
});

await check('schedule: unscheduled tasks stay off unless chosen, and per-task choices apply', async () => {
    const tasks = await ok('/ScheduledTasks');
    const restart = tasks.find((t) => t.Name === 'Scheduled restart');
    expect(restart && restart.Triggers.length === 0, 'Scheduled restart should start with no schedule');
    const row = (plan, id) => plan.SchedulePlan.find((p) => p.TaskId.replace(/-/g, '') === id.replace(/-/g, ''));
    let plan = await ok('/JellyfinMedic/Schedule/PreviewScheduleDiff');
    let r = row(plan, restart.Id);
    expect(r.Choice === 'keep' && !r.Changes, 'unscheduled task would be given a schedule: ' + JSON.stringify(r));

    const scan = tasks.find((t) => t.Key === 'RefreshLibrary');
    expect(scan, 'no library scan task');
    expect(row(plan, scan.Id).Choice === 'medic', 'library scan should default to Medic');
    const set = await api('POST', `/JellyfinMedic/Schedule/SetChoice?taskId=${scan.Id}&choice=off`);
    expect(set.status === 200, 'SetChoice ' + set.status);
    plan = await ok('/JellyfinMedic/Schedule/PreviewScheduleDiff');
    r = row(plan, scan.Id);
    expect(r.Choice === 'off' && r.ProposedSchedule === 'Manual only' && r.Changes, 'off not planned: ' + JSON.stringify(r));
    await api('POST', `/JellyfinMedic/Schedule/SetChoice?taskId=${scan.Id}&choice=keep`);
    plan = await ok('/JellyfinMedic/Schedule/PreviewScheduleDiff');
    r = row(plan, scan.Id);
    expect(r.Choice === 'keep' && !r.Changes, 'keep not honoured: ' + JSON.stringify(r));
    await api('POST', `/JellyfinMedic/Schedule/SetChoice?taskId=${scan.Id}&choice=medic`);
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

await check('IPTV lists each duplicate channel and its copies', async () => {
    const added = await api('POST', '/LiveTv/TunerHosts', { Type: 'm3u', Url: '/media/livetv/channels.m3u', FriendlyName: 'e2e', TunerCount: 1 });
    expect(added.status === 200, 'adding the M3U tuner failed ' + added.status);
    let channels = 0;
    for (let i = 0; i < 30 && channels < 9; i++) {
        await sleep(2000);
        channels = (await api('GET', '/LiveTv/Channels')).json?.TotalRecordCount || 0;
    }
    expect(channels >= 9, `only ${channels} channels`);
    const r = await api('POST', '/JellyfinMedic/Iptv');
    expect(r.status === 200, 'IPTV analysis ' + r.status);
    const dups = r.json.Channels.Duplicates || [];
    const bbc = dups.find((d) => /bbc one/i.test(d.Name));
    expect(bbc && bbc.Versions.length === 3, 'BBC One copies: ' + JSON.stringify(dups));
    return `${dups.length} channels with copies`;
});

await check('track cleanup leaves excluded films alone', async () => {
    const s = await ok('/JellyfinMedic/MedicSettings');
    const before = await ok('/JellyfinMedic/Tracks/Scan');
    const paths = (r) => (r.Changing?.length ? r.Changing : r.Sample || []).map((p) => p.Path || '').join('|');
    expect(/Two Tracks/.test(paths(before)) && /Keep Me Too/.test(paths(before)), 'both French-track films should be planned: ' + paths(before));
    await api('POST', '/JellyfinMedic/MedicSettings', { ...s, TracksExclude: 'Keep Me Too\n/media/elsewhere' });
    const after = await ok('/JellyfinMedic/Tracks/Scan');
    expect(/Two Tracks/.test(paths(after)) && !/Keep Me Too/.test(paths(after)), 'exclusion ignored: ' + paths(after));
    expect((await ok('/JellyfinMedic/MedicSettings')).TracksExclude === 'Keep Me Too\n/media/elsewhere', 'exclusions not saved');
    await api('POST', '/JellyfinMedic/MedicSettings', { ...s, TracksExclude: '' });
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

await check('schedule Preview: choosing per task updates the plan', async () => {
    errors.length = 0;
    await page.goto(JF + '/web/index.html#/configurationpage?name=JellyfinMedic');
    await page.waitForSelector('#JellyfinMedicPage .so-tab', { timeout: 30000 });
    const gotIt = page.locator('#md-whatsnew-close');
    if (await gotIt.isVisible().catch(() => false)) await gotIt.click();
    await page.click('#JellyfinMedicPage .so-tab[data-tab="schedule"]');
    await page.click('#md-preview');
    await page.locator('#md-preview-box select').first().waitFor({ timeout: 30000 });
    const name = await page.locator('#md-preview-box tbody tr td').first().textContent();
    const rowSelect = () => page.locator('#md-preview-box tbody tr', { hasText: name }).locator('select');
    const was = await rowSelect().inputValue();
    const to = was === 'keep' ? 'off' : 'keep';
    await rowSelect().selectOption(to);
    await page.waitForFunction(([n, v]) => Array.from(document.querySelectorAll('#md-preview-box tbody tr'))
        .some((tr) => tr.cells[0].textContent === n && tr.querySelector('select').value === v && !tr.querySelector('select').disabled), [name, to], { timeout: 30000 });
    await rowSelect().selectOption(was);
    await sleep(1500);
    const ours = errors.filter((e) => !/ResizeObserver|^CancelledError/i.test(e));
    expect(ours.length === 0, ours.join(' | '));
});

await check('admin banner on the home page for a serious error, and it can be dismissed', async () => {
    // Pretend the database reported damage, by adding the line to the newest log file.
    const fs = await import('node:fs');
    const dir = `${process.env.WORK}/config/log`;
    const newest = fs.readdirSync(dir).filter((f) => f.endsWith('.log')).sort().pop();
    const now = new Date().toISOString().replace('T', ' ').slice(0, 19);
    fs.appendFileSync(`${dir}/${newest}`, `[${now}.000 +00:00] [ERR] [42] Microsoft.EntityFrameworkCore: SQLite Error 11: 'database disk image is malformed'.\n`);
    let alerts = [];
    for (let i = 0; i < 70 && !alerts.some((a) => a.Kind === 'critical'); i++) {
        alerts = (await api('GET', '/JellyfinMedic/Alerts')).json || [];
        if (!alerts.some((a) => a.Kind === 'critical')) await sleep(2000); // cached for 2 minutes
    }
    expect(alerts.some((a) => a.Id.startsWith('critical:database')), 'no database alert: ' + JSON.stringify(alerts));

    errors.length = 0;
    await page.goto(JF + '/web/index.html#/home');
    await page.reload(); // the banner checks at most every 5 minutes within one page load
    const banner = page.locator('#jellyfin-medic-banners');
    await banner.waitFor({ timeout: 30000 });
    expect(/database looks damaged/i.test(await banner.textContent()), 'wrong banner text');
    await banner.locator('button[aria-label="Dismiss"]').first().click();
    await page.reload();
    await sleep(5000);
    expect(!(await banner.isVisible().catch(() => false)), 'banner came back after dismissing');
    const ours = errors.filter((e) => !/ResizeObserver|^CancelledError/i.test(e));
    expect(ours.length === 0, ours.join(' | '));
});

await check('Picks: 30 + a genre explains a short list, and "Show me different ones" moves on', async () => {
    await page.goto(JF + '/MedicPicks/Page');
    await page.waitForSelector('#prefs:not([hidden])', { timeout: 30000 });
    const names = () => page.evaluate(() => Array.from(document.querySelectorAll('#library li')).map((li) => li.querySelector('h3, strong, .title')?.textContent || li.textContent).join('|'));
    await page.selectOption('#count', '30');
    await page.click('#genres-summary');
    await page.locator('#genre-list input').first().check();
    await page.click('#apply');
    await page.waitForFunction(() => /Updated just now/.test(document.getElementById('prefs-note').textContent), null, { timeout: 60000 });
    expect(!(await page.locator('#apply').isDisabled()), 'Update button still disabled');
    expect(await page.locator('#few-note').isVisible(), 'no note explaining the short list');

    // Back to any genre, 5 at a time, then cycle.
    await page.click('#genres-clear');
    await page.selectOption('#count', '5');
    await page.click('#apply');
    await page.waitForFunction(() => /Updated just now/.test(document.getElementById('prefs-note').textContent), null, { timeout: 60000 });
    const first = await names();
    await page.evaluate(() => { document.getElementById('prefs-note').textContent = ''; });
    await page.click('#more');
    await page.waitForFunction(() => /Updated just now/.test(document.getElementById('prefs-note').textContent), null, { timeout: 60000 });
    const second = await names();
    expect(first && second && first !== second, `same picks after "Show me different ones": ${first}`);
    return 'picks changed';
});

await browser.close();
console.log(`${passed}/${passed + failed} passed`);
process.exit(failed ? 1 : 0);
