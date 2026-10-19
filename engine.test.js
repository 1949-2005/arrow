import { test, before, after } from 'node:test';
import assert from 'node:assert/strict';
import http from 'node:http';
import crypto from 'node:crypto';
import fs from 'node:fs/promises';
import os from 'node:os';
import path from 'node:path';
import { Manager, sanitize } from './engine.js';

const sleep = ms => new Promise(r => setTimeout(r, ms));
const sha = b => crypto.createHash('sha256').update(b).digest('hex');
const data = crypto.randomBytes(6 * 1024 * 1024);
const key = crypto.randomBytes(16);
const parts = [crypto.randomBytes(100000), crypto.randomBytes(120000), crypto.randomBytes(90000)];
const ranges = [];
const cookies = [];

function encrypt(buf, seq) {
  const iv = Buffer.alloc(16);
  iv.writeUInt32BE(seq, 12);
  const c = crypto.createCipheriv('aes-128-cbc', key, iv);
  return Buffer.concat([c.update(buf), c.final()]);
}

let open = 0; // live connections to /limited.bin
let stalledOnce = false;

const server = http.createServer(async (req, res) => {
  const u = req.url;
  if (u === '/limited.bin' && open >= 2) { res.writeHead(503); return res.end(); }
  if (u === '/limited.bin') { open++; res.on('close', () => open--); }
  if (u === '/file.bin' || u === '/slow.bin' || u === '/limited.bin' || u === '/stall.bin') {
    ranges.push(req.headers.range);
    cookies.push(req.headers.cookie);
    const m = /bytes=(\d+)-(\d*)/.exec(req.headers.range ?? '');
    const start = m ? Number(m[1]) : 0, end = m?.[2] ? Number(m[2]) : data.length - 1;
    res.writeHead(m ? 206 : 200, { 'content-length': end - start + 1, ...(m && { 'content-range': `bytes ${start}-${end}/${data.length}` }) });
    if (u === '/stall.bin' && !stalledOnce) { stalledOnce = true; return res.write(data.subarray(start, start + 1048576)); } // then hangs
    if (u === '/file.bin' || u === '/stall.bin') return res.end(data.subarray(start, end + 1));
    let closed = false;
    res.on('close', () => { closed = true; });
    for (let p = start; p <= end && !closed; p += 65536) { res.write(data.subarray(p, Math.min(p + 65536, end + 1))); await sleep(15); }
    return res.end();
  }
  if (u === '/redirect') { res.writeHead(302, { location: `http://localhost:${server.address().port}/file.bin` }); return res.end(); }
  if (u === '/norange.bin') { res.writeHead(200); return res.end(data); }
  if (u === '/master.m3u8') return res.end('#EXTM3U\n#EXT-X-STREAM-INF:BANDWIDTH=100\nlow.m3u8\n#EXT-X-STREAM-INF:BANDWIDTH=900\nhi/index.m3u8\n');
  if (u === '/hi/index.m3u8') {
    return res.end('#EXTM3U\n#EXT-X-MEDIA-SEQUENCE:7\n#EXT-X-KEY:METHOD=AES-128,URI="/key.bin"\n'
      + parts.map((_, i) => `#EXTINF:4,\nseg${i}.ts\n`).join('') + '#EXT-X-ENDLIST\n');
  }
  if (u === '/key.bin') return res.end(key);
  const seg = /^\/hi\/seg(\d)\.ts$/.exec(u);
  if (seg) return res.end(encrypt(parts[seg[1]], 7 + Number(seg[1])));
  res.writeHead(404).end();
});

let base, dir, mgr;
before(async () => {
  await new Promise(r => server.listen(0, '127.0.0.1', r));
  base = `http://127.0.0.1:${server.address().port}`;
  dir = await fs.mkdtemp(path.join(os.tmpdir(), 'fastdl-'));
  mgr = new Manager(path.join(dir, 'state.json'));
  await mgr.load();
  mgr.update({ dir: path.join(dir, 'out'), connections: 4, maxActive: 5 });
});
after(() => { server.closeAllConnections(); server.close(); });

async function until(fn, ms = 30000) {
  for (const t = Date.now(); !fn(); await sleep(20)) if (Date.now() - t > ms) throw new Error('timed out');
}
async function finished(d) {
  await until(() => ['done', 'error'].includes(d.status));
  assert.equal(d.status, 'done', d.error);
  return fs.readFile(d.file);
}
const got = d => d.segments?.reduce((a, s) => a + s.pos - s.start, 0) ?? 0;

test('splits into several connections and the file matches', async () => {
  const d = mgr.add({ url: base + '/file.bin' });
  assert.equal(sha(await finished(d)), sha(data));
  assert.ok(d.segments.length > 1, 'should use several connections');
  assert.equal(d.name, 'file.bin');
});

test('a second download with the same name gets a numbered copy', async () => {
  const d = mgr.add({ url: base + '/file.bin' });
  await finished(d);
  assert.equal(d.name, 'file (1).bin');
});

test('pause, restart the app, resume: continues where it stopped', async () => {
  const d = mgr.add({ url: base + '/slow.bin' });
  await until(() => got(d) > 1.5e6);
  await mgr.act(d.id, 'pause');
  await until(() => !mgr.running.has(d.id));
  await until(() => !mgr.saving);
  await mgr.save();

  const mgr2 = new Manager(mgr.stateFile);
  await mgr2.load();
  const d2 = mgr2.list.find(x => x.id === d.id);
  assert.equal(d2.status, 'paused');
  ranges.length = 0;
  await mgr2.act(d.id, 'resume');
  assert.equal(sha(await finished(d2)), sha(data));
  assert.ok(ranges.length && ranges.every(r => r && !r.startsWith('bytes=0-')), `restarted from zero: ${ranges}`);
});

test('server that allows only 2 connections: finishes with fewer instead of failing', async () => {
  const d = mgr.add({ url: base + '/limited.bin' });
  assert.equal(sha(await finished(d)), sha(data));
});

test('a connection that goes silent is cut off and the download recovers', async () => {
  mgr.stallMs = 300;
  mgr.update({ connections: 1 });
  const d = mgr.add({ url: base + '/stall.bin' });
  assert.equal(sha(await finished(d)), sha(data));
  assert.ok(stalledOnce);
  mgr.update({ connections: 4 });
  mgr.stallMs = 30000;
});

test('server without range support still downloads, on one connection', async () => {
  const d = mgr.add({ url: base + '/norange.bin' });
  assert.equal(sha(await finished(d)), sha(data));
  assert.equal(d.resumable, false);
});

test('HLS: picks best quality, decrypts AES-128, joins segments, files it under Video', async () => {
  const d = mgr.add({ url: base + '/master.m3u8', title: 'My Video' });
  assert.equal(sha(await finished(d)), sha(Buffer.concat(parts)));
  assert.equal(d.name, 'My Video.ts');
  assert.equal(path.basename(path.dirname(d.file)), 'Video');
});

test('browser cookies are not passed on to another host after a redirect', async () => {
  cookies.length = 0;
  const d = mgr.add({ url: base + '/redirect', headers: { Cookie: 'session=secret', 'X-Evil': 'dropped' } });
  assert.equal(sha(await finished(d)), sha(data));
  assert.ok(cookies.length && cookies.every(c => c === undefined), 'cookie leaked to redirect target');
  assert.deepEqual(Object.keys(d.headers), ['cookie']);
});

test('speed limit slows the download down', async () => {
  mgr.update({ speedLimitKB: 2048 });
  const t = Date.now();
  const d = mgr.add({ url: base + '/file.bin' });
  await finished(d);
  mgr.update({ speedLimitKB: 0 });
  assert.ok(Date.now() - t > 1800, `6 MB at 2 MB/s took only ${Date.now() - t} ms`);
});

test('file names cannot escape the folder', () => {
  assert.equal(sanitize('../../a:b?.exe'), '_.._a_b_.exe');
  assert.equal(sanitize('CON.txt'), '_CON.txt');
  assert.equal(sanitize('...'), 'download');
});
