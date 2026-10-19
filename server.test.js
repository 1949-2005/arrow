import { test, before, after } from 'node:test';
import assert from 'node:assert/strict';
import http from 'node:http';
import net from 'node:net';
import fs from 'node:fs/promises';
import os from 'node:os';
import path from 'node:path';
import { spawn } from 'node:child_process';
import { fileURLToPath } from 'node:url';

const sleep = ms => new Promise(r => setTimeout(r, ms));
let child, dir, base, port;

before(async () => {
  dir = await fs.mkdtemp(path.join(os.tmpdir(), 'fastdl-server-'));
  const probe = net.createServer();
  await new Promise(r => probe.listen(0, '127.0.0.1', r));
  port = probe.address().port;
  await new Promise(r => probe.close(r));
  base = `http://127.0.0.1:${port}`;
  child = spawn(process.execPath, [fileURLToPath(new URL('./server.js', import.meta.url))], {
    env: { ...process.env, FASTDL_PORT: String(port), FASTDL_STATE: path.join(dir, 'state.json') },
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
  for (const [url, body] of [['/api/settings', { dir }], ['/api/action', { id: 'x', op: 'open' }], ['/api/state']]) {
    const res = await call(url, { origin, body });
    assert.equal(res.status, 403, url);
    assert.deepEqual(await res.json(), { error: 'not allowed from extensions' });
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
