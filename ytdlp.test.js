import { test, before, after } from 'node:test';
import assert from 'node:assert/strict';
import fs from 'node:fs/promises';
import { existsSync } from 'node:fs';
import os from 'node:os';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import { Manager } from './engine.js';
import { ytdlpArgs, youtubeId } from './ytdlp.js';

// fixtures/fake-ytdlp.mjs stands in for yt-dlp: no network
const FAKE = [process.execPath, path.join(path.dirname(fileURLToPath(import.meta.url)), 'fixtures', 'fake-ytdlp.mjs')];
const sleep = ms => new Promise(r => setTimeout(r, ms));
const bytes = (fid, from, n) => Buffer.from(Array.from({ length: n }, (_, i) => (from + i + fid.charCodeAt(0)) % 251));
const alive = pid => { try { process.kill(pid, 0); return true; } catch { return false; } };

let dir, mgr, logFile;
before(async () => {
  dir = await fs.mkdtemp(path.join(os.tmpdir(), 'fastdl-yt-'));
  logFile = process.env.FAKE_YTDLP_LOG = path.join(dir, 'fake.log');
  mgr = new Manager(path.join(dir, 'state.json'));
  await mgr.load();
  mgr.update({ dir: path.join(dir, 'out'), maxActive: 5 });
  mgr.ytdlp = FAKE;
  mgr.ffmpeg = dir;
});
after(() => fs.rm(dir, { recursive: true, force: true, maxRetries: 5 }));

async function until(check, what, ms = 15000) {
  for (const end = Date.now() + ms; !check();) {
    if (Date.now() > end) throw new Error('timed out waiting for ' + what);
    await sleep(10);
  }
}
const settled = d => until(() => !['queued', 'downloading'].includes(d.status), 'the download to stop');
const view = d => mgr.view().downloads.find(x => x.id === d.id);
const logLines = async () => (await fs.readFile(logFile, 'utf8').catch(() => '')).split('\n');

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

test('quality → yt-dlp arguments', () => {
  const run = (quality, speedLimitKB = 0) => ytdlpArgs({ quality, tmpDir: 'T', url: 'https://www.youtube.com/watch?v=jNQXAC9IVRw' }, { speedLimitKB }, 'F');
  const has = (args, ...seq) => assert.ok(args.some((_, i) => seq.every((s, j) => args[i + j] === s)), `${seq.join(' ')} in ${args.join(' ')}`);
  const sel = ['-f', 'bv*+ba/b', '-S'];
  has(run('best'), ...sel, 'res,vcodec:h264,ext:mp4:m4a', '--merge-output-format', 'mp4');
  for (const q of ['1080', '720', '480']) has(run(q), ...sel, `res:${q},vcodec:h264,ext:mp4:m4a`, '--merge-output-format', 'mp4');
  has(run('mp3'), '-f', 'ba/b', '-x', '--audio-format', 'mp3', '--audio-quality', '0');
  const base = run('best');
  has(base, '--no-playlist', '--match-filter', '!is_live', '--newline', '--no-colors', '--ffmpeg-location', 'F', '-P', 'T', '-o', 'media.%(ext)s');
  assert.equal(base.at(-1), 'https://www.youtube.com/watch?v=jNQXAC9IVRw');
  assert.ok(!base.includes('--limit-rate'));
  has(run('best', 250), '--limit-rate', '250K');
  assert.ok(!run('mp3', 5).some(a => /cookie/i.test(a)), 'never cookies');
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
    await until(() => d.got >= 50000, 'some progress');
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
