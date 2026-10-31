export const APP = 'http://127.0.0.1:9614';

export const appAlive = () => fetch(APP + '/api/ping').then(r => r.ok, () => false);

const YT_HOSTS = new Set(['youtube.com', 'www.youtube.com', 'm.youtube.com', 'music.youtube.com']);
const YT_ID = /^[\w-]{11}$/;
const parse = url => { try { return new URL(url); } catch { return null; } };

// A single YouTube video page: watch?v=, /shorts/, /live/, /embed/ on the YouTube hosts, or youtu.be/ID. Its video id, else null.
export function youTubeId(url) {
  const u = parse(url);
  if (!u || !/^https?:$/.test(u.protocol)) return null;
  const id = u.hostname === 'youtu.be' ? u.pathname.slice(1) : !YT_HOSTS.has(u.hostname) ? null
    : u.pathname === '/watch' ? u.searchParams.get('v') : u.pathname.match(/^\/(?:shorts|live|embed)\/([^/]+)\/?$/)?.[1];
  return YT_ID.test(id ?? '') ? id : null;
}
export const isYouTubeVideo = url => !!youTubeId(url);

// One video's page on the other sites the app lists with yt-dlp (ytdlp.js SITES): a Twitch VOD or clip, a Vimeo,
// Dailymotion, X post, TikTok, Instagram post or reel, Streamable, Reddit post or Bilibili video, a Kick VOD or clip. The app then offers
// that video's qualities, as IDM's list, instead of the one stream the page happens to play. Channel and live pages
// stay with the grabbed stream (yt-dlp refuses live streams).
const VIDEO_PAGES = [
  [/(^|\.)twitch\.tv$/, /^\/(videos\/\d+|[\w-]+\/clip\/[\w-]+)\/?$/],
  [/^clips\.twitch\.tv$/, /^\/[\w-]+\/?$/],
  [/(^|\.)kick\.com$/, /^\/[\w-]+\/(videos\/[\da-f]{8}-[\da-f]{4}-[\da-f]{4}-[\da-f]{4}-[\da-f]{12}|clips\/clip_[\w-]+)\/?$/],
  [/(^|\.)vimeo\.com$/, /^\/\d+\/?$/],
  [/(^|\.)dailymotion\.com$/, /^\/video\/\w+\/?$/],
  [/(^|\.)(x|twitter)\.com$/, /^\/\w+\/status\/\d+(\/video\/\d+)?\/?$/],
  [/(^|\.)tiktok\.com$/, /^\/@[\w.-]+\/video\/\d+\/?$/],
  [/(^|\.)instagram\.com$/, /^\/(p|reel|reels)\/[\w-]+\/?$/],
  [/(^|\.)streamable\.com$/, /^\/\w+\/?$/],
  [/(^|\.)reddit\.com$/, /^\/r\/\w+\/comments\/\w+/],
  [/(^|\.)bilibili\.com$/, /^\/video\/\w+\/?$/],
];
export function isVideoPage(url) {
  const u = parse(url);
  return !!u && /^https?:$/.test(u.protocol) && VIDEO_PAGES.some(([host, p]) => host.test(u.hostname.toLowerCase()) && p.test(u.pathname));
}

const isYouTubeHost = h => h === 'youtu.be' || h === 'youtube.com' || h.endsWith('.youtube.com');

// Hands a link to the app with the browser's cookies and referrer, so logged-in downloads work.
// The browser's user agent is left out: servers drop clients that claim to be Chrome but aren't.
// YouTube login cookies never leave the browser: the app does not need them for public videos.
// confirm: the app keeps it waiting for the Download File Info window. Returns the download's id, or null.
export async function send({ url, referrer, name, title, quality, format, confirm }) {
  const headers = {};
  if (referrer) headers.referer = referrer;
  const host = parse(url)?.hostname ?? '';
  const cookies = isYouTubeHost(host) ? [] : await chrome.cookies.getAll({ url }).catch(() => []);
  if (cookies.length) headers.cookie = cookies.map(c => `${c.name}=${c.value}`).join('; ');
  const res = await fetch(APP + '/api/add', {
    method: 'POST',
    headers: { 'content-type': 'application/json' },
    body: JSON.stringify({ url, name, title, headers, quality, format, confirm }),
  }).catch(() => null);
  return res?.ok ? (await res.json().catch(() => null))?.id ?? null : null;
}

// Every Download click in the browser: the app keeps the download waiting (confirm: true) and shows IDM's
// Download File Info window itself. Returns the id, or null when the app refused it (not running, or its window closed).
export const sendConfirm = item => send({ ...item, confirm: true });

const isHttp = url => /^https?:$/.test(parse(url)?.protocol ?? '');

// What the video button downloads. `media` is the tab's list from the grabber ({url, type, size}).
// blob: and MSE players have no usable src, so the grabber's HLS playlist or biggest video file stands in.
// On YouTube, format is the key picked in the video button's format list, title the video's title from that list.
// A video page of the other yt-dlp sites goes as the page too: the Download File Info window then lists its qualities.
export function pickMedia({ pageUrl, src, title, quality, format, media = [] }) {
  if (isYouTubeVideo(pageUrl) || isVideoPage(pageUrl)) return { url: pageUrl, quality, format, title };
  if (isHttp(src)) return { url: src, referrer: pageUrl, title };
  const ok = media.filter(m => isHttp(m.url));
  const found = ok.find(m => m.type === 'HLS stream')
    ?? ok.filter(m => /^video\//.test(m.type)).sort((a, b) => b.size - a.size)[0];
  // an HLS stream keeps the quality picked in the video button's list (its key names the playlist's BANDWIDTH)
  return found ? { url: found.url, referrer: pageUrl, title, ...(format && found.type === 'HLS stream' && { format }) }
    : { error: 'No video found yet. Play it for a few seconds, then try again.' };
}

// Runs in the YouTube page's MAIN world (chrome.scripting), so it must be self-contained: the formats YouTube's own
// player already has for video `id`, as plain data, or null. Feeds optionsFromPlayer().
export function playerData(id) {
  try {
    for (const el of document.querySelectorAll('.html5-video-player')) {
      try {
        if (el.getVideoData?.()?.video_id !== id) continue;
        const r = el.getPlayerResponse?.(), d = r?.videoDetails, s = r?.streamingData;
        if (d?.videoId !== id || !Array.isArray(s?.adaptiveFormats)) continue; // the previous video's response, mid-navigation
        return {
          title: String(d.title ?? ''), seconds: Number(d.lengthSeconds), live: !!d.isLive,
          formats: s.adaptiveFormats.map(f => ({
            itag: f.itag, mimeType: f.mimeType, contentLength: f.contentLength, width: f.width, height: f.height, fps: f.fps,
            bitrate: f.averageBitrate ?? f.bitrate, qualityLabel: f.qualityLabel,
            hdr: ['COLOR_TRANSFER_CHARACTERISTICS_SMPTEST2084', 'COLOR_TRANSFER_CHARACTERISTICS_ARIB_STD_B67'].includes(f.colorInfo?.transferCharacteristics)
              || /HDR/.test(f.qualityLabel ?? ''),
            drc: !!f.isDrc, track: f.audioTrack ? { id: f.audioTrack.id, isDefault: f.audioTrack.audioIsDefault } : null,
          })),
        };
      } catch {}
    }
  } catch {}
  return null;
}

const MP3_KBPS = 192; // as ytdlp.js
const EXTS = { 'video/mp4': 'mp4', 'video/webm': 'webm', 'audio/mp4': 'm4a', 'audio/webm': 'webm' };

// playerData() → the same [{key, group, label, tag?, size}] as ytdlp.js formatOptions() for that video, so a pick is a key
// the app knows. The page is not trusted: only numeric itags make keys. Formats without contentLength are left out;
// video with none at all (a recording YouTube is still processing) gives [], so the app's list is used.
export function optionsFromPlayer(data) {
  if (!data || data.live || !Array.isArray(data.formats)) return [];
  const all = data.formats.map(f => {
    const m = /^(\w+\/[\w-]+)\s*(?:;\s*codecs="([^"]*)")?/.exec(typeof f?.mimeType === 'string' ? f.mimeType : '');
    return m && /^\d+$/.test(String(f.itag)) && EXTS[m[1]] && { ...f, id: String(f.itag), ext: EXTS[m[1]], audio: m[1].startsWith('audio/'),
      avc: (m[2] ?? '').startsWith('avc1'), size: Number(f.contentLength), rate: Number(f.bitrate) || 0, p: Math.min(f.width || f.height, f.height) };
  }).filter(Boolean);
  const best = list => list.reduce((a, f) => !a || f.rate > a.rate ? f : a, null);
  // the player can list one itag several times (a DRC copy, other variants): yt-dlp keeps the first, so its size is the one the app shows
  const seen = new Set();
  const sized = all.filter(f => f.size > 0 && !f.drc && (!f.track || f.track.isDefault) && !seen.has(f.id) && seen.add(f.id));
  if (!sized.some(f => !f.audio) && all.some(f => !f.audio)) return [];
  const video = sized.filter(f => !f.audio && !f.hdr && f.p >= 144);
  const audio = sized.filter(f => f.audio);
  const tag = h => h >= 720 && h <= 1080 ? 'HD' : h === 1440 ? '2K' : h === 2160 ? '4K' : null;
  // Several audio tracks (dubbed videos): yt-dlp names one itag's tracks <itag>-0…<itag>-<n-1> in its worst-first order,
  // and the default (original) track sorts last. shortcut: mirrors yt-dlp 2026.08 naming; if it changes, the app's window shows its own list.
  const tracks = {};
  for (const f of all) if (f.audio && f.track && !f.drc) (tracks[f.id] ??= new Set()).add(f.track.id);
  for (const f of audio) if (tracks[f.id]?.size > 1) f.id += '-' + (tracks[f.id].size - 1);
  const m4a = best(audio.filter(f => f.ext === 'm4a')), opus = best(audio.filter(f => f.ext === 'webm'));
  const options = [];
  for (const [group, ext, a] of [['MP4', 'mp4', m4a], ['WEBM', 'webm', opus]]) {
    if (!a) continue;
    const vids = video.filter(f => f.ext === ext);
    for (const h of [...new Set(vids.map(f => f.p))].sort((x, y) => y - x)) {
      const same = vids.filter(f => f.p === h);
      // at equal height prefer H.264, as ytdlp.js
      const v = (ext === 'mp4' && best(same.filter(f => f.avc))) || best(same);
      options.push({ key: `${ext}:${v.id}+${a.id}`, group, label: `${h}p${v.fps >= 50 ? '60' : ''}`, ...(tag(h) && { tag: tag(h) }), size: v.size + a.size });
    }
  }
  if (m4a) options.push({ key: `m4a:${m4a.id}`, group: 'Audio', label: 'M4A', size: m4a.size });
  if (all.some(f => f.audio) && data.seconds > 0) options.push({ key: 'mp3', group: 'Audio', label: 'MP3', size: Math.round(data.seconds * MP3_KBPS * 1000 / 8) });
  return options;
}
