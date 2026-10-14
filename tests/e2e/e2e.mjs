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

await check('Medic Picks keeps both keys when they are saved at the same time', async () => {
    // The settings page saves the TMDb and Seerr keys together; neither may be lost.
    try {
        for (let i = 0; i < 20; i++) {
            await api('POST', '/MedicPicks/Admin/TmdbKey', { Key: '' });
            await api('POST', '/MedicPicks/Admin/SeerrKey', { Key: '' });
            const saved = await Promise.all([
                api('POST', '/MedicPicks/Admin/TmdbKey', { Key: 'e2e-tmdb-key' }),
                api('POST', '/MedicPicks/Admin/SeerrKey', { Key: 'e2e-seerr-key' })
            ]);
            expect(saved.every((r) => r.status === 204), 'save: ' + saved.map((r) => r.status).join(', '));
            const status = await ok('/MedicPicks/Admin/Status');
            expect(status.tmdbKeySet && status.seerrKeySet, `round ${i + 1}: ` + JSON.stringify(status));
        }
        return '20 rounds, both keys kept';
    } finally {
        await api('POST', '/MedicPicks/Admin/TmdbKey', { Key: '' });
        await api('POST', '/MedicPicks/Admin/SeerrKey', { Key: '' });
    }
});

await check('support export hides secrets in name/value pairs, tokens and dashless keys', async () => {
    // A plugin's settings with fake secrets in shapes that used to get through. Written from inside the
    // container: the folder belongs to Jellyfin's user, not the one running the tests.
    const { execFileSync } = await import('node:child_process');
    const file = '/config/plugins/configurations/E2E.SecretShapes.xml';
    const secrets = ['FAKEeyJhbGciOiJIUzI1NiJ9', '0123456789abcdef0123456789abcdef', 'fedcba9876543210fedcba9876543210', 'FAKE-COOKIE-VALUE', 'FAKEbasicDXNlcjpwYXNz', 'fakeattrcookie'];
    const xml = `<?xml version="1.0" encoding="utf-8"?>
<PluginConfiguration>
  <Headers>
    <HeaderItem><Key>Authorization</Key><Value>Bearer FAKEeyJhbGciOiJIUzI1NiJ9.FAKEpayload.FAKEsig</Value></HeaderItem>
    <HeaderItem><Key>X-Api-Key</Key><Value>0123456789abcdef0123456789abcdef</Value></HeaderItem>
    <HeaderItem><Key>Accept</Key><Value>application/json</Value></HeaderItem>
    <Header name="Cookie" value="sid=fakeattrcookie" />
  </Headers>
  <ServerKey>fedcba9876543210fedcba9876543210</ServerKey>
  <AuthCookie>session=FAKE-COOKIE-VALUE; Path=/</AuthCookie>
  <ProxyAuth>Basic FAKEbasicDXNlcjpwYXNz</ProxyAuth>
  <LibraryId>3f2504e0-4f89-11d3-9a0c-0305e82c3301</LibraryId>
</PluginConfiguration>`;
    execFileSync('docker', ['exec', '-i', 'medic-e2e', 'sh', '-c', 'cat > "$1"', 'sh', file], { input: xml });
    try {
        const r = await api('GET', '/JellyfinMedic/Export');
        expect(r.status === 200, 'export ' + r.status);
        const part = r.text.slice(r.text.indexOf('E2E.SecretShapes.xml'));
        const leaked = secrets.filter((s) => part.includes(s));
        expect(leaked.length === 0, 'leaked: ' + leaked.join(', ') + '\n' + part.slice(0, 1200));
        expect(part.includes('<Key>Authorization</Key>') && part.includes('<Value>application/json</Value>') && part.includes('name="Cookie"'), 'header names or harmless values were hidden:\n' + part.slice(0, 1200));
        expect(part.includes('3f2504e0-4f89-11d3-9a0c-0305e82c3301'), 'a dashed id was hidden');
        return 'no secret left, labels and ids kept';
    } finally {
        execFileSync('docker', ['exec', 'medic-e2e', 'rm', '-f', file]);
    }
});

await check('Dashboards polling at the same moment don\'t queue behind each other', async () => {
    // Several Dashboards (or tabs) ask for "now" within the same second; none should wait for another's CPU reading.
    await ok('/JellyfinMedic/Now');
    const started = Date.now();
    await Promise.all([1, 2, 3, 4].map(() => ok('/JellyfinMedic/Now')));
    const seconds = (Date.now() - started) / 1000;
    expect(seconds < 1.5, `4 readings at once took ${seconds.toFixed(1)} s`);
    return `4 at once in ${seconds.toFixed(2)} s`;
});

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

await check('security: sign-ins from several places are counted per account', async () => {
    // Two people with long names each sign in from two addresses. Counted per account that's two each,
    // which isn't "several places"; lumping long entries together made it one "user" with four.
    // A third person signs in from three addresses, which is several places: the check must still see them.
    // run.sh trusts Docker's gateway as a proxy, so X-Forwarded-For sets each sign-in's address.
    const people = [
        ['a-very-long-account-name-for-testing-one', [10, 11]],
        ['a-very-long-account-name-for-testing-two', [12, 13]],
        ['roamer', [14, 15, 16]]
    ];
    const ids = [];
    try {
        for (const [name, addresses] of people) {
            const made = await api('POST', '/Users/New', { Name: name, Password: 'e2e-pass' });
            expect(made.status === 200, 'user not created: ' + made.text);
            ids.push(made.json.Id);
            for (const last of addresses) {
                const res = await fetch(JF + '/Users/AuthenticateByName', {
                    method: 'POST',
                    headers: { Authorization: AUTH.replace('medic-e2e', `e2e-${name}-${last}`), 'Content-Type': 'application/json', 'X-Forwarded-For': `203.0.113.${last}` },
                    body: JSON.stringify({ Username: name, Pw: 'e2e-pass' })
                });
                expect(res.status === 200, `sign-in ${name} from 203.0.113.${last}: ${res.status}`);
            }
        }
        await sleep(2000);
        // Without this the test would pass for the wrong reason: all sign-ins from one address.
        const log = await ok('/System/ActivityLog/Entries?limit=100');
        const seen = (log.Items || []).filter((e) => /203\.0\.113\.1[0-6]/.test((e.ShortOverview || '') + (e.Overview || ''))).length;
        expect(seen >= 7, `the forwarded addresses weren't used (${seen} entries); run.sh sets Docker's gateway as a known proxy before Jellyfin starts`);
        const findings = ((await ok('/JellyfinMedic/Report')).Findings || []).filter((f) => f.Area === 'Security');
        // "An account signs in from several places", or "N accounts sign in from several places".
        const roaming = findings.find((f) => /signs? in from several places/.test(f.Title));
        expect(/roamer: 3 addresses/.test(roaming?.Current || ''), 'the account with three addresses wasn\'t reported: ' + JSON.stringify(roaming));
        expect(!/a-very-long|a user/.test(roaming.Current), 'the accounts with two addresses were counted: ' + roaming.Current);
        return 'roamer reported with 3 addresses; the two accounts with 2 each not reported';
    } finally {
        for (const id of ids) await api('DELETE', `/Users/${id}`);
    }
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

await check('resource monitor measures Jellyfin and its FFmpeg processes', async () => {
    let res = null;
    for (let i = 0; i < 25 && !res; i++) {
        res = (await ok('/JellyfinMedic/Now')).Resources;
        if (!res) await sleep(2000);
    }
    expect(res, 'no resource reading after 50 seconds');
    expect(res.MemoryGb > 0.05 && res.MemoryGb < 64, 'odd memory ' + res.MemoryGb);
    expect(res.CpuPercent >= 0 && res.CpuPercent <= 100, 'odd CPU ' + res.CpuPercent);
    expect(Array.isArray(await ok('/JellyfinMedic/ResourceEvents')), 'events list');

    // Start a transcode, and the FFmpeg process Jellyfin starts for it should be counted.
    let film = null;
    for (let i = 0; i < 30 && !film; i++) {
        film = (await ok('/Items?Recursive=true&IncludeItemTypes=Movie&searchTerm=Long')).Items[0];
        if (!film) await sleep(2000);
    }
    expect(film, 'Long Film not scanned');
    const url = `/Videos/${film.Id}/main.m3u8?MediaSourceId=${film.Id}&VideoCodec=h264&AudioCodec=aac&VideoBitrate=4000000&MaxWidth=1280&PlaySessionId=e2e-tx`;
    const playlist = await api('GET', url);
    expect(playlist.status === 200, 'transcode playlist ' + playlist.status);
    const segment = playlist.text.split('\n').find((l) => l && !l.startsWith('#'));
    api('GET', `/Videos/${film.Id}/${segment}`).catch(() => {});
    let seen = 0;
    for (let i = 0; i < 20 && !seen; i++) {
        await sleep(3000);
        seen = (await ok('/JellyfinMedic/Now')).Resources?.FfmpegProcesses || 0;
    }
    await api('DELETE', '/Videos/ActiveEncodings?deviceId=medic-e2e&playSessionId=e2e-tx');
    expect(seen > 0, 'the transcode\'s FFmpeg process was not counted');
    return `CPU ${res.CpuPercent}%, ${res.MemoryGb} GB, disk ${res.DiskMBps} MB/s, GPU ${res.GpuPercent ?? 'n/a'}`;
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
    const forUpdates = tasks.find((t) => t.Name === 'Restart Jellyfin for waiting updates');
    expect(forUpdates && forUpdates.Triggers.length === 0, 'Restart Jellyfin for waiting updates should start with no schedule: ' + JSON.stringify(forUpdates?.Triggers));
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

await check('"Suggest" mode flags a better schedule in Checks', async () => {
    const titles = ((await ok('/JellyfinMedic/Report')).Findings || []).map((f) => f.Title);
    expect(titles.includes('A better schedule is available'), 'no suggestion: ' + titles.filter((t) => /schedul/i.test(t)).join(' | '));
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

const MEDIC_DATA = '/config/plugins/configurations/JellyfinMedic';

await check('schedule apply changes nothing when Medic can\'t save its schedule, and a restore puts it back', async () => {
    const { execFileSync } = await import('node:child_process');
    const triggers = async () => JSON.stringify((await ok('/ScheduledTasks'))
        .map((t) => [t.Id, t.Triggers])
        .sort((a, b) => a[0].localeCompare(b[0])));
    const before = await triggers();
    // A folder where Medic first writes its schedule file makes that write fail, as a full disk would.
    execFileSync('docker', ['exec', 'medic-e2e', 'mkdir', '-p', `${MEDIC_DATA}/managed_schedule.json.tmp`]);
    try {
        const blocked = await api('POST', '/JellyfinMedic/Schedule/ApplyRecommendedSchedule');
        expect(blocked.json?.Success === false && /nothing was changed/.test(blocked.json.Message), `blocked apply: ${blocked.status} ${blocked.text.slice(0, 200)}`);
        expect(await triggers() === before, 'triggers changed although the apply failed');
    } finally {
        execFileSync('docker', ['exec', 'medic-e2e', 'rmdir', `${MEDIC_DATA}/managed_schedule.json.tmp`]);
    }

    const applied = (await api('POST', '/JellyfinMedic/Schedule/ApplyRecommendedSchedule')).json;
    expect(applied?.Success && applied.AppliedTasks > 0 && await triggers() !== before, 'apply: ' + JSON.stringify(applied));
    const restored = (await api('POST', `/JellyfinMedic/Schedule/RestorePreviousSchedule?backupFileName=${encodeURIComponent(applied.BackupFile)}`)).json;
    expect(restored?.Success && await triggers() === before, 'restore: ' + JSON.stringify(restored));
    return `${applied.AppliedTasks} tasks applied, then put back`;
});

await check('schedule backups each get their own name, and the newest 50 are kept', async () => {
    for (let i = 0; i < 55; i++) {
        const r = await api('POST', '/JellyfinMedic/Schedule/BackupCurrentSchedule');
        expect(r.json?.Success, `backup ${i}: ${r.status} ${r.text.slice(0, 200)}`);
    }
    const list = await ok('/JellyfinMedic/Schedule/ListBackups');
    expect(list.length === 50, `${list.length} backups kept`);
    return '55 made in a row, 50 kept';
});

await check('a damaged Medic file is set aside instead of written over', async () => {
    const { execFileSync } = await import('node:child_process');
    const file = `${MEDIC_DATA}/task_choices.json`;
    execFileSync('docker', ['exec', 'medic-e2e', 'sh', '-c', `echo '{"damaged": true' > ${file}`]);
    const scan = (await ok('/ScheduledTasks')).find((t) => t.Key === 'RefreshLibrary');
    const set = await api('POST', `/JellyfinMedic/Schedule/SetChoice?taskId=${scan.Id}&choice=medic`);
    expect(set.status === 200, 'SetChoice ' + set.status);
    const kept = execFileSync('docker', ['exec', 'medic-e2e', 'sh', '-c', `cat ${file}.bad 2>/dev/null || echo missing`]).toString();
    expect(kept.includes('"damaged"'), 'the damaged file was not kept: ' + kept.trim());
    return 'kept as task_choices.json.bad';
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

// ---------- Track cleanup on real files ----------

const { execFileSync } = await import('node:child_process');
const fs = await import('node:fs');
const movies = `${process.env.WORK}/media/movies`;

// A short film with an audio track per language, in that order.
function makeFilm(file, languages, seconds) {
    const inputs = languages.flatMap((_, i) => ['-f', 'lavfi', '-i', `sine=frequency=${300 + i * 200}:duration=${seconds}`]);
    const maps = languages.flatMap((_, i) => ['-map', String(i + 1)]);
    const tags = languages.flatMap((l, i) => [`-metadata:s:a:${i}`, `language=${l}`]);
    execFileSync('ffmpeg', ['-loglevel', 'error', '-y', '-f', 'lavfi', '-i', `testsrc=size=320x180:rate=10:duration=${seconds}`,
        ...inputs, '-map', '0', ...maps, '-c:v', 'libx264', '-preset', 'ultrafast', '-c:a', 'aac', ...tags, file]);
}

const audioLanguages = (file) => JSON.parse(execFileSync('ffprobe', ['-v', 'error', '-select_streams', 'a', '-show_entries', 'stream_tags=language', '-of', 'json', file]).toString())
    .streams.map((x) => x.tags?.language).join(',');

async function runTracks() {
    const started = await api('POST', '/JellyfinMedic/Tracks/Run?dryRun=false');
    expect(started.status === 200, 'track run ' + started.status + ' ' + started.text);
    let p = null;
    for (let i = 0; i < 60 && (!p || p.Running); i++) {
        await sleep(1000);
        p = await ok('/JellyfinMedic/Tracks/Progress');
    }
    return p;
}

// Adds a film to the library and waits until Jellyfin has read its tracks. Returns its file.
async function addFilm(name, languages, seconds) {
    const dir = `${movies}/${name}`;
    const file = `${dir}/${name}.mkv`;
    fs.mkdirSync(dir, { recursive: true });
    makeFilm(file, languages, seconds);
    await api('POST', '/Library/Refresh');
    const title = encodeURIComponent(name.replace(/ \(\d{4}\)$/, ''));
    let audio = 0;
    for (let i = 0; i < 45 && audio < languages.length; i++) {
        await sleep(2000);
        const item = (await ok(`/Items?Recursive=true&IncludeItemTypes=Movie&SearchTerm=${title}&Fields=MediaStreams`)).Items[0];
        audio = (item?.MediaStreams || []).filter((m) => m.Type === 'Audio').length;
    }
    expect(audio === languages.length, `Jellyfin did not pick up ${name}`);
    return file;
}

// Runs fn with track cleanup settings changed, and puts them back after.
async function withTrackSettings(changes, fn) {
    const s = await ok('/JellyfinMedic/MedicSettings');
    await api('POST', '/JellyfinMedic/MedicSettings', { ...s, ...changes });
    try {
        return await fn();
    } finally {
        await api('POST', '/JellyfinMedic/MedicSettings', s);
    }
}

await check('track cleanup checks the file itself before changing it', async () => {
    // Replace a film the way Sonarr would, with the French track first. Jellyfin (real time monitor off)
    // still lists English first, so a plan from its list alone would keep French and drop English.
    const file = `${movies}/Two Tracks (2015)/Two Tracks (2015).mkv`;
    const replacement = `${process.env.WORK}/reordered.mkv`;
    makeFilm(replacement, ['fre', 'eng'], 4);
    fs.renameSync(replacement, file);

    // In place, so the test leaves no folder owned by the container's user behind.
    return withTrackSettings({ TracksExclude: 'Keep Me Too', TracksReplaceInPlace: true }, async () => {
        const first = await runTracks();
        expect(first.Changed === 0 && first.Failed === 1, 'stale plan was not refused: ' + JSON.stringify(first.Recent));
        expect(audioLanguages(file) === 'fre,eng', 'the file was changed anyway: ' + audioLanguages(file));

        // Medic asked Jellyfin to read the file again; once it has, the run strips the French track.
        let firstLanguage = '';
        for (let i = 0; i < 30 && !firstLanguage.startsWith('fr'); i++) {
            await sleep(2000);
            const item = (await ok('/Items?Recursive=true&IncludeItemTypes=Movie&SearchTerm=Two%20Tracks&Fields=MediaStreams')).Items[0];
            firstLanguage = item?.MediaStreams?.find((m) => m.Type === 'Audio')?.Language || '';
        }
        expect(firstLanguage.startsWith('fr'), 'Jellyfin did not read the file again');
        const second = await runTracks();
        expect(second.Changed === 1 && second.Failed === 0, 'second run: ' + JSON.stringify(second.Recent));
        expect(audioLanguages(file) === 'eng', 'English should be the only audio left: ' + audioLanguages(file));
        return 'stale plan refused, then English kept';
    });
});

await check('track cleanup puts back an original left out by an interrupted swap', async () => {
    // What Jellyfin stopping between the two moves leaves behind: the original kept aside, the
    // cleaned copy still in its temp file, and nothing at the film's own path.
    const dir = `${movies}/Keep Me Too (2016)`;
    const file = `${dir}/Keep Me Too (2016).mkv`;
    const kept = `${dir}/.medic-originals/Keep Me Too (2016).mkv`;
    const temp = `${file}.medic-tmp.mkv`;
    fs.mkdirSync(`${dir}/.medic-originals`, { recursive: true });
    fs.renameSync(file, kept);
    fs.copyFileSync(kept, temp);

    return withTrackSettings({ TracksExclude: 'Two Tracks' }, async () => {
        const p = await runTracks();
        const notes = JSON.stringify(p.Recent);
        expect((p.Recent || []).some((r) => /Put back the original of Keep Me Too/.test(r)), 'original not put back: ' + notes);
        expect(!fs.existsSync(temp), 'the temp file was left behind');
        expect(p.Changed === 1 && p.Failed === 0, 'the film was not cleaned after being put back: ' + notes);
        expect(audioLanguages(file) === 'eng', 'cleaned film: ' + audioLanguages(file));
        expect(audioLanguages(kept) === 'eng,fre', 'kept original: ' + audioLanguages(kept));
        return 'original put back, then cleaned';
    });
});

await check('track cleanup refuses a new copy that comes out shorter than the original', async () => {
    // A file whose end can't be read (cut short here) still says it is 60 s long. FFmpeg copies what it can
    // and exits 0, even with -xerror; the copy must not replace the original.
    const file = await addFilm('Cut Short (2018)', ['eng', 'fre'], 60);
    fs.truncateSync(file, Math.floor(fs.statSync(file).size / 2));
    const size = fs.statSync(file).size;

    return withTrackSettings({ TracksExclude: 'Two Tracks\nKeep Me Too', TracksReplaceInPlace: true }, async () => {
        const p = await runTracks();
        const notes = JSON.stringify(p.Recent);
        expect(p.Changed === 0 && p.Failed === 1, 'a short copy was accepted: ' + notes);
        expect((p.Recent || []).some((r) => /Cut Short.*didn't come out right \(it lasts/.test(r)), 'not refused for its length: ' + notes);
        expect(fs.statSync(file).size === size && audioLanguages(file) === 'eng,fre', 'the original was changed');
        expect(!fs.existsSync(`${file}.medic-tmp.mkv`), 'the temp file was left behind');
        return 'short copy refused, original untouched';
    });
});

await check('track cleanup leaves hardlinked files alone', async () => {
    // A film a torrent client seeds through a hard link: a cleaned copy would be a second file, not a smaller one.
    const file = await addFilm('Seeded (2019)', ['eng', 'fre'], 4);
    const seed = `${process.env.WORK}/seeding-copy.mkv`;
    fs.linkSync(file, seed);
    try {
        return await withTrackSettings({ TracksExclude: 'Cut Short' }, async () => {
            const p = await runTracks();
            const notes = JSON.stringify(p.Recent);
            expect(p.Changed === 0 && p.Failed === 1 && /Seeded.*2 hard links/.test(notes), 'the hardlinked film was cleaned: ' + notes);
            expect(audioLanguages(file) === 'eng,fre' && fs.statSync(file).nlink === 2, 'the film was changed');
            return 'skipped, still shared with the seeding copy';
        });
    } finally {
        fs.unlinkSync(seed);
    }
});

await check('track cleanup keeps the file\'s owner and permissions', async () => {
    // Jellyfin runs as root here and writes the new copy; Sonarr and Radarr, as another user, must still be able to manage it.
    const file = await addFilm('Owned (2020)', ['eng', 'fre'], 4);
    fs.chmodSync(file, 0o660);
    const before = fs.statSync(file);
    return withTrackSettings({ TracksExclude: 'Cut Short\nSeeded', TracksReplaceInPlace: true }, async () => {
        const p = await runTracks();
        expect(p.Changed === 1 && p.Failed === 0, 'not cleaned: ' + JSON.stringify(p.Recent));
        const after = fs.statSync(file);
        expect(audioLanguages(file) === 'eng', 'cleaned film: ' + audioLanguages(file));
        expect((after.mode & 0o777) === 0o660 && after.uid === before.uid && after.gid === before.gid,
            `now ${(after.mode & 0o777).toString(8)} ${after.uid}:${after.gid}, was 660 ${before.uid}:${before.gid}`);
        return 'mode 660 and owner kept';
    });
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

await check('Medic Picks "My requests" says Seerr is out of reach instead of failing', async () => {
    // A Seerr that refuses connections, then one that never answers: both must come back as
    // reachable:false, which the page shows, and the second within the time limit per call.
    const http = await import('node:http');
    const silent = http.createServer(() => { /* never answers */ });
    await new Promise((resolve) => silent.listen(18991, '0.0.0.0', resolve));
    const plugin = (await ok('/Plugins')).find((p) => p.Name === 'Medic Picks');
    const cfg = await ok(`/Plugins/${plugin.Id}/Configuration`);
    await api('POST', '/MedicPicks/Admin/SeerrKey', { Key: 'e2e-seerr-key' });
    const myRequests = async (url) => {
        await api('POST', `/Plugins/${plugin.Id}/Configuration`, { ...cfg, JellyseerrUrl: url, ShowRequests: true });
        const started = Date.now();
        const r = await api('GET', '/MedicPicks/Me/Requests');
        return { ...r, seconds: (Date.now() - started) / 1000 };
    };
    try {
        const refused = await myRequests('http://host.docker.internal:9');
        expect(refused.status === 200 && refused.json?.reachable === false, `refused: ${refused.status} ${refused.text.slice(0, 200)}`);
        const silentSeerr = await myRequests('http://host.docker.internal:18991');
        expect(silentSeerr.status === 200 && silentSeerr.json?.reachable === false, `no answer: ${silentSeerr.status} ${silentSeerr.text.slice(0, 200)}`);
        expect(silentSeerr.seconds < 30, `took ${silentSeerr.seconds} s`);
        return `both reported as out of reach (the silent one after ${silentSeerr.seconds.toFixed(0)} s)`;
    } finally {
        await api('POST', `/Plugins/${plugin.Id}/Configuration`, cfg);
        await api('POST', '/MedicPicks/Admin/SeerrKey', { Key: '' });
        silent.closeAllConnections();
        silent.close();
    }
});

await check('My picks page names itself with the plugin\'s version', async () => {
    const plugin = (await ok('/Plugins')).find((p) => p.Name === 'Medic Picks');
    expect(plugin, 'Medic Picks not installed');
    const version = plugin.Version.split('.').slice(0, 3).join('.');
    const page = (await api('GET', '/MedicPicks/Page')).text;
    const sent = (page.match(/var VERSION = '([^']*)'/) || [])[1];
    expect(sent === version, `the page says ${sent}, the plugin is ${version}`);
    return version;
});

await check('Medic Picks gives people only a Seerr address they can open', async () => {
    const id = 'b34ec5d1-b70f-435f-b77d-9b76979b03c7';
    const cfg = await ok(`/Plugins/${id}/Configuration`);
    const save = async (changes) => {
        const r = await api('POST', `/Plugins/${id}/Configuration`, { ...cfg, ...changes });
        expect(r.status === 204 || r.status === 200, 'saving settings ' + r.status);
    };
    try {
        // The server reaches Seerr inside Docker; people open it at its public address.
        await save({ JellyseerrUrl: 'http://seerr:5055', SeerrLinkUrl: 'https://requests.example.com/', SeerrDirectRequests: false });
        let me = await ok('/MedicPicks/Me');
        expect(me.seerrUrl === 'https://requests.example.com' && me.requestMode === 'link', `link: ${me.seerrUrl} (${me.requestMode})`);
        // Never an address that isn't http or https in a link.
        await save({ JellyseerrUrl: 'javascript:alert(1)//', SeerrLinkUrl: '', SeerrDirectRequests: false });
        me = await ok('/MedicPicks/Me');
        // No address at all: the field is left out of the answer.
        expect(me.seerrUrl == null && me.requestMode === 'none', `javascript: address: ${me.seerrUrl} (${me.requestMode})`);
        return 'public address in links; javascript: never';
    } finally {
        await api('POST', `/Plugins/${id}/Configuration`, cfg);
    }
});

// ---------- Medic Profiles, against a pretend Sonarr ----------

const fakeArr = await import('./fake-arr.mjs');
const arrServer = await fakeArr.start(18989);
const ARR = process.env.ARR_URL || 'http://host.docker.internal:18989';

await check('Medic Picks approves and declines requests as the admin\'s own Seerr account', async () => {
    // Without X-API-User, Seerr acts as its owner account for any call made with the API key.
    const seerrServer = await fakeArr.startSeerr(18992);
    const plugin = (await ok('/Plugins')).find((p) => p.Name === 'Medic Picks');
    const cfg = await ok(`/Plugins/${plugin.Id}/Configuration`);
    await api('POST', `/Plugins/${plugin.Id}/Configuration`, { ...cfg, JellyseerrUrl: 'http://host.docker.internal:18992', ShowRequests: true });
    await api('POST', '/MedicPicks/Admin/SeerrKey', { Key: 'e2e-seerr-key' });
    try {
        const approved = await api('POST', '/MedicPicks/Admin/Requests/7/Approve');
        const declined = await api('POST', '/MedicPicks/Admin/Requests/8/Decline');
        expect(approved.json?.ok && declined.json?.ok, `answers: ${approved.text} ${declined.text}`);
        const made = fakeArr.seerr.decisions.map((d) => `${d.action} ${d.id} as ${d.asUser}`).join(', ');
        expect(made === 'approve 7 as 42, decline 8 as 42', 'Seerr saw: ' + made);
        return made;
    } finally {
        await api('POST', `/Plugins/${plugin.Id}/Configuration`, cfg);
        await api('POST', '/MedicPicks/Admin/SeerrKey', { Key: '' });
        seerrServer.close();
    }
});

await check('Medic Picks shows only your own requests and keeps Discover-off accounts from requesting', async () => {
    // This Seerr ignores ?requestedBy= and lists someone else's request too; Medic Picks must drop it.
    const seerrServer = await fakeArr.startSeerr(18993);
    const plugin = (await ok('/Plugins')).find((p) => p.Name === 'Medic Picks');
    const cfg = await ok(`/Plugins/${plugin.Id}/Configuration`);
    const setCfg = (changes) => api('POST', `/Plugins/${plugin.Id}/Configuration`, { ...cfg, JellyseerrUrl: 'http://host.docker.internal:18993', ShowRequests: true, SeerrDirectRequests: true, ...changes });
    await setCfg({});
    await api('POST', '/MedicPicks/Admin/SeerrKey', { Key: 'e2e-seerr-key' });
    try {
        const mine = await ok('/MedicPicks/Me/Requests');
        const ids = (mine.requests || []).map((r) => r.id).join(',');
        expect(ids === '11', 'requests shown: ' + ids);

        await setCfg({ DiscoverDisabledUserIds: [ctx.userId] });
        const refused = await api('POST', '/MedicPicks/Request', { TmdbId: 552, MediaType: 'movie' });
        expect(refused.status === 403, `a Discover-off account could request: ${refused.status} ${refused.text.slice(0, 200)}`);
        return 'only request 11 shown; request refused with 403';
    } finally {
        await api('POST', `/Plugins/${plugin.Id}/Configuration`, cfg);
        await api('POST', '/MedicPicks/Admin/SeerrKey', { Key: '' });
        seerrServer.close();
    }
});

await check('Medic Profiles connects to Sonarr', async () => {
    const saved = await api('POST', '/MedicProfiles/Settings', { SonarrUrl: ARR, SonarrKey: 'test-key', RadarrUrl: '' });
    expect(saved.status === 200 || saved.status === 204, 'saving settings ' + saved.status);
    const test = await api('POST', '/MedicProfiles/Test/sonarr');
    expect(test.json?.ok, 'test failed: ' + test.text);
});

await check('Medic Profiles never sends a saved key to a new server, and keeps a damaged keys file', async () => {
    const keySet = async () => (await ok('/MedicProfiles/Status')).sonarr.keySet;
    try {
        // Same server, key box left blank: the saved key stays.
        await api('POST', '/MedicProfiles/Settings', { SonarrUrl: ARR + '/', RadarrUrl: '' });
        expect(await keySet(), 'the key was dropped although the server is the same');
        // Another server, key box left blank: the key must be typed again.
        await api('POST', '/MedicProfiles/Settings', { SonarrUrl: 'http://host.docker.internal:18994', RadarrUrl: '' });
        expect(!(await keySet()), 'the saved key was kept for a different server');
    } finally {
        // Back to the pretend Sonarr whatever happened, so a failure here doesn't also fail the checks after it.
        await api('POST', '/MedicProfiles/Settings', { SonarrUrl: ARR, SonarrKey: 'test-key', RadarrUrl: '' });
    }

    // A keys file that can't be read is set aside, not replaced by one holding only the new key.
    const { execFileSync } = await import('node:child_process');
    const dir = '/config/data/medic-profiles';
    execFileSync('docker', ['exec', 'medic-e2e', 'sh', '-c', `echo '{not json' > ${dir}/secrets.json`]);
    await api('POST', '/MedicProfiles/Settings', { SonarrUrl: ARR, SonarrKey: 'test-key', RadarrUrl: '' });
    const kept = execFileSync('docker', ['exec', 'medic-e2e', 'cat', `${dir}/secrets.json.bad`]).toString();
    expect(kept.includes('{not json'), 'the damaged file was not kept: ' + kept);
    expect(await keySet(), 'the new key was not saved');
    return 'key dropped for a new server; damaged file kept as secrets.json.bad';
});

await check('Medic Profiles shows indexer status', async () => {
    const r = await ok('/MedicProfiles/Indexers');
    const byName = Object.fromEntries((r.rows || []).map((x) => [x.name, x]));
    expect(byName['Broken Indexer']?.state === 'failing', 'broken: ' + JSON.stringify(byName['Broken Indexer']));
    expect(byName['Good Indexer']?.state === 'ok', 'good: ' + JSON.stringify(byName['Good Indexer']));
    expect(byName['Unused Indexer']?.state === 'off', 'unused: ' + JSON.stringify(byName['Unused Indexer']));
    expect((r.warnings || []).some((w) => /Broken Indexer/.test(w)), 'no health warning');
});

await check('Medic Profiles says when the queue is cut short, and can remove a download but leave it in the client', async () => {
    fakeArr.queue.total = 1234;
    try {
        const r = await ok('/MedicProfiles/Queue');
        expect(/first 1,000 of Sonarr's 1,234 queue entries/.test(r.notes?.sonarr || ''), 'note: ' + JSON.stringify(r.notes));
        const row = r.rows[0];
        const removed = await api('POST', '/MedicProfiles/Queue/Remove', { app: 'sonarr', queueIds: row.queueIds, block: false, keepInClient: true, title: row.mediaTitle });
        expect(removed.json?.ok, 'remove: ' + removed.text);
        const sent = fakeArr.received.filter((x) => x.method === 'DELETE' && x.path === '/api/v3/queue/bulk').at(-1);
        expect(/removeFromClient=false/.test(sent?.query || ''), 'sent: ' + JSON.stringify(sent));
        return `${r.rows.length} rows; removeFromClient=false sent`;
    } finally {
        fakeArr.queue.total = 0;
    }
});

await check('Medic Profiles blocks program files in qBittorrent, keeping the owner\'s own entries', async () => {
    const qbitServer = await fakeArr.startQbit(18080);
    // The owner's own list, switched off on purpose, holding *.zip, which is also one of Medic Profiles' patterns.
    Object.assign(fakeArr.qbit.prefs, { excluded_file_names_enabled: false, excluded_file_names: '*.nfo\n*.zip' });
    try {
        const QB = ARR.replace('18989', '18080');
        await api('POST', '/MedicProfiles/Settings', { SonarrUrl: ARR, RadarrUrl: '', QbitUrl: QB, QbitUser: 'admin', QbitPassword: 'secret' });
        let state = await ok('/MedicProfiles/FileFilter');
        expect(state.reachable && !state.blocksPrograms, 'before: ' + JSON.stringify(state));
        const set = await api('POST', '/MedicProfiles/FileFilter', { block: true, archives: false });
        expect(set.json?.ok, 'block failed: ' + set.text);
        const sent = fakeArr.qbit.sets.at(-1);
        expect(sent.excluded_file_names_enabled === true, 'list not switched on');
        const list = sent.excluded_file_names.split('\n');
        expect(list.includes('*.exe') && list.includes('*.nfo') && list.includes('*.zip') && !list.includes('*.rar'), 'list: ' + sent.excluded_file_names);
        state = await ok('/MedicProfiles/FileFilter');
        expect(state.blocksPrograms && !state.blocksArchives, 'after: ' + JSON.stringify(state));
        await api('POST', '/MedicProfiles/FileFilter', { block: false, archives: false });
        const after = fakeArr.qbit.prefs;
        expect(after.excluded_file_names === '*.nfo\n*.zip' && after.excluded_file_names_enabled === false,
            `unblock left: ${JSON.stringify(after.excluded_file_names)}, switched ${after.excluded_file_names_enabled ? 'on' : 'off'}`);
        // One session for all of the above: opening the tab again doesn't sign in again.
        await ok('/MedicProfiles/FileFilter');
        expect(fakeArr.qbit.logins === 1, `signed in ${fakeArr.qbit.logins} times`);

        // A wrong password is tried once, not each time the tab opens: each failure counts towards qBittorrent's ban.
        await api('POST', '/MedicProfiles/Settings', { SonarrUrl: ARR, RadarrUrl: '', QbitUrl: QB, QbitUser: 'admin', QbitPassword: 'wrong' });
        state = await ok('/MedicProfiles/FileFilter');
        expect(!state.reachable && /user name and password/.test(state.message), 'wrong password: ' + JSON.stringify(state));
        await ok('/MedicProfiles/FileFilter');
        expect(fakeArr.qbit.failedLogins === 1, `the wrong password was tried ${fakeArr.qbit.failedLogins} times`);
        return 'one sign-in for the session, one try for a wrong password';
    } finally {
        qbitServer.close();
    }
});

// ---------- Exposure check, without asking any outside service ----------

await check('exposure check says it tested from this server, and a failed TLS handshake counts as an answer', async () => {
    const run = async (host, port, protocol) => {
        const r = await api('POST', `/JellyfinMedic/ExposureCheck?host=${encodeURIComponent(host)}&port=${port}&protocol=${protocol}`);
        expect(r.status === 200, `${host}:${port} returned ${r.status} ${r.text.slice(0, 200)}`);
        return r.json;
    };
    // The pretend Sonarr only speaks plain http, so an https request to it fails the TLS handshake,
    // the same way a certificate this server doesn't trust does.
    const arr = new URL(ARR);
    const tls = await run(arr.hostname, Number(arr.port), 'https');
    expect(tls.Reachable && tls.FromThisServer, 'TLS failure: ' + JSON.stringify(tls));
    const unknown = await run('medic-e2e.invalid', 443, 'https');
    expect(unknown.Checked && !unknown.Reachable && unknown.FromThisServer, 'unknown domain: ' + JSON.stringify(unknown));
    const withPath = await run('medic-e2e.invalid/System', 443, 'https');
    expect(withPath.Error && !withPath.Checked, 'host with a path: ' + JSON.stringify(withPath));
    const defaults = await ok('/JellyfinMedic/ExposureDefaults');
    expect(!defaults.PublicIp, 'public IP looked up without being asked: ' + defaults.PublicIp);
});

// ---------- Pages in a real browser ----------

const browser = await chromium.launch({ executablePath: process.env.CHROMIUM || undefined });
// A fixed language: GitHub's runner otherwise hands the browser "en-us@posix", which Jellyfin's web app can't format numbers with.
const page = await browser.newPage({ locale: 'en-GB' });
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
await openPage('Medic Picks settings', '/configurationpage?name=Medic%20Picks', '#MedicPicksConfigForm');

await check('Medic Picks settings: the user boxes show, and a key that isn\'t saved says so', async () => {
    await page.goto(JF + '/web/index.html#/configurationpage?name=Medic%20Picks');
    await page.waitForSelector('#DiscoverUsers .discoverUser', { timeout: 30000 });
    const upgraded = await page.evaluate(() => Array.from(document.querySelectorAll('#DiscoverUsers .discoverUser')).every((i) => i.classList.contains('emby-checkbox')));
    expect(upgraded, 'the user boxes are plain inputs, not emby-checkbox');
    const discoverWas = await page.isChecked('#EnableDiscover');
    const tmdbKey = /MedicPicks\/Admin\/TmdbKey/;
    await page.route(tmdbKey, (route) => route.fulfill({ status: 500, body: '' }));
    try {
        await page.fill('#TmdbKey', 'e2e-not-a-real-key');
        await page.click('#SaveTmdbKey');
        await page.getByText('The TMDb key wasn\'t saved').waitFor({ timeout: 10000 });
        expect(await page.isChecked('#EnableDiscover') === discoverWas, 'Discover picks stayed switched on after the key failed to save');
    } finally {
        await page.unroute(tmdbKey);
        await page.keyboard.press('Escape');
    }
});

await check('Medic Profiles says so when one of its lists can\'t be loaded', async () => {
    const blocked = /MedicProfiles\/Blocked/;
    await page.route(blocked, (route) => route.abort());
    try {
        await page.goto(JF + '/web/index.html#/configurationpage?name=MedicProfiles');
        await page.waitForSelector('#MedicProfilesPage .mp-tab', { timeout: 30000 });
        await page.click('#MedicProfilesPage .mp-tab[data-tab="blocked"]');
        await page.getByText('Medic Profiles couldn’t load the blocked list').waitFor({ timeout: 10000 });
    } finally {
        await page.unroute(blocked);
    }
});

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

await check('exposure check result says when it only tested from this server', async () => {
    errors.length = 0;
    await page.goto(JF + '/web/index.html#/configurationpage?name=JellyfinMedic');
    await page.waitForSelector('#JellyfinMedicPage .so-tab', { timeout: 30000 });
    const gotIt = page.locator('#md-whatsnew-close');
    if (await gotIt.isVisible().catch(() => false)) await gotIt.click();
    await page.click('#JellyfinMedicPage .so-tab[data-tab="dashboard"]');
    await sleep(2000); // the boxes fill in from Medic's suggestions first
    await page.fill('#md-expose-host', 'medic-e2e.invalid');
    await page.fill('#md-expose-port', '443');
    await page.selectOption('#md-expose-protocol', 'https');
    await page.click('#md-expose');
    await page.waitForFunction(() => !/^Checking/.test(document.getElementById('md-expose-result').textContent), null, { timeout: 30000 });
    const result = page.locator('#md-expose-result');
    const text = await result.textContent();
    expect(/when this server tried it/.test(text) && !/from the internet/.test(text), 'said: ' + text);
    expect(!/md-ok-text/.test(await result.getAttribute('class')), 'shown as all clear: ' + text);
    const ours = errors.filter((e) => !/ResizeObserver|^CancelledError/i.test(e));
    expect(ours.length === 0, ours.join(' | '));
});

await check('Medic stops polling while its browser tab is hidden and after you leave the page', async () => {
    await page.goto(JF + '/web/index.html#/configurationpage?name=JellyfinMedic');
    await page.waitForSelector('#JellyfinMedicPage .so-tab', { timeout: 30000 });
    const gotIt = page.locator('#md-whatsnew-close');
    if (await gotIt.isVisible().catch(() => false)) await gotIt.click();
    await page.click('#JellyfinMedicPage .so-tab[data-tab="dashboard"]');
    // The dashboard's live tiles ask for /JellyfinMedic/Now every 15 seconds.
    let polls = 0;
    const count = (req) => { if (/\/JellyfinMedic\/Now(\?|$)/.test(req.url())) polls++; };
    page.on('request', count);
    try {
        await page.evaluate(() => {
            Object.defineProperty(document, 'hidden', { configurable: true, get: () => true });
            document.dispatchEvent(new Event('visibilitychange'));
        });
        polls = 0;
        await sleep(17000);
        const whileHidden = polls;
        await page.evaluate(() => {
            delete document.hidden;
            document.dispatchEvent(new Event('visibilitychange'));
        });
        await sleep(2000);
        expect(whileHidden === 0, `${whileHidden} polls while the tab was hidden`);
        expect(polls > 0, 'no poll after the tab was shown again');

        await page.goto(JF + '/web/index.html#/home');
        await page.waitForURL(/#\/home/, { timeout: 30000 });
        await sleep(1000);
        polls = 0;
        await sleep(17000);
        expect(polls === 0, `${polls} polls after leaving the page`);
        return 'none while hidden or away';
    } finally {
        page.off('request', count);
    }
});

// Opens Medic's page on a tab, closing "What's new" if it's showing. Fresh reloads it, so nothing the
// page kept from an earlier visit (the films it found, say) is reused.
async function medicTab(name, fresh) {
    await page.goto(JF + '/web/index.html#/configurationpage?name=JellyfinMedic');
    if (fresh) await page.reload();
    await page.waitForSelector('#JellyfinMedicPage .so-tab', { timeout: 30000 });
    const gotIt = page.locator('#md-whatsnew-close');
    if (await gotIt.isVisible().catch(() => false)) await gotIt.click();
    await page.click(`#JellyfinMedicPage .so-tab[data-tab="${name}"]`);
}

await check('Medic page: Off/Suggest and the time range are separate buttons, and the arrow keys reach every tab', async () => {
    await medicTab('settings');
    const pressed = () => page.evaluate(() => Array.from(document.querySelectorAll('#JellyfinMedicPage .md-seg button[aria-pressed="true"]'))
        .map((b) => b.dataset.range || b.dataset.mode).sort().join(','));
    await page.click('#JellyfinMedicPage .md-seg button[data-mode="off"]');
    expect(await pressed() === 'full,off', 'after Off: ' + await pressed());
    await page.click('#JellyfinMedicPage .so-tab[data-tab="schedule"]');
    await page.click('#JellyfinMedicPage .md-seg button[data-range="night"]');
    expect(await pressed() === 'night,off', 'after Night: ' + await pressed());
    await page.click('#JellyfinMedicPage .md-seg button[data-range="full"]');

    await page.click('#JellyfinMedicPage .so-tab[data-tab="tracks"]');
    await page.keyboard.press('ArrowRight');
    const now = await page.evaluate(() => document.querySelector('#JellyfinMedicPage .so-tab[aria-selected="true"]').dataset.tab);
    expect(now === 'media', 'ArrowRight from Tracks went to ' + now);
    const linked = await page.evaluate(() => {
        const tab = document.querySelector('#JellyfinMedicPage .so-tab[data-tab="media"]');
        const panel = document.getElementById(tab.getAttribute('aria-controls') || '-');
        return !!panel && panel.dataset.panel === 'media' && panel.getAttribute('aria-labelledby') === tab.id;
    });
    expect(linked, 'the Media report tab and its panel don\'t point at each other');
    return 'Off kept the range; Night kept Off; Tracks then Media report';
});

await check('"What\'s new" takes the focus, closes with Escape and gives the focus back', async () => {
    await medicTab('settings');
    await page.click('#md-changelog-all');
    await page.waitForSelector('#md-whatsnew:not([hidden])', { timeout: 15000 });
    const inside = await page.evaluate(() => document.activeElement?.id);
    expect(inside === 'md-whatsnew-close', 'focus is on ' + inside);
    await page.keyboard.press('Escape');
    expect(await page.locator('#md-whatsnew').isHidden(), 'Escape left it open');
    const back = await page.evaluate(() => document.activeElement?.id);
    expect(back === 'md-changelog-all', 'focus went to ' + back);
});

await check('playback test sends its token in a header, not in the address', async () => {
    // The samples have no 1080p film, so the page is shown the 720p one when it asks for 1080p.
    const longFilm = (await ok(`/Items?Recursive=true&IncludeItemTypes=Movie&searchTerm=Long&Fields=MediaSources,Path&UserId=${ctx.userId}`)).Items[0];
    expect(longFilm, 'Long Film not scanned');
    await page.route(/\/Items\?.*MinWidth=1800/, (route) => route.fulfill({ json: { Items: [longFilm], TotalRecordCount: 1 } }));
    const videoUrls = [];
    const watch = (req) => { if (/\/Videos\//.test(req.url())) videoUrls.push(req.url()); };
    const accept = (dialog) => dialog.accept();
    page.on('request', watch);
    page.on('dialog', accept);
    try {
        await medicTab('tests', true);
        await page.click('#so-run-playback');
        await page.waitForSelector('#so-playback-results .so-test-result', { timeout: 180000 });
        const card = await page.locator('#so-playback-results .so-test-result').first().textContent();
        expect(!/Transcoding failed/.test(card), 'the test failed: ' + card.slice(0, 300));
        expect(videoUrls.length >= 3, `${videoUrls.length} video requests`);
        const withKey = videoUrls.filter((u) => /[?&](api_key|ApiKey)=/i.test(u));
        expect(withKey.length === 0, 'token in the address: ' + withKey[0]);
        return `${videoUrls.length} requests, none with the token in the address`;
    } finally {
        page.off('request', watch);
        page.off('dialog', accept);
        await page.unroute(/\/Items\?.*MinWidth=1800/);
    }
});

await check('admin banner on the home page for a serious error, and it can be dismissed', async () => {
    // Pretend the database reported damage, by adding the line to the newest log file.
    // Written from inside the container: the log belongs to Jellyfin's user, not the one running the tests.
    const { execFileSync } = await import('node:child_process');
    const newest = execFileSync('docker', ['exec', 'medic-e2e', 'sh', '-c', 'ls /config/log/*.log | sort | tail -1']).toString().trim();
    const now = new Date().toISOString().replace('T', ' ').slice(0, 19);
    // A warning that only mentions the words must not count (Jellyfin logs these routinely).
    const warning = `[${now}.000 +00:00] [WRN] [28] Jellyfin.Server.Implementations.Item.BaseItemRepository: Skipping item with unknown type. This may indicate a removed plugin or database corruption.`;
    execFileSync('docker', ['exec', 'medic-e2e', 'sh', '-c', `echo "$1" >> "$2"`, 'sh', warning, newest]);
    await sleep(1000);
    expect(!((await api('GET', '/JellyfinMedic/Alerts')).json || []).some((a) => a.Kind === 'critical'), 'a warning raised a banner');
    // "What's new" was seen on Medic's page above, so its home banner is gone too.
    expect(!((await api('GET', '/JellyfinMedic/Alerts')).json || []).some((a) => a.Kind === 'whatsnew'), '"What\'s new" banner still showing after seeing it in Medic');
    const line = `[${now}.000 +00:00] [ERR] [42] Microsoft.EntityFrameworkCore: SQLite Error 11: 'database disk image is malformed'.`;
    execFileSync('docker', ['exec', 'medic-e2e', 'sh', '-c', `echo "$1" >> "$2"`, 'sh', line, newest]);
    let alerts = [];
    for (let i = 0; i < 70 && !alerts.some((a) => a.Kind === 'critical'); i++) {
        alerts = (await api('GET', '/JellyfinMedic/Alerts')).json || [];
        if (!alerts.some((a) => a.Kind === 'critical')) await sleep(2000); // cached for 2 minutes
    }
    expect(alerts.some((a) => a.Id.startsWith('critical:database')), 'no database alert: ' + JSON.stringify(alerts));
    const inChecks = ((await ok('/JellyfinMedic/Report')).Findings || []).some((f) => f.Area === 'Logs' && /database looks damaged/.test(f.Title));
    expect(inChecks, 'the banner\'s problem is missing from Checks');

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

await check('one-time on every device: a dismissed banner and a seen "What\'s new" stay gone on another device', async () => {
    // A second, fresh browser is a new device: nothing remembered locally.
    const other = await browser.newContext({ locale: 'en-GB' });
    const p2 = await other.newPage();
    const errs = [];
    p2.on('pageerror', (e) => errs.push(e.message));
    await p2.goto(JF + '/web/index.html#/login');
    await p2.waitForSelector('#txtManualName', { timeout: 60000 });
    await p2.fill('#txtManualName', 'admin');
    await p2.fill('#txtManualPassword', 'admin');
    await p2.click('.manualLoginForm button[type="submit"]');
    await p2.waitForURL(/home/, { timeout: 60000 });
    await sleep(6000);
    expect(!(await p2.locator('#jellyfin-medic-banners').isVisible().catch(() => false)), 'dismissed banner came back on another device');
    await p2.goto(JF + '/web/index.html#/configurationpage?name=JellyfinMedic');
    await p2.waitForSelector('#JellyfinMedicPage .so-tab', { timeout: 30000 });
    await sleep(3000);
    expect(!(await p2.locator('#md-whatsnew').isVisible()), '"What\'s new" showed again on another device');
    await p2.click('#JellyfinMedicPage .so-tab[data-tab="settings"]');
    await p2.click('#md-changelog-all');
    await p2.waitForSelector('#md-whatsnew:not([hidden])', { timeout: 15000 });
    const versions = await p2.locator('#md-whatsnew-body h3').count();
    expect(versions >= 5, 'full changelog shows only ' + versions + ' versions');
    await other.close();
    const ours = errs.filter((e) => !/ResizeObserver|^CancelledError/i.test(e));
    expect(ours.length === 0, ours.join(' | '));
    return versions + ' versions in the full changelog';
});

await browser.close();
arrServer.close();
console.log(`${passed}/${passed + failed} passed`);
process.exit(failed ? 1 : 0);
