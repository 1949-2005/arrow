import fs from 'node:fs/promises';
import { existsSync, readdirSync, statSync } from 'node:fs';
import path from 'node:path';
import readline from 'node:readline';
import { spawn, execFile } from 'node:child_process';
import { promisify } from 'node:util';
import { markOfTheWeb, sanitize } from './engine.js';

// Legacy choices, kept for downloads saved before the format list (and the Add URL dialog).
export const QUALITIES = ['best', '1080', '720', '480', 'mp3'];
// A format key names exactly what gets downloaded: mp4:137+140, webm:248+251, m4a:140, mp3.
// The ids are yt-dlp format_ids ("140", "140-drc"); keys are also checked against the video's list before use.
export const FORMAT_KEY = /^(?:(?:mp4|webm):[\w-]+\+[\w-]+|m4a:[\w-]+|mp3)$/;
const MP3_KBPS = 192; // constant bitrate, so the MP3's size is known up front: duration × 24000 bytes
const FORMATS_MS = 15 * 60000;
const run = promisify(execFile);
const HOSTS = ['youtube.com', 'www.youtube.com', 'm.youtube.com', 'music.youtube.com'];
const HINT = ' (if YouTube changed something: winget upgrade yt-dlp.yt-dlp)';
const LIVE = 'Live streams are not supported';
const errorText = line => line.replace(/^ERROR:\s*(\[[^\]]+\]\s*([\w-]+:\s*)?)?/, '');

// The file extension of a format key, or of a legacy quality.
export const extOf = (format, quality) => '.' + (format ?? (quality === 'mp3' ? 'mp3' : 'mp4')).split(':')[0];

// Other sites yt-dlp downloads from (with www., m. and the like in front): their pages' video, as YouTube's
export const SITES = ['vimeo.com', 'dailymotion.com', 'x.com', 'twitter.com', 'tiktok.com', 'instagram.com', 'facebook.com', 'fb.watch',
  'reddit.com', 'twitch.tv', 'soundcloud.com', 'bilibili.com', 'streamable.com'];

// A page link of one of SITES → its site, null for anything else (YouTube included: youtubeId() has its rules)
export function videoSite(u) {
  const h = u.hostname.toLowerCase();
  return SITES.find(s => h === s || h.endsWith('.' + s)) ?? null;
}

// YouTube link → video ID, null for other sites. Channels, playlists and the like are refused.
export function youtubeId(u) {
  if (u.hostname !== 'youtu.be' && !HOSTS.includes(u.hostname)) return null;
  const id = u.hostname === 'youtu.be' ? u.pathname.slice(1)
    : u.pathname === '/watch' ? u.searchParams.get('v')
    : /^\/(?:shorts|live|embed)\/([^/]*)\/?$/.exec(u.pathname)?.[1];
  if (!/^[\w-]{11}$/.test(id ?? '')) throw new Error('Paste the link of one video (youtube.com/watch?v=…, youtu.be/…, /shorts/…), not a channel or playlist');
  return id;
}

const exe = n => process.platform === 'win32' ? n + '.exe' : n;
const subdirs = (dir, prefix) => { try { return readdirSync(dir).filter(n => n.startsWith(prefix)).map(n => path.join(dir, n)); } catch { return []; } };
const onPath = n => (process.env.PATH ?? '').split(path.delimiter).filter(Boolean).map(p => path.join(p, exe(n)));
// WinGet installs land here; a FastDL started before the install still has the old PATH.
const winget = (...p) => process.env.LOCALAPPDATA ? path.join(process.env.LOCALAPPDATA, 'Microsoft', 'WinGet', ...p) : '';

export function findYtdlp() {
  const f = [process.env.FASTDL_YTDLP, ...onPath('yt-dlp'), winget('Links', 'yt-dlp.exe'),
    ...subdirs(winget('Packages'), 'yt-dlp.yt-dlp_').map(p => path.join(p, 'yt-dlp.exe'))].find(f => f && existsSync(f));
  if (!f) throw new Error('yt-dlp is not installed. Run: winget install yt-dlp.yt-dlp');
  return f;
}

// --ffmpeg-location takes the binary or its folder; the folder also gives yt-dlp ffprobe.
export function findFfmpeg() {
  if (process.env.FASTDL_FFMPEG && existsSync(process.env.FASTDL_FFMPEG)) return process.env.FASTDL_FFMPEG;
  const f = [...onPath('ffmpeg'), ...subdirs(winget('Packages'), 'yt-dlp.FFmpeg_').flatMap(p => subdirs(p, 'ffmpeg-'))
    .map(p => path.join(p, 'bin', 'ffmpeg.exe'))].find(existsSync);
  if (!f) throw new Error('ffmpeg is not installed. Run: winget install yt-dlp.FFmpeg');
  return path.dirname(f);
}

// The program itself (HLS joins a separate sound track with it); FASTDL_FFMPEG may name it or its folder.
export function ffmpegExe() {
  const f = findFfmpeg();
  return statSync(f).isDirectory() ? path.join(f, exe('ffmpeg')) : f;
}

function formatArgs(d) {
  if (d.format != null && !FORMAT_KEY.test(d.format)) throw new Error(`unknown format "${d.format}"`);
  const [kind, ids] = (d.format ?? '').split(':');
  if (kind === 'mp3' || (!d.format && d.quality === 'mp3')) return ['-f', 'ba/b', '-x', '--audio-format', 'mp3', '--audio-quality', MP3_KBPS + 'K'];
  if (kind === 'mp4' || kind === 'webm') return ['-f', ids, '--merge-output-format', kind];
  if (kind === 'm4a') return ['-f', ids];
  const q = d.quality ?? 'best';
  // at equal resolution prefer H.264: AV1/VP9 inside MP4 doesn't play in every player
  return ['-f', 'bv*+ba/b', '-S', q === 'best' ? 'res,vcodec:h264,ext:mp4:m4a' : `res:${q},vcodec:h264,ext:mp4:m4a`, '--merge-output-format', 'mp4'];
}

export function ytdlpArgs(d, settings, ffmpeg) {
  // the lower of the global limit (when on) and this download's own; read once per run
  const kb = Math.min(...[settings.speedLimitOn && settings.speedLimitKB, d.limitKB].filter(k => k > 0));
  return [
    // --ignore-config: a user yt-dlp.conf could add cookies or change the output names we parse
    '--ignore-config', '--no-playlist', '--match-filter', '!is_live', '--newline', '--no-colors',
    '--ffmpeg-location', ffmpeg, '-P', d.tmpDir, '-o', 'media.%(ext)s',
    ...formatArgs(d),
    ...(kb < Infinity ? ['--limit-rate', `${kb}K`] : []),
    ...(settings.proxy ? ['--proxy', settings.proxy] : []),
    // fragments at once (a recording YouTube is still processing comes in pieces); --limit-rate holds per fragment, so one while limited
    '-N', String(kb < Infinity ? 1 : settings.connections || 1),
    // --print implies --quiet, which would hide the progress lines
    '--no-quiet',
    '--print', 'before_dl:FDLINFO %(format_id)s %(filesize,filesize_approx)s %(title)j',
    '--progress-template', 'download:FDLP %(info.format_id)s %(info.vcodec)s %(progress.status)s %(progress.downloaded_bytes)s %(progress.total_bytes,progress.total_bytes_estimate)s',
    '--progress-template', 'postprocess:FDLPP %(progress.postprocessor)s',
    '--print', 'after_move:FDLFILE %(filepath)j',
    '--', d.url,
  ];
}

// The video's formats with their real sizes, as the format list shows them. Cached per video for 15 minutes;
// callers that come while yt-dlp is still running share that run.
export async function listFormats(mgr, url) {
  const u = new URL(url);
  const yt = youtubeId(u);
  if (!yt && !videoSite(u)) throw new Error('Only video links from YouTube and the other supported sites have a format list');
  const id = yt ?? u.href; // the cache's key
  const page = yt ? `https://www.youtube.com/watch?v=${yt}` : u.href;
  const hit = mgr.formatCache.get(id);
  if (hit && Date.now() - hit.at < FORMATS_MS) return hit.list;
  const list = readFormats(mgr, page, !!yt).catch(e => {
    if (mgr.formatCache.get(id)?.list === list) mgr.formatCache.delete(id);
    throw e;
  });
  mgr.formatCache.set(id, { at: Date.now(), list });
  return list;
}

async function readFormats(mgr, page, youtube) {
  const cmd = mgr.ytdlp ?? [findYtdlp()];
  let out;
  try {
    // skip=hls,dash: the list only offers https formats, so the stream manifests are not fetched (about 0.6 s less)
    ({ stdout: out } = await run(cmd[0], [...cmd.slice(1), '-J', '--no-playlist', '--ignore-config', '--no-warnings', ...(youtube ? ['--extractor-args', 'youtube:skip=hls,dash'] : []),
      ...(mgr.settings?.proxy ? ['--proxy', mgr.settings.proxy] : []), '--', page],
      { windowsHide: true, maxBuffer: 64 * 1024 * 1024, timeout: 120000 }));
  } catch (e) {
    const line = /^ERROR:.*$/m.exec(e.stderr ?? '')?.[0];
    throw new Error(line ? errorText(line) : e.killed ? 'yt-dlp took too long to answer' : e.message);
  }
  const info = JSON.parse(out);
  if (info.is_live) throw new Error(LIVE);
  return { title: String(info.title ?? ''), options: await formatOptions(info, mgr) };
}

// The exact size of a format without filesize: a one-byte range request with the format's own headers.
async function exactSize(mgr, f) {
  const headers = Object.fromEntries(Object.entries(f.http_headers ?? {}).map(([k, v]) => [k.toLowerCase(), String(v)]).filter(([k]) => k !== 'cookie'));
  try {
    const res = await mgr.get(f.url, { headers, cookieHost: null }, AbortSignal.timeout(15000), 'bytes=0-0');
    res.body?.cancel().catch(() => {});
    return res.status === 206 ? Number(/\/(\d+)\s*$/.exec(res.headers.get('content-range') ?? '')?.[1]) || null : null;
  } catch {
    return null;
  }
}

// yt-dlp's -J → [{key, group, label, tag?, approx?, size}]. Every size is real: yt-dlp's filesize, else the range answer,
// else the format is left out. MP4/WEBM sizes are the video plus the audio stream. A recording YouTube is still processing
// (post_live) only has http_dash_segments formats: their size is yt-dlp's filesize_approx, and their options get approx: true.
export async function formatOptions(info, mgr) {
  const p = f => Math.min(f.width ?? f.height, f.height); // shorts are 1080x1920: YouTube calls that 1080p
  const usable = (info.formats ?? []).filter(f => ['https', 'http_dash_segments'].includes(f.protocol) && f.url && /^[\w-]+$/.test(f.format_id ?? ''));
  const video = usable.filter(f => f.vcodec && f.vcodec !== 'none' && f.acodec === 'none' && ['mp4', 'webm'].includes(f.ext)
    && (!f.dynamic_range || f.dynamic_range === 'SDR') && p(f) >= 144);
  const audio = usable.filter(f => f.vcodec === 'none' && f.acodec && f.acodec !== 'none');
  const dash = f => f.protocol === 'http_dash_segments'; // in pieces: a range request would give one piece's size
  const approx = (...list) => list.some(dash) && { approx: true };
  const sized = new Map(await Promise.all([...video, ...audio.filter(f => ['m4a', 'webm'].includes(f.ext))]
    .map(async f => [f, f.filesize > 0 ? f.filesize : dash(f) ? f.filesize_approx : await exactSize(mgr, f)])));
  const ok = list => list.filter(f => sized.get(f) > 0);
  const tag = h => h >= 720 && h <= 1080 ? 'HD' : h === 1440 ? '2K' : h === 2160 ? '4K' : null;
  // yt-dlp lists formats worst first, so the last one is the best
  const m4a = ok(audio.filter(f => f.ext === 'm4a')).at(-1), opus = ok(audio.filter(f => f.ext === 'webm')).at(-1);
  const options = [];
  for (const [group, ext, a] of [['MP4', 'mp4', m4a], ['WEBM', 'webm', opus]]) {
    if (!a) continue;
    const vids = ok(video.filter(f => f.ext === ext));
    for (const h of [...new Set(vids.map(p))].sort((x, y) => y - x)) {
      const same = vids.filter(f => p(f) === h);
      // at equal height prefer H.264: AV1/VP9 inside MP4 doesn't play in every player
      const v = (ext === 'mp4' && same.filter(f => f.vcodec.startsWith('avc1')).at(-1)) || same.at(-1);
      options.push({ key: `${ext}:${v.format_id}+${a.format_id}`, group, label: `${h}p${v.fps >= 50 ? '60' : ''}`,
        ...(tag(h) && { tag: tag(h) }), ...approx(v, a), size: sized.get(v) + sized.get(a) });
    }
  }
  if (m4a) options.push({ key: `m4a:${m4a.format_id}`, group: 'Audio', label: 'M4A', ...approx(m4a), size: sized.get(m4a) });
  if (audio.length && info.duration > 0) options.push({ key: 'mp3', group: 'Audio', label: 'MP3', size: Math.round(info.duration * MP3_KBPS * 1000 / 8) });
  return options;
}

export async function removeTmp(d) {
  if (!d.tmpDir) return;
  await fs.rm(d.tmpDir, { recursive: true, force: true, maxRetries: 5 });
  await fs.rmdir(path.dirname(d.tmpDir)).catch(() => {}); // .fastdl itself, only once empty
  delete d.tmpDir;
}

const num = s => /^\d+(\.\d+)?$/.test(s) ? Math.round(Number(s)) : null; // yt-dlp prints NA when unknown

// One yt-dlp run per attempt; resume re-runs it and yt-dlp continues its .part files in d.tmpDir.
export async function downloadVideo(d, ctl, mgr) {
  const signal = ctl.ac.signal;
  const cmd = mgr.ytdlp ?? [findYtdlp()];
  const ffmpeg = mgr.ffmpeg ?? findFfmpeg();
  d.tmpDir ??= path.join(mgr.settings.dir, '.fastdl', d.id);
  await fs.mkdir(d.tmpDir, { recursive: true });
  d.streams ??= {}; // format_id → {got, size}, kept across pauses so the bar does not restart
  d.phase = null;
  let expected = null, error = null, live = false, file = null;

  const total = () => {
    let got = 0, size = 0;
    for (const s of Object.values(d.streams)) { got += s.got; size += s.size ?? s.got; }
    d.got = got;
    d.size = Math.max(expected ?? size, got);
  };
  const onLine = line => {
    let m;
    if ((m = /^FDLINFO (\S+) (\S+) (.*)$/.exec(line))) {
      const ids = m[1].split('+');
      for (const k of Object.keys(d.streams)) if (!ids.includes(k)) delete d.streams[k]; // leftovers from other formats
      expected = num(m[2]);
      try { d.title = sanitize(JSON.parse(m[3])); } catch {}
      total();
    } else if ((m = /^FDLP (\S+) (\S+) (\S+) (\S+) (\S+)$/.exec(line))) {
      const [, id, vcodec, status, got, size] = m;
      const s = d.streams[id] ??= { got: 0, size: null };
      const now = num(got) ?? (status === 'finished' ? num(size) ?? s.got : s.got); // "already downloaded" has no byte count
      if (id in (ctl.seen ??= {})) ctl.bytes += Math.max(0, now - s.got); // first line of a run may include resumed bytes
      ctl.seen[id] = true;
      s.got = now;
      s.size = num(size) ?? s.size;
      d.phase = vcodec === 'none' ? 'audio' : 'video';
      total();
    } else if ((m = /^FDLPP (\S+)/.exec(line))) {
      if (m[1] === 'Merger') d.phase = 'merging';
      if (m[1] === 'ExtractAudio') d.phase = 'converting';
    } else if ((m = /^FDLFILE (.*)$/.exec(line))) {
      try { file = JSON.parse(m[1]); } catch {}
    } else if (line.startsWith('ERROR:')) {
      error ??= errorText(line);
    } else if (/does not pass filter \(!is_live\)/.test(line)) live = true;
  };

  // a key from an old list or another client: only keys this video offers reach -f
  if (d.format && !(await listFormats(mgr, d.url)).options.some(o => o.key === d.format)) {
    throw new Error(`This video no longer offers the format ${d.format}. Download it again.`);
  }
  if (signal.aborted) return;
  const proc = spawn(cmd[0], [...cmd.slice(1), ...ytdlpArgs(d, mgr.settings, ffmpeg)],
    { stdio: ['ignore', 'pipe', 'pipe'], windowsHide: true, detached: process.platform !== 'win32' });
  readline.createInterface({ input: proc.stdout }).on('line', onLine);
  readline.createInterface({ input: proc.stderr }).on('line', onLine);
  let killed;
  // yt-dlp.exe is a launcher with a child process, and ffmpeg may be running: kill the whole tree
  const kill = () => {
    killed = process.platform === 'win32'
      ? new Promise(r => spawn('taskkill', ['/PID', String(proc.pid), '/T', '/F'], { stdio: 'ignore', windowsHide: true }).on('error', r).on('close', r))
      : (() => { try { process.kill(-proc.pid, 'SIGKILL'); } catch {} })();
  };
  signal.addEventListener('abort', kill, { once: true });
  let code;
  try {
    code = await new Promise((resolve, reject) => proc.on('error', reject).on('close', resolve));
  } finally {
    signal.removeEventListener('abort', kill);
    await killed;
  }
  if (signal.aborted) return;
  if (live || /live event/i.test(error ?? '')) throw new Error(LIVE);
  if (error) throw new Error(error + HINT);
  if (code !== 0) throw new Error(`yt-dlp stopped with exit code ${code}${HINT}`);
  if (!file) throw new Error(`yt-dlp did not produce a file${HINT}`);

  const src = path.join(d.tmpDir, path.basename(file));
  d.size = (await fs.stat(src)).size;
  mgr.assignName(d, 'video' + path.extname(src));
  delete d.tmp; // assignName's .fdl name; yt-dlp works in d.tmpDir instead
  await fs.rename(src, d.file).catch(async e => {
    if (e.code !== 'EXDEV') throw e;
    await fs.copyFile(src, d.file);
    await fs.rm(src);
  });
  await removeTmp(d);
  delete d.streams;
  delete d.phase;
  await markOfTheWeb(d);
}
