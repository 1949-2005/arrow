import fs from 'node:fs/promises';
import { existsSync, readdirSync } from 'node:fs';
import path from 'node:path';
import readline from 'node:readline';
import { spawn } from 'node:child_process';
import { markOfTheWeb, sanitize } from './engine.js';

export const QUALITIES = ['best', '1080', '720', '480', 'mp3'];
const HOSTS = ['youtube.com', 'www.youtube.com', 'm.youtube.com', 'music.youtube.com'];
const HINT = ' (if YouTube changed something: winget upgrade yt-dlp.yt-dlp)';
const LIVE = 'Live streams are not supported';

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

export function ytdlpArgs(d, settings, ffmpeg) {
  const q = d.quality ?? 'best';
  return [
    // --ignore-config: a user yt-dlp.conf could add cookies or change the output names we parse
    '--ignore-config', '--no-playlist', '--match-filter', '!is_live', '--newline', '--no-colors',
    '--ffmpeg-location', ffmpeg, '-P', d.tmpDir, '-o', 'media.%(ext)s',
    ...(q === 'mp3' ? ['-f', 'ba/b', '-x', '--audio-format', 'mp3', '--audio-quality', '0']
      // at equal resolution prefer H.264: AV1/VP9 inside MP4 doesn't play in every player
      : ['-f', 'bv*+ba/b', '-S', q === 'best' ? 'res,vcodec:h264,ext:mp4:m4a' : `res:${q},vcodec:h264,ext:mp4:m4a`, '--merge-output-format', 'mp4']),
    ...(settings.speedLimitKB > 0 ? ['--limit-rate', `${settings.speedLimitKB}K`] : []),
    // --print implies --quiet, which would hide the progress lines
    '--no-quiet',
    '--print', 'before_dl:FDLINFO %(format_id)s %(filesize,filesize_approx)s %(title)j',
    '--progress-template', 'download:FDLP %(info.format_id)s %(info.vcodec)s %(progress.status)s %(progress.downloaded_bytes)s %(progress.total_bytes,progress.total_bytes_estimate)s',
    '--progress-template', 'postprocess:FDLPP %(progress.postprocessor)s',
    '--print', 'after_move:FDLFILE %(filepath)j',
    '--', d.url,
  ];
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
      error ??= line.replace(/^ERROR:\s*(\[[^\]]+\]\s*([\w-]+:\s*)?)?/, '');
    } else if (/does not pass filter \(!is_live\)/.test(line)) live = true;
  };

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
