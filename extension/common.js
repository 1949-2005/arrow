export const APP = 'http://127.0.0.1:9614';

export const appAlive = () => fetch(APP + '/api/ping').then(r => r.ok, () => false);

// Hands a link to the app with the browser's cookies and referrer, so logged-in downloads work.
// The browser's user agent is left out: servers drop clients that claim to be Chrome but aren't.
export async function send({ url, referrer, name, title }) {
  const headers = {};
  if (referrer) headers.referer = referrer;
  const cookies = await chrome.cookies.getAll({ url }).catch(() => []);
  if (cookies.length) headers.cookie = cookies.map(c => `${c.name}=${c.value}`).join('; ');
  const res = await fetch(APP + '/api/add', {
    method: 'POST',
    headers: { 'content-type': 'application/json' },
    body: JSON.stringify({ url, name, title, headers }),
  }).catch(() => null);
  return !!res?.ok;
}
