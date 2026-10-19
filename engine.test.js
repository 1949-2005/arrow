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
let stalledOnce = false, busyOnce = false, segStalledOnce = false;

const server = http.createServer(async (req, res) => {
  let u = req.url;
  if (u === '/busy.bin' && !busyOnce) { busyOnce = true; res.writeHead(429, { 'retry-after': '0' }); return res.end(); }
  if (u === '/busy.bin') u = '/file.bin';
  if (u === '/empty.bin') { res.writeHead(req.headers.range ? 416 : 200, { 'content-length': 0 }); return res.end(); }
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
  if (u === '/byterange.m3u8') return res.end('#EXTM3U\n#EXTINF:4,\n#EXT-X-BYTERANGE:1000@0\nall.ts\n#EXT-X-ENDLIST\n');
  if (u === '/hstall/index.m3u8') return res.end('#EXTM3U\n' + parts.map((_, i) => `#EXTINF:4,\nseg${i}.ts\n`).join('') + '#EXT-X-ENDLIST\n');
  const plain = /^\/hstall\/seg(\d)\.ts$/.exec(u);
  if (plain) {
    const p = parts[plain[1]];
    res.writeHead(200, { 'content-length': p.length });
    if (plain[1] === '0' && !segStalledOnce) { segStalledOnce = true; return res.write(p.subarray(0, 50000)); } // then hangs
    return res.end(p);
  }
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
after(async () => {
  server.closeAllConnections();
  server.close();
  await fs.rm(dir, { recursive: true, force: true, maxRetries: 3 }); // ~70 MB of test downloads
});

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
  ranges.length = 0;
  const d = mgr.add({ url: base + '/file.bin' });
  assert.equal(sha(await finished(d)), sha(data));
  assert.ok(new Set(ranges).size > 1, 'should use several connections');
  assert.equal(d.name, 'file.bin');
});

test('zero-byte file whose server answers 416 to ranges', async () => {
  const d = mgr.add({ url: base + '/empty.bin' });
  assert.equal((await finished(d)).length, 0);
  assert.equal(d.resumable, false);
});

test('429 on the probe is retried after Retry-After', async () => {
  const d = mgr.add({ url: base + '/busy.bin' });
  assert.equal(sha(await finished(d)), sha(data));
  assert.ok(busyOnce);
});

test('a second download with the same name gets a numbered copy', async () => {
  const d = mgr.add({ url: base + '/file.bin' });
  await finished(d);
  assert.equal(d.name, 'file (1).bin');
});

test('a finished download drops its segments and reports got = size', async () => {
  const d = mgr.add({ url: base + '/file.bin' });
  await finished(d);
  assert.equal(d.segments, undefined);
  const v = mgr.view().downloads.find(x => x.id === d.id);
  assert.equal(v.got, data.length);
  assert.equal(v.got, v.size);
});

test('finished files get the Mark-of-the-Web', { skip: process.platform !== 'win32' }, async () => {
  const d = mgr.add({ url: base + '/file.bin', headers: { referer: 'https://example.com/page' } });
  await finished(d);
  const zone = await fs.readFile(d.file + ':Zone.Identifier', 'utf8');
  assert.match(zone, /ZoneId=3\r\n/);
  assert.match(zone, /ReferrerUrl=https:\/\/example\.com\/page\r\n/);
  assert.ok(zone.includes(`HostUrl=${base}/file.bin`), zone);
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

test('pause all: running and queued downloads pause, scheduled ones keep their start time', async () => {
  const m = new Manager(path.join(dir, 'pause-all.json'));
  await m.load();
  m.update({ dir: path.join(dir, 'out'), maxActive: 1 });
  const running = m.add({ url: base + '/slow.bin' }), queued = m.add({ url: base + '/slow.bin' }), later = m.add({ url: base + '/slow.bin', scheduled: true });
  await until(() => got(running) > 0);
  assert.equal(queued.status, 'queued');
  m.pauseAll();
  await until(() => !m.running.size);
  assert.deepEqual([running.status, queued.status, later.status], ['paused', 'paused', 'scheduled']);
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

test('HLS: byte-range playlists are refused', async () => {
  const d = mgr.add({ url: base + '/byterange.m3u8' });
  await until(() => ['done', 'error'].includes(d.status));
  assert.equal(d.status, 'error');
  assert.equal(d.error, 'byte-range HLS playlists are not supported');
});

test('HLS: a segment that goes silent mid-body is cut off and retried', async () => {
  mgr.stallMs = 300;
  try {
    const d = mgr.add({ url: base + '/hstall/index.m3u8' });
    assert.equal(sha(await finished(d)), sha(Buffer.concat(parts)));
    assert.ok(segStalledOnce);
  } finally {
    mgr.stallMs = 30000;
  }
});

test('browser cookies are not passed on to another host after a redirect', async () => {
  cookies.length = 0;
  const d = mgr.add({ url: base + '/redirect', headers: { Cookie: 'session=secret', 'X-Evil': 'dropped' } });
  assert.deepEqual(Object.keys(d.headers), ['cookie']);
  assert.equal(sha(await finished(d)), sha(data));
  assert.ok(cookies.length && cookies.every(c => c === undefined), 'cookie leaked to redirect target');
  assert.equal(d.headers.cookie, undefined, 'finished downloads must not keep login cookies in state.json');
});

test('speed limit slows the download down', async () => {
  mgr.update({ speedLimitKB: 2048 });
  const t = Date.now();
  const d = mgr.add({ url: base + '/file.bin' });
  await finished(d);
  mgr.update({ speedLimitKB: 0 });
  assert.ok(Date.now() - t > 1800, `6 MB at 2 MB/s took only ${Date.now() - t} ms`);
});

// mgr.power is swapped for a recorder: these tests must never put the PC to sleep or shut it down.
async function withPower(fn) {
  const ops = [];
  mgr.power = op => ops.push(op);
  mgr.powerDelayMs = 200;
  try {
    await fn(ops);
  } finally {
    mgr.update({ whenDone: '' });
    mgr.power = () => {};
    mgr.powerDelayMs = 60000;
  }
}

test('when all downloads finish: counts down after the last one, then sleeps once', () => withPower(async ops => {
  mgr.update({ whenDone: 'sleep' });
  await finished(mgr.add({ url: base + '/file.bin' }));
  mgr.tick(); // the 1 s timer does this too; a download that ended between two ticks still counts
  assert.ok(mgr.view().powerIn > 0, 'countdown should run');
  const d = mgr.add({ url: base + '/file.bin' }); // more work during the countdown: wait for it
  mgr.tick();
  assert.equal(mgr.view().powerIn, null);
  await finished(d);
  mgr.tick();
  assert.ok(mgr.view().powerIn > 0);
  assert.deepEqual(ops, [], 'nothing may happen before the countdown ends');
  await sleep(250);
  mgr.tick();
  assert.deepEqual(ops, ['sleep']);
  assert.equal(mgr.view().whenDone, '', 'the choice is used once');
  assert.equal(mgr.view().powerIn, null);
}));

test('when all downloads finish: Cancel keeps the PC on; choosing while idle waits for a download', () => withPower(async ops => {
  mgr.update({ whenDone: 'shutdown' });
  await finished(mgr.add({ url: base + '/file.bin' }));
  mgr.tick();
  assert.ok(mgr.view().powerIn > 0);
  mgr.update({ whenDone: '' }); // the countdown's Cancel button
  assert.equal(mgr.view().powerIn, null);
  await sleep(250);
  mgr.tick();
  mgr.update({ whenDone: 'shutdown' });
  mgr.tick();
  assert.equal(mgr.view().powerIn, null, 'nothing finished since the choice was made');
  assert.deepEqual(ops, []);
}));

test('file names cannot escape the folder', () => {
  assert.equal(sanitize('../../a:b?.exe'), '_.._a_b_.exe');
  assert.equal(sanitize('CON.txt'), '_CON.txt');
  assert.equal(sanitize('...'), 'download');
});
