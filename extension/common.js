export const APP = 'http://127.0.0.1:9614';

export const appAlive = () => fetch(APP + '/api/ping').then(r => r.ok, () => false);

const YT_HOSTS = new Set(['youtube.com', 'www.youtube.com', 'm.youtube.com', 'music.youtube.com']);
const YT_ID = /^[\w-]{11}$/;
const parse = url => { try { return new URL(url); } catch { return null; } };

// A single YouTube video page: watch?v=, /shorts/, /live/, /embed/ on the YouTube hosts, or youtu.be/ID.
export function isYouTubeVideo(url) {
  const u = parse(url);
  if (!u || !/^https?:$/.test(u.protocol)) return false;
  if (u.hostname === 'youtu.be') return YT_ID.test(u.pathname.slice(1));
  if (!YT_HOSTS.has(u.hostname)) return false;
  if (u.pathname === '/watch') return YT_ID.test(u.searchParams.get('v') ?? '');
  const m = u.pathname.match(/^\/(?:shorts|live|embed)\/([^/]+)\/?$/);
  return !!m && YT_ID.test(m[1]);
}

const isYouTubeHost = h => h === 'youtu.be' || h === 'youtube.com' || h.endsWith('.youtube.com');

// Hands a link to the app with the browser's cookies and referrer, so logged-in downloads work.
// The browser's user agent is left out: servers drop clients that claim to be Chrome but aren't.
// YouTube login cookies never leave the browser: the app does not need them for public videos.
export async function send({ url, referrer, name, title, quality }) {
  const headers = {};
  if (referrer) headers.referer = referrer;
  const host = parse(url)?.hostname ?? '';
  const cookies = isYouTubeHost(host) ? [] : await chrome.cookies.getAll({ url }).catch(() => []);
  if (cookies.length) headers.cookie = cookies.map(c => `${c.name}=${c.value}`).join('; ');
  const res = await fetch(APP + '/api/add', {
    method: 'POST',
    headers: { 'content-type': 'application/json' },
    body: JSON.stringify({ url, name, title, headers, quality }),
  }).catch(() => null);
  return !!res?.ok;
}
