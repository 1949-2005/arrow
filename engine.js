import fs from 'node:fs/promises';
import { existsSync, mkdirSync, writeFileSync, renameSync } from 'node:fs';
import path from 'node:path';
import os from 'node:os';
import crypto from 'node:crypto';
import { downloadHls, parsePlaylist, soundTemps } from './hls.js';
import { downloadVideo, youtubeId, removeTmp, QUALITIES, FORMAT_KEY, listFormats, extOf } from './ytdlp.js';

const MIN_SPLIT = 512 * 1024; // a connection is only split off when each half keeps at least this much
// Honest name on purpose: some servers drop clients that claim to be Chrome but don't look like it.
const UA = 'Mozilla/5.0 (compatible; FastDL/1.0)';
const HEADERS_ALLOWED = ['cookie', 'referer', 'user-agent'];
const sleep = ms => new Promise(r => setTimeout(r, ms));
const clamp = (v, lo, hi) => Math.min(hi, Math.max(lo, Math.round(Number(v)) || 0));
const describe = t => String(t ?? '').replace(/\p{Cc}/gu, '').trim().slice(0, 300).trim() || null;
const DUPLICATES = ['ask', 'number', 'replace', 'resume'];
const HHMM = /^([01]\d|2[0-3]):[0-5]\d$/;
const MAIN = 'main', MAIN_NAME = 'Main download queue';
const cleanName = n => typeof n === 'string' ? n.replace(/\p{Cc}/gu, '').trim() : '';
// A queue's fields: s over q, checked like the other settings (anything wrong falls back to the default)
const queueFields = (q, s = {}) => {
  const v = k => s[k] ?? q[k];
  return {
    id: q.id, name: q.name,
    maxActive: clamp(v('maxActive'), 1, 20),
    startAt: HHMM.test(v('startAt')) ? v('startAt') : '',
    days: Array.isArray(v('days')) ? [...new Set(v('days').filter(n => Number.isInteger(n) && n >= 0 && n <= 6))].sort() : [], // Sunday = 0; empty = every day
    stopAt: HHMM.test(v('stopAt')) ? v('stopAt') : '',
    retries: clamp(v('retries'), 0, 10),
    whenDone: ['sleep', 'shutdown'].includes(v('whenDone')) ? v('whenDone') : '',
  };
};

export const CATEGORIES = {
  Video: ['mp4', 'mkv', 'avi', 'mov', 'wmv', 'flv', 'webm', 'm4v', 'ts', '3gp'],
  Music: ['mp3', 'wav', 'flac', 'aac', 'm4a', 'ogg', 'wma', 'opus'],
  Compressed: ['zip', 'rar', '7z', 'tar', 'gz', 'bz2', 'xz', 'iso'],
  Documents: ['pdf', 'doc', 'docx', 'xls', 'xlsx', 'ppt', 'pptx', 'txt', 'epub', 'csv'],
  Programs: ['exe', 'msi', 'apk', 'dmg', 'deb', 'rpm', 'appimage'],
};
const isCategory = c => typeof c === 'string' && (c === 'Other' || Object.hasOwn(CATEGORIES, c));

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

// Global speed limit shared by every connection (and each download's own, ctl.limiter). Callers go into debt and sleep it off,
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
  settings = { dir: path.join(os.homedir(), 'Downloads'), connections: 8, maxActive: 3, speedLimitKB: 0, speedLimitOn: false, categories: true, clipboard: false, folders: {}, duplicates: 'ask', showComplete: true, queues: [queueFields({ id: MAIN, name: MAIN_NAME })] };
  list = [];
  running = new Map();
  // Running queues, never saved: id → {tries: download id → retries used this run, waiting: download id → retry time, worked}
  runs = new Map();
  fired = new Map(); // '<queue id> start|stop' → the day and time it last fired, so each fires once a day
  limiter = new Limiter();
  stallMs = 30000;
  dirty = false;
  saving = false;
  // "When all downloads finish": used once and never saved, so a forgotten "shut down" can't fire days later
  whenDone = ''; // '' | 'sleep' | 'shutdown'
  powerAt = 0; // end of the countdown, 0 when none runs
  powerOp = ''; // what the running countdown does
  powerAll = false; // started by "When all downloads finish" (new work stops it), not by one download's own whenDone
  powerDelayMs = 60000;
  power = () => {}; // server.js puts the real sleep/shutdown here; tests and other users of Manager stay harmless
  opener = () => {}; // a download's whenDone 'open': server.js opens the file like the Open action
  worked = false; // a download started since the last tick, so even one that ended between ticks counts
  pendingMs = 30 * 60000; // a Download File Info window left open longer than this loses its download
  formatCache = new Map(); // YouTube video id → {at, list}, see listFormats()

  constructor(stateFile) {
    this.stateFile = stateFile;
  }

  async load() {
    try {
      const s = JSON.parse(await fs.readFile(this.stateFile, 'utf8'));
      Object.assign(this.settings, s.settings);
      if (s.settings?.speedLimitKB > 0 && s.settings.speedLimitOn == null) this.settings.speedLimitOn = true; // the limit was on before the switch existed
      this.list = s.list ?? [];
    } catch (e) {
      if (e.code !== 'ENOENT') throw e; // never overwrite a state file we could not read
    }
    this.list = this.list.filter(d => d.status !== 'pending'); // its Download File Info window is gone
    for (const d of this.list) {
      if (d.status === 'downloading') d.status = 'queued';
      if (d.status === 'scheduled') d.queue ??= MAIN; // from before queues
      if (d.queue && d.status === 'queued') d.status = 'scheduled'; // no queue runs after a restart: as if it was stopped
    }
    this.update({});
    for (const d of this.list) if (d.queue && !this.queue(d.queue)) d.queue = MAIN; // its queue was dropped as broken
    setInterval(() => this.tick(), 1000).unref();
  }

  update(s) {
    const t = this.settings;
    if (s.dir != null && path.isAbsolute(String(s.dir))) t.dir = String(s.dir);
    if (s.connections != null) t.connections = clamp(s.connections, 1, 32);
    if (s.maxActive != null) t.maxActive = clamp(s.maxActive, 1, 20);
    if (s.speedLimitKB != null) t.speedLimitKB = clamp(s.speedLimitKB, 0, 1e7);
    // checked from t too, so a loaded state.json gets the same rules
    t.speedLimitOn = !!(s.speedLimitOn ?? t.speedLimitOn);
    t.showComplete = !!(s.showComplete ?? t.showComplete);
    const dup = s.duplicates ?? t.duplicates;
    t.duplicates = DUPLICATES.includes(dup) ? dup : 'ask';
    if (s.categories != null) t.categories = !!s.categories;
    if (s.clipboard != null) t.clipboard = !!s.clipboard;
    // "Remember this folder for <Category>": category → absolute folder; anything else is dropped (also from a loaded state.json)
    const f = s.folders ?? t.folders;
    t.folders = Object.fromEntries(Object.entries(f && typeof f === 'object' ? f : {}).filter(([k, v]) => isCategory(k) && typeof v === 'string' && path.isAbsolute(v)));
    // Queues change through queues(), never here; this checks a loaded state.json: the main queue first and always there,
    // the others with a unique id and name. The old single start time (also from an older app) is the main queue's.
    const old = Array.isArray(t.queues) ? t.queues : [];
    const ids = new Set([MAIN]), names = new Set([MAIN_NAME.toLowerCase()]);
    t.queues = [queueFields({ ...old.find(q => q?.id === MAIN), id: MAIN, name: MAIN_NAME }, { startAt: s.scheduleAt ?? t.scheduleAt })];
    for (const q of old) {
      const name = cleanName(q?.name);
      if (typeof q?.id !== 'string' || ids.has(q.id) || !name || name.length > 40 || names.has(name.toLowerCase())) continue; // the main one, or a broken or doubled entry
      ids.add(q.id);
      names.add(name.toLowerCase());
      t.queues.push(queueFields({ ...q, name }));
    }
    delete t.scheduleAt;
    if (s.whenDone != null) {
      this.whenDone = ['sleep', 'shutdown'].includes(s.whenDone) ? s.whenDone : '';
      if (!this.whenDone) this.powerAt = 0; // Cancel stops any countdown at once, also one a download started
    }
    this.limiter.rate = t.speedLimitOn ? t.speedLimitKB * 1024 : 0;
    this.dirty = true;
    this.pump();
  }

  // confirm: the extension's downloads wait as 'pending' until the Download File Info window starts them
  // format: a key from formats(); checked against the video's list before yt-dlp gets it (confirm(), downloadVideo())
  // scheduled: into a queue (default the main one) as 'scheduled'
  add({ url, name, title, headers, scheduled, quality, format, confirm, description, queue }) {
    const u = new URL(url);
    if (!/^https?:$/.test(u.protocol)) throw new Error('only http and https links are supported');
    const yt = youtubeId(u);
    if (yt && !QUALITIES.includes(quality = String(quality ?? 'best'))) throw new Error(`unknown quality "${quality}" (use ${QUALITIES.join(', ')})`);
    if (yt && format != null && !FORMAT_KEY.test(format = String(format))) throw new Error(`unknown format "${format}"`);
    const q = scheduled && !confirm ? String(queue ?? MAIN) : null;
    if (q && !this.queue(q)) throw new Error('queue not found');
    const d = {
      id: crypto.randomUUID(),
      url: yt ? `https://www.youtube.com/watch?v=${yt}` : u.href,
      name: name ? sanitize(name) : null,
      title: title ? sanitize(title) : null,
      headers: yt ? {} : Object.fromEntries(Object.entries(headers ?? {}) // never cookies for yt-dlp
        .map(([k, v]) => [k.toLowerCase(), v])
        .filter(([k, v]) => HEADERS_ALLOWED.includes(k) && typeof v === 'string')),
      cookieHost: u.hostname,
      kind: yt ? 'video' : /\.m3u8$/i.test(u.pathname) ? 'hls' : 'http',
      ...(yt && { quality }),
      ...(yt && format != null && { format }),
      status: confirm ? 'pending' : q ? 'scheduled' : 'queued',
      queue: q,
      added: Date.now(),
      description: describe(description),
    };
    // Without a window to ask (Add links, batches) only 'replace' differs from numbering
    const x = !confirm && this.settings.duplicates === 'replace' && this.duplicate(d);
    if (x) {
      // d waits outside the list until x's temp files are gone: they have the same names as d's
      this.act(x.id, 'remove').then(() => x.file && this.setFile(d, x.file), () => {}).then(() => {
        this.list.push(d);
        this.dirty = true;
        this.pump();
      });
      return d;
    }
    this.list.push(d);
    if (confirm) this.probe(d);
    this.dirty = true;
    this.pump();
    return d;
  }

  // Another download of the same link, not one waiting for its window or being removed
  duplicate(d, id) {
    return this.list.find(x => x !== d && x.url === d.url && !['pending', 'removed'].includes(x.status) && (id == null || x.id === id));
  }

  // Defaults for the Download File Info window: name, category, size. Never throws; a failed probe keeps its error for the window.
  async probe(d) {
    let name, size = null, type = null, error = null, list = null;
    const dup = () => { const x = this.duplicate(d); return x && { duplicate: { id: x.id, name: x.name ?? x.title, status: x.status } }; };
    const catOf = n => Object.keys(CATEGORIES).find(c => CATEGORIES[c].includes(path.extname(n).slice(1).toLowerCase())) ?? 'Other';
    if (d.kind === 'video') {
      // Until yt-dlp's list is in, the window shows "Getting formats…" with the tab title ("(3) Title - YouTube") as the name.
      const tab = (d.title ?? '').replace(/^\(\d+\)\s*/, '').replace(/\s+-\s+YouTube$/i, '') || 'video';
      name = sanitize(tab + extOf(d.format, d.quality));
      if (d.status === 'pending') d.probe = { name, category: catOf(name), size, type, error, listing: true, ...dup() };
      try {
        const { title, options } = await this.formats(d.url);
        if (d.format && !options.some(o => o.key === d.format)) delete d.format; // not offered: the window picks again
        const want = d.format ?? (d.quality === 'mp3' ? 'mp3' : null);
        const pick = options.find(o => o.key === want) ?? options.find(o => o.group === 'MP4') ?? options[0];
        if (title) d.title = sanitize(title);
        name = sanitize((title || tab) + extOf(pick?.key ?? d.format, d.quality));
        size = pick?.size ?? null;
        list = { options, format: pick?.key ?? null };
      } catch (e) {
        error = e.message;
      }
    } else if (d.kind === 'hls') {
      let ext = '.ts'; // MP4 pieces, or a separate sound track that ffmpeg joins (X), give an .mp4
      try {
        const { map, audio } = await parsePlaylist(this, d, AbortSignal.timeout(15000));
        if (map || audio) ext = '.mp4';
      } catch (e) {
        error = e.cause?.message || e.message;
      }
      name = this.baseName(d, (path.posix.basename(new URL(d.url).pathname, '.m3u8') || 'video') + ext);
    } else {
      let found = nameFromUrl(d.url);
      try {
        const res = await this.get(d.url, d, AbortSignal.timeout(15000), 'bytes=0-0');
        res.body?.cancel().catch(() => {});
        if (!res.ok) throw new Error(`HTTP ${res.status}`);
        found = nameFromDisposition(res.headers.get('content-disposition') ?? '') || nameFromUrl(res.finalUrl);
        const total = /\/(\d+)\s*$/.exec(res.headers.get('content-range') ?? '')?.[1];
        size = total != null ? Number(total) : Number(res.headers.get('content-length')) || null;
        type = res.headers.get('content-type')?.split(';')[0].trim() || null;
      } catch (e) {
        error = e.cause?.message || e.message;
      }
      name = this.baseName(d, found);
    }
    if (d.status === 'pending') d.probe = { name, category: catOf(name), size, type, error, ...(d.kind === 'video' && { options: [], format: null, ...list }), ...dup() };
  }

  formats(url) {
    return listFormats(this, url);
  }

  // The Download File Info window's answer. Only our own page can call it (server.js), so the folder is the user's choice.
  // format: a key from the video's list (fetched again when the cache expired); quality: a legacy choice.
  // replace: the duplicate's id (probe.duplicate), which goes and leaves its file to this one; rememberDuplicates: the Duplicate window's "remember".
  // later: into queue (default the main one) as 'scheduled'; startQueue starts that queue.
  async confirm(id, { action, path: file, quality, format, remember, description, replace, rememberDuplicates, queue, startQueue } = {}) {
    const gone = 'This download is no longer waiting to start.';
    const d = this.list.find(x => x.id === id);
    if (d?.status !== 'pending') throw new Error(gone);
    // also on cancel: the Duplicate window's "resume the existing one" cancels the new one
    if (DUPLICATES.includes(rememberDuplicates) && rememberDuplicates !== 'ask') { this.settings.duplicates = rememberDuplicates; this.dirty = true; }
    if (action === 'cancel') {
      this.list.splice(this.list.indexOf(d), 1);
      return;
    }
    if (action !== 'start' && action !== 'later') throw new Error('unknown action');
    const q = action === 'later' ? String(queue ?? MAIN) : null;
    if (q && !this.queue(q)) throw new Error('queue not found');
    const video = d.kind === 'video';
    if (video && quality != null && !QUALITIES.includes(String(quality))) throw new Error(`unknown quality "${quality}"`);
    const p = String(file ?? '').trim();
    // a drive or a network share, and a file name after the last separator
    if (!/^([a-z]:[\\/]|\\\\[^\\/]+[\\/][^\\/]+[\\/])/i.test(p) || /[\\/]$/.test(p)) throw new Error('Save As needs a full path with a file name, like C:\\Downloads\\file.zip');
    if (video && format != null) {
      format = String(format);
      if (!FORMAT_KEY.test(format) || !(await this.formats(d.url)).options.some(o => o.key === format)) throw new Error(`unknown format "${format}"`);
      if (d.status !== 'pending' || !this.list.includes(d)) throw new Error(gone); // answered or cancelled meanwhile
      d.format = format;
    } else if (video && quality != null) {
      d.quality = String(quality);
      delete d.format;
    }
    let name = sanitize(path.basename(p));
    if (video) name = name.slice(0, name.length - path.extname(name).length) + extOf(d.format, d.quality);
    const dir = path.dirname(p);
    try { mkdirSync(dir, { recursive: true }); } catch (e) { throw new Error('Could not create the folder: ' + e.message); }
    const old = replace != null && this.duplicate(d, replace);
    if (old) {
      await this.act(old.id, 'remove'); // before d gets its file: the old temp files have the same names
      if (d.status !== 'pending' || !this.list.includes(d)) throw new Error(gone);
    }
    // the existing file is allowed: the finished download replaces it
    this.setFile(d, old && path.join(dir, name) === old.file ? old.file : this.uniqueFile(dir, name));
    if (description !== undefined) d.description = describe(description);
    if (isCategory(remember)) this.settings.folders[remember] = path.dirname(d.file);
    delete d.probe;
    d.status = action === 'start' ? 'queued' : 'scheduled';
    d.queue = q && (this.queue(q) ? q : MAIN); // removed while we waited: the main queue takes it, as remove does
    if (action === 'start') d.notify = true; // the app shows its Download complete window for it
    this.dirty = true;
    if (q && startQueue) this.startQueue(d.queue);
    this.pump();
  }

  // url: for 'url'; description, kb, value, queue: for 'describe', 'limit', 'whenDone', 'queue'
  async act(id, op, url, { description, kb, value, queue } = {}) {
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
      if (d.kind === 'video') throw new Error('YouTube downloads keep their link');
      const u = new URL(url);
      if (!/^https?:$/.test(u.protocol)) throw new Error('only http and https links are supported');
      d.url = u.href;
    } else if (op === 'describe') {
      d.description = describe(description);
    } else if (op === 'limit') { // HTTP and HLS read it per chunk, yt-dlp at its next run
      d.limitKB = clamp(kb, 0, 1e7);
    } else if (op === 'whenDone') {
      d.whenDone = ['open', 'sleep', 'shutdown'].includes(value) ? value : '';
    } else if (op === 'queue') { // into a queue as 'scheduled' (a running one keeps running), or out of every queue (null)
      if (queue == null) {
        d.queue = null;
        if (d.status === 'scheduled') d.status = 'paused';
      } else {
        if (!this.queue(queue)) throw new Error('queue not found');
        if (['pending', 'done', 'removed'].includes(d.status)) throw new Error('only unfinished downloads can go into a queue');
        d.queue = queue;
        if (['paused', 'error', 'queued'].includes(d.status)) d.status = 'scheduled';
      }
    } else if (op === 'up' || op === 'down') { // a queue's order is the list's order among its downloads
      if (!d.queue) throw new Error('not in a queue');
      const same = this.list.filter(x => x.queue === d.queue);
      const o = same[same.indexOf(d) + (op === 'up' ? -1 : 1)];
      if (o) {
        const i = this.list.indexOf(d), j = this.list.indexOf(o);
        [this.list[i], this.list[j]] = [o, d];
      }
    } else if (op === 'remove' || op === 'removeFile') {
      const wasDone = d.status === 'done';
      if (ctl) { d.status = 'removed'; ctl.ac.abort(); await ctl.done; }
      this.list.splice(this.list.indexOf(d), 1);
      if (d.tmp) for (const f of [d.tmp, ...(d.kind === 'hls' ? soundTemps(d) : [])]) await fs.rm(f, { force: true });
      await removeTmp(d);
      if (op === 'removeFile' && wasDone) await fs.rm(d.file, { force: true });
    } else throw new Error('unknown action');
    this.dirty = true;
    this.pump();
  }

  // Tray "Pause all": running queues stop (their downloads wait in them again), other running and waiting downloads pause.
  // Scheduled ones keep their start time.
  pauseAll() {
    for (const id of this.runs.keys()) this.stopQueue(id);
    for (const d of this.list) if (['downloading', 'queued'].includes(d.status)) d.status = 'paused';
    for (const ctl of this.running.values()) ctl.ac.abort();
    this.dirty = true;
  }

  // At most maxActive downloads run, and at most a running queue's own maxActive of its downloads.
  // A queue that is not running holds nothing back: its downloads only run when resumed by hand.
  pump() {
    const n = {}; // running downloads per queue
    for (const d of this.list) {
      if (d.status === 'scheduled' && this.runs.has(d.queue)) d.status = 'queued'; // added to a queue while it runs
      if (d.queue && this.running.has(d.id)) n[d.queue] = (n[d.queue] ?? 0) + 1;
    }
    for (const d of this.list) {
      if (this.running.size >= this.settings.maxActive) break;
      if (d.status !== 'queued' || this.running.has(d.id)) continue; // a paused run may still be stopping
      const q = this.runs.has(d.queue) && this.queue(d.queue);
      if (q && (n[q.id] ?? 0) >= q.maxActive) continue;
      if (q) n[q.id] = (n[q.id] ?? 0) + 1;
      this.start(d);
    }
  }

  queue(id) {
    return this.settings.queues.find(q => q.id === id);
  }

  // A new or changed queue name: 1–40 characters, not another queue's (case does not count)
  queueName(name, id) {
    const n = cleanName(name), taken = this.settings.queues.some(q => q.id !== id && q.name.toLowerCase() === n.toLowerCase());
    if (!n || n.length > 40 || taken) throw new Error(taken ? `There is already a queue named "${n}"` : 'A queue name needs 1 to 40 characters');
    return n;
  }

  // The Scheduler (POST /api/queues): add {name, …fields} → {id}; update {id, …fields}; remove, start, stop {id} → {ok}
  queues({ op, id, ...s } = {}) {
    const t = this.settings;
    if (op === 'add') {
      const q = queueFields({ id: crypto.randomUUID(), name: this.queueName(s.name) }, s);
      t.queues.push(q);
      this.dirty = true;
      return { id: q.id };
    }
    const q = this.queue(id);
    if (!q) throw new Error('queue not found');
    if (op === 'update') {
      if (id === MAIN && s.name != null && s.name !== q.name) throw new Error('The main queue keeps its name');
      Object.assign(q, queueFields({ ...q, name: s.name == null ? q.name : this.queueName(s.name, id) }, s));
    } else if (op === 'remove') {
      if (id === MAIN) throw new Error('The main queue cannot be removed');
      if (this.runs.has(id)) this.stopQueue(id); // its downloads wait in the main queue instead
      for (const d of this.list) if (d.queue === id) d.queue = MAIN;
      t.queues.splice(t.queues.indexOf(q), 1);
    } else if (op === 'start') this.startQueue(id);
    else if (op === 'stop') this.stopQueue(id);
    else throw new Error('unknown queue action');
    this.dirty = true;
    this.pump();
    return { ok: true };
  }

  startQueue(id) {
    if (!this.queue(id)) throw new Error('queue not found');
    if (!this.runs.has(id)) this.runs.set(id, { tries: new Map(), waiting: new Map(), worked: false });
    this.dirty = true;
    this.pump(); // its 'scheduled' downloads become 'queued' there
  }

  // No pump() here: Pause all must not start other downloads in between; callers pump
  stopQueue(id) {
    this.runs.delete(id);
    for (const d of this.list) {
      if (d.queue !== id || !['downloading', 'queued'].includes(d.status)) continue;
      d.status = 'scheduled'; // paused like Pause, but it stays in the queue
      this.running.get(d.id)?.ac.abort();
    }
    this.dirty = true;
  }

  start(d) {
    const ctl = { ac: new AbortController(), active: new Set(), bytes: 0, last: 0, speed: 0, limiter: new Limiter() };
    this.running.set(d.id, ctl);
    this.worked = true;
    d.status = 'downloading';
    d.lastTry = Date.now();
    d.error = null;
    const run = this.runs.get(d.queue);
    if (run) run.worked = true;
    ctl.done = (async () => {
      try {
        await (d.kind === 'hls' ? downloadHls(d, ctl, this) : d.kind === 'video' ? downloadVideo(d, ctl, this) : this.runHttp(d, ctl));
        if (!ctl.ac.signal.aborted) {
          d.status = 'done';
          d.finished = Date.now();
          delete d.headers.cookie; // no reason to keep a login session on disk once the file is here
          if (d.whenDone === 'open') this.opener(d);
          else if (d.whenDone) this.countdown(d.whenDone, false); // even while other downloads still run
        }
      } catch (e) {
        if (d.status === 'downloading') { d.status = 'error'; d.error = e.cause?.message || e.message; }
        // its queue tries it again in 10 s (tick()), up to the queue's retries in this run
        const run = this.runs.get(d.queue), tries = run?.tries.get(d.id) ?? 0;
        if (d.status === 'error' && run && tries < this.queue(d.queue).retries) {
          run.tries.set(d.id, tries + 1);
          run.waiting.set(d.id, Date.now() + 10000);
        }
      }
      this.running.delete(d.id);
      this.dirty = true;
      this.pump();
    })();
  }

  // A chunk waits for the global limit and for this download's own one
  async take(d, ctl, n) {
    await this.limiter.take(n);
    ctl.limiter.rate = (d.limitKB ?? 0) * 1024;
    await ctl.limiter.take(n);
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

  // The file name: the download's own name, else its title, else the server's, with the server's extension
  baseName(d, suggested) {
    suggested ||= 'download';
    let name = suggested;
    if (d.title) name = d.title + path.extname(suggested);
    if (d.name) name = path.extname(d.name) ? d.name : d.name + path.extname(suggested);
    return sanitize(name);
  }

  // A file already chosen in the Download File Info window (confirm()) is kept.
  assignName(d, suggested) {
    if (d.file) return;
    const name = this.baseName(d, suggested), ext = path.extname(name);
    const cat = Object.keys(CATEGORIES).find(c => CATEGORIES[c].includes(ext.slice(1).toLowerCase())) ?? 'Other';
    const dir = this.settings.folders[cat] ?? (this.settings.categories && cat !== 'Other' ? path.join(this.settings.dir, cat) : this.settings.dir);
    mkdirSync(dir, { recursive: true });
    this.setFile(d, this.uniqueFile(dir, name));
  }

  // A name taken on disk, half downloaded (.fdl) or by another download gets " (1)", " (2)", …
  uniqueFile(dir, name) {
    const ext = path.extname(name), base = name.slice(0, name.length - ext.length);
    const taken = f => existsSync(f) || existsSync(f + '.fdl') || this.list.some(x => x.file === f);
    let file = path.join(dir, name);
    for (let n = 1; taken(file); n++) file = path.join(dir, `${base} (${n})${ext}`);
    return file;
  }

  setFile(d, file) {
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
              await this.take(d, ctl, n);
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

  // now: the clock the queues' start and stop times and retries use (tests pass their own)
  tick(now = new Date()) {
    const stale = d => d.status === 'pending' && Date.now() - d.added >= this.pendingMs;
    if (this.list.some(stale)) this.list = this.list.filter(d => !stale(d));
    for (const ctl of this.running.values()) {
      ctl.speed = ctl.speed * 0.3 + (ctl.bytes - ctl.last) * 0.7;
      ctl.last = ctl.bytes;
    }
    // Queues start at startAt on their days and stop at stopAt, each once a day: a start or stop by hand in that minute sticks
    const hm = now.toTimeString().slice(0, 5), day = now.toDateString();
    const due = (q, what, at) => at === hm && this.fired.get(q.id + what) !== day + at && this.fired.set(q.id + what, day + at);
    for (const q of this.settings.queues) {
      if (due(q, ' start', q.startAt) && (!q.days.length || q.days.includes(now.getDay()))) this.startQueue(q.id);
      if (due(q, ' stop', q.stopAt) && this.runs.has(q.id)) this.stopQueue(q.id);
    }
    for (const [id, run] of this.runs) {
      for (const [did, at] of run.waiting) {
        if (+now < at) continue;
        run.waiting.delete(did);
        const d = this.list.find(x => x.id === did);
        if (d?.status === 'error' && d.queue === id) { d.status = 'queued'; this.dirty = true; }
      }
    }
    this.pump();
    // A running queue with nothing left to do stops; its own whenDone counts down even while other downloads run
    for (const [id, run] of this.runs) {
      if (run.waiting.size || this.list.some(d => d.queue === id && ['queued', 'downloading', 'scheduled'].includes(d.status))) continue;
      this.runs.delete(id);
      const q = this.queue(id);
      if (run.worked && q.whenDone) this.countdown(q.whenDone, false); // a queue started with nothing in it does nothing
    }
    // Paused and failed downloads don't wait; scheduled ones do when their queue runs or has a start time, and so do retries.
    const busy = this.list.some(d => ['downloading', 'queued'].includes(d.status) || (d.status === 'scheduled' && (this.runs.has(d.queue) || !!this.queue(d.queue)?.startAt)))
      || [...this.runs.values()].some(r => r.waiting.size);
    if (this.powerAll && busy) this.powerAt = 0; // new work came in
    if (this.whenDone && !busy && this.worked && (!this.powerAt || this.powerAll)) this.countdown(this.whenDone, true); // the last download just ended
    else if (this.powerAt && Date.now() >= this.powerAt) {
      const op = this.powerOp;
      if (this.powerAll) this.whenDone = '';
      this.powerAt = 0;
      this.power(op);
    }
    this.worked = busy;
    if (this.dirty || this.running.size) this.save();
  }

  // The one 60 s countdown: "When all downloads finish" (all = true), or a download's or a queue's own whenDone
  countdown(op, all) {
    this.powerOp = op;
    this.powerAll = all;
    this.powerAt = Date.now() + this.powerDelayMs;
  }

  async save() {
    if (this.saving) return; // the next tick catches up
    this.saving = true;
    this.dirty = false;
    try {
      const tmp = this.stateFile + '.tmp';
      await fs.writeFile(tmp, this.json());
      await fs.rename(tmp, this.stateFile);
    } catch (e) {
      this.dirty = true;
      console.error('could not save state:', e.message);
    } finally {
      this.saving = false;
    }
  }

  // pending downloads are never saved: they live only as long as their Download File Info window
  json() {
    return JSON.stringify({ settings: this.settings, list: this.list.filter(d => d.status !== 'pending') });
  }

  saveSync() {
    writeFileSync(this.stateFile + '.tmp', this.json());
    renameSync(this.stateFile + '.tmp', this.stateFile);
  }

  view() {
    return {
      settings: this.settings,
      downloads: this.list.map(d => {
        const ctl = this.running.get(d.id);
        return {
          id: d.id, url: d.url, name: d.name ?? d.title, file: d.file, size: d.size, status: d.status, error: d.error,
          resumable: d.resumable, kind: d.kind, hls: d.hls, title: d.title, phase: d.phase, quality: d.quality, format: d.format, added: d.added, probe: d.probe,
          description: d.description ?? null, limitKB: d.limitKB ?? 0, whenDone: d.whenDone ?? '', lastTry: d.lastTry ?? null, notify: !!d.notify, queue: d.queue ?? null,
          got: d.status === 'done' ? d.size : d.kind === 'hls' ? d.hls?.bytes ?? 0 : d.kind === 'video' ? d.got ?? 0 : (d.segments ?? []).reduce((a, s) => a + s.pos - s.start, 0),
          speed: ctl ? Math.round(ctl.speed) : 0,
          connections: ctl ? ctl.active.size : 0,
          segments: d.status === 'done' ? null : d.segments?.map(s => [s.start, s.end, s.pos, ctl?.active.has(s) ? 1 : 0]), // 1: a connection works on it now
        };
      }),
      queues: this.settings.queues.map(q => {
        const own = this.list.filter(d => d.queue === q.id);
        const count = (...st) => own.filter(d => st.includes(d.status)).length;
        return { ...q, running: this.runs.has(q.id), waiting: count('scheduled', 'queued'), active: count('downloading'), total: own.length };
      }),
      whenDone: this.whenDone, // the status bar's "When done" choice
      powerOp: this.powerAt ? this.powerOp : null, // what the running countdown does: its strip names it
      powerIn: this.powerAt ? Math.max(0, Math.ceil((this.powerAt - Date.now()) / 1000)) : null, // seconds left
      exts: Object.values(CATEGORIES).flat(), // file types the UI offers from the clipboard
      categories: CATEGORIES, // the UI's sidebar sorts by these
    };
  }
}
