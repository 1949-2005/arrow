import { test, before, after } from 'node:test';
import assert from 'node:assert/strict';
import http from 'node:http';
import net from 'node:net';
import fs from 'node:fs/promises';
import { existsSync } from 'node:fs';
import os from 'node:os';
import path from 'node:path';
import { spawn, execFileSync } from 'node:child_process';
import { fileURLToPath } from 'node:url';

const sleep = ms => new Promise(r => setTimeout(r, ms));
const serverJs = fileURLToPath(new URL('./server.js', import.meta.url));
let child, dir, base, port;

before(async () => {
  dir = await fs.mkdtemp(path.join(os.tmpdir(), 'fastdl-server-'));
  const probe = net.createServer();
  await new Promise(r => probe.listen(0, '127.0.0.1', r));
  port = probe.address().port;
  await new Promise(r => probe.close(r));
  base = `http://127.0.0.1:${port}`;
  child = spawn(process.execPath, [serverJs], {
    // FASTDL_STARTUP: the Start with Windows shortcut goes to the temp folder, never the real Startup folder
    env: { ...process.env, FASTDL_PORT: String(port), FASTDL_STATE: path.join(dir, 'state.json'), FASTDL_STARTUP: dir },
    stdio: 'ignore',
  });
  for (const t = Date.now(); ; await sleep(50)) {
    try { if ((await fetch(base + '/api/ping')).ok) break; } catch {}
    if (Date.now() - t > 10000) throw new Error('server did not start');
  }
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
  for (const [url, body] of [['/api/settings', { dir, startup: true, whenDone: 'shutdown' }], ['/api/action', { id: 'x', op: 'open' }], ['/api/state']]) {
    const res = await call(url, { origin, body });
    assert.equal(res.status, 403, url);
    assert.deepEqual(await res.json(), { error: 'not allowed from extensions' });
  }
  const st = await (await call('/api/state')).json();
  assert.equal(st.settings.startup, false);
  assert.equal(st.whenDone, '');
  assert.ok(!existsSync(path.join(dir, 'FastDL.lnk')));
});

test('Start with Windows: the app page creates and removes a minimized shortcut to this server', { skip: process.platform !== 'win32' }, async () => {
  const link = path.join(dir, 'FastDL.lnk');
  const startup = async () => (await (await call('/api/state')).json()).settings.startup;
  assert.equal(await startup(), false);
  assert.equal((await call('/api/settings', { origin: base, body: { startup: true } })).status, 200);
  assert.equal(await startup(), true);
  const read = '[Console]::OutputEncoding = [Text.Encoding]::UTF8; $s = (New-Object -ComObject WScript.Shell).CreateShortcut($env:L); ($s.TargetPath, $s.Arguments, $s.WorkingDirectory, $s.WindowStyle) -join "|"';
  const props = execFileSync('powershell.exe', ['-NoProfile', '-NonInteractive', '-Command', read], { env: { ...process.env, L: link } }).toString().trim();
  assert.equal(props.toLowerCase(), [process.execPath, `"${serverJs}"`, path.dirname(serverJs), 7].join('|').toLowerCase()); // 7 = minimized
  assert.equal((await call('/api/settings', { origin: base, body: { startup: false } })).status, 200);
  assert.equal(await startup(), false);
  assert.ok(!existsSync(link));
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
