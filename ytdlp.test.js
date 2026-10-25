import { test, before, after } from 'node:test';
import assert from 'node:assert/strict';
import fs from 'node:fs/promises';
import { existsSync } from 'node:fs';
import os from 'node:os';
import path from 'node:path';
import http from 'node:http';
import { fileURLToPath } from 'node:url';
import { Manager } from './engine.js';
import { ytdlpArgs, youtubeId, formatOptions, findYtdlp } from './ytdlp.js';

// Stands in for googlevideo.com: answers the one-byte range request for formats that have no exact filesize
const ranges = [];
const media = http.createServer((req, res) => {
  ranges.push({ url: req.url, range: req.headers.range, ua: req.headers['user-agent'], accept: req.headers.accept });
  const total = { '/approx/134': 51234, '/approx/271': 700000 }[req.url];
  if (!total) return res.writeHead(404).end();
  res.writeHead(206, { 'content-range': `bytes 0-0/${total}`, 'content-length': 1 }).end('x');
});

// fixtures/fake-ytdlp.mjs stands in for yt-dlp: no network
const FAKE = [process.execPath, path.join(path.dirname(fileURLToPath(import.meta.url)), 'fixtures', 'fake-ytdlp.mjs')];
const sleep = ms => new Promise(r => setTimeout(r, ms));
const bytes = (fid, from, n) => Buffer.from(Array.from({ length: n }, (_, i) => (from + i + fid.charCodeAt(0)) % 251));
const alive = pid => { try { process.kill(pid, 0); return true; } catch { return false; } };

let dir, mgr, logFile;
before(async () => {
  dir = await fs.mkdtemp(path.join(os.tmpdir(), 'fastdl-yt-'));
  logFile = process.env.FAKE_YTDLP_LOG = path.join(dir, 'fake.log');
  await new Promise(r => media.listen(0, '127.0.0.1', r));
  process.env.FAKE_YTDLP_BASE = `http://127.0.0.1:${media.address().port}`;
  mgr = new Manager(path.join(dir, 'state.json'));
  await mgr.load();
  mgr.update({ dir: path.join(dir, 'out'), maxActive: 5 });
  mgr.ytdlp = FAKE;
  mgr.ffmpeg = dir;
});
after(() => {
  media.close();
  return fs.rm(dir, { recursive: true, force: true, maxRetries: 5 });
});

async function until(check, what, ms = 15000) {
  for (const end = Date.now() + ms; !check();) {
    if (Date.now() > end) throw new Error('timed out waiting for ' + what);
    await sleep(10);
  }
}
const settled = d => until(() => !['queued', 'downloading'].includes(d.status), 'the download to stop');
const view = d => mgr.view().downloads.find(x => x.id === d.id);
const logLines = async () => (await fs.readFile(logFile, 'utf8').catch(() => '')).split('\n');

test("WinGet's yt-dlp comes before an older one another app put on PATH", async () => {
  const dir = await fs.mkdtemp(path.join(os.tmpdir(), 'glide-find-'));
  const env = { LOCALAPPDATA: process.env.LOCALAPPDATA, PATH: process.env.PATH, FASTDL_YTDLP: process.env.FASTDL_YTDLP };
  try {
    const links = path.join(dir, 'Microsoft', 'WinGet', 'Links'), other = path.join(dir, 'Parabolic');
    await fs.mkdir(links, { recursive: true });
    await fs.mkdir(other);
    const name = process.platform === 'win32' ? 'yt-dlp.exe' : 'yt-dlp';
    await fs.writeFile(path.join(links, 'yt-dlp.exe'), '');
    await fs.writeFile(path.join(other, name), '');
    delete process.env.FASTDL_YTDLP;
    process.env.LOCALAPPDATA = dir;
    process.env.PATH = other;
    assert.equal(findYtdlp(), path.join(links, 'yt-dlp.exe'));
    await fs.rm(links, { recursive: true });
    assert.equal(findYtdlp(), path.join(other, name), 'PATH when WinGet has none');
  } finally {
    for (const [k, v] of Object.entries(env)) v === undefined ? delete process.env[k] : process.env[k] = v;
    await fs.rm(dir, { recursive: true, force: true });
  }
});

test('YouTube links are normalized; channels and playlists are refused', () => {
  const m = new Manager(path.join(dir, 'unused.json'));
  for (const url of ['https://www.youtube.com/watch?v=jNQXAC9IVRw&list=PLx&t=3', 'http://youtube.com/shorts/jNQXAC9IVRw', 'https://m.youtube.com/live/jNQXAC9IVRw',
    'https://music.youtube.com/watch?v=jNQXAC9IVRw', 'https://www.youtube.com/embed/jNQXAC9IVRw', 'https://youtu.be/jNQXAC9IVRw?si=abc']) {
    const d = m.add({ url, scheduled: true, headers: { cookie: 'SID=1', referer: 'https://www.youtube.com/' } });
    assert.equal(d.url, 'https://www.youtube.com/watch?v=jNQXAC9IVRw', url);
    assert.equal(d.kind, 'video');
    assert.equal(d.quality, 'best');
    assert.deepEqual(d.headers, {}, 'no cookies stored for video downloads');
  }
  for (const url of ['https://www.youtube.com/@somechannel', 'https://www.youtube.com/playlist?list=PL123', 'https://www.youtube.com/channel/UC123',
    'https://www.youtube.com/', 'https://www.youtube.com/watch?v=tooShort', 'https://youtu.be/', 'https://m.youtube.com/shorts/']) {
    assert.throws(() => m.add({ url }), /^Error: Paste the link of one video .*not a channel or playlist$/, url);
  }
  assert.equal(m.add({ url: 'https://youtu.be/jNQXAC9IVRw', quality: 'mp3', scheduled: true }).quality, 'mp3');
  assert.throws(() => m.add({ url: 'https://youtu.be/jNQXAC9IVRw', quality: '4k' }), /unknown quality/);
  assert.equal(youtubeId(new URL('https://example.com/watch?v=jNQXAC9IVRw')), null);
  assert.equal(m.add({ url: 'https://example.com/a.bin', scheduled: true, quality: 'zzz' }).kind, 'http', 'quality is ignored for other sites');
});

test('format keys and legacy qualities → yt-dlp arguments', () => {
  const run = (quality, speedLimitKB = 0, format, limitKB, speedLimitOn = true) => ytdlpArgs({ quality, format, limitKB, tmpDir: 'T', url: 'https://www.youtube.com/watch?v=jNQXAC9IVRw' }, { speedLimitKB, speedLimitOn }, 'F');
  const has = (args, ...seq) => assert.ok(args.some((_, i) => seq.every((s, j) => args[i + j] === s)), `${seq.join(' ')} in ${args.join(' ')}`);
  const sel = ['-f', 'bv*+ba/b', '-S'];
  has(run('best'), ...sel, 'res,vcodec:h264,ext:mp4:m4a', '--merge-output-format', 'mp4');
  for (const q of ['1080', '720', '480']) has(run(q), ...sel, `res:${q},vcodec:h264,ext:mp4:m4a`, '--merge-output-format', 'mp4');
  const mp3 = ['-f', 'ba/b', '-x', '--audio-format', 'mp3', '--audio-quality', '192K']; // constant bitrate: the size shown is the size made
  has(run('mp3'), ...mp3);
  has(run('best', 0, 'mp3'), ...mp3);
  has(run('best', 0, 'mp4:137+140'), '-f', '137+140', '--merge-output-format', 'mp4');
  has(run('best', 0, 'webm:248+251'), '-f', '248+251', '--merge-output-format', 'webm');
  const m4a = run('mp3', 0, 'm4a:140-drc');
  has(m4a, '-f', '140-drc');
  assert.ok(!m4a.includes('-x') && !m4a.includes('--merge-output-format') && !m4a.includes('-S'));
  for (const bad of ['bv*+ba', 'mp4:137', 'mp4:137+140/b', 'mkv:1+2', 'm4a:140 -x', 'mp3:1']) assert.throws(() => run('best', 0, bad), /unknown format/, bad);
  const base = run('best');
  has(base, '--no-playlist', '--match-filter', '!is_live', '--newline', '--no-colors', '--ffmpeg-location', 'F', '-P', 'T', '-o', 'media.%(ext)s');
  assert.equal(base.at(-1), 'https://www.youtube.com/watch?v=jNQXAC9IVRw');
  assert.ok(!base.includes('--limit-rate'));
  has(run('best', 250), '--limit-rate', '250K');
  // the lower of the global limit (only when switched on) and the download's own
  has(run('best', 250, undefined, 100), '--limit-rate', '100K');
  has(run('best', 250, undefined, 400), '--limit-rate', '250K');
  has(run('best', 250, undefined, 400, false), '--limit-rate', '400K');
  assert.ok(!run('best', 250, undefined, 0, false).includes('--limit-rate'));
  assert.ok(!run('mp3', 5).some(a => /cookie/i.test(a)), 'never cookies');
  // fragments at once, as many as the connections setting; one while a limit is on (--limit-rate holds per fragment)
  const n = (settings, limitKB) => { const a = ytdlpArgs({ quality: 'best', limitKB, tmpDir: 'T', url: 'u' }, settings, 'F'); return a[a.indexOf('-N') + 1]; };
  assert.equal(n({ connections: 8 }), '8');
  assert.equal(n({ connections: 8, speedLimitOn: true, speedLimitKB: 250 }), '1');
  assert.equal(n({ connections: 8, speedLimitOn: false, speedLimitKB: 250 }), '8');
  assert.equal(n({ connections: 8 }, 100), '1');
});

test('format list of a recording YouTube is still processing (post_live): pieces, approximate sizes, no range requests', async () => {
  const f = (format_id, ext, vcodec, acodec, filesize_approx, more) => ({ format_id, ext, vcodec, acodec, filesize_approx, protocol: 'http_dash_segments', url: 'https://x/' + format_id, ...more });
  const v = (id, h, size, fps = 30) => f(id, 'mp4', 'avc1.4d401f', 'none', size, { width: h * 16 / 9, height: h, fps, dynamic_range: 'SDR' });
  const info = { live_status: 'post_live', duration: 3480, formats: [
    f('139', 'm4a', 'none', 'mp4a.40.5', 20880000), f('140', 'm4a', 'none', 'mp4a.40.2', 62640000),
    v('160', 144, 83617005), v('133', 240, 182376360), v('134', 360, 336094050), v('135', 480, 468688574),
    v('298', 720, 1147967175, 60), v('299', 1080, 2237727000, 60),
    f('sb0', 'mhtml', 'none', 'none', null, { protocol: 'mhtml' }),
  ] };
  const noRange = { get: async () => { throw new Error('no range requests for pieces'); } };
  assert.deepEqual(await formatOptions(info, noRange), [
    { key: 'mp4:299+140', group: 'MP4', label: '1080p60', tag: 'HD', approx: true, size: 2300367000 },
    { key: 'mp4:298+140', group: 'MP4', label: '720p60', tag: 'HD', approx: true, size: 1210607175 },
    { key: 'mp4:135+140', group: 'MP4', label: '480p', approx: true, size: 531328574 },
    { key: 'mp4:134+140', group: 'MP4', label: '360p', approx: true, size: 398734050 },
    { key: 'mp4:133+140', group: 'MP4', label: '240p', approx: true, size: 245016360 },
    { key: 'mp4:160+140', group: 'MP4', label: '144p', approx: true, size: 146257005 },
    { key: 'm4a:140', group: 'Audio', label: 'M4A', approx: true, size: 62640000 },
    { key: 'mp3', group: 'Audio', label: 'MP3', size: 83520000 }, // duration × 192 kbit/s, as for any video
  ]);
  assert.ok(OPTIONS.every(o => !('approx' in o)), 'normal videos: exact sizes');
});

test('video download: title, size and progress from yt-dlp, filed under Video, temp folder removed, Mark-of-the-Web', async () => {
  const d = mgr.add({ url: 'https://youtu.be/okVideo0001' });
  await settled(d);
  assert.equal(d.status, 'done', d.error);
  assert.equal(d.title, 'Fake vidéo_ okVideo0001');
  assert.equal(d.file, path.join(mgr.settings.dir, 'Video', 'Fake vidéo_ okVideo0001.mp4'));
  assert.deepEqual(await fs.readFile(d.file), Buffer.concat([bytes('v1', 0, 40000), bytes('a1', 0, 20000)]));
  const v = view(d);
  assert.equal(v.name, 'Fake vidéo_ okVideo0001.mp4');
  assert.equal(v.size, 60000);
  assert.equal(v.got, 60000);
  assert.equal(v.quality, 'best');
  assert.equal(v.segments, null);
  assert.ok(!existsSync(path.join(mgr.settings.dir, '.fastdl')), 'temp folders removed');
  if (process.platform === 'win32') {
    const zone = await fs.readFile(d.file + ':Zone.Identifier', 'utf8');
    assert.match(zone, /ZoneId=3\r\n/);
    assert.match(zone, /HostUrl=https:\/\/www\.youtube\.com\/watch\?v=okVideo0001\r\n/);
  }
  const again = mgr.add({ url: 'https://www.youtube.com/watch?v=okVideo0001' });
  await settled(again);
  assert.equal(path.basename(again.file), 'Fake vidéo_ okVideo0001 (1).mp4', 'names stay unique');
});

// The expected list for fixtures/ytdlp-formats.json
const OPTIONS = [
  { key: 'mp4:299+140', group: 'MP4', label: '1080p60', tag: 'HD', size: 530000 },
  { key: 'mp4:136+140', group: 'MP4', label: '720p', tag: 'HD', size: 230000 }, // H.264 over the later AV1 398
  { key: 'mp4:134+140', group: 'MP4', label: '360p', size: 81234 }, // approx-only 134: 51234 from the range answer
  { key: 'mp4:160+140', group: 'MP4', label: '144p', size: 40000 }, // H.264 over AV1 394; 108p and the m3u8 269 left out
  { key: 'webm:313+251', group: 'WEBM', label: '2160p', tag: '4K', size: 925000 }, // the HDR 337 left out
  { key: 'webm:271+251', group: 'WEBM', label: '1440p', tag: '2K', size: 725000 }, // approx-only 271: 700000 from the range answer
  { key: 'webm:303+251', group: 'WEBM', label: '1080p60', tag: 'HD', size: 425000 }, // no 360p: 243 has no size at all
  { key: 'webm:278+251', group: 'WEBM', label: '144p', size: 34000 },
  { key: 'm4a:140', group: 'Audio', label: 'M4A', size: 30000 },
  { key: 'mp3', group: 'Audio', label: 'MP3', size: 456000 }, // 19 s × 192 kbit/s
];
const lists = async id => (await logLines()).filter(l => l === 'list ' + id).length;

test('format list: real sizes only, H.264 first, labels and tags, one option per height', async () => {
  ranges.length = 0;
  assert.deepEqual(await mgr.formats('https://youtu.be/okList00001'), { title: 'Fake vidéo: okList00001', options: OPTIONS });
  // only the formats without filesize were asked, one byte each, with their own headers
  assert.deepEqual(ranges.map(r => r.url).sort(), ['/approx/134', '/approx/271', '/none/243']);
  assert.ok(ranges.every(r => r.range === 'bytes=0-0'));
  assert.deepEqual(ranges.find(r => r.url === '/approx/134').ua, 'Mozilla/5.0 (fake yt-dlp)');
  assert.deepEqual(ranges.find(r => r.url === '/approx/134').accept, '*/*');
});

test('format list: one yt-dlp run for callers at the same time, then cached for 15 minutes', async () => {
  const url = 'https://www.youtube.com/watch?v=okCache0001';
  const [a, b] = await Promise.all([mgr.formats(url), mgr.formats('https://youtu.be/okCache0001')]);
  assert.equal(a, b);
  assert.equal(await lists('okCache0001'), 1);
  assert.equal(await mgr.formats(url), a);
  assert.equal(await lists('okCache0001'), 1, 'cached');
  mgr.formatCache.get('okCache0001').at -= 15 * 60000;
  assert.deepEqual(await mgr.formats(url), a);
  assert.equal(await lists('okCache0001'), 2, 'fetched again once expired');
});

test('format list: yt-dlp errors come back as messages and are not cached; only single YouTube videos', async () => {
  await assert.rejects(mgr.formats('https://youtu.be/errListing1'), { message: "Private video. Sign in if you've been granted access to this video" });
  await assert.rejects(mgr.formats('https://youtu.be/errListing1'));
  assert.equal(await lists('errListing1'), 2);
  await assert.rejects(mgr.formats('https://youtu.be/liveListin1'), { message: 'Live streams are not supported' });
  await assert.rejects(mgr.formats('https://example.com/watch?v=okList00001'), /Only video links from YouTube and the other supported sites/);
  await assert.rejects(mgr.formats('https://www.youtube.com/playlist?list=PL1'), /not a channel or playlist/);
});

test('pending YouTube download: the name is yt-dlp\'s title with the chosen format\'s extension, the list goes to the window', async () => {
  const v = mgr.add({ url: 'https://youtu.be/okProbe0001', title: '(3) Tab title - YouTube', confirm: true });
  await until(() => v.probe, 'the first probe');
  await until(() => !v.probe.listing, 'the list');
  assert.deepEqual(v.probe, { name: 'Fake vidéo_ okProbe0001.mp4', category: 'Video', size: 530000, type: null, error: null, options: OPTIONS, format: 'mp4:299+140' });
  const a = mgr.add({ url: 'https://youtu.be/okProbe0001', format: 'm4a:140', confirm: true });
  await until(() => a.probe && !a.probe.listing, 'the list');
  assert.deepEqual([a.probe.name, a.probe.category, a.probe.size, a.probe.format], ['Fake vidéo_ okProbe0001.m4a', 'Music', 30000, 'm4a:140']);
  const m = mgr.add({ url: 'https://youtu.be/okProbe0001', quality: 'mp3', confirm: true }); // legacy quality
  await until(() => m.probe && !m.probe.listing, 'the list');
  assert.deepEqual([m.probe.name, m.probe.size, m.probe.format], ['Fake vidéo_ okProbe0001.mp3', 456000, 'mp3']);
  const x = mgr.add({ url: 'https://youtu.be/okProbe0001', format: 'mp4:999+140', confirm: true }); // not offered
  await until(() => x.probe && !x.probe.listing, 'the list');
  assert.deepEqual([x.probe.format, x.format], ['mp4:299+140', undefined]);
  const e = mgr.add({ url: 'https://youtu.be/errProbe001', title: '(3) Tab title - YouTube', confirm: true });
  await until(() => e.probe && !e.probe.listing, 'the failed list');
  assert.deepEqual(e.probe, { name: 'Tab title.mp4', category: 'Video', size: null, type: null, error: "Private video. Sign in if you've been granted access to this video", options: [], format: null });
  for (const d of [v, a, m, x, e]) await mgr.confirm(d.id, { action: 'cancel' });
});

test('confirm: the format sets the extension; keys this video does not offer are refused; legacy qualities still work', async () => {
  const folder = path.join(dir, 'yt');
  const v = mgr.add({ url: 'https://youtu.be/okConfirm01', confirm: true });
  await mgr.confirm(v.id, { action: 'later', path: path.join(folder, 'Clip.mp4'), format: 'webm:303+251' });
  assert.deepEqual([v.format, v.name, v.status], ['webm:303+251', 'Clip.webm', 'scheduled']);
  assert.equal(mgr.view().downloads.find(x => x.id === v.id).format, 'webm:303+251');
  const w = mgr.add({ url: 'https://youtu.be/okConfirm01', confirm: true });
  await mgr.confirm(w.id, { action: 'later', path: path.join(folder, 'Song'), format: 'm4a:140' });
  assert.equal(w.name, 'Song.m4a');
  const x = mgr.add({ url: 'https://youtu.be/okConfirm01', confirm: true });
  for (const bad of ['mp4:999+140', 'mp4:137', 'bv*+ba/b', 'mp3 ']) {
    await assert.rejects(mgr.confirm(x.id, { action: 'later', path: path.join(folder, 'a.mp4'), format: bad }), /unknown format/, bad);
  }
  assert.equal(x.status, 'pending');
  await mgr.confirm(x.id, { action: 'later', path: path.join(folder, 'Me at the zoo.mp4'), quality: 'mp3' });
  assert.deepEqual([x.quality, x.name], ['mp3', 'Me at the zoo.mp3']);
  const y = mgr.add({ url: 'https://youtu.be/okConfirm01', quality: 'mp3', confirm: true });
  await mgr.confirm(y.id, { action: 'later', path: path.join(folder, 'clip'), quality: '720' });
  assert.deepEqual([y.quality, y.name], ['720', 'clip.mp4']);
  await assert.rejects(mgr.confirm(y.id, { action: 'later', path: path.join(folder, 'a.mp4'), quality: '4k' }), /no longer waiting/);
  const z = mgr.add({ url: 'https://youtu.be/okConfirm01', confirm: true });
  await assert.rejects(mgr.confirm(z.id, { action: 'later', path: path.join(folder, 'a.mp4'), quality: '4k' }), /unknown quality/);
  assert.throws(() => mgr.add({ url: 'https://youtu.be/okConfirm01', format: 'bv*+ba' }), /unknown format/);
  for (const d of [v, w, x, y, z]) await mgr.act(d.id, 'remove');
});

test('format downloads: m4a filed under Music, webm under Video; a key the video does not offer never reaches yt-dlp', async () => {
  const a = mgr.add({ url: 'https://youtu.be/okFormat001', format: 'm4a:140' });
  await settled(a);
  assert.equal(a.status, 'done', a.error);
  assert.equal(a.file, path.join(mgr.settings.dir, 'Music', 'Fake vidéo_ okFormat001.m4a'));
  const w = mgr.add({ url: 'https://youtu.be/okFormat001', format: 'webm:278+251' });
  await settled(w);
  assert.equal(w.file, path.join(mgr.settings.dir, 'Video', 'Fake vidéo_ okFormat001.webm'));
  const runs = (await logLines()).filter(l => l.startsWith('run ')).length;
  const x = mgr.add({ url: 'https://youtu.be/okFormat001', format: 'mp4:999+140' });
  await settled(x);
  assert.equal(x.status, 'error');
  assert.match(x.error, /no longer offers the format mp4:999\+140/);
  assert.equal((await logLines()).filter(l => l.startsWith('run ')).length, runs, 'no download run');
  await mgr.act(x.id, 'remove');
});

test('a path chosen in the Download File Info window is kept, with the quality picked there', async () => {
  const d = mgr.add({ url: 'https://youtu.be/okAudio0002', confirm: true });
  const file = path.join(dir, 'chosen', 'My song.mp3');
  await mgr.confirm(d.id, { action: 'start', path: path.join(dir, 'chosen', 'My song.mp4'), quality: 'mp3' });
  await settled(d);
  assert.equal(d.status, 'done', d.error);
  assert.equal(d.file, file);
  assert.equal(d.tmp, undefined);
  assert.deepEqual(await fs.readFile(file), bytes('a1', 0, 30000));
});

test('mp3: filed under Music, size added up from the streams when yt-dlp gives none up front', async () => {
  const d = mgr.add({ url: 'https://youtu.be/okAudio0001', quality: 'mp3' });
  await settled(d);
  assert.equal(d.status, 'done', d.error);
  assert.equal(d.file, path.join(mgr.settings.dir, 'Music', 'Fake vidéo_ okAudio0001.mp3'));
  assert.equal(d.size, 30000);
  assert.deepEqual(await fs.readFile(d.file), bytes('a1', 0, 30000));
});

test('an ERROR line becomes the error message, without the prefix', async () => {
  const d = mgr.add({ url: 'https://youtu.be/errPrivate1' });
  await settled(d);
  assert.equal(d.status, 'error');
  assert.equal(d.error, "Private video. Sign in if you've been granted access to this video (if YouTube changed something: winget upgrade yt-dlp.yt-dlp)");
  await mgr.act(d.id, 'remove'); // its temp folder too
});

test('a failing yt-dlp is updated by winget once a day, and the download or list tried again once', async () => {
  const marker = process.env.FAKE_YTDLP_UPDATED = path.join(dir, 'updated');
  const wlog = path.join(dir, 'winget.log');
  // fake winget: logs its arguments; exit 0 and a newer yt-dlp (the marker), or FAKE_WINGET_EXIT (nothing newer)
  const fake = path.join(dir, 'winget.mjs');
  await fs.writeFile(fake, `import fs from 'node:fs';
fs.appendFileSync(${JSON.stringify(wlog)}, process.argv.slice(2).join(' ') + '\\n');
if (process.env.FAKE_WINGET_EXIT) process.exit(Number(process.env.FAKE_WINGET_EXIT));
fs.writeFileSync(${JSON.stringify(marker)}, '');
`);
  const calls = async () => (await fs.readFile(wlog, 'utf8').catch(() => '')).split('\n').filter(Boolean);
  try {
    mgr.winget = [process.execPath, fake];
    mgr.ytdlpUpdate = null;
    const d = mgr.add({ url: 'https://youtu.be/staleVideo1' });
    await settled(d);
    assert.equal(d.status, 'done', d.error);
    assert.deepEqual(await calls(), ['upgrade --id yt-dlp.yt-dlp -e --silent --accept-source-agreements --accept-package-agreements --disable-interactivity']);
    await fs.rm(marker); // YouTube changes again the same day: no second update, the error stays
    const e = mgr.add({ url: 'https://youtu.be/staleVideo2' });
    await settled(e);
    assert.match(e.error, /^The page needs to be reloaded\. \(if YouTube changed something/);
    assert.equal((await calls()).length, 1);

    mgr.ytdlpUpdate = null; // a day later: the format list fails, winget updates, the list comes
    assert.ok((await mgr.formats('https://youtu.be/oldListing1')).options.length);
    assert.equal((await calls()).length, 2);

    await fs.rm(marker);
    mgr.ytdlpUpdate = null;
    process.env.FAKE_WINGET_EXIT = '1'; // nothing newer: the error, after one winget run
    await assert.rejects(mgr.formats('https://youtu.be/oldListing2'), { message: 'The page needs to be reloaded.' });
    assert.equal((await calls()).length, 3);
    for (const x of [d, e]) await mgr.act(x.id, 'remove');
  } finally {
    delete process.env.FAKE_YTDLP_UPDATED;
    delete process.env.FAKE_WINGET_EXIT;
    mgr.winget = undefined;
  }
});

test('live streams are refused', async () => {
  const d = mgr.add({ url: 'https://youtu.be/liveStream1' });
  await settled(d);
  assert.equal(d.status, 'error');
  assert.equal(d.error, 'Live streams are not supported');
  await mgr.act(d.id, 'remove');
});

test('pause kills yt-dlp and its children; resume continues the .part files', async () => {
  await fs.rm(logFile, { force: true });
  const d = mgr.add({ url: 'https://youtu.be/slowVideo01' });
  const seen = [];
  const watch = setInterval(() => { const v = view(d); seen.push([v.got, v.size, v.phase]); }, 5);
  try {
    await until(() => d.got >= 50000, 'some progress'); // the fake waits there for the pause
    const ctl = mgr.running.get(d.id);
    await mgr.act(d.id, 'pause');
    await ctl.done;
    assert.equal(d.status, 'paused');
    const pids = (await logLines()).filter(l => /^(run|child) /.test(l)).map(l => Number(l.split(' ')[1]));
    assert.equal(pids.length, 2);
    for (const pid of pids) assert.ok(!alive(pid), `process ${pid} still running after pause`);
    assert.ok(existsSync(path.join(d.tmpDir, 'media.fv1.mp4.part')));

    await mgr.act(d.id, 'resume');
    await settled(d);
    assert.equal(d.status, 'done', d.error);
  } finally {
    clearInterval(watch);
  }
  const resumed = (await logLines()).find(l => l.startsWith('resume v1 '));
  assert.ok(resumed && Number(resumed.split(' ')[2]) >= 50000, 'second run continued the .part file');
  assert.deepEqual(await fs.readFile(d.file), Buffer.concat([bytes('v1', 0, 200000), bytes('a1', 0, 100000)]));
  // one bar for both streams: size known up front, progress never goes backwards
  const live = seen.filter(([got]) => got > 0);
  assert.ok(live.every(([, size]) => size === 300000), 'size from yt-dlp up front');
  assert.ok(live.every(([got], i) => i === 0 || got >= live[i - 1][0]), 'progress never jumps back');
  const phases = new Set(live.map(s => s[2]));
  assert.ok(phases.has('video') && phases.has('audio'), [...phases].join());
});

test('remove deletes the temp folder', async () => {
  const d = mgr.add({ url: 'https://youtu.be/slowRemove1' });
  await until(() => d.got > 0, 'some progress');
  const tmp = d.tmpDir;
  assert.ok(existsSync(tmp));
  await mgr.act(d.id, 'remove');
  assert.ok(!mgr.list.includes(d));
  assert.ok(!existsSync(tmp));
  assert.ok(!existsSync(path.dirname(tmp)), '.fastdl removed once empty');
});

test('missing yt-dlp or ffmpeg gives a clear error', async () => {
  const keys = ['PATH', 'LOCALAPPDATA', 'FASTDL_YTDLP', 'FASTDL_FFMPEG'];
  const saved = Object.fromEntries(keys.map(k => [k, process.env[k]]));
  const tools = [mgr.ytdlp, mgr.ffmpeg];
  try {
    process.env.PATH = process.env.LOCALAPPDATA = path.join(dir, 'nothing-here');
    delete process.env.FASTDL_YTDLP;
    delete process.env.FASTDL_FFMPEG;
    mgr.ytdlp = mgr.ffmpeg = undefined;
    let d = mgr.add({ url: 'https://youtu.be/okVideo0002' });
    await settled(d);
    assert.equal(d.error, 'yt-dlp is not installed. Run: winget install yt-dlp.yt-dlp');
    mgr.ytdlp = FAKE;
    d = mgr.add({ url: 'https://youtu.be/okVideo0003' });
    await settled(d);
    assert.equal(d.error, 'ffmpeg is not installed. Run: winget install yt-dlp.FFmpeg');
  } finally {
    for (const k of keys) saved[k] === undefined ? delete process.env[k] : (process.env[k] = saved[k]);
    [mgr.ytdlp, mgr.ffmpeg] = tools;
  }
});

test('other video sites go through yt-dlp too: their page link kept, no headers, the same format list', async () => {
  // the fake yt-dlp reads the video ID from ?v=
  const page = 'https://www.vimeo.com/channels/x?v=okVimeo0001';
  const d = mgr.add({ url: page, headers: { referer: 'https://vimeo.com/', cookie: 'secret' }, scheduled: true });
  assert.equal(d.kind, 'video');
  assert.equal(d.url, page);
  assert.deepEqual(d.headers, {});
  assert.equal(d.quality, 'best');
  const list = await mgr.formats(page);
  assert.ok(list.options.length > 0);
  const added = [d];
  for (const host of ['x.com', 'm.tiktok.com', 'www.instagram.com', 'player.twitch.tv', 'soundcloud.com']) {
    const x = mgr.add({ url: `https://${host}/a?v=okSite00001`, scheduled: true });
    added.push(x);
    assert.equal(x.kind, 'video', host);
  }
  const other = mgr.add({ url: 'https://notvimeo.com/a.mp4', scheduled: true });
  added.push(other);
  assert.equal(other.kind, 'http');
  for (const x of added) await mgr.act(x.id, 'remove');
});
