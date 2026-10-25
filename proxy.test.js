import { test } from 'node:test';
import assert from 'node:assert/strict';
import fs from 'node:fs/promises';
import { readFileSync } from 'node:fs';
import os from 'node:os';
import path from 'node:path';
import net from 'node:net';
import { spawn } from 'node:child_process';
import { fileURLToPath } from 'node:url';
import { cleanProxy } from './engine.js';
import { proxyEnv } from './desktop.js';
import { ytdlpArgs } from './ytdlp.js';

const here = path.dirname(fileURLToPath(import.meta.url));
const sleep = ms => new Promise(r => setTimeout(r, ms));

test('proxy setting: http(s)://host:port, or empty for none', () => {
  assert.equal(cleanProxy(''), '');
  assert.equal(cleanProxy('  '), '');
  assert.equal(cleanProxy('http://10.0.0.1:8080'), 'http://10.0.0.1:8080');
  assert.equal(cleanProxy('https://user:pw@proxy.example.com:3128/'), 'https://user:pw@proxy.example.com:3128');
  for (const bad of ['proxy:8080', 'socks5://h:1080', 'http://h', 'http://h:80/path', 'http://h:80?x=1', 'nope']) assert.equal(cleanProxy(bad), null, bad);
});

test('proxy env: what the engine must run with, null when it already does', () => {
  const want = { NODE_USE_ENV_PROXY: '1', HTTP_PROXY: 'http://p:1', HTTPS_PROXY: 'http://p:1', NO_PROXY: '127.0.0.1,localhost,::1' };
  assert.deepEqual(proxyEnv('http://p:1', {}), want);
  assert.equal(proxyEnv('http://p:1', want), null);
  assert.deepEqual(proxyEnv('http://q:2', want).HTTPS_PROXY, 'http://q:2');
  assert.equal(proxyEnv('', {}), null);
  assert.deepEqual(Object.keys(proxyEnv('', want)), Object.keys(want)); // all removed
  assert.equal(proxyEnv('', { https_proxy: 'http://p:1' }) !== null, true, 'a stray lowercase variable is removed too');
});

test('yt-dlp gets the proxy', () => {
  const args = ytdlpArgs({ quality: 'best', tmpDir: 'T', url: 'u' }, { proxy: 'http://p:1' }, 'F');
  assert.equal(args[args.indexOf('--proxy') + 1], 'http://p:1');
  assert.ok(!ytdlpArgs({ quality: 'best', tmpDir: 'T', url: 'u' }, { proxy: '' }, 'F').includes('--proxy'));
});

test('changing the proxy restarts the engine with it; a bad one is refused', async () => {
  const dir = await fs.mkdtemp(path.join(os.tmpdir(), 'glide-proxy-'));
  const probe = net.createServer();
  await new Promise(r => probe.listen(0, '127.0.0.1', r));
  const port = probe.address().port;
  await new Promise(r => probe.close(r));
  const base = `http://127.0.0.1:${port}`;
  const env = { ...process.env, FASTDL_PORT: String(port), FASTDL_STATE: path.join(dir, 'state.json'), FASTDL_STARTUP: dir };
  for (const k of ['NODE_USE_ENV_PROXY', 'HTTP_PROXY', 'HTTPS_PROXY', 'NO_PROXY', 'http_proxy', 'https_proxy', 'no_proxy']) delete env[k];
  spawn(process.execPath, [path.join(here, 'desktop.js')], { env, stdio: 'ignore' });
  const state = async () => { for (const t = Date.now(); Date.now() - t < 15000; await sleep(100)) { try { return await (await fetch(base + '/api/state')).json(); } catch {} } throw new Error('no engine'); };
  const post = (body) => fetch(base + '/api/settings', { method: 'POST', headers: { 'content-type': 'application/json' }, body: JSON.stringify(body) });
  try {
    assert.equal((await state()).proxyActive, false);
    const bad = await post({ proxy: 'socks5://h:1' });
    assert.equal(bad.status, 400);
    assert.match((await bad.json()).error, /http:\/\/host:port/);
    assert.equal((await post({ proxy: 'http://127.0.0.1:9' })).status, 200);
    let p;
    for (const t = Date.now(); Date.now() - t < 15000; await sleep(200)) { try { p = await (await fetch(base + '/api/state')).json(); if (p.proxyActive) break; } catch {} }
    assert.equal(p?.proxyActive, true, 'the new engine runs with the proxy');
    assert.equal(JSON.parse(readFileSync(path.join(dir, 'state.json'), 'utf8')).settings.proxy, 'http://127.0.0.1:9');
  } finally {
    await fetch(base + '/api/quit', { method: 'POST', headers: { 'content-type': 'application/json' }, body: '{}' }).catch(() => {});
    await sleep(300);
    await fs.rm(dir, { recursive: true, force: true, maxRetries: 5, retryDelay: 200 });
  }
});
