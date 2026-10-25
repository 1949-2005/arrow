// Phone access (remote.js) against a real engine on spare ports with temp state: pairing by QR secret, the device
// token, the phone's API, files saved by one-time tickets, and what never leaves (token hashes) or gets in (other hosts).
import { test, before, after } from 'node:test';
import assert from 'node:assert/strict';
import http from 'node:http';
import net from 'node:net';
import fs from 'node:fs/promises';
import { readFileSync } from 'node:fs';
import os from 'node:os';
import path from 'node:path';
import { spawn } from 'node:child_process';
import { fileURLToPath } from 'node:url';
import { lanAddresses, hostOk, deviceName, cleanRemote, fileOfUrl } from './remote.js';

const here = path.dirname(fileURLToPath(import.meta.url));
const NAME = JSON.parse(readFileSync(path.join(here, 'brand.json'), 'utf8')).name;
const sleep = ms => new Promise(r => setTimeout(r, ms));
const freePort = () => new Promise(r => { const s = net.createServer().listen(0, '127.0.0.1', () => { const p = s.address().port; s.close(() => r(p)); }); });
let dir, engine, files, app, phone, remotePort;

const local = (url, body) => fetch(app + url, { method: body ? 'POST' : 'GET', headers: body ? { 'content-type': 'application/json' } : {}, body: body && JSON.stringify(body) });
const remote = (url, { body, token, method } = {}) => fetch(phone + url, {
  method: method ?? (body ? 'POST' : 'GET'),
  headers: { ...(body && { 'content-type': 'application/json' }), ...(token && { authorization: 'Bearer ' + token }), 'user-agent': 'Mozilla/5.0 (iPhone; CPU iPhone OS 18_0 like Mac OS X)' },
  body: body && JSON.stringify(body),
});
const secretOf = url => new URLSearchParams(new URL(url).hash.slice(1)).get('pair');

before(async () => {
  dir = await fs.mkdtemp(path.join(os.tmpdir(), 'glide-remote-'));
  files = http.createServer((req, res) => res.writeHead(200, { 'content-length': 11 }).end('hello phone')).listen(0, '127.0.0.1');
  const port = await freePort();
  remotePort = await freePort();
  app = `http://127.0.0.1:${port}`;
  phone = `http://127.0.0.1:${remotePort}`;
  engine = spawn(process.execPath, [path.join(here, 'server.js')], {
    env: { ...process.env, FASTDL_PORT: String(port), FASTDL_REMOTE_PORT: String(remotePort), FASTDL_STATE: path.join(dir, 'state.json'), FASTDL_STARTUP: dir },
    stdio: 'ignore',
  });
  for (const t = Date.now(); ; await sleep(50)) {
    try { if ((await fetch(app + '/api/ping')).ok) break; } catch { }
    if (Date.now() - t > 10000) throw new Error('engine did not start');
  }
  await local('/api/settings', { dir: path.join(dir, 'downloads') }); // never the user's Downloads folder
});

after(async () => {
  engine?.kill();
  files?.close();
  await sleep(300);
  await fs.rm(dir, { recursive: true, force: true, maxRetries: 5, retryDelay: 200 });
});

test('helpers: private addresses only, IP hosts only, device names, names of unnamed links, settings cleaned', () => {
  assert.deepEqual(lanAddresses({
    'vEthernet (WSL)': [{ family: 'IPv4', address: '172.20.0.1', internal: false }],
    'Wi-Fi': [{ family: 'IPv4', address: '192.168.1.23', internal: false }, { family: 'IPv6', address: 'fe80::1', internal: false }],
    lo: [{ family: 'IPv4', address: '127.0.0.1', internal: true }],
    public: [{ family: 'IPv4', address: '8.8.8.8', internal: false }],
  }), ['192.168.1.23', '172.20.0.1']);
  assert.ok(hostOk('192.168.1.23:9615', 9615) && hostOk('[::1]:9615', 9615));
  assert.ok(!hostOk('evil.example:9615', 9615) && !hostOk('192.168.1.23:80', 9615) && !hostOk(undefined, 9615));
  assert.equal(deviceName('Mozilla/5.0 (iPhone; CPU iPhone OS 18_0)'), 'iPhone');
  assert.equal(deviceName('Mozilla/5.0 (Linux; Android 14)'), 'Android phone');
  assert.equal(fileOfUrl('https://x.example/a/my%20file.zip?t=1'), 'my file.zip');
  assert.equal(fileOfUrl('https://www.youtube.com/watch?v=D0laZjrY_5c'), 'youtube.com/watch?v=D0laZjrY_5c');
  assert.deepEqual(cleanRemote({ enabled: 1, devices: [{ id: 'a', hash: 'x' }, { id: 'b', name: 'iPad', hash: 'f'.repeat(64) }] }).devices.map(d => d.id), ['b']);
});

test('off by default: no phone port, and no pairing', async () => {
  assert.equal((await (await local('/api/remote', {})).json()).enabled, false);
  await assert.rejects(fetch(phone + '/'), 'nothing listens');
  assert.equal((await local('/api/remote/pair', {})).status, 400);
});

let token;
test('pairing: the QR link\'s secret works once and gives a device token; wrong ones are refused', async () => {
  const on = await (await local('/api/remote', { enabled: true })).json();
  assert.equal(on.enabled, true);
  assert.equal(on.listening, true);
  const page = await remote('/');
  assert.equal(page.status, 200);
  const html = await page.text();
  assert.ok(html.includes(`<title>${NAME}</title>`) && !html.includes('__NAME__'), 'the page carries brand.json\'s name');
  const pair = await (await local('/api/remote/pair', { address: '127.0.0.1' })).json();
  assert.match(pair.url, new RegExp(`^http://127\\.0\\.0\\.1:${remotePort}/#pair=[\\w-]{22}$`));
  assert.ok(Array.isArray(pair.qr) && pair.qr.length >= 21 && pair.qr.every(r => r.length === pair.qr.length), 'a QR grid');
  assert.equal((await remote('/api/pair', { body: { secret: 'wrong' } })).status, 403);
  const ok = await remote('/api/pair', { body: { secret: secretOf(pair.url) } });
  assert.equal(ok.status, 200);
  ({ token } = await ok.json());
  assert.match(token, /^[\w-]{43}$/);
  assert.equal((await remote('/api/pair', { body: { secret: secretOf(pair.url) } })).status, 403, 'once');
  const info = await (await local('/api/remote', {})).json();
  assert.deepEqual(info.devices.map(d => d.name), ['iPhone']);
  assert.ok(!JSON.stringify(info).includes('hash'), 'the token hashes never leave');
  const state = await (await local('/api/state')).text();
  assert.ok(!state.includes('"remote"') && !state.includes(token), '/api/state has neither');
});

test('the phone\'s API needs its token, and an IP address as the host', async () => {
  assert.equal((await remote('/api/state')).status, 401);
  assert.equal((await remote('/api/state', { token: 'x'.repeat(43) })).status, 401);
  const named = await new Promise(r => http.get({ host: '127.0.0.1', port: remotePort, path: '/api/state', headers: { host: 'glide.example:' + remotePort, authorization: 'Bearer ' + token } }, r));
  assert.equal(named.statusCode, 421, 'a DNS name as the host is refused');
  named.resume();
  const s = await (await remote('/api/state', { token })).json();
  assert.equal(s.device, 'iPhone');
  assert.deepEqual(s.downloads, []);
});

test('the phone adds a download, pauses it, and saves the finished file once', async () => {
  const url = `http://127.0.0.1:${files.address().port}/note.txt`;
  const { id } = await (await remote('/api/add', { body: { url }, token })).json();
  let d;
  for (const t = Date.now(); Date.now() - t < 10000; await sleep(100)) {
    d = (await (await remote('/api/state', { token })).json()).downloads.find(x => x.id === id);
    if (d?.status === 'done') break;
  }
  assert.equal(d.status, 'done');
  assert.equal(d.name, 'note.txt');
  const ticket = await (await remote('/api/ticket', { body: { id }, token })).json();
  assert.match(ticket.url, /^\/file\/[\w-]{32}$/);
  const file = await remote(ticket.url);
  assert.equal(file.status, 200);
  assert.match(file.headers.get('content-disposition'), /attachment; filename="note\.txt"/);
  assert.equal(await file.text(), 'hello phone');
  assert.equal((await remote(ticket.url)).status, 404, 'a ticket works once');
  assert.equal((await remote('/api/action', { body: { id, op: 'removeFile' }, token })).status, 400, 'only pause, resume and remove');
  assert.equal((await remote('/api/action', { body: { id, op: 'remove' }, token })).status, 200);
});

test('a removed phone is out; ten wrong secrets a minute and pairing waits; off closes the port', async () => {
  const info = await (await local('/api/remote', {})).json();
  await local('/api/remote/remove', { id: info.devices[0].id });
  assert.equal((await remote('/api/state', { token })).status, 401);
  for (let i = 0; i < 9; i++) await remote('/api/pair', { body: { secret: 'nope' + i } }); // one wrong one came before
  assert.equal((await remote('/api/pair', { body: { secret: 'again' } })).status, 429);
  await local('/api/remote', { enabled: false });
  await assert.rejects(fetch(phone + '/'), 'nothing listens');
});
