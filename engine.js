import fs from 'node:fs/promises';
import { existsSync, mkdirSync, writeFileSync, renameSync } from 'node:fs';
import path from 'node:path';
import os from 'node:os';
import crypto from 'node:crypto';
import { downloadHls } from './hls.js';

const MIN_SPLIT = 512 * 1024; // a connection is only split off when each half keeps at least this much
// Honest name on purpose: some servers drop clients that claim to be Chrome but don't look like it.
const UA = 'Mozilla/5.0 (compatible; FastDL/1.0)';
const HEADERS_ALLOWED = ['cookie', 'referer', 'user-agent'];
const sleep = ms => new Promise(r => setTimeout(r, ms));
const clamp = (v, lo, hi) => Math.min(hi, Math.max(lo, Math.round(Number(v)) || 0));

export const CATEGORIES = {
  Video: ['mp4', 'mkv', 'avi', 'mov', 'wmv', 'flv', 'webm', 'm4v', 'ts', '3gp'],
  Music: ['mp3', 'wav', 'flac', 'aac', 'm4a', 'ogg', 'wma', 'opus'],
  Compressed: ['zip', 'rar', '7z', 'tar', 'gz', 'bz2', 'xz', 'iso'],
  Documents: ['pdf', 'doc', 'docx', 'xls', 'xlsx', 'ppt', 'pptx', 'txt', 'epub', 'csv'],
  Programs: ['exe', 'msi', 'apk', 'dmg', 'deb', 'rpm', 'appimage'],
};

// Windows-safe file name: no path separators, reserved characters or device names
export function sanitize(name) {
  name = String(name).replace(/[<>:"/\\|?*\x00-\x1f]/g, '_').replace(/^[\s.]+|[\s.]+$/g, '');
  const ext = path.extname(name);
  if (name.length > 180) name = name.slice(0, 180 - ext.length) + ext;
  if (/^(con|prn|aux|nul|com\d|lpt\d)(\.|$)/i.test(name)) name = '_' + name;
  return name || 'download';
}

function nameFromDisposition(h) {
  const star = /filename\*\s*=\s*[^']*'[^']*'([^;]+)/i.exec(h);
  if (star) try { return decodeURIComponent(star[1].trim()); } catch {}
  return /filename\s*=\s*"?([^";]+)"?/i.exec(h)?.[1]?.trim();
}

export function nameFromUrl(u) {
  try { return decodeURIComponent(path.posix.basename(new URL(u).pathname)); } catch { return ''; }
}

// Mark-of-the-Web, as browsers write it, so SmartScreen and Office Protected View still check the file.
export async function markOfTheWeb(d) {
  if (process.platform !== 'win32') return;
  const u = new URL(d.url);
  u.username = u.password = '';
  const referer = d.headers.referer?.replace(/[\r\n]/g, '');
  const lines = ['[ZoneTransfer]', 'ZoneId=3', referer && `ReferrerUrl=${referer}`, `HostUrl=${u.href}`];
  await fs.writeFile(d.file + ':Zone.Identifier', lines.filter(Boolean).join('\r\n') + '\r\n').catch(() => {}); // FAT/exFAT have no streams
}

// Global speed limit shared by every connection. Callers go into debt and sleep it off,
// so chunks bigger than one second's budget still work.
class Limiter {
  rate = 0;
  allowance = 0;
  last = Date.now();
  async take(n) {
    if (!this.rate) return;
    const now = Date.now();
    this.allowance = Math.min(this.rate, this.allowance + (now - this.last) / 1000 * this.rate) - n;
    this.last = now;
    if (this.allowance < 0) await sleep(-this.allowance / this.rate * 1000);
  }
}

export class Manager {
  settings = { dir: path.join(os.homedir(), 'Downloads'), connections: 8, maxActive: 3, speedLimitKB: 0, scheduleAt: '', categories: true };
  list = [];
  running = new Map();
  limiter = new Limiter();
  stallMs = 30000;
  dirty = false;
  saving = false;

  constructor(stateFile) {
    this.stateFile = stateFile;
  }

  async load() {
    try {
      const s = JSON.parse(await fs.readFile(this.stateFile, 'utf8'));
      Object.assign(this.settings, s.settings);
      this.list = s.list ?? [];
    } catch (e) {
      if (e.code !== 'ENOENT') throw e; // never overwrite a state file we could not read
    }
    for (const d of this.list) if (d.status === 'downloading') d.status = 'queued';
    this.update({});
    setInterval(() => this.tick(), 1000).unref();
  }

  update(s) {
    const t = this.settings;
    if (s.dir != null && path.isAbsolute(String(s.dir))) t.dir = String(s.dir);
    if (s.connections != null) t.connections = clamp(s.connections, 1, 32);
    if (s.maxActive != null) t.maxActive = clamp(s.maxActive, 1, 20);
    if (s.speedLimitKB != null) t.speedLimitKB = clamp(s.speedLimitKB, 0, 1e7);
    if (s.scheduleAt != null) t.scheduleAt = /^([01]\d|2[0-3]):[0-5]\d$/.test(s.scheduleAt) ? s.scheduleAt : '';
    if (s.categories != null) t.categories = !!s.categories;
    this.limiter.rate = t.speedLimitKB * 1024;
    this.dirty = true;
    this.pump();
  }

  add({ url, name, title, headers, scheduled }) {
    const u = new URL(url);
    if (!/^https?:$/.test(u.protocol)) throw new Error('only http and https links are supported');
    const d = {
      id: crypto.randomUUID(),
      url: u.href,
      name: name ? sanitize(name) : null,
      title: title ? sanitize(title) : null,
      headers: Object.fromEntries(Object.entries(headers ?? {})
        .map(([k, v]) => [k.toLowerCase(), v])
        .filter(([k, v]) => HEADERS_ALLOWED.includes(k) && typeof v === 'string')),
      cookieHost: u.hostname,
      kind: /\.m3u8$/i.test(u.pathname) ? 'hls' : 'http',
      status: scheduled ? 'scheduled' : 'queued',
      added: Date.now(),
    };
    this.list.push(d);
    this.dirty = true;
    this.pump();
    return d;
  }

  async act(id, op, url) {
    const d = this.list.find(x => x.id === id);
    if (!d) throw new Error('download not found');
    const ctl = this.running.get(id);
    if (op === 'pause') {
      if (ctl || ['queued', 'scheduled'].includes(d.status)) d.status = 'paused';
      ctl?.ac.abort();
    } else if (op === 'resume') {
      if (['paused', 'error', 'scheduled'].includes(d.status)) d.status = 'queued';
    } else if (op === 'url') { // "refresh download address" for expired links
      if (ctl) throw new Error('pause the download first');
      const u = new URL(url);
      if (!/^https?:$/.test(u.protocol)) throw new Error('only http and https links are supported');
      d.url = u.href;
    } else if (op === 'remove' || op === 'removeFile') {
      const wasDone = d.status === 'done';
      if (ctl) { d.status = 'removed'; ctl.ac.abort(); await ctl.done; }
      this.list.splice(this.list.indexOf(d), 1);
      if (d.tmp) await fs.rm(d.tmp, { force: true });
      if (op === 'removeFile' && wasDone) await fs.rm(d.file, { force: true });
    } else throw new Error('unknown action');
    this.dirty = true;
    this.pump();
  }

  pump() {
    for (const d of this.list) {
      if (this.running.size >= this.settings.maxActive) break;
      if (d.status === 'queued') this.start(d);
    }
  }

  start(d) {
    const ctl = { ac: new AbortController(), active: new Set(), bytes: 0, last: 0, speed: 0 };
    this.running.set(d.id, ctl);
    d.status = 'downloading';
    d.error = null;
    ctl.done = (async () => {
      try {
        await (d.kind === 'hls' ? downloadHls(d, ctl, this) : this.runHttp(d, ctl));
        if (!ctl.ac.signal.aborted) { d.status = 'done'; d.finished = Date.now(); }
      } catch (e) {
        if (d.status === 'downloading') { d.status = 'error'; d.error = e.cause?.message || e.message; }
      }
      this.running.delete(d.id);
      this.dirty = true;
      this.pump();
    })();
  }

  // Follows redirects by hand so browser cookies only go to the host they came from.
  // A server that goes silent for stallMs is cut off; readers re-arm the timer with res.watch() per chunk.
  async get(url, d, signal, range) {
    const conn = new AbortController();
    const stalled = () => conn.abort(new Error(`no data from server for ${this.stallMs / 1000}s`));
    let timer = setTimeout(stalled, this.stallMs);
    try {
      return await this.follow(url, d, AbortSignal.any([signal, conn.signal]), range, res => {
        res.watch = () => { clearTimeout(timer); timer = setTimeout(stalled, this.stallMs); };
        res.unwatch = () => clearTimeout(timer);
      });
    } finally {
      clearTimeout(timer);
    }
  }

  async follow(url, d, signal, range, decorate) {
    for (let hops = 0; hops < 10; hops++) {
      const { cookie, ...headers } = d.headers;
      const res = await fetch(url, {
        signal,
        redirect: 'manual',
        headers: {
          'user-agent': UA,
          ...headers,
          ...(cookie && new URL(url).hostname === d.cookieHost && { cookie }),
          'accept-encoding': 'identity',
          ...(range && { range }),
        },
      });
      const location = res.headers.get('location');
      if (res.status < 300 || res.status > 399 || !location) { res.finalUrl = url; decorate(res); return res; }
      res.body?.cancel().catch(() => {});
      url = new URL(location, url).href;
    }
    throw new Error('too many redirects');
  }

  assignName(d, suggested) {
    if (d.file) return;
    suggested ||= 'download';
    let name = suggested;
    if (d.title) name = d.title + path.extname(suggested);
    if (d.name) name = path.extname(d.name) ? d.name : d.name + path.extname(suggested);
    name = sanitize(name);
    const ext = path.extname(name), base = name.slice(0, name.length - ext.length);
    const cat = this.settings.categories && Object.keys(CATEGORIES).find(c => CATEGORIES[c].includes(ext.slice(1).toLowerCase()));
    const dir = cat ? path.join(this.settings.dir, cat) : this.settings.dir;
    mkdirSync(dir, { recursive: true });
    const taken = f => existsSync(f) || existsSync(f + '.fdl') || this.list.some(x => x.file === f);
    let file = path.join(dir, name);
    for (let n = 1; taken(file); n++) file = path.join(dir, `${base} (${n})${ext}`);
    d.file = file;
    d.name = path.basename(file);
    d.tmp = file + '.fdl';
  }

  async runHttp(d, ctl) {
    const signal = ctl.ac.signal;
    let first = null;
    if (!d.segments || !d.resumable || !existsSync(d.tmp)) {
      // First request doubles as the probe and becomes connection #1, like IDM.
      let res, range = 'bytes=0-';
      for (let t = 1; ; t++) {
        let wait = 1000 * t;
        try {
          res = await this.get(d.url, d, signal, range);
          // zero-byte files can't satisfy any range: ask again without one
          if (res.status === 416 && range) { res.body?.cancel().catch(() => {}); range = null; continue; }
          if (res.status < 500 && res.status !== 429) break;
          res.body?.cancel().catch(() => {});
          const after = res.headers.get('retry-after');
          if (/^\d+$/.test(after)) wait = Math.min(Number(after), 30) * 1000;
          throw new Error(`HTTP ${res.status}`);
        } catch (e) {
          if (signal.aborted || t >= 4) throw e;
          await sleep(wait);
        }
      }
      if (!res.ok) throw new Error(`HTTP ${res.status}`);
      if (/mpegurl/i.test(res.headers.get('content-type') ?? '')) {
        res.body?.cancel().catch(() => {});
        d.kind = 'hls';
        return downloadHls(d, ctl, this);
      }
      const total = /\/(\d+)\s*$/.exec(res.headers.get('content-range') ?? '')?.[1];
      const length = Number(res.headers.get('content-length')) || null;
      d.resumable = !!range && ((res.status === 206 && total != null) || (res.status === 200 && /bytes/i.test(res.headers.get('accept-ranges') ?? '') && !!length));
      d.size = total != null ? Number(total) : length;
      this.assignName(d, nameFromDisposition(res.headers.get('content-disposition') ?? '') || nameFromUrl(res.finalUrl));
      d.segments = [{ start: 0, end: d.size, pos: 0 }];
      first = res;
    }

    const fh = await fs.open(d.tmp, first ? 'w' : 'r+');
    try {
      if (first && d.size) await fh.truncate(d.size);
      await new Promise((resolve, reject) => {
        let err;
        ctl.max = d.resumable ? this.settings.connections : 1;
        const launch = (s, res) => {
          ctl.active.add(s);
          this.worker(d, s, ctl, fh, res)
            .catch(e => {
              // servers that cap connections per user: drop this one, the others take over its piece
              if (ctl.active.size > 1 && !signal.aborted) ctl.max = ctl.active.size - 1;
              else { err ??= e; ctl.ac.abort(); }
            })
            .then(() => {
              ctl.active.delete(s);
              if (!signal.aborted) fill();
              if (!ctl.active.size) err ? reject(err) : resolve();
            });
        };
        const fill = () => {
          for (const s of d.segments) {
            if (ctl.active.size < ctl.max && !ctl.active.has(s) && (s.end == null || s.pos < s.end)) launch(s);
          }
          // Dynamic segmentation: a free connection takes the back half of the biggest remaining piece.
          while (d.resumable && ctl.active.size < ctl.max) {
            let big;
            for (const s of ctl.active) if (!big || s.end - s.pos > big.end - big.pos) big = s;
            if (!big || big.end - big.pos < 2 * MIN_SPLIT) break;
            const mid = big.pos + Math.floor((big.end - big.pos) / 2);
            const s = { start: mid, end: big.end, pos: mid };
            big.end = mid;
            d.segments.push(s);
            launch(s);
          }
        };
        if (first) launch(d.segments[0], first);
        fill();
        if (!ctl.active.size) err ? reject(err) : resolve();
      });
    } finally {
      await fh.close();
    }
    if (signal.aborted) return;
    if (d.segments.some(s => s.end == null || s.pos < s.end)) throw new Error('download incomplete');
    await fs.rename(d.tmp, d.file);
    delete d.segments; // finished: keeps state.json from growing with history
    await markOfTheWeb(d);
  }

  async worker(d, s, ctl, fh, res) {
    const signal = ctl.ac.signal;
    for (let tries = 0; s.end == null || s.pos < s.end;) {
      try {
        if (!res) {
          if (!d.resumable) s.pos = 0; // no range support: every retry starts over
          res = await this.get(d.url, d, signal, d.resumable && `bytes=${s.pos}-${s.end - 1}`);
          const cr = res.headers.get('content-range') ?? '';
          if (d.resumable && !(res.status === 206 && cr.startsWith(`bytes ${s.pos}-`) && cr.endsWith(`/${d.size}`))) {
            throw new Error(`server refused to resume (HTTP ${res.status})`);
          }
          if (!res.ok) throw new Error(`HTTP ${res.status}`);
        }
        const r = res;
        r.watch();
        try {
          for await (const chunk of r.body ?? []) {
            r.unwatch(); // only time spent waiting on the server counts as a stall, not our speed limit
            // the segment can shrink while we stream, when a free connection splits it
            const n = s.end == null ? chunk.length : Math.min(chunk.length, s.end - s.pos);
            if (n > 0) {
              await this.limiter.take(n);
              await fh.write(chunk, 0, n, s.pos);
              s.pos = s.end == null ? s.pos + n : Math.min(s.pos + n, s.end);
              ctl.bytes += n;
              tries = 0;
            }
            if (s.end != null && s.pos >= s.end) break;
            r.watch();
          }
        } finally {
          r.unwatch();
          res = null;
        }
        if (s.end == null) s.end = d.size = s.pos; // unknown length: end of stream means done
        else if (s.pos < s.end) throw new Error('connection closed early');
      } catch (e) {
        res = null;
        if (signal.aborted) return;
        if (++tries > (ctl.active.size > 1 ? 2 : 5)) throw e; // give up sooner while other connections are still working
        await sleep(1000 * tries);
      }
    }
  }

  tick() {
    for (const ctl of this.running.values()) {
      ctl.speed = ctl.speed * 0.3 + (ctl.bytes - ctl.last) * 0.7;
      ctl.last = ctl.bytes;
    }
    const at = this.settings.scheduleAt, now = new Date(), key = now.toDateString() + at;
    if (at && now.toTimeString().startsWith(at) && this.fired !== key) {
      this.fired = key;
      for (const d of this.list) if (d.status === 'scheduled') d.status = 'queued';
      this.dirty = true;
      this.pump();
    }
    if (this.dirty || this.running.size) this.save();
  }

  async save() {
    if (this.saving) return; // the next tick catches up
    this.saving = true;
    this.dirty = false;
    try {
      const tmp = this.stateFile + '.tmp';
      await fs.writeFile(tmp, JSON.stringify({ settings: this.settings, list: this.list }));
      await fs.rename(tmp, this.stateFile);
    } catch (e) {
      this.dirty = true;
      console.error('could not save state:', e.message);
    } finally {
      this.saving = false;
    }
  }

  saveSync() {
    writeFileSync(this.stateFile + '.tmp', JSON.stringify({ settings: this.settings, list: this.list }));
    renameSync(this.stateFile + '.tmp', this.stateFile);
  }

  view() {
    return {
      settings: this.settings,
      downloads: this.list.map(d => {
        const ctl = this.running.get(d.id);
        return {
          id: d.id, url: d.url, name: d.name ?? d.title, file: d.file, size: d.size, status: d.status, error: d.error,
          resumable: d.resumable, kind: d.kind, hls: d.hls,
          got: d.status === 'done' ? d.size : d.kind === 'hls' ? d.hls?.bytes ?? 0 : (d.segments ?? []).reduce((a, s) => a + s.pos - s.start, 0),
          speed: ctl ? Math.round(ctl.speed) : 0,
          connections: ctl ? ctl.active.size : 0,
          segments: d.status === 'done' ? null : d.segments?.map(s => [s.start, s.end, s.pos]),
        };
      }),
    };
  }
}
