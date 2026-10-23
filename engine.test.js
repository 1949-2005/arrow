import { test, before, after } from 'node:test';
import assert from 'node:assert/strict';
import http from 'node:http';
import crypto from 'node:crypto';
import fs from 'node:fs/promises';
import { existsSync, mkdirSync } from 'node:fs';
import os from 'node:os';
import path from 'node:path';
import { execFileSync } from 'node:child_process';
import { Manager, sanitize, CATEGORIES } from './engine.js';
import { parsePlaylist, soundTemps } from './hls.js';
import { ffmpegExe } from './ytdlp.js';

const sleep = ms => new Promise(r => setTimeout(r, ms));
let ffmpeg = null; // the real one: it makes and joins the sound-track test video
try { ffmpeg = ffmpegExe(); } catch {}
const sha = b => crypto.createHash('sha256').update(b).digest('hex');
const data = crypto.randomBytes(6 * 1024 * 1024);
const key = crypto.randomBytes(16);
const parts = [crypto.randomBytes(100000), crypto.randomBytes(120000), crypto.randomBytes(90000)];
const ranges = [];
const cookies = [];
const genHits = [];

function encrypt(buf, seq) {
  const iv = Buffer.alloc(16);
  iv.writeUInt32BE(seq, 12);
  const c = crypto.createCipheriv('aes-128-cbc', key, iv);
  return Buffer.concat([c.update(buf), c.final()]);
}

let open = 0; // live connections to /limited.bin
let stalledOnce = false, busyOnce = false, segStalledOnce = false, genSlow = false;

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
  if (u === '/dispo') { // ignores Range, like many servers
    res.writeHead(200, { 'content-disposition': 'attachment; filename="Q3 report.pdf"', 'content-type': 'application/pdf; charset=binary', 'content-length': 10 });
    return res.end('0123456789');
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
  if (u === '/tracks/master.m3u8') { // sound in separate tracks, per quality; the "hi" group has a default
    return res.end('#EXTM3U\n#EXT-X-MEDIA:TYPE=AUDIO,GROUP-ID="lo",NAME="lo",URI="lo-sound.m3u8"\n'
      + '#EXT-X-MEDIA:TYPE=AUDIO,GROUP-ID="hi",NAME="fr",URI="fr.m3u8"\n#EXT-X-MEDIA:TYPE=AUDIO,GROUP-ID="hi",NAME="en",DEFAULT=YES,URI="en.m3u8"\n'
      + '#EXT-X-STREAM-INF:BANDWIDTH=900,AUDIO="hi"\nhi.m3u8\n#EXT-X-STREAM-INF:BANDWIDTH=100,AUDIO="lo"\nlo.m3u8\n');
  }
  const track = /^\/tracks\/([\w-]+)\.m3u8$/.exec(u);
  if (track) return res.end(`#EXTM3U\n#EXTINF:4,\n${track[1]}.ts\n#EXT-X-ENDLIST\n`);
  if (u.startsWith('/gen/')) { // ffmpeg-made HLS, see makeHls()
    genHits.push(u);
    if (genSlow && /\/1\/seg/.test(u)) await sleep(300); // slow sound pieces, so a pause can land between them
    const f = await fs.readFile(path.join(dir, u)).catch(() => null);
    if (f) return res.end(f);
  }
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
  const v = mgr.view().downloads.find(x => x.id === d.id);
  assert.deepEqual([v.referer, 'headers' in v], ['https://example.com/page', false], 'view() shows the referer for Properties, never the headers');
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

test("HLS: the chosen quality's separate sound track is found (its group's default)", async () => {
  const { segs, audio } = await parsePlaylist(mgr, { url: base + '/tracks/master.m3u8', headers: {} }, AbortSignal.timeout(5000));
  assert.equal(segs[0].url, base + '/tracks/hi.ts');
  assert.equal(audio.segs[0].url, base + '/tracks/en.ts');
  assert.equal((await parsePlaylist(mgr, { url: base + '/master.m3u8', headers: {} }, AbortSignal.timeout(5000))).audio, null);
});

// A 3 s clip as HLS with its sound in a separate track, like X serves it; served under /gen/
function makeHls(type) {
  const folder = path.join(dir, 'gen', type);
  mkdirSync(folder, { recursive: true });
  execFileSync(ffmpeg, ['-v', 'error', '-f', 'lavfi', '-i', 'testsrc=size=160x90:rate=10:duration=3', '-f', 'lavfi', '-i', 'sine=frequency=440:duration=3',
    '-map', '0:v', '-map', '1:a', '-c:v', 'libx264', '-preset', 'ultrafast', '-g', '10', '-c:a', 'aac', '-b:a', '64k', '-f', 'hls', '-hls_time', '1', '-hls_playlist_type', 'vod',
    '-hls_segment_type', type, '-var_stream_map', 'v:0,agroup:aud a:0,agroup:aud', '-master_pl_name', 'master.m3u8',
    '-hls_segment_filename', `%v/seg%d.${type === 'fmp4' ? 'm4s' : 'ts'}`, '%v/index.m3u8'], { cwd: folder });
  return `${base}/gen/${type}/master.m3u8`;
}
const ffprobe = (...args) => execFileSync(path.join(path.dirname(ffmpeg), 'ffprobe' + path.extname(ffmpeg)), ['-v', 'error', ...args]).toString().trim();
// JSON's top-level list: in csv, MPEG-TS files also list each stream again under their program
const streams = file => JSON.parse(ffprobe('-show_entries', 'stream=codec_type', '-of', 'json', file)).streams.map(s => s.codec_type);

test('HLS: a separate sound track (X) is downloaded too and joined into an .mp4 by ffmpeg', { skip: !ffmpeg && 'needs ffmpeg' }, async () => {
  for (const type of ['fmp4', 'mpegts']) {
    const url = makeHls(type);
    const p = mgr.add({ url, confirm: true });
    await until(() => p.probe);
    assert.equal(p.probe.name, 'master.mp4', type);
    await mgr.confirm(p.id, { action: 'cancel' });

    const d = mgr.add({ url, title: 'With sound ' + type });
    await finished(d);
    assert.equal(d.name, `With sound ${type}.mp4`);
    assert.deepEqual(streams(d.file), ['video', 'audio'], type);
    assert.equal(d.size, (await fs.stat(d.file)).size);
    assert.ok(![d.tmp, ...soundTemps(d)].some(existsSync), 'temp files are gone');
  }
  // a .ts name picked in the Download File Info window gets a real MPEG-TS file
  const p = mgr.add({ url: `${base}/gen/fmp4/master.m3u8`, confirm: true });
  const file = path.join(dir, 'picked', 'kept name.ts');
  await mgr.confirm(p.id, { action: 'start', path: file });
  await finished(p);
  assert.deepEqual(streams(file), ['video', 'audio']);
  assert.equal(ffprobe('-show_entries', 'format=format_name', '-of', 'csv=p=0', file), 'mpegts');
});

test('HLS with a separate sound track: paused in the sound track, it resumes there', { skip: !ffmpeg && 'needs ffmpeg' }, async () => {
  const url = makeHls('fmp4');
  mgr.update({ connections: 1 });
  genSlow = true;
  try {
    const d = mgr.add({ url, title: 'Paused with sound' });
    await until(() => d.hls?.parts?.[1]?.done > 0);
    await mgr.act(d.id, 'pause');
    await until(() => !mgr.running.has(d.id));
    assert.ok(d.hls.parts[1].done < d.hls.total - d.hls.parts[0].done, 'paused before the sound track ended');
    genSlow = false;
    genHits.length = 0;
    await mgr.act(d.id, 'resume');
    await finished(d);
    assert.deepEqual(streams(d.file), ['video', 'audio']);
    assert.ok(!genHits.some(u => /\/0\/(seg|init)/.test(u)), `video pieces fetched again: ${genHits}`);
  } finally {
    genSlow = false;
    mgr.update({ connections: 4 });
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
  mgr.update({ speedLimitKB: 2048, speedLimitOn: true });
  const t = Date.now();
  const d = mgr.add({ url: base + '/file.bin' });
  await finished(d);
  mgr.update({ speedLimitKB: 0, speedLimitOn: false });
  assert.ok(Date.now() - t > 1800, `6 MB at 2 MB/s took only ${Date.now() - t} ms`);
});

// These tests tick by hand, so they get a Manager that is never loaded: it has no 1 s timer. On the shared mgr, a timer
// tick right after a download ended could start the 200 ms countdown, which a slowed-down test then found already over.
// Its power is a recorder: these tests must never put the PC to sleep or shut it down.
function withPower(fn) {
  const m = new Manager(path.join(dir, 'power.json'));
  m.update({ dir: path.join(dir, 'out') });
  m.powerDelayMs = 200;
  const ops = [];
  m.power = op => ops.push(op);
  return fn(m, ops);
}

test('when all downloads finish: counts down after the last one, then sleeps once', () => withPower(async (m, ops) => {
  m.update({ whenDone: 'sleep' });
  await finished(m.add({ url: base + '/file.bin' }));
  m.tick(); // what the 1 s timer does; a download that ended between two ticks still counts
  assert.ok(m.view().powerIn > 0, 'countdown should run');
  assert.equal(m.view().powerAll, true, 'started by "When all downloads finish"');
  const d = m.add({ url: base + '/file.bin' }); // more work during the countdown: wait for it
  m.tick();
  assert.equal(m.view().powerIn, null);
  await finished(d);
  m.tick();
  assert.ok(m.view().powerIn > 0);
  assert.deepEqual(ops, [], 'nothing may happen before the countdown ends');
  await sleep(250);
  m.tick();
  assert.deepEqual(ops, ['sleep']);
  assert.equal(m.view().whenDone, '', 'the choice is used once');
  assert.equal(m.view().powerIn, null);
}));

test('when all downloads finish: Cancel keeps the PC on; choosing while idle waits for a download', () => withPower(async (m, ops) => {
  m.update({ whenDone: 'shutdown' });
  await finished(m.add({ url: base + '/file.bin' }));
  m.tick();
  assert.ok(m.view().powerIn > 0);
  m.update({ whenDone: '' }); // the countdown's Cancel button
  assert.equal(m.view().powerIn, null);
  await sleep(250);
  m.tick();
  m.update({ whenDone: 'shutdown' });
  m.tick();
  assert.equal(m.view().powerIn, null, 'nothing finished since the choice was made');
  assert.deepEqual(ops, []);
}));

test("a download's own shutdown starts the countdown while another still runs; Cancel stops it", () => withPower(async (m, ops) => {
  const other = m.add({ url: base + '/slow.bin' });
  await m.act(other.id, 'limit', undefined, { kb: 1024 }); // keeps it running for seconds
  const d = m.add({ url: base + '/file.bin' });
  await m.act(d.id, 'whenDone', undefined, { value: 'shutdown' });
  await finished(d);
  m.tick();
  assert.equal(other.status, 'downloading');
  assert.ok(m.view().powerIn > 0, 'other downloads do not hold it back');
  assert.equal(m.view().powerOp, 'shutdown', 'view names the countdown');
  assert.equal(m.view().powerAll, false, 'one download started it');
  assert.equal(m.view().whenDone, '', 'the status bar choice stays as it was');
  m.update({ whenDone: '' }); // the strip's Cancel
  assert.equal(m.view().powerIn, null);
  await sleep(250);
  m.tick();
  assert.deepEqual(ops, []);

  const e = m.add({ url: base + '/file.bin' });
  await m.act(e.id, 'whenDone', undefined, { value: 'sleep' });
  await finished(e);
  m.tick();
  assert.deepEqual(ops, []);
  await sleep(250);
  m.tick();
  assert.deepEqual(ops, ['sleep'], 'fires while the other one still runs');
  assert.equal(other.status, 'downloading');
  assert.equal(m.view().powerIn, null);
  await m.act(other.id, 'pause');
  await until(() => !m.running.size);
}));

test("a download's own when done 'open' calls the opener; other downloads open nothing", () => withPower(async (m, ops) => {
  const opened = [];
  m.opener = d => opened.push(d.id);
  const a = m.add({ url: base + '/file.bin' }), b = m.add({ url: base + '/file.bin' });
  await m.act(a.id, 'whenDone', undefined, { value: 'open' });
  await finished(a);
  await finished(b);
  assert.deepEqual(opened, [a.id]);
  m.tick();
  assert.equal(m.view().powerIn, null);
  assert.deepEqual(ops, []);
}));

test('per-download limit: slows that download while another runs at full speed; HLS too', async () => {
  const t = Date.now();
  const slow = mgr.add({ url: base + '/file.bin' });
  await mgr.act(slow.id, 'limit', undefined, { kb: 4096 }); // 6 MB at 4 MB/s
  assert.equal(slow.status, 'downloading', 'it applies to a running download');
  const fast = mgr.add({ url: base + '/file.bin' });
  await finished(fast);
  assert.equal(slow.status, 'downloading', 'the other one was not held back');
  assert.equal(sha(await finished(slow)), sha(data));
  assert.ok(Date.now() - t > 1200, `6 MB at 4 MB/s took only ${Date.now() - t} ms`);
  const h = mgr.add({ url: base + '/master.m3u8', title: 'Limited' });
  await mgr.act(h.id, 'limit', undefined, { kb: 200 }); // 310 kB at 200 kB/s
  const th = Date.now();
  assert.equal(sha(await finished(h)), sha(Buffer.concat(parts)));
  assert.ok(Date.now() - th > 1000, `310 kB at 200 kB/s took only ${Date.now() - th} ms`);
});

test('segments: a 4th value says which pieces a connection works on now', async () => {
  const d = mgr.add({ url: base + '/slow.bin' });
  await until(() => got(d) > 1e6 && mgr.running.get(d.id)?.active.size > 1);
  const rows = () => mgr.view().downloads.find(x => x.id === d.id).segments;
  const r = rows(), n = mgr.running.get(d.id).active.size;
  assert.ok(r.every(s => s.length === 4 && [0, 1].includes(s[3])), JSON.stringify(r));
  assert.equal(r.filter(s => s[3] === 1).length, n);
  await mgr.act(d.id, 'pause');
  await until(() => !mgr.running.has(d.id));
  assert.ok(rows().every(s => s[3] === 0));
  await mgr.act(d.id, 'remove');
});

test('description, limit and when done: set through add, confirm and act, checked, saved and in view()', async () => {
  const m = new Manager(path.join(dir, 'fields.json')); // never loaded: no timer
  m.update({ dir: path.join(dir, 'out') });
  const d = m.add({ url: base + '/file.bin', scheduled: true, description: '  a\tb\nc\x7f\u0085 ' });
  assert.equal(d.description, 'abc', 'control characters removed, trimmed');
  const v = () => m.view().downloads.find(x => x.id === d.id);
  assert.deepEqual([v().limitKB, v().whenDone, v().lastTry, v().notify], [0, '', null, false]);
  await m.act(d.id, 'describe', undefined, { description: 'x'.repeat(400) });
  assert.equal(d.description.length, 300);
  await m.act(d.id, 'describe', undefined, { description: ' \n ' });
  assert.equal(v().description, null, 'empty is null');
  for (const [kb, want] of [['250', 250], [-1, 0], [1e9, 1e7], ['x', 0], [64, 64]]) {
    await m.act(d.id, 'limit', undefined, { kb });
    assert.equal(v().limitKB, want, String(kb));
  }
  for (const [value, want] of [['open', 'open'], ['reboot', ''], ['shutdown', 'shutdown'], [undefined, ''], ['sleep', 'sleep']]) {
    await m.act(d.id, 'whenDone', undefined, { value });
    assert.equal(v().whenDone, want, String(value));
  }
  await assert.rejects(() => m.act('nope', 'limit', undefined, { kb: 1 }), /download not found/);
  const saved = JSON.parse(m.json()).list.find(x => x.id === d.id);
  assert.deepEqual([saved.limitKB, saved.whenDone], [64, 'sleep']);

  // the Download File Info window: its description wins, and only "Start Download" asks for the complete window
  const later = m.add({ url: base + '/file.bin', confirm: true, description: 'from the page' });
  await m.confirm(later.id, { action: 'later', path: path.join(dir, 'fields', 'later.bin') });
  assert.equal(later.description, 'from the page', 'kept when the window sends none');
  assert.equal(later.notify, undefined);
  const s = m.add({ url: base + '/file.bin', confirm: true });
  const t = Date.now();
  await m.confirm(s.id, { action: 'start', path: path.join(dir, 'fields', 'start.bin'), description: ' Mine ' });
  assert.equal(s.description, 'Mine');
  assert.equal(m.view().downloads.find(x => x.id === s.id).notify, true);
  assert.ok(s.lastTry >= t, 'start() sets the last try');
  await finished(s);
  for (const x of [...m.list]) await m.act(x.id, 'remove');
});

test('settings: the speed limit switch and its migration, duplicates, complete window', async () => {
  const file = path.join(dir, 'switch.json');
  const m = new Manager(file);
  assert.deepEqual([m.settings.speedLimitOn, m.settings.duplicates, m.settings.showComplete], [false, 'ask', true]);
  m.update({ speedLimitKB: 300 });
  assert.equal(m.limiter.rate, 0, 'off: the number alone does not limit');
  m.update({ speedLimitOn: true });
  assert.equal(m.limiter.rate, 300 * 1024);
  m.update({ speedLimitOn: false });
  assert.equal(m.limiter.rate, 0);
  assert.equal(m.settings.speedLimitKB, 300, 'turning it off keeps the number');
  m.update({ duplicates: 'replace', showComplete: false });
  assert.deepEqual([m.settings.duplicates, m.settings.showComplete], ['replace', false]);
  m.update({ duplicates: 'always' });
  assert.equal(m.settings.duplicates, 'ask', 'unknown values fall back to the default');

  for (const [settings, on, rate] of [[{ speedLimitKB: 500 }, true, 500], [{ speedLimitKB: 500, speedLimitOn: false }, false, 0], [{ speedLimitKB: 0 }, false, 0], [{}, false, 0]]) {
    await fs.writeFile(file, JSON.stringify({ settings, list: [] }));
    const l = new Manager(file);
    await l.load();
    assert.equal(l.settings.speedLimitOn, on, JSON.stringify(settings));
    assert.equal(l.limiter.rate, rate * 1024, JSON.stringify(settings));
  }
  await fs.writeFile(file, JSON.stringify({ settings: { duplicates: 'nope', showComplete: 0 }, list: [] }));
  const l = new Manager(file);
  await l.load();
  assert.deepEqual([l.settings.duplicates, l.settings.showComplete], ['ask', false]);
});

test('duplicates: the probe names the existing download; number, replace, resume, remember, and adds without the window', async () => {
  const m = new Manager(path.join(dir, 'dup.json')); // never loaded: no timer
  m.update({ dir: path.join(dir, 'dup'), categories: false });
  const url = base + '/file.bin';
  const x = m.add({ url });
  await finished(x);
  await fs.writeFile(x.file, 'old');
  const p = m.add({ url, confirm: true });
  await until(() => p.probe);
  assert.deepEqual(p.probe.duplicate, { id: x.id, name: 'file.bin', status: 'done' });
  assert.deepEqual(m.view().downloads.find(v => v.id === p.id).probe.duplicate, p.probe.duplicate);

  // number: as before
  await m.confirm(p.id, { action: 'later', path: x.file });
  assert.equal(p.name, 'file (1).bin');
  await m.act(p.id, 'remove');

  // replace: the existing entry goes and the new one writes over its file
  const r = m.add({ url, confirm: true });
  await m.confirm(r.id, { action: 'start', path: x.file, replace: x.id, rememberDuplicates: 'replace' });
  assert.ok(!m.list.includes(x));
  assert.equal(r.file, x.file);
  assert.equal(sha(await finished(r)), sha(data));
  assert.equal(m.settings.duplicates, 'replace');

  // replace a running one: it stops and its temp file goes before the new one uses the name
  const slowUrl = base + '/slow.bin';
  const s = m.add({ url: slowUrl });
  await m.act(s.id, 'limit', undefined, { kb: 1024 });
  await until(() => s.file && got(s) > 0);
  const q = m.add({ url: slowUrl, confirm: true });
  await until(() => q.probe);
  assert.equal(q.probe.duplicate.status, 'downloading');
  await m.confirm(q.id, { action: 'start', path: s.file, replace: s.id });
  assert.ok(!m.list.includes(s) && !m.running.has(s.id));
  assert.equal(q.file, s.file);
  assert.equal(sha(await finished(q)), sha(data));

  // resume: the app cancels the new one (and resumes or shows the existing one)
  const c = m.add({ url, confirm: true });
  await m.confirm(c.id, { action: 'cancel', rememberDuplicates: 'resume' });
  assert.ok(!m.list.includes(c));
  assert.equal(m.settings.duplicates, 'resume');
  for (const bad of ['ask', 'always', 1]) {
    const b = m.add({ url, confirm: true });
    await m.confirm(b.id, { action: 'cancel', rememberDuplicates: bad });
  }
  assert.equal(m.settings.duplicates, 'resume');

  // without the window: 'resume' and 'ask' number, 'replace' replaces
  for (const duplicates of ['resume', 'ask']) {
    m.update({ duplicates });
    const n = m.add({ url });
    await finished(n);
    assert.notEqual(n.file, r.file, duplicates);
    assert.ok(m.list.includes(r), duplicates);
  }
  m.update({ duplicates: 'replace' });
  await fs.writeFile(r.file, 'old');
  const z = m.add({ url });
  await finished(z);
  assert.ok(!m.list.includes(r));
  assert.equal(z.file, r.file);
  assert.equal(sha(await fs.readFile(z.file)), sha(data));
  for (const y of [...m.list]) await m.act(y.id, 'remove');
});

// The Download File Info window: downloads from the extension wait as 'pending' until it answers.
test('pending: never started; the probe reads name and size with a one-byte range', async () => {
  ranges.length = 0;
  const d = mgr.add({ url: base + '/file.bin', confirm: true });
  assert.equal(d.status, 'pending');
  await until(() => d.probe);
  mgr.pump();
  await sleep(50);
  assert.equal(d.status, 'pending');
  assert.ok(!mgr.running.has(d.id));
  assert.deepEqual(ranges, ['bytes=0-0']);
  const { duplicate, ...probe } = d.probe; // earlier tests downloaded the same link: see the duplicates test
  assert.deepEqual(probe, { name: 'file.bin', category: 'Other', size: data.length, type: null, error: null });
  assert.equal(duplicate.status, 'done');
  assert.equal(d.file, undefined, 'no file before the window answers');
  assert.deepEqual(mgr.view().downloads.find(x => x.id === d.id).probe, d.probe);
  await mgr.confirm(d.id, { action: 'cancel' });
});

test('pending probe: Content-Disposition name, Content-Length size and type; a failure keeps its error', async () => {
  const a = mgr.add({ url: base + '/dispo', confirm: true });
  await until(() => a.probe);
  assert.deepEqual(a.probe, { name: 'Q3 report.pdf', category: 'Documents', size: 10, type: 'application/pdf', error: null });
  const b = mgr.add({ url: base + '/denied/secret.zip', confirm: true });
  await until(() => b.probe);
  assert.deepEqual(b.probe, { name: 'secret.zip', category: 'Compressed', size: null, type: null, error: 'HTTP 404' });
  const c = mgr.add({ url: 'http://127.0.0.1:1/x/notes.txt', confirm: true }); // connection refused
  await until(() => c.probe);
  assert.equal(c.probe.name, 'notes.txt');
  assert.ok(c.probe.error, 'keeps the network error');
  for (const d of [a, b, c]) await mgr.confirm(d.id, { action: 'cancel' });
});

// YouTube's pending probe runs yt-dlp: ytdlp.test.js covers it with the fake
test('pending probe: HLS names come from the link', async () => {
  const h = mgr.add({ url: base + '/live/stream.m3u8', confirm: true });
  await until(() => h.probe);
  assert.deepEqual([h.probe.name, h.probe.category], ['stream.ts', 'Video']);
  await mgr.confirm(h.id, { action: 'cancel' });
});

test('confirm start: the chosen path is used as it is, its folders are created', async () => {
  const d = mgr.add({ url: base + '/file.bin', confirm: true });
  const file = path.join(dir, 'picked', 'deep', 'My file.bin');
  await mgr.confirm(d.id, { action: 'start', path: file });
  assert.equal(d.file, file);
  assert.equal(d.name, 'My file.bin');
  assert.equal(d.tmp, file + '.fdl');
  assert.equal(sha(await finished(d)), sha(data));
  assert.equal(d.file, file);
});

test('confirm start: HLS keeps the chosen path too', async () => {
  const d = mgr.add({ url: base + '/master.m3u8', title: 'My Video', confirm: true });
  const file = path.join(dir, 'picked', 'clip.ts');
  await mgr.confirm(d.id, { action: 'start', path: file });
  assert.equal(sha(await finished(d)), sha(Buffer.concat(parts)));
  assert.equal(d.file, file);
});

test('confirm: taken names are numbered, file names are cleaned, bad paths are refused', async () => {
  const folder = path.join(dir, 'taken');
  await fs.mkdir(folder, { recursive: true });
  await fs.writeFile(path.join(folder, 'a.zip'), 'x');
  await fs.writeFile(path.join(folder, 'a (1).zip.fdl'), 'x');
  const d = mgr.add({ url: base + '/file.bin', confirm: true });
  for (const bad of [undefined, '', 'a.zip', 'folder\\a.zip', '\\a.zip', folder + '\\']) {
    await assert.rejects(() => mgr.confirm(d.id, { action: 'later', path: bad }), /Save As needs a full path/, String(bad));
  }
  await assert.rejects(() => mgr.confirm(d.id, { action: 'go', path: path.join(folder, 'a.zip') }), /unknown action/);
  assert.equal(d.status, 'pending');
  await mgr.confirm(d.id, { action: 'later', path: path.join(folder, 'a.zip') });
  assert.equal(d.file, path.join(folder, 'a (2).zip'));
  const e = mgr.add({ url: base + '/file.bin', confirm: true });
  await mgr.confirm(e.id, { action: 'later', path: path.join(folder, 'a (2).zip') }); // another download's name
  assert.equal(e.name, 'a (2) (1).zip');
  const f = mgr.add({ url: base + '/file.bin', confirm: true });
  await mgr.confirm(f.id, { action: 'later', path: path.join(folder, 'CON.txt') });
  assert.equal(f.name, '_CON.txt');
  for (const x of [d, e, f]) await mgr.act(x.id, 'remove');
});

test('confirm later waits in the main queue, cancel removes, and only a pending download can be confirmed', async () => {
  const d = mgr.add({ url: base + '/file.bin', confirm: true });
  await mgr.confirm(d.id, { action: 'later', path: path.join(dir, 'later', 'f.bin') });
  assert.deepEqual([d.status, d.queue], ['scheduled', 'main']);
  assert.equal(d.probe, undefined);
  await sleep(50);
  assert.ok(!mgr.running.has(d.id), 'later must not start it');
  await assert.rejects(() => mgr.confirm(d.id, { action: 'start', path: path.join(dir, 'f.bin') }), /no longer waiting/);
  const c = mgr.add({ url: base + '/file.bin', confirm: true });
  await mgr.confirm(c.id, { action: 'cancel' });
  assert.ok(!mgr.list.includes(c));
  await assert.rejects(() => mgr.confirm(c.id, { action: 'cancel' }), /no longer waiting/);
  await assert.rejects(() => mgr.confirm('nope', { action: 'cancel' }), /no longer waiting/);
  await mgr.act(d.id, 'remove');
});


test('types: the five built-ins by default, in sidebar order, and view() follows the settings', () => {
  const m = new Manager(path.join(dir, 'types-default.json')); // never loaded: no timer
  assert.deepEqual(Object.keys(m.settings.types), ['Video', 'Music', 'Programs', 'Documents', 'Compressed']);
  assert.deepEqual(m.settings.types, CATEGORIES);
  m.update({ types: { Video: ['mp4'], Books: ['epub', 'mobi'] } });
  const v = m.view();
  assert.deepEqual(v.categories, m.settings.types);
  assert.deepEqual(Object.keys(v.categories), ['Video', 'Music', 'Programs', 'Documents', 'Compressed', 'Books']);
  assert.deepEqual(v.exts, Object.values(m.settings.types).flat());
  assert.ok(v.exts.includes('mobi') && !v.exts.includes('mkv'));
});

test('types: built-ins keep their place and names, types can change, a type moves to the later category', () => {
  const m = new Manager(path.join(dir, 'types-edit.json'));
  m.update({ types: { Compressed: ['zip', 'cbz'], Video: ['mp4', 'mkv', 'mp4'], Docs: ['pdf'], Music: 'not a list' } });
  const t = m.settings.types;
  assert.deepEqual(Object.keys(t), ['Video', 'Music', 'Programs', 'Documents', 'Compressed', 'Docs']);
  assert.deepEqual(t.Video, ['mp4', 'mkv']); // doubled type kept once
  assert.deepEqual(t.Compressed, ['zip', 'cbz']);
  assert.deepEqual(t.Music, CATEGORIES.Music); // a wrong list keeps the old one; a missing built-in too (Programs)
  assert.deepEqual(t.Programs, CATEGORIES.Programs);
  assert.deepEqual(t.Docs, ['pdf']);
  assert.ok(!t.Documents.includes('pdf'), 'Docs came later in the POST: it took pdf');
  m.update({ types: { ...t, Video: [...t.Video, 'pdf'] } }); // Video is earlier than Docs: Docs keeps it
  assert.deepEqual(m.settings.types.Docs, ['pdf']);
  assert.ok(!m.settings.types.Video.includes('pdf'));
  m.update({ types: { Docs: ['pdf'], Documents: ['pdf', 'txt'] } }); // now Documents is the later one
  assert.deepEqual(m.settings.types.Documents, ['pdf', 'txt']);
  assert.deepEqual(m.settings.types.Docs, []);
  const z = { id: 'z' };
  m.update({ dir: path.join(dir, 'types-out'), categories: true });
  m.assignName(z, 'comic.cbz'); // assignName uses the new types
  assert.equal(z.file, path.join(dir, 'types-out', 'Compressed', 'comic.cbz'));
});

test('types: invalid category names and file types are rejected', () => {
  const m = new Manager(path.join(dir, 'types-bad.json'));
  const before = structuredClone(m.settings.types);
  for (const bad of [null, 'Video', ['Video'], 5]) {
    m.update({ types: bad });
    assert.deepEqual(m.settings.types, before, `types: ${JSON.stringify(bad)}`);
  }
  m.update({ types: {
    Other: ['a'], all: ['b'], video: ['c'], '': ['d'], ' Pad': ['e'], ['x'.repeat(31)]: ['f'], 'Bad/name': ['g'], 'Ok name_1-2': ['h'],
    'OK NAME_1-2': ['i'], Fine: ['.mp3', 'MP3', 'toolongtype1', 'ok1', 'a b', 7, 'é'],
  } });
  assert.deepEqual(Object.keys(m.settings.types).slice(5), ['Ok name_1-2', 'Fine']);
  assert.deepEqual(m.settings.types['Ok name_1-2'], ['h']);
  assert.deepEqual(m.settings.types.Fine, ['ok1']);
  assert.deepEqual(m.settings.types.Music, CATEGORIES.Music);
});

test('types: custom categories are added, renamed and removed with their folder; their downloads become Other', async () => {
  const file = path.join(dir, 'types-custom.json');
  const m = new Manager(file);
  m.update({ dir: path.join(dir, 'types-c'), categories: true, types: { ...CATEGORIES, Books: ['epub', 'mobi'], Fonts: ['ttf'] } });
  assert.deepEqual(m.settings.types.Documents, CATEGORIES.Documents.filter(e => e !== 'epub'));
  const b = { id: 'b' };
  m.assignName(b, 'novel.mobi');
  assert.equal(b.file, path.join(dir, 'types-c', 'Books', 'novel.mobi'));
  const pending = { id: 'p', status: 'pending', probe: { name: 'novel.mobi', category: 'Books' } };
  m.list.push(pending);
  m.update({ folders: { Books: path.join(dir, 'shelf'), Fonts: path.join(dir, 'fonts') } });
  assert.deepEqual(Object.keys(m.settings.folders), ['Books', 'Fonts']);

  // rename: the new name at the old one's place
  m.update({ types: { ...CATEGORIES, Novels: ['epub', 'mobi'], Fonts: ['ttf'] } });
  assert.deepEqual(Object.keys(m.settings.types).slice(5), ['Novels', 'Fonts']);
  assert.deepEqual(m.settings.folders, { Novels: path.join(dir, 'shelf'), Fonts: path.join(dir, 'fonts') });
  assert.equal(pending.probe.category, 'Novels');
  const n = { id: 'n' };
  m.assignName(n, 'next.mobi');
  assert.equal(n.file, path.join(dir, 'shelf', 'next.mobi'));

  // remove: its folder goes, its file types and downloads are Other
  m.update({ types: { ...CATEGORIES, Fonts: ['ttf'] } });
  assert.deepEqual(Object.keys(m.settings.types).slice(5), ['Fonts']);
  assert.deepEqual(m.settings.folders, { Fonts: path.join(dir, 'fonts') });
  assert.equal(pending.probe.category, 'Other');
  assert.ok(!m.view().exts.includes('mobi'));
  const o = { id: 'o' };
  m.assignName(o, 'last.mobi');
  assert.equal(o.file, path.join(dir, 'types-c', 'last.mobi'));
  m.list = [];

  // a loaded state.json goes through the same checks
  await m.save();
  const saved = JSON.parse(await fs.readFile(file, 'utf8'));
  saved.settings.types = { Fonts: ['ttf', 'Bad'], Video: ['mp4'], Other: ['x'], fonts: ['otf'] };
  saved.settings.folders = { ...saved.settings.folders, Gone: path.join(dir, 'gone') };
  await fs.writeFile(file, JSON.stringify(saved));
  const m2 = new Manager(file);
  await m2.load();
  assert.deepEqual(m2.settings.types, { ...CATEGORIES, Video: ['mp4'], Fonts: ['ttf'] });
  assert.deepEqual(m2.settings.folders, { Fonts: path.join(dir, 'fonts') });
  await fs.writeFile(file, JSON.stringify({ ...saved, settings: { ...saved.settings, types: 'broken' } }));
  const m3 = new Manager(file);
  await m3.load();
  assert.deepEqual(m3.settings.types, CATEGORIES);
});

test('confirm remember: the folder is kept for its category and used for that category\'s next downloads', async () => {
  const file = path.join(dir, 'remember.json');
  const m = new Manager(file); // never loaded: no timer
  m.update({ dir: path.join(dir, 'out'), categories: true });
  assert.deepEqual(m.settings.folders, {});
  const a = m.add({ url: base + '/file.bin', confirm: true });
  await m.confirm(a.id, { action: 'later', path: path.join(dir, 'films', 'a.mp4'), remember: 'Video' });
  assert.deepEqual(m.settings.folders, { Video: path.join(dir, 'films') });
  const b = m.add({ url: base + '/file.bin', confirm: true });
  await m.confirm(b.id, { action: 'start', path: path.join(dir, 'tunes', 'b.mp3'), remember: 'Music' });
  assert.equal(m.settings.folders.Music, path.join(dir, 'tunes'));
  for (const bad of ['Nope', 'toString', ['Video'], 1]) {
    const c = m.add({ url: base + '/file.bin', confirm: true });
    await m.confirm(c.id, { action: 'later', path: path.join(dir, 'elsewhere', 'c.mp4'), remember: bad });
  }
  assert.deepEqual(Object.keys(m.settings.folders), ['Video', 'Music']);
  const c = m.add({ url: base + '/file.bin', confirm: true });
  await m.confirm(c.id, { action: 'cancel', remember: 'Programs' });
  assert.equal(m.settings.folders.Programs, undefined);

  const v = { id: 'v' }, z = { id: 'z' }, o = { id: 'o' };
  m.assignName(v, 'clip.mp4'); // remembered: wins over <dir>\\Video
  m.assignName(z, 'pack.zip'); // not remembered: the usual rule
  m.assignName(o, 'notes.xyz');
  assert.equal(v.file, path.join(dir, 'films', 'clip.mp4'));
  assert.equal(z.file, path.join(dir, 'out', 'Compressed', 'pack.zip'));
  assert.equal(o.file, path.join(dir, 'out', 'notes.xyz'));
  m.update({ categories: false });
  const v2 = { id: 'v2' };
  m.assignName(v2, 'clip.mp4'); // also with "Sort into folders" off
  assert.equal(v2.file, path.join(dir, 'films', 'clip.mp4'));

  await until(() => !m.running.size);
  await m.save();
  const m2 = new Manager(file);
  await m2.load();
  assert.deepEqual(m2.settings.folders, { Video: path.join(dir, 'films'), Music: path.join(dir, 'tunes') });
  const saved = JSON.parse(await fs.readFile(file, 'utf8'));
  saved.settings.folders = { Video: 'relative\\x', Music: 5, Nope: path.join(dir, 'n'), Other: path.join(dir, 'misc') };
  await fs.writeFile(file, JSON.stringify(saved));
  const m3 = new Manager(file);
  await m3.load();
  assert.deepEqual(m3.settings.folders, { Other: path.join(dir, 'misc') });
  await fs.writeFile(file, JSON.stringify({ ...saved, settings: { ...saved.settings, folders: 'C:\\x' } }));
  const m4 = new Manager(file);
  await m4.load();
  assert.deepEqual(m4.settings.folders, {});
  for (const x of [...m.list]) await m.act(x.id, 'remove');
});


test('pending downloads are never saved, are dropped on load, and expire after 30 minutes', async () => {
  const file = path.join(dir, 'pending.json');
  const m = new Manager(file); // never loaded: no timer, these tests tick by hand
  m.update({ dir: path.join(dir, 'out') });
  const p = m.add({ url: base + '/file.bin', confirm: true }), kept = m.add({ url: base + '/file.bin', scheduled: true });
  await m.save();
  assert.deepEqual(JSON.parse(await fs.readFile(file, 'utf8')).list.map(d => d.id), [kept.id]);
  m.saveSync();
  assert.deepEqual(JSON.parse(await fs.readFile(file, 'utf8')).list.map(d => d.id), [kept.id]);

  await fs.writeFile(file, JSON.stringify({ settings: {}, list: [{ ...kept, id: 'old', status: 'pending' }, kept] }));
  const m2 = new Manager(file);
  await m2.load();
  assert.deepEqual(m2.list.map(d => d.id), [kept.id]);

  m.tick();
  assert.ok(m.list.includes(p), 'not 30 minutes old yet');
  p.added -= m.pendingMs;
  m.tick();
  assert.deepEqual(m.list, [kept]);
});

// Queues: never-loaded Managers (no 1 s timer) ticked by hand with their own clock. 2026-10-18 is a Sunday (day 0).
const clock = (hm, day) => new Date(`2026-10-${18 + day}T${hm}:30`);
const slow = (m, queue) => { const d = m.add({ url: base + '/slow.bin', scheduled: true, queue }); d.limitKB = 1024; return d; }; // runs for seconds
const qview = (m, id) => m.view().queues.find(q => q.id === id);
const removeAll = async m => { for (const x of [...m.list]) await m.act(x.id, 'remove'); };

test('queues: the main one comes first and keeps its name; fields are checked; names are unique; remove sends downloads to the main queue', async () => {
  const m = new Manager(path.join(dir, 'queues.json'));
  m.update({ dir: path.join(dir, 'out') });
  const fields = { maxActive: 1, startAt: '', days: [], stopAt: '', retries: 0, whenDone: '' };
  assert.deepEqual(m.settings.queues, [{ id: 'main', name: 'Main download queue', ...fields }]);
  const { id } = m.queues({ op: 'add', name: '  Night ' });
  assert.ok(id && id !== 'main', 'the engine makes the id');
  assert.equal(m.queue(id).name, 'Night');
  for (const name of ['night', 'MAIN download queue', '', ' \n ', 'x'.repeat(41), undefined, 5]) {
    assert.throws(() => m.queues({ op: 'add', name }), /already a queue|1 to 40/, String(name));
  }
  m.queues({ op: 'update', id, maxActive: 50, startAt: '23:30', days: [6, 1, 1, 9, '2', 0], stopAt: '24:00', retries: 3, whenDone: 'shutdown' });
  assert.deepEqual(m.queue(id), { id, name: 'Night', maxActive: 20, startAt: '23:30', days: [0, 1, 6], stopAt: '', retries: 3, whenDone: 'shutdown' });
  m.queues({ op: 'update', id, maxActive: 'x', retries: 99, whenDone: 'reboot', name: 'Late night' });
  assert.deepEqual(m.queue(id), { id, name: 'Late night', maxActive: 1, startAt: '23:30', days: [0, 1, 6], stopAt: '', retries: 10, whenDone: '' }, 'unknown values fall back, the others stay');
  assert.throws(() => m.queues({ op: 'update', id, name: 'main download queue' }), /already a queue/);
  assert.throws(() => m.queues({ op: 'update', id: 'main', name: 'Mine' }), /keeps its name/);
  m.queues({ op: 'update', id: 'main', name: 'Main download queue', maxActive: 2 });
  assert.equal(m.queue('main').maxActive, 2);
  assert.throws(() => m.queues({ op: 'remove', id: 'main' }), /cannot be removed/);
  assert.throws(() => m.queues({ op: 'start', id: 'nope' }), /queue not found/);
  assert.throws(() => m.queues({ op: 'fly', id }), /unknown queue action/);
  m.update({ queues: [] });
  assert.deepEqual(m.settings.queues.map(q => q.id), ['main', id], 'settings cannot replace the queues');

  const a = m.add({ url: base + '/file.bin', scheduled: true, queue: id }), b = m.add({ url: base + '/file.bin', scheduled: true });
  assert.deepEqual([a.status, a.queue, b.status, b.queue], ['scheduled', id, 'scheduled', 'main']);
  assert.throws(() => m.add({ url: base + '/file.bin', scheduled: true, queue: 'nope' }), /queue not found/);
  assert.deepEqual(qview(m, id), { ...m.queue(id), running: false, waiting: 1, active: 0, total: 1 });
  assert.equal(m.view().downloads.find(x => x.id === a.id).queue, id);
  m.queues({ op: 'remove', id });
  assert.deepEqual([a.queue, a.status], ['main', 'scheduled']);
  assert.deepEqual(m.settings.queues.map(q => q.id), ['main']);
  assert.equal(qview(m, 'main').total, 2);
  await removeAll(m);
});

test('queues: start at HH:MM on its days only, once a day; stop at HH:MM or by hand puts its downloads back to scheduled', async () => {
  const m = new Manager(path.join(dir, 'clock.json'));
  m.update({ dir: path.join(dir, 'out'), maxActive: 5 });
  const { id } = m.queues({ op: 'add', name: 'Night', startAt: '02:00', days: [1, 3], stopAt: '06:00', maxActive: 2 });
  const a = slow(m, id), b = slow(m, id), other = slow(m, 'main');
  const st = () => [a.status, b.status];
  m.tick(clock('02:00', 0)); // Sunday: not one of its days
  m.tick(clock('01:59', 1));
  assert.deepEqual(st(), ['scheduled', 'scheduled']);
  assert.equal(qview(m, id).running, false);
  m.tick(clock('02:00', 1)); // Monday
  assert.deepEqual(st(), ['downloading', 'downloading']);
  assert.deepEqual([qview(m, id).running, qview(m, id).active, other.status], [true, 2, 'scheduled']);
  m.queues({ op: 'stop', id }); // by hand, in that same minute
  assert.deepEqual(st(), ['scheduled', 'scheduled']);
  m.tick(clock('02:00', 1));
  assert.deepEqual(st(), ['scheduled', 'scheduled'], 'once a day');
  await until(() => !m.running.size);
  m.tick(clock('02:00', 3)); // Wednesday
  assert.deepEqual(st(), ['downloading', 'downloading']);
  m.tick(clock('06:00', 3));
  assert.deepEqual(st(), ['scheduled', 'scheduled']);
  assert.equal(qview(m, id).running, false);
  await until(() => !m.running.size);
  m.queues({ op: 'start', id }); // by hand, at any time
  assert.deepEqual(st(), ['downloading', 'downloading']);
  m.tick(clock('06:00', 3));
  assert.deepEqual(st(), ['downloading', 'downloading'], 'the stop time fired already today');
  await removeAll(m);
});

test('queues: at most the queue\'s maxActive of its downloads run, within the global maxActive; Resume is not held back by a queue that is not running', async () => {
  const m = new Manager(path.join(dir, 'active.json'));
  m.update({ dir: path.join(dir, 'out'), maxActive: 2 });
  const { id } = m.queues({ op: 'add', name: 'Two', maxActive: 2 });
  const a = [slow(m, id), slow(m, id), slow(m, id)], x = [slow(m, 'main'), slow(m, 'main')];
  m.queues({ op: 'start', id });
  m.queues({ op: 'start', id: 'main' });
  assert.deepEqual([...a, ...x].map(d => d.status), ['downloading', 'downloading', 'queued', 'queued', 'queued'], 'the global limit');
  m.update({ maxActive: 10 });
  assert.deepEqual([...a, ...x].map(d => d.status), ['downloading', 'downloading', 'queued', 'downloading', 'queued'], 'each queue\'s own limit');
  assert.deepEqual(qview(m, id), { ...m.queue(id), running: true, waiting: 1, active: 2, total: 3 });

  const { id: idle } = m.queues({ op: 'add', name: 'Idle' }); // maxActive 1, not running
  const h = [slow(m, idle), slow(m, idle)];
  for (const d of h) await m.act(d.id, 'resume');
  assert.deepEqual(h.map(d => d.status), ['downloading', 'downloading']);
  await removeAll(m);
});

test('queues: the order is the list\'s; up and down swap within the queue; downloads move into and out of queues', async () => {
  const m = new Manager(path.join(dir, 'order.json'));
  m.update({ dir: path.join(dir, 'out') });
  const { id } = m.queues({ op: 'add', name: 'Other' });
  const a = m.add({ url: base + '/file.bin', scheduled: true }), b = m.add({ url: base + '/file.bin', scheduled: true, queue: id });
  const c = m.add({ url: base + '/file.bin', scheduled: true }), e = m.add({ url: base + '/file.bin', scheduled: true });
  const order = q => m.list.filter(d => d.queue === q);
  await m.act(c.id, 'up');
  assert.deepEqual(order('main'), [c, a, e]);
  assert.deepEqual(m.list, [c, b, a, e], 'other downloads keep their place');
  await m.act(c.id, 'up'); // the first stays first
  await m.act(b.id, 'down'); // the only one in its queue
  assert.deepEqual(m.list, [c, b, a, e]);
  await m.act(c.id, 'down');
  await m.act(e.id, 'down');
  assert.deepEqual(order('main'), [a, c, e]);
  a.status = 'done'; // finished, still in its queue but hidden there: moves skip it
  await m.act(c.id, 'up');
  assert.deepEqual(order('main'), [a, c, e], 'c is already the first one waiting');
  await m.act(e.id, 'up');
  assert.deepEqual(order('main'), [a, e, c]);
  await assert.rejects(() => m.act(a.id, 'down'), /finished/);
  a.status = 'scheduled';

  await m.act(b.id, 'queue', undefined, { queue: 'main' });
  assert.deepEqual([b.queue, b.status], ['main', 'scheduled']);
  await m.act(c.id, 'queue', undefined, { queue: null });
  assert.deepEqual([c.queue, c.status], [null, 'paused'], 'out of every queue');
  await assert.rejects(() => m.act(c.id, 'up'), /not in a queue/);
  await m.act(c.id, 'queue', undefined, { queue: id });
  assert.deepEqual([c.queue, c.status], [id, 'scheduled'], 'a paused one waits in the queue');
  await assert.rejects(() => m.act(c.id, 'queue', undefined, { queue: 'nope' }), /queue not found/);
  await removeAll(m);
});

test('queues: while a queue runs, a failed download is tried again after 10 s, up to its retries', async () => {
  const m = new Manager(path.join(dir, 'retry.json'));
  m.update({ dir: path.join(dir, 'out') });
  const { id } = m.queues({ op: 'add', name: 'Retry', retries: 2 });
  const d = m.add({ url: base + '/denied/x.zip', scheduled: true, queue: id }); // 404: fails at once
  m.queues({ op: 'start', id });
  const tries = [];
  for (let i = 0; i < 2; i++) {
    await until(() => d.status === 'error' && !m.running.size);
    tries.push(d.lastTry);
    m.tick(new Date(Date.now() + 9000));
    assert.equal(d.status, 'error', 'not before 10 s');
    assert.equal(qview(m, id).running, true, 'a retry still to come keeps the queue running');
    m.tick(new Date(Date.now() + 10001));
    assert.notEqual(d.status, 'error', 'tried again');
  }
  await until(() => d.status === 'error' && !m.running.size);
  tries.push(d.lastTry);
  m.tick(new Date(Date.now() + 10001));
  assert.equal(d.status, 'error', 'two retries, then it stays failed');
  assert.equal(new Set(tries).size, 3);
  assert.equal(qview(m, id).running, false, 'nothing left: the queue stopped');

  const z = m.add({ url: base + '/denied/z.zip', scheduled: true }); // the main queue tries nothing again
  m.queues({ op: 'start', id: 'main' });
  await until(() => z.status === 'error' && !m.running.size);
  m.tick(new Date(Date.now() + 10001));
  assert.equal(z.status, 'error');
  await m.act(d.id, 'resume'); // by hand, its queue not running: no retry
  await until(() => d.status === 'error' && !m.running.size);
  m.tick(new Date(Date.now() + 10001));
  assert.equal(d.status, 'error');
  await removeAll(m);
});

test("queues: a done queue stops and its whenDone starts the countdown, which new work does not stop; Cancel does", () => withPower(async (m, ops) => {
  const { id } = m.queues({ op: 'add', name: 'Then sleep', whenDone: 'sleep' });
  m.queues({ op: 'start', id });
  m.tick();
  assert.equal(qview(m, id).running, false, 'an empty queue stops at once');
  assert.equal(m.view().powerIn, null, 'and does nothing');
  const a = m.add({ url: base + '/file.bin', scheduled: true, queue: id });
  m.queues({ op: 'start', id });
  await finished(a);
  m.tick();
  assert.equal(qview(m, id).running, false);
  assert.equal(m.view().powerOp, 'sleep');
  const b = m.add({ url: base + '/slow.bin' }); // new work
  b.limitKB = 1024;
  m.tick();
  assert.ok(m.view().powerIn > 0, 'new work does not stop it');
  await sleep(250);
  m.tick();
  assert.deepEqual(ops, ['sleep']);
  assert.equal(m.view().powerIn, null);

  const c = m.add({ url: base + '/file.bin', scheduled: true, queue: id });
  m.queues({ op: 'start', id });
  await finished(c);
  m.tick();
  assert.ok(m.view().powerIn > 0);
  m.update({ whenDone: '' }); // the strip's Cancel
  await sleep(250);
  m.tick();
  assert.deepEqual(ops, ['sleep']);
  await removeAll(m);
}));

test('queues: Later goes into the chosen queue and can start it; Pause all stops running queues', async () => {
  const m = new Manager(path.join(dir, 'later.json'));
  m.update({ dir: path.join(dir, 'out'), maxActive: 5 });
  const { id } = m.queues({ op: 'add', name: 'Later' });
  const p = m.add({ url: base + '/slow.bin', confirm: true });
  p.limitKB = 1024;
  await assert.rejects(() => m.confirm(p.id, { action: 'later', path: path.join(dir, 'later2', 'p.bin'), queue: 'nope' }), /queue not found/);
  assert.equal(p.status, 'pending');
  await m.confirm(p.id, { action: 'later', path: path.join(dir, 'later2', 'p.bin'), queue: id, startQueue: true });
  assert.deepEqual([p.queue, p.status, qview(m, id).running], [id, 'downloading', true]);
  const s = m.add({ url: base + '/file.bin', confirm: true });
  await m.confirm(s.id, { action: 'start', path: path.join(dir, 'later2', 's.bin'), queue: id });
  assert.equal(s.queue, null, 'Start Download puts it in no queue');
  await finished(s);

  const free = m.add({ url: base + '/slow.bin' });
  free.limitKB = 1024;
  const later = slow(m, 'main');
  m.pauseAll();
  assert.deepEqual([p.status, free.status, later.status, qview(m, id).running], ['scheduled', 'paused', 'scheduled', false]);
  await until(() => !m.running.size);
  assert.deepEqual([p.status, free.status], ['scheduled', 'paused'], 'nothing started again');
  await removeAll(m);
});

test('queues: migration of the old start time and scheduled downloads; running is not saved', async () => {
  const file = path.join(dir, 'migrate.json');
  const dl = (id, status, queue) => ({ id, url: base + '/slow.bin', name: null, title: null, headers: {}, cookieHost: '127.0.0.1', kind: 'http', status, added: 1, limitKB: 1024, ...(queue && { queue }) });
  await fs.writeFile(file, JSON.stringify({
    settings: { dir: path.join(dir, 'out'), scheduleAt: '02:30', queues: [{ id: 'main', name: 'Mine', maxActive: 3 }, { id: 'n', name: 'Night', days: [1] }, { id: 'n', name: 'Again' }, { id: 'x', name: 'NIGHT' }, { name: 'No id' }, { id: 'e', name: ' ' }, null] },
    list: [dl('s', 'scheduled'), dl('q', 'downloading', 'n'), dl('g', 'paused', 'gone')],
  }));
  const m = new Manager(file);
  await m.load();
  assert.equal(m.settings.scheduleAt, undefined);
  assert.deepEqual(m.settings.queues, [
    { id: 'main', name: 'Main download queue', maxActive: 3, startAt: '02:30', days: [], stopAt: '', retries: 0, whenDone: '' },
    { id: 'n', name: 'Night', maxActive: 1, startAt: '', days: [1], stopAt: '', retries: 0, whenDone: '' },
  ]);
  const st = id => m.list.find(d => d.id === id);
  assert.deepEqual([st('s').queue, st('s').status], ['main', 'scheduled'], 'an old scheduled download waits in the main queue');
  assert.deepEqual([st('q').queue, st('q').status], ['n', 'scheduled'], 'no queue runs after a restart');
  assert.deepEqual([st('g').queue, st('g').status], ['main', 'paused']);
  assert.ok(!m.running.size);

  m.queues({ op: 'start', id: 'n' });
  assert.equal(st('q').status, 'downloading');
  assert.ok(!('running' in JSON.parse(m.json()).settings.queues[1]));
  m.saveSync();
  const m2 = new Manager(file);
  await m2.load();
  assert.ok(m2.view().queues.every(q => !q.running));
  assert.equal(m2.list.find(d => d.id === 'q').status, 'scheduled');
  assert.ok(!m2.running.size);
  await removeAll(m);
});

test('file names cannot escape the folder', () => {
  assert.equal(sanitize('../../a:b?.exe'), '_.._a_b_.exe');
  assert.equal(sanitize('CON.txt'), '_CON.txt');
  assert.equal(sanitize('...'), 'download');
});
