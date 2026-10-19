import { test } from 'node:test';
import assert from 'node:assert/strict';
import fs from 'node:fs';
import { execFileSync } from 'node:child_process';

// Fake MV3 browser: every method returns a Promise and is recorded in `calls`.
const calls = [];
const listeners = {};
const local = {}, session = {}, badges = {};
const event = name => ({ addListener: fn => { listeners[name] = fn; } });
const rec = (name, fn = () => {}) => (...args) => { calls.push([name, ...args]); return Promise.resolve(fn(...args)); };
const pick = (store, keys) => Object.fromEntries([keys].flat().filter(k => k in store).map(k => [k, store[k]]));

globalThis.chrome = {
  runtime: { id: 'ext-id', onInstalled: event('onInstalled') },
  contextMenus: { create: rec('contextMenus.create'), onClicked: event('contextMenus.onClicked') },
  downloads: { onCreated: event('downloads.onCreated'), cancel: rec('downloads.cancel'), erase: rec('downloads.erase'), download: rec('downloads.download') },
  storage: {
    local: { get: rec('local.get', k => pick(local, k)), set: rec('local.set', o => { Object.assign(local, o); }) },
    session: {
      get: rec('session.get', k => pick(session, k)),
      set: rec('session.set', o => { Object.assign(session, o); }),
      remove: rec('session.remove', k => { for (const x of [k].flat()) delete session[x]; }),
    },
  },
  action: { setBadgeText: rec('setBadgeText', ({ tabId, text }) => { badges[tabId] = text; }) },
  tabs: {
    onUpdated: event('tabs.onUpdated'), onRemoved: event('tabs.onRemoved'),
    query: rec('tabs.query', () => [{ id: 9, url: tabUrl, title: 'Tab' }]), create: rec('tabs.create'),
  },
  webRequest: { onHeadersReceived: event('webRequest.onHeadersReceived') },
  cookies: { getAll: rec('cookies.getAll', () => [{ name: 'a', value: '1' }, { name: 'b', value: '2' }]) },
};

let tabUrl = 'https://x.com/';

// Fake fetch: `app` is 'up', 'down' (connection refused) or 'error' (/api/add answers 500).
let app = 'up';
const requests = [];
globalThis.fetch = async (url, opts = {}) => {
  requests.push({ url, method: opts.method ?? 'GET', body: opts.body ? JSON.parse(opts.body) : undefined, headers: opts.headers });
  if (app === 'down') throw new TypeError('fetch failed');
  const status = url.endsWith('/api/add') && app === 'error' ? 500 : 200;
  return { ok: status < 300, status };
};

await import('./extension/background.js');
const { isYouTubeVideo, send } = await import('./extension/common.js');

function reset(mode = 'up') {
  calls.length = requests.length = 0;
  app = mode;
  for (const o of [local, session, badges]) for (const k in o) delete o[k];
}
const called = name => calls.filter(c => c[0] === name);
async function until(cond, ms = 1000) {
  const end = Date.now() + ms;
  while (!cond()) {
    if (Date.now() > end) throw new Error('timed out waiting for ' + cond);
    await new Promise(r => setTimeout(r, 5));
  }
}
// let the remember() promise chain drain, for asserting that something did NOT happen
const settle = () => new Promise(r => setTimeout(r, 30));
const media = (tabId, url, headers) => listeners['webRequest.onHeadersReceived']({
  tabId, url, responseHeaders: Object.entries(headers).map(([name, value]) => ({ name, value })),
});
const MB5 = String(5 * 1024 * 1024);

test('media grabber remembers real media and HLS, skips fragments and small files', async () => {
  reset();
  media(-1, 'https://x.com/bg.mp4', { 'Content-Type': 'video/mp4', 'Content-Length': MB5 });
  media(1, 'https://x.com/small.mp4', { 'Content-Type': 'video/mp4', 'Content-Length': '102400' });
  media(1, 'https://x.com/seg1', { 'Content-Type': 'video/MP2T', 'Content-Length': MB5 });
  media(1, 'https://x.com/seg2.ts', { 'Content-Type': 'video/mp4', 'Content-Length': MB5 });
  media(1, 'https://x.com/seg3.m4s?x=1', { 'Content-Type': 'video/mp4', 'Content-Length': MB5 });
  media(1, 'https://rr1.googlevideo.com/videoplayback?id=1', { 'Content-Type': 'video/mp4', 'Content-Length': MB5 });
  media(1, 'https://rr1.googlevideo.com/master.m3u8', { 'Content-Type': 'application/vnd.apple.mpegurl' });
  media(1, 'https://x.com/movie.mp4', { 'Content-Type': 'video/mp4; codecs=avc1', 'Content-Length': MB5 });
  media(1, 'https://x.com/movie.mp4', { 'Content-Type': 'video/mp4', 'Content-Length': MB5 });
  media(1, 'https://x.com/live', { 'Content-Type': 'application/vnd.apple.mpegurl' });
  media(1, 'https://x.com/a/index.m3u8?token=1', { 'Content-Type': 'text/plain' });
  media(1, 'https://x.com/range.webm', { 'Content-Type': 'video/webm', 'Content-Length': '2', 'Content-Range': 'bytes 0-1/123456789' });

  await until(() => badges[1] === '4');
  await settle();
  assert.deepEqual(session.tab1, [
    { url: 'https://x.com/movie.mp4', type: 'video/mp4', size: 5 * 1024 * 1024 },
    { url: 'https://x.com/live', type: 'HLS stream', size: 0 },
    { url: 'https://x.com/a/index.m3u8?token=1', type: 'HLS stream', size: 0 },
    { url: 'https://x.com/range.webm', type: 'video/webm', size: 123456789 },
  ]);
  assert.equal(badges[1], String(session.tab1.length));
  assert.ok(!('tab-1' in session) && !(-1 in badges));
});

test('tab cleanup: navigation and close clear entries and badge', async () => {
  reset();
  media(2, 'https://x.com/v.mp4', { 'Content-Type': 'video/mp4', 'Content-Length': MB5 });
  await until(() => badges[2] === '1');
  listeners['tabs.onUpdated'](2, { status: 'loading' });
  await settle();
  assert.equal(session.tab2.length, 1);
  assert.equal(badges[2], '1');

  listeners['tabs.onUpdated'](2, { url: 'https://y.com/' });
  await until(() => !('tab2' in session) && badges[2] === '');

  media(3, 'https://x.com/v.mp4', { 'Content-Type': 'video/mp4', 'Content-Length': MB5 });
  await until(() => badges[3] === '1');
  listeners['tabs.onRemoved'](3, {});
  await until(() => !('tab3' in session) && badges[3] === '');
});

test('tab cleanup is not undone by a media response still being remembered', async () => {
  reset();
  media(4, 'https://x.com/old.mp4', { 'Content-Type': 'video/mp4', 'Content-Length': MB5 });
  listeners['tabs.onUpdated'](4, { url: 'https://y.com/' }); // navigation before remember() finished
  await settle();
  assert.ok(!('tab4' in session), 'old page media resurrected after navigation');
  assert.equal(badges[4], '');
});

const item = (over = {}) => ({
  id: 7, url: 'https://x.com/redirect', finalUrl: 'https://cdn.x.com/file.zip',
  filename: 'C:\\Users\\me\\Downloads\\file.zip', referrer: 'https://x.com/page', ...over,
});
const takeover = it => listeners['downloads.onCreated'](it);

test('download takeover: cancel, erase, then hand to the app with cookies and referrer', async () => {
  reset();
  await takeover(item());
  assert.deepEqual(calls.filter(c => c[0].startsWith('downloads.')), [['downloads.cancel', 7], ['downloads.erase', { id: 7 }]]);
  const adds = requests.filter(r => r.url.endsWith('/api/add'));
  assert.equal(adds.length, 1);
  assert.equal(adds[0].url, 'http://127.0.0.1:9614/api/add');
  assert.equal(adds[0].method, 'POST');
  assert.deepEqual(adds[0].body, { url: 'https://cdn.x.com/file.zip', name: 'file.zip', headers: { referer: 'https://x.com/page', cookie: 'a=1; b=2' } });
  assert.doesNotMatch(JSON.stringify(requests).toLowerCase(), /user-agent/);
  assert.deepEqual(called('cookies.getAll')[0][1], { url: 'https://cdn.x.com/file.zip' });
  // cancel before erase before add
  const iCancel = calls.findIndex(c => c[0] === 'downloads.cancel');
  const iErase = calls.findIndex(c => c[0] === 'downloads.erase');
  assert.ok(iCancel < iErase);

  reset();
  await takeover(item({ finalUrl: '', filename: '' }));
  assert.equal(requests.at(-1).body.url, 'https://x.com/redirect');
  assert.equal(requests.at(-1).body.name, undefined);
});

test('download takeover is skipped when it should be', async () => {
  for (const [mode, setup, it] of [
    ['down', () => {}, item()],
    ['up', () => { local.intercept = false; }, item()],
    ['up', () => {}, item({ byExtensionId: 'ext-id' })],
    ['up', () => {}, item({ url: 'blob:https://x.com/1', finalUrl: 'blob:https://x.com/1' })],
    ['up', () => {}, item({ url: 'data:text/plain,hi', finalUrl: '' })],
  ]) {
    reset(mode);
    setup();
    await takeover(it);
    assert.equal(called('downloads.cancel').length, 0, JSON.stringify(it));
    assert.equal(requests.filter(r => r.url.endsWith('/api/add')).length, 0);
  }
});

test('download takeover gives the download back when the app refuses it', async () => {
  reset('error');
  await takeover(item());
  assert.equal(called('downloads.cancel').length, 1);
  assert.deepEqual(called('downloads.download'), [['downloads.download', { url: 'https://cdn.x.com/file.zip' }]]);
});

test('context menu sends the link or the media source with the tab as referer', async () => {
  reset();
  listeners['contextMenus.onClicked']({ menuItemId: 'fastdl', linkUrl: 'https://x.com/f.zip' }, { url: 'https://x.com/page' });
  await until(() => requests.some(r => r.url.endsWith('/api/add')));
  assert.equal(requests.at(-1).body.url, 'https://x.com/f.zip');
  assert.deepEqual(requests.at(-1).body.headers, { referer: 'https://x.com/page', cookie: 'a=1; b=2' });

  reset();
  listeners['contextMenus.onClicked']({ menuItemId: 'fastdl', srcUrl: 'https://x.com/v.mp4', mediaType: 'video' }, { url: 'https://x.com/watch' });
  await until(() => requests.some(r => r.url.endsWith('/api/add')));
  assert.equal(requests.at(-1).body.url, 'https://x.com/v.mp4');
  assert.equal(requests.at(-1).body.headers.referer, 'https://x.com/watch');
});

test('onInstalled creates the context menu', () => {
  reset();
  listeners.onInstalled({ reason: 'install' });
  const [[, menu]] = called('contextMenus.create');
  assert.equal(menu.id, 'fastdl');
  assert.deepEqual(menu.contexts, ['link', 'video', 'audio', 'image']);
});

test('extension files are syntactically valid and the manifest points at real files', () => {
  for (const f of ['common.js', 'background.js', 'popup.js']) {
    execFileSync(process.execPath, ['--check', '--input-type=module'], { input: fs.readFileSync(new URL('./extension/' + f, import.meta.url)) });
  }
  const m = JSON.parse(fs.readFileSync(new URL('./extension/manifest.json', import.meta.url), 'utf8'));
  assert.equal(m.manifest_version, 3);
  assert.equal(m.background.type, 'module');
  for (const f of [m.background.service_worker, m.action.default_popup]) {
    assert.ok(fs.existsSync(new URL('./extension/' + f, import.meta.url)), f);
  }
  assert.match(fs.readFileSync(new URL('./extension/popup.html', import.meta.url), 'utf8'), /<script type="module" src="popup.js">/);
});

test('the extension shows the app name from brand.json', () => {
  const read = f => fs.readFileSync(new URL(f, import.meta.url), 'utf8');
  const { name } = JSON.parse(read('./brand.json'));
  const m = JSON.parse(read('./extension/manifest.json'));
  assert.deepEqual([m.name, m.action.default_title], [name, name]);
  const files = ['popup.html', 'popup.js', 'background.js'].map(f => read('./extension/' + f)).join('\n');
  for (const text of [`<h1>${name}</h1>`, `>Open ${name}<`, `'Download with ${name}'`, `Is ${name} running?`]) assert.ok(files.includes(text), text);
});

test('isYouTubeVideo accepts single-video URLs only, on the exact YouTube hosts', () => {
  for (const u of [
    'https://www.youtube.com/watch?v=dQw4w9WgXcQ',
    'https://youtube.com/watch?v=dQw4w9WgXcQ&list=PL1&t=42',
    'https://m.youtube.com/watch?v=a_b-c_d-e_f',
    'https://music.youtube.com/watch?v=dQw4w9WgXcQ',
    'https://www.youtube.com/shorts/dQw4w9WgXcQ',
    'https://www.youtube.com/live/dQw4w9WgXcQ?si=x',
    'https://www.youtube.com/embed/dQw4w9WgXcQ',
    'https://youtu.be/dQw4w9WgXcQ?t=10',
  ]) assert.ok(isYouTubeVideo(u), u);
  for (const u of [
    'https://www.youtube.com/@channel',
    'https://www.youtube.com/channel/UC1234567890',
    'https://www.youtube.com/playlist?list=PL123',
    'https://www.youtube.com/',
    'https://www.youtube.com/watch?v=short',
    'https://www.youtube.com/watch?v=dQw4w9WgXcQ1',
    'https://www.youtube.com/shorts/dQw4w9WgXcQ/extra',
    'https://youtube.com.evil.example/watch?v=dQw4w9WgXcQ',
    'https://evilyoutube.com/watch?v=dQw4w9WgXcQ',
    'https://youtu.be.evil.example/dQw4w9WgXcQ',
    'https://vimeo.com/watch?v=dQw4w9WgXcQ',
    'ftp://www.youtube.com/watch?v=dQw4w9WgXcQ',
    'not a url', undefined,
  ]) assert.ok(!isYouTubeVideo(u), String(u));
});

test('send() passes quality and keeps YouTube cookies in the browser', async () => {
  reset();
  assert.equal(await send({ url: 'https://www.youtube.com/watch?v=dQw4w9WgXcQ', referrer: 'https://www.youtube.com/', quality: '720' }), true);
  assert.deepEqual(requests.at(-1).body, { url: 'https://www.youtube.com/watch?v=dQw4w9WgXcQ', quality: '720', headers: { referer: 'https://www.youtube.com/' } });
  assert.equal(called('cookies.getAll').length, 0);

  reset();
  await send({ url: 'https://youtu.be/dQw4w9WgXcQ' });
  assert.deepEqual(requests.at(-1).body, { url: 'https://youtu.be/dQw4w9WgXcQ', headers: {} });
  assert.equal(called('cookies.getAll').length, 0);

  reset();
  await send({ url: 'https://youtube.com.evil.example/f.zip' });
  assert.equal(requests.at(-1).body.headers.cookie, 'a=1; b=2');
});

test('context menu on a YouTube link sends no cookies', async () => {
  reset();
  listeners['contextMenus.onClicked']({ menuItemId: 'fastdl', linkUrl: 'https://www.youtube.com/watch?v=dQw4w9WgXcQ' }, { url: 'https://www.youtube.com/' });
  await until(() => requests.some(r => r.url.endsWith('/api/add')));
  assert.deepEqual(requests.at(-1).body.headers, { referer: 'https://www.youtube.com/' });
  assert.equal(called('cookies.getAll').length, 0);
});

// Minimal DOM: only what popup.js touches. A fresh module URL runs popup.js again for each popup.
let popups = 0;
async function openPopup(url) {
  const el = () => ({ textContent: '', className: '', value: '', children: [], append(...c) { this.children.push(...c); } });
  const els = {};
  globalThis.document = { querySelector: s => (els[s] ??= el()), createElement: el };
  tabUrl = url;
  await import('./extension/popup.js?' + ++popups);
  return els;
}

test('popup shows the YouTube section only on video pages', async () => {
  reset();
  assert.equal((await openPopup('https://www.youtube.com/watch?v=dQw4w9WgXcQ'))['#yt'].hidden, false);
  assert.equal((await openPopup('https://www.youtube.com/@channel'))['#yt'].hidden, true);
  assert.equal((await openPopup('https://x.com/'))['#yt'].hidden, true);
});

test('popup YouTube button sends url + quality, restores and saves the quality', async () => {
  reset();
  let els = await openPopup('https://www.youtube.com/shorts/dQw4w9WgXcQ');
  assert.equal(els['#quality'].value, 'best');
  els['#quality'].value = '480';
  els['#quality'].onchange({ target: els['#quality'] });
  assert.equal(local.quality, '480');
  await els['#ytdl'].onclick();
  assert.deepEqual(requests.at(-1).body, { url: 'https://www.youtube.com/shorts/dQw4w9WgXcQ', quality: '480', headers: {} });
  assert.equal(els['#ytdl'].textContent, 'Sent ✓');
  assert.equal(els['#ytdl'].disabled, true);

  els = await openPopup('https://youtu.be/dQw4w9WgXcQ');
  assert.equal(els['#quality'].value, '480');

  app = 'down';
  await els['#ytdl'].onclick();
  assert.equal(els['#ytdl'].textContent, 'Failed. Is Arrow running?');
  assert.equal(els['#ytdl'].disabled, false);
});
