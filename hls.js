import crypto from 'node:crypto';
import fs from 'node:fs/promises';
import { existsSync } from 'node:fs';
import path from 'node:path';

const sleep = ms => new Promise(r => setTimeout(r, ms));
const attrs = s => Object.fromEntries([...s.matchAll(/([A-Z0-9-]+)=("[^"]*"|[^,]*)/g)].map(m => [m[1], m[2].replace(/^"|"$/g, '')]));

async function fetchText(mgr, d, url, signal) {
  const res = await mgr.get(url, d, signal);
  if (!res.ok) throw new Error(`HTTP ${res.status} for playlist`);
  return { body: (await res.text()).trimStart(), url: res.finalUrl };
}

export async function parsePlaylist(mgr, d, signal) {
  let { body, url } = await fetchText(mgr, d, d.url, signal);
  if (body.includes('#EXT-X-STREAM-INF')) { // master playlist: take the highest quality
    const lines = body.split(/\r?\n/);
    let best;
    lines.forEach((line, i) => {
      if (!line.startsWith('#EXT-X-STREAM-INF')) return;
      const uri = lines.slice(i + 1).find(x => x.trim() && !x.startsWith('#'))?.trim();
      const bw = Number(attrs(line).BANDWIDTH) || 0;
      if (uri && (!best || bw > best.bw)) best = { bw, uri };
    });
    if (!best) throw new Error('playlist has no streams');
    ({ body, url } = await fetchText(mgr, d, new URL(best.uri, url).href, signal));
  }
  if (!body.startsWith('#EXTM3U')) throw new Error('not an HLS playlist');
  let seq = 0, key = null, map = null;
  const segs = [];
  for (let line of body.split(/\r?\n/)) {
    line = line.trim();
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

// Downloads segments in parallel but writes them in order, so the output is one playable file.
export async function downloadHls(d, ctl, mgr) {
  const signal = ctl.ac.signal;
  const { segs, map } = await parsePlaylist(mgr, d, signal);
  mgr.assignName(d, (path.posix.basename(new URL(d.url).pathname, '.m3u8') || 'video') + (map ? '.mp4' : '.ts'));
  if (!d.hls || d.hls.total !== segs.length || !existsSync(d.tmp)) d.hls = { done: 0, bytes: 0, total: segs.length };

  const get = async url => {
    const res = await mgr.get(url, d, signal);
    if (!res.ok) throw new Error(`HTTP ${res.status}`);
    return Buffer.from(await res.arrayBuffer());
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

  const fh = await fs.open(d.tmp, d.hls.done ? 'r+' : 'w');
  try {
    await fh.truncate(d.hls.done ? d.hls.bytes : 0);
    if (!d.hls.done) {
      d.hls.bytes = 0;
      if (map) { const m = await get(map); await fh.write(m, 0, m.length, 0); d.hls.bytes = m.length; }
    }
    const queue = [];
    let i = d.hls.done;
    const more = () => {
      while (queue.length < mgr.settings.connections && i < segs.length) {
        const p = fetchSeg(segs[i++]);
        p.catch(() => {}); // awaited below; this only stops early failures counting as unhandled
        queue.push(p);
      }
    };
    more();
    while (queue.length) {
      const buf = await queue.shift();
      more();
      await mgr.limiter.take(buf.length);
      await fh.write(buf, 0, buf.length, d.hls.bytes);
      d.hls.bytes += buf.length;
      d.hls.done++;
      ctl.bytes += buf.length;
    }
  } finally {
    await fh.close();
  }
  if (signal.aborted) return;
  d.size = d.hls.bytes;
  await fs.rename(d.tmp, d.file);
}
