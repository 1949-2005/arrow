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

test('extensions may only ping and add', async () => {
  const origin = 'chrome-extension://abc';
  assert.equal((await call('/api/ping', { origin })).status, 200);
  assert.equal((await call('/api/add', { origin, body: scheduled })).status, 200);
  const forbidden = [['/api/settings', { dir, startup: true, whenDone: 'shutdown' }], ['/api/action', { id: 'x', op: 'open' }], ['/api/state'],
    ['/api/open', {}], ['/api/pause-all', {}], ['/api/quit', {}]];
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

test('Start with Windows: the app page creates and removes a shortcut that starts the exe in the tray', { skip: process.platform !== 'win32' }, async () => {
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

test('the page and its icon carry the name and icon from brand.json', async () => {
  assert.ok((await (await call('/')).text()).includes(`<title>${brand.name}</title>`));
  const icon = await call('/favicon.ico');
  assert.equal(icon.headers.get('content-type'), 'image/x-icon');
  assert.deepEqual(Buffer.from(await icon.arrayBuffer()), readFileSync(path.join(app, brand.icon)));
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

test('request bodies keep UTF-8 intact', async () => {
  const title = 'Vidéo – 日本';
  assert.equal((await call('/api/add', { body: { ...scheduled, title } })).status, 200);
  const { downloads } = await (await call('/api/state')).json();
  assert.ok(downloads.some(d => d.name === title), JSON.stringify(downloads.map(d => d.name)));
});
