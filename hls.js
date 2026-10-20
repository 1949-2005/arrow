import crypto from 'node:crypto';
import fs from 'node:fs/promises';
import { existsSync } from 'node:fs';
import path from 'node:path';
import { spawn } from 'node:child_process';
import { markOfTheWeb } from './engine.js';
import { ffmpegExe } from './ytdlp.js';

const sleep = ms => new Promise(r => setTimeout(r, ms));
const attrs = s => Object.fromEntries([...s.matchAll(/([A-Z0-9-]+)=("[^"]*"|[^,]*)/g)].map(m => [m[1], m[2].replace(/^"|"$/g, '')]));

// A separate sound track and the joined file sit next to d.tmp until ffmpeg is done.
export const soundTemps = d => ['.audio.fdl', '.join.fdl'].map(x => d.tmp.replace(/\.fdl$/, x));

async function fetchText(mgr, d, url, signal) {
  const res = await mgr.get(url, d, signal);
  if (!res.ok) throw new Error(`HTTP ${res.status} for playlist`);
  return { body: (await res.text()).trimStart(), url: res.finalUrl };
}

// One media playlist: its segments (each with its AES-128 key) and the fMP4 init segment.
function parseMedia(body, url) {
  if (!body.startsWith('#EXTM3U')) throw new Error('not an HLS playlist');
  let seq = 0, key = null, map = null;
  const segs = [];
  for (let line of body.split(/\r?\n/)) {
    line = line.trim();
    if (line.startsWith('#EXT-X-BYTERANGE')) throw new Error('byte-range HLS playlists are not supported');
    if (line.startsWith('#EXT-X-MEDIA-SEQUENCE:')) seq = Number(line.slice(22)) || 0;
    else if (line.startsWith('#EXT-X-KEY:')) {
      const a = attrs(line.slice(11));
      if (a.METHOD === 'NONE') key = null;
      else if (a.METHOD === 'AES-128') key = { uri: new URL(a.URI, url).href, iv: a.IV };
      else throw new Error(`${a.METHOD} encryption (DRM) is not supported`);
    } else if (line.startsWith('#EXT-X-MAP:')) map = new URL(attrs(line.slice(11)).URI, url).href;
    else if (line && !line.startsWith('#')) segs.push({ url: new URL(line, url).href, key, seq: seq + segs.length });
  }
  if (!segs.length) throw new Error('playlist has no segments');
  return { segs, map };
}

// audio: the chosen quality's sound when it is a separate track (X serves video and sound apart), else null.
export async function parsePlaylist(mgr, d, signal) {
  let { body, url } = await fetchText(mgr, d, d.url, signal);
  let audio = null;
  if (body.includes('#EXT-X-STREAM-INF')) { // master playlist: take the highest quality
    const lines = body.split(/\r?\n/);
    let best;
    lines.forEach((line, i) => {
      if (!line.startsWith('#EXT-X-STREAM-INF')) return;
      const uri = lines.slice(i + 1).find(x => x.trim() && !x.startsWith('#'))?.trim();
      const a = attrs(line);
      const bw = Number(a.BANDWIDTH) || 0;
      if (uri && (!best || bw > best.bw)) best = { bw, uri, group: a.AUDIO };
    });
    if (!best) throw new Error('playlist has no streams');
    // its AUDIO group's default track, else the group's first; a track without a URI is inside the video already
    const tracks = lines.filter(l => l.startsWith('#EXT-X-MEDIA:')).map(attrs)
      .filter(a => a.TYPE === 'AUDIO' && a['GROUP-ID'] === best.group && a.URI);
    const sound = tracks.find(a => a.DEFAULT === 'YES') ?? tracks[0];
    if (sound) {
      const t = await fetchText(mgr, d, new URL(sound.URI, url).href, signal);
      audio = parseMedia(t.body, t.url);
    }
    ({ body, url } = await fetchText(mgr, d, new URL(best.uri, url).href, signal));
  }
  return { ...parseMedia(body, url), audio };
}

// ffmpeg copies the video and its sound into one file, without re-encoding.
async function join(ffmpeg, video, sound, out, format, signal) {
  let err = '';
  const code = await new Promise(resolve => {
    const proc = spawn(ffmpeg, ['-y', '-v', 'error', '-i', video, '-i', sound, '-map', '0:v:0', '-map', '1:a:0', '-c', 'copy', '-f', format, out],
      { stdio: ['ignore', 'ignore', 'pipe'], windowsHide: true, signal });
    proc.stderr.on('data', c => { err += c; });
    // on pause, wait for 'close': ffmpeg must let go of the files before they can be resumed or removed
    proc.on('error', e => { if (e.name !== 'AbortError') { err ||= e.message; resolve(null); } }).on('close', resolve);
  });
  if (signal.aborted) return;
  if (code !== 0) throw new Error(`ffmpeg could not join the video and its sound: ${err.trim().split(/\r?\n/)[0] || `exit code ${code}`}`);
}

// Downloads segments in parallel but writes them in order, so each track is one playable file.
// A separate sound track gets its own file, and ffmpeg joins the two at the end.
export async function downloadHls(d, ctl, mgr) {
  const signal = ctl.ac.signal;
  delete d.phase;
  const { segs, map, audio } = await parsePlaylist(mgr, d, signal);
  const ffmpeg = audio && ffmpegExe(); // before anything downloads: without it the sound can't be joined
  mgr.assignName(d, (path.posix.basename(new URL(d.url).pathname, '.m3u8') || 'video') + (map || audio ? '.mp4' : '.ts'));
  const [soundTmp, joined] = soundTemps(d);
  const tracks = [{ segs, map, tmp: d.tmp }, ...(audio ? [{ ...audio, tmp: soundTmp }] : [])];
  const total = tracks.reduce((n, t) => n + t.segs.length, 0);
  if (d.hls?.total !== total || d.hls.parts?.length !== tracks.length || tracks.some((t, i) => d.hls.parts[i].done && !existsSync(t.tmp))) {
    d.hls = { done: 0, bytes: 0, total, parts: tracks.map(() => ({ done: 0, bytes: 0 })) };
  }
  const sum = () => { // the totals the UI shows, over both tracks
    d.hls.done = d.hls.parts.reduce((n, p) => n + p.done, 0);
    d.hls.bytes = d.hls.parts.reduce((n, p) => n + p.bytes, 0);
  };

  const get = async url => {
    const res = await mgr.get(url, d, signal);
    if (!res.ok) throw new Error(`HTTP ${res.status}`);
    const chunks = [];
    res.watch(); // chunk by chunk so the stall watchdog covers the body too
    try {
      for await (const c of res.body ?? []) { res.unwatch(); chunks.push(c); res.watch(); }
    } finally {
      res.unwatch();
    }
    return Buffer.concat(chunks);
  };
  const keys = new Map();
  const fetchSeg = async s => {
    for (let t = 1; ; t++) {
      try {
        let buf = await get(s.url);
        if (s.key) {
          if (!keys.has(s.key.uri)) keys.set(s.key.uri, get(s.key.uri).catch(e => { keys.delete(s.key.uri); throw e; }));
          const iv = Buffer.alloc(16);
          if (s.key.iv) Buffer.from(s.key.iv.replace(/^0x/i, '').padStart(32, '0'), 'hex').copy(iv);
          else iv.writeUInt32BE(s.seq, 12); // default IV is the media sequence number
          const dc = crypto.createDecipheriv('aes-128-cbc', await keys.get(s.key.uri), iv);
          buf = Buffer.concat([dc.update(buf), dc.final()]);
        }
        return buf;
      } catch (e) {
        if (signal.aborted || t >= 5) throw e;
        await sleep(1000 * t);
      }
    }
  };

  for (const [n, t] of tracks.entries()) {
    const part = d.hls.parts[n];
    if (signal.aborted) return;
    if (part.done === t.segs.length) continue; // finished before a pause
    const fh = await fs.open(t.tmp, part.done ? 'r+' : 'w');
    try {
      await fh.truncate(part.done ? part.bytes : 0);
      if (!part.done) {
        part.bytes = 0;
        if (t.map) { const m = await get(t.map); await fh.write(m, 0, m.length, 0); part.bytes = m.length; }
        sum();
      }
      const queue = [];
      let i = part.done;
      const more = () => {
        while (queue.length < mgr.settings.connections && i < t.segs.length) {
          const p = fetchSeg(t.segs[i++]);
          p.catch(() => {}); // awaited below; this only stops early failures counting as unhandled
          queue.push(p);
        }
      };
      more();
      while (queue.length) {
        const buf = await queue.shift();
        more();
        await mgr.limiter.take(buf.length);
        await fh.write(buf, 0, buf.length, part.bytes);
        part.bytes += buf.length;
        part.done++;
        sum();
        ctl.bytes += buf.length;
      }
    } finally {
      await fh.close();
    }
  }
  if (signal.aborted) return;
  if (audio) {
    d.phase = 'merging';
    await join(ffmpeg, d.tmp, soundTmp, joined, /\.ts$/i.test(d.file) ? 'mpegts' : 'mp4', signal);
    if (signal.aborted) return;
    await fs.rename(joined, d.file);
    await Promise.all([d.tmp, soundTmp].map(f => fs.rm(f, { force: true })));
    delete d.phase;
  } else await fs.rename(d.tmp, d.file);
  d.size = (await fs.stat(d.file)).size;
  await markOfTheWeb(d);
}
