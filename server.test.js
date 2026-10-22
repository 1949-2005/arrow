import { test, before, after } from 'node:test';
import assert from 'node:assert/strict';
import http from 'node:http';
import net from 'node:net';
import fs from 'node:fs/promises';
import { existsSync, readFileSync } from 'node:fs';
import os from 'node:os';
import path from 'node:path';
import { spawn, execFileSync } from 'node:child_process';
import { fileURLToPath } from 'node:url';

const sleep = ms => new Promise(r => setTimeout(r, ms));
const serverJs = fileURLToPath(new URL('./server.js', import.meta.url));
const app = path.dirname(serverJs);
const brand = JSON.parse(readFileSync(path.join(app, 'brand.json'), 'utf8'));
let child, dir, base, port;

// server.js on a free port, with its state in the given temp folder
async function startServer(dir) {
  const probe = net.createServer();
  await new Promise(r => probe.listen(0, '127.0.0.1', r));
  const port = probe.address().port;
  await new Promise(r => probe.close(r));
  const base = `http://127.0.0.1:${port}`;
  const child = spawn(process.execPath, [serverJs], {
    // FASTDL_STARTUP: the Start with Windows shortcut goes to the temp folder, never the real Startup folder
    env: { ...process.env, FASTDL_PORT: String(port), FASTDL_STATE: path.join(dir, 'state.json'), FASTDL_STARTUP: dir },
    stdio: 'ignore',
  });
  for (const t = Date.now(); ; await sleep(50)) {
    try { if ((await fetch(base + '/api/ping')).ok) break; } catch {}
    if (Date.now() - t > 10000) throw new Error('server did not start');
  }
  return { child, base, port };
}

before(async () => {
  dir = await fs.mkdtemp(path.join(os.tmpdir(), 'fastdl-server-'));
  ({ child, base, port } = await startServer(dir));
});
after(async () => {
  child?.kill();
  await fs.rm(dir, { recursive: true, force: true, maxRetries: 3 });
});

const call = (url, { origin, body } = {}) => fetch(base + url, {
  method: body ? 'POST' : 'GET',
  headers: { ...(origin && { origin }), ...(body && { 'content-type': 'application/json' }) },
  body: body && JSON.stringify(body),
});
const scheduled = { url: 'http://127.0.0.1:1/x', scheduled: true }; // never starts: no network, no files

test('extensions may only ping, add, list YouTube formats and show the window', async () => {
  const origin = 'chrome-extension://abc';
  assert.equal((await call('/api/ping', { origin })).status, 200);
  assert.equal((await call('/api/add', { origin, body: scheduled })).status, 200);
  assert.equal((await call('/api/open', { origin, body: {} })).status, 200);
  // /api/formats is open to them, for YouTube single-video links only (a real one would run yt-dlp: not here)
  for (const [url, error] of [['https://example.com/v.mp4', /Only YouTube video links/], ['https://www.youtube.com/@channel', /not a channel or playlist/], ['nope', /Invalid URL/]]) {
    const res = await call('/api/formats', { origin, body: { url } });
    assert.equal(res.status, 400, url);
    assert.match((await res.json()).error, error);
  }
  const site = await call('/api/formats', { origin: 'https://evil.example', body: { url: 'https://youtu.be/jNQXAC9IVRw' } });
  assert.equal(site.status, 403, 'other websites never reach it');
  const forbidden = [['/api/settings', { dir, startup: true, whenDone: 'shutdown' }], ['/api/action', { id: 'x', op: 'open' }], ['/api/state'],
    ['/api/pause-all', {}], ['/api/quit', {}], ['/api/queues', { op: 'add', name: 'From a page' }], ['/api/confirm', { id: 'x', action: 'start', path: 'C:\\Users\\Public\\a.exe' }],
    ['/api/state?x=1']];
  for (const [url, body] of forbidden) {
    const res = await call(url, { origin, body });
    assert.equal(res.status, 403, url);
    assert.deepEqual(await res.json(), { error: 'not allowed from extensions' });
  }
  const st = await (await call('/api/state')).json();
  assert.equal(st.settings.startup, false);
  assert.equal(st.whenDone, '');
  assert.ok(!existsSync(path.join(dir, brand.name + '.lnk')));
});

test('Start with Windows: the app creates and removes a shortcut that starts the native exe in the tray', { skip: process.platform !== 'win32' }, async () => {
  const link = path.join(dir, brand.name + '.lnk');
  const startup = async () => (await (await call('/api/state')).json()).settings.startup;
  assert.equal(await startup(), false);
  assert.equal((await call('/api/settings', { origin: base, body: { startup: true } })).status, 200);
  assert.equal(await startup(), true);
  const read = '[Console]::OutputEncoding = [Text.Encoding]::UTF8; $s = (New-Object -ComObject WScript.Shell).CreateShortcut($env:L); ($s.TargetPath, $s.Arguments, $s.WorkingDirectory) -join "|"';
  const props = execFileSync('powershell.exe', ['-NoProfile', '-NonInteractive', '-Command', read], { env: { ...process.env, L: link } }).toString().trim();
  assert.equal(props.toLowerCase(), [path.join(app, brand.name + '.exe'), '--tray', app].join('|').toLowerCase());
  assert.equal((await call('/api/settings', { origin: base, body: { startup: false } })).status, 200);
  assert.equal(await startup(), false);
  assert.ok(!existsSync(link));
});

test('Open (tray, extension popup, a second start) raises `open` in /api/state, which the native app watches', async () => {
  const open = async () => (await (await call('/api/state')).json()).open;
  const before = await open();
  assert.equal(typeof before, 'number');
  assert.deepEqual(await (await call('/api/open', { body: {} })).json(), { ok: true });
  await call('/api/open', { origin: 'chrome-extension://abc', body: {} });
  assert.equal(await open(), before + 2);
});

test('routes with a query string work', async () => {
  assert.equal((await call('/api/state?t=1')).status, 200);
  assert.deepEqual(await (await call('/api/ping?x', { origin: 'chrome-extension://abc' })).json(), { app: 'fastdl' });
  assert.equal((await call('/api/add?from=ext', { origin: 'chrome-extension://abc', body: scheduled })).status, 200);
});

test('extension downloads wait as pending only while the native app is open; it confirms or cancels them', async () => {
  // a fresh engine: nothing has polled /api/state yet, so no window would show the pending download
  const pdir = await fs.mkdtemp(path.join(os.tmpdir(), 'fastdl-pending-'));
  try {
    const s = await startServer(pdir);
    const add = () => fetch(s.base + '/api/add', { method: 'POST', headers: { origin: 'chrome-extension://abc' }, body: JSON.stringify({ url: 'http://127.0.0.1:1/z/f.zip', confirm: true }) });
    const refused = await add();
    assert.equal(refused.status, 503);
    assert.deepEqual(await refused.json(), { error: `${brand.name} is not open. Start it from the Start menu.` });
    const fromExt = await fetch(s.base + '/api/state', { headers: { origin: 'chrome-extension://abc' } }); // extensions don't count
    assert.equal(fromExt.status, 403);
    assert.equal((await add()).status, 503);
    assert.equal((await fetch(s.base + '/api/add', { method: 'POST', body: JSON.stringify(scheduled) })).status, 200, 'other downloads need no window');
    await fetch(s.base + '/api/state'); // the native app polls, without an Origin
    const ok = await add();
    assert.equal(ok.status, 200);
    const { id } = await ok.json();
    assert.equal((await (await fetch(s.base + '/api/state')).json()).downloads.find(d => d.id === id).status, 'pending');
    s.child.kill();
  } finally {
    await fs.rm(pdir, { recursive: true, force: true, maxRetries: 3 });
  }

  await call('/api/state');
  const { id } = await (await call('/api/add', { origin: 'chrome-extension://abc', body: { url: 'http://127.0.0.1:1/z/f.zip', confirm: true } })).json();
  const find = async () => (await (await call('/api/state')).json()).downloads.find(d => d.id === id);
  assert.equal((await find()).status, 'pending');
  for (let t = Date.now(); !(await find()).probe && Date.now() - t < 5000;) await sleep(50);
  assert.equal((await find()).probe.name, 'f.zip');
  const bad = await call('/api/confirm', { origin: base, body: { id, action: 'start', path: 'f.zip' } });
  assert.equal(bad.status, 400);
  assert.match((await bad.json()).error, /full path/);
  assert.equal((await call('/api/confirm', { origin: base, body: { id, action: 'later', path: path.join(dir, 'Later', 'f.zip') } })).status, 200);
  assert.deepEqual([(await find()).status, (await find()).queue], ['scheduled', 'main'], 'Later waits in the main queue');
  assert.equal((await find()).file, path.join(dir, 'Later', 'f.zip'));
  assert.ok(existsSync(path.join(dir, 'Later')), 'folder created');
  const { id: id2 } = await (await call('/api/add', { body: { url: 'http://127.0.0.1:1/z/g.zip', confirm: true } })).json();
  assert.deepEqual(await (await call('/api/confirm', { body: { id: id2, action: 'cancel' } })).json(), { ok: true });
  assert.ok(!(await (await call('/api/state')).json()).downloads.some(d => d.id === id2));
});

test('tray: Pause all pauses running downloads; scheduled ones keep their time', async () => {
  await call('/api/settings', { body: { dir } });
  const { id: later } = await (await call('/api/add', { body: scheduled })).json();
  // port 1 refuses at once and the probe retries for seconds: it stays "downloading", and no file is made
  const { id: now } = await (await call('/api/add', { body: { url: 'http://127.0.0.1:1/y' } })).json();
  const status = async id => (await (await call('/api/state')).json()).downloads.find(d => d.id === id).status;
  assert.equal(await status(now), 'downloading');
  assert.equal((await call('/api/pause-all', { body: {} })).status, 200);
  assert.equal(await status(now), 'paused');
  assert.equal(await status(later), 'scheduled');
});

test('tray: Quit saves the download list and ends the app', async () => {
  const qdir = await fs.mkdtemp(path.join(os.tmpdir(), 'fastdl-quit-'));
  try {
    const s = await startServer(qdir);
    const exited = new Promise(r => s.child.on('exit', r));
    await fetch(s.base + '/api/add', { method: 'POST', body: JSON.stringify(scheduled) });
    assert.deepEqual(await (await fetch(s.base + '/api/quit', { method: 'POST' })).json(), { ok: true });
    assert.equal(await exited, 0);
    assert.equal(JSON.parse(readFileSync(path.join(qdir, 'state.json'), 'utf8')).list.length, 1);
  } finally {
    await fs.rm(qdir, { recursive: true, force: true, maxRetries: 3 });
  }
});

test('other websites get nothing', async () => {
  const origin = 'https://evil.example';
  assert.equal((await call('/api/ping', { origin })).status, 403);
  assert.equal((await call('/api/add', { origin, body: scheduled })).status, 403);
  assert.equal((await call('/api/state', { origin })).status, 403);
});

test('wrong Host header is refused (DNS rebinding)', async () => {
  const status = await new Promise((resolve, reject) => {
    http.request({ host: '127.0.0.1', port, path: '/api/ping', headers: { host: `evil.example:${port}` } }, res => {
      res.resume();
      resolve(res.statusCode);
    }).on('error', reject).end();
  });
  assert.equal(status, 403);
});

test('the app itself (no Origin) has full access', async () => {
  assert.equal((await call('/api/settings', { body: { dir } })).status, 200);
  assert.equal((await call('/api/state')).status, 200);
  const { id } = await (await call('/api/add', { body: scheduled })).json();
  const res = await call('/api/action', { body: { id, op: 'open' } });
  assert.equal(res.status, 400); // only finished downloads can be opened
});

test('actions describe, limit and whenDone, and the new settings, through the API', async () => {
  const { id } = await (await call('/api/add', { body: { ...scheduled, description: ' From the API ' } })).json();
  const find = async () => (await (await call('/api/state')).json()).downloads.find(d => d.id === id);
  assert.equal((await find()).description, 'From the API');
  for (const body of [{ op: 'describe', description: 'Notes\n' }, { op: 'limit', kb: 5e7 }, { op: 'whenDone', value: 'open' }]) {
    assert.deepEqual(await (await call('/api/action', { body: { id, ...body } })).json(), { ok: true }, body.op);
  }
  const d = await find();
  assert.deepEqual([d.description, d.limitKB, d.whenDone, d.lastTry, d.notify], ['Notes', 1e7, 'open', null, false]);
  const ext = await call('/api/action', { origin: 'chrome-extension://abc', body: { id, op: 'limit', kb: 1 } });
  assert.equal(ext.status, 403, 'extensions still cannot act on downloads');

  const set = async body => (await call('/api/settings', { body })).json();
  assert.deepEqual(await set({ speedLimitKB: 100, speedLimitOn: true, duplicates: 'number', showComplete: false }),
    { ...(await set({})), speedLimitKB: 100, speedLimitOn: true, duplicates: 'number', showComplete: false });
  const off = await set({ speedLimitOn: false, duplicates: 'later' });
  assert.deepEqual([off.speedLimitKB, off.speedLimitOn, off.duplicates, off.showComplete], [100, false, 'ask', false]);
  await set({ speedLimitKB: 0, showComplete: true });
});

test('request bodies keep UTF-8 intact', async () => {
  const title = 'Vidéo – 日本';
  assert.equal((await call('/api/add', { body: { ...scheduled, title } })).status, 200);
  const { downloads } = await (await call('/api/state')).json();
  assert.ok(downloads.some(d => d.name === title), JSON.stringify(downloads.map(d => d.name)));
});

test('queues through the API: add, update, remove, start, stop; actions queue, up and down; view() counts', async () => {
  const queues = async body => {
    const res = await call('/api/queues', { body });
    return [res.status, await res.json()];
  };
  const state = async () => (await call('/api/state')).json();
  const [, { id }] = await queues({ op: 'add', name: 'API queue' });
  const [dupStatus, dup] = await queues({ op: 'add', name: 'api QUEUE' });
  assert.deepEqual([dupStatus, dup], [400, { error: 'There is already a queue named "api QUEUE"' }]);
  assert.deepEqual(await queues({ op: 'update', id, maxActive: 2, startAt: '03:00', days: [5] }), [200, { ok: true }]);
  assert.deepEqual((await state()).queues.find(q => q.id === id),
    { id, name: 'API queue', maxActive: 2, startAt: '03:00', days: [5], stopAt: '', retries: 0, whenDone: '', running: false, waiting: 0, active: 0, total: 0 });
  assert.equal((await queues({ op: 'remove', id: 'main' }))[0], 400);

  const add = async () => (await (await call('/api/add', { body: { ...scheduled, queue: id } })).json()).id;
  const a = await add(), b = await add();
  assert.deepEqual(await (await call('/api/action', { body: { id: b, op: 'up' } })).json(), { ok: true });
  const mine = async () => (await state()).downloads.filter(d => d.queue === id);
  assert.deepEqual((await mine()).map(d => d.id), [b, a]);
  assert.deepEqual(await queues({ op: 'start', id }), [200, { ok: true }]);
  let q = (await state()).queues.find(x => x.id === id);
  assert.deepEqual([q.running, q.active, q.waiting, q.total], [true, 2, 0, 2]);
  assert.deepEqual(await queues({ op: 'stop', id }), [200, { ok: true }]);
  assert.deepEqual((await mine()).map(d => d.status), ['scheduled', 'scheduled']);
  await call('/api/action', { body: { id: a, op: 'queue', queue: null } });
  assert.deepEqual((await mine()).map(d => d.id), [b]);
  assert.equal((await state()).downloads.find(d => d.id === a).status, 'paused');
  assert.deepEqual(await queues({ op: 'remove', id }), [200, { ok: true }]);
  assert.ok(!(await state()).queues.some(x => x.id === id));
  assert.equal((await state()).downloads.find(d => d.id === b).queue, 'main', 'its downloads go to the main queue');
  for (const x of [a, b]) await call('/api/action', { body: { id: x, op: 'remove' } });
});
