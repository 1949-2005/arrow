import { test } from 'node:test';
import assert from 'node:assert/strict';
import fs from 'node:fs';
import { execFileSync } from 'node:child_process';
import vm from 'node:vm';

// Fake MV3 browser: every method returns a Promise and is recorded in `calls`.
const calls = [];
const listeners = {};
const local = {}, session = {}, badges = {};
const event = name => ({ addListener: fn => { listeners[name] = fn; } });
const rec = (name, fn = () => {}) => (...args) => { calls.push([name, ...args]); return Promise.resolve(fn(...args)); };
const pick = (store, keys) => Object.fromEntries([keys].flat().filter(k => k in store).map(k => [k, store[k]]));

globalThis.chrome = {
  runtime: { id: 'ext-id', onInstalled: event('onInstalled'), onMessage: event('runtime.onMessage') },
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
  windows: { create: rec('windows.create'), getLastFocused: rec('windows.getLastFocused', () => ({ left: 100, top: 40, width: 1400, height: 900 })) },
  cookies: { getAll: rec('cookies.getAll', () => [{ name: 'a', value: '1' }, { name: 'b', value: '2' }]) },
};

let tabUrl = 'https://x.com/';

// Fake fetch: `app` is 'up', 'down' (connection refused) or 'error' (/api/add answers 500, /api/formats an error).
let app = 'up';
const requests = [];
const LIST = { title: 'Me at the zoo', options: [{ key: 'mp4:160+140', group: 'MP4', label: '144p', size: 504566 }, { key: 'mp3', group: 'Audio', label: 'MP3', size: 456000 }] };
globalThis.fetch = async (url, opts = {}) => {
  requests.push({ url, method: opts.method ?? 'GET', body: opts.body ? JSON.parse(opts.body) : undefined, headers: opts.headers });
  if (app === 'down') throw new TypeError('fetch failed');
  const status = /\/api\/(add|formats)$/.test(url) && app === 'error' ? 400 : 200;
  const body = url.endsWith('/api/formats') ? (status === 200 ? LIST : { error: 'Private video' }) : { id: 'dl-1' };
  return { ok: status < 300, status, json: async () => body };
};

await import('./extension/background.js');
const { isYouTubeVideo, send, pickMedia } = await import('./extension/common.js');

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
// The Download File Info window for the app's download dl-1: 560x470, centred on the 1400x900 browser window at 100,40
const dialog = { url: 'http://127.0.0.1:9614/confirm?id=dl-1', type: 'popup', width: 560, height: 470, focused: true, left: 520, top: 255 };

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
  assert.deepEqual(adds[0].body, { url: 'https://cdn.x.com/file.zip', name: 'file.zip', headers: { referer: 'https://x.com/page', cookie: 'a=1; b=2' }, confirm: true });
  assert.deepEqual(called('windows.create'), [['windows.create', dialog]]);
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
    assert.equal(called('windows.create').length, 0);
  }
});

test('download takeover gives the download back when the app refuses it', async () => {
  reset('error');
  await takeover(item());
  assert.equal(called('downloads.cancel').length, 1);
  assert.deepEqual(called('downloads.download'), [['downloads.download', { url: 'https://cdn.x.com/file.zip' }]]);
  assert.equal(called('windows.create').length, 0);
});

test('context menu sends the link or the media source with the tab as referer', async () => {
  reset();
  await listeners['contextMenus.onClicked']({ menuItemId: 'fastdl', linkUrl: 'https://x.com/f.zip' }, { url: 'https://x.com/page' });
  assert.equal(requests.at(-1).body.url, 'https://x.com/f.zip');
  assert.deepEqual(requests.at(-1).body.headers, { referer: 'https://x.com/page', cookie: 'a=1; b=2' });
  assert.equal(requests.at(-1).body.confirm, true);
  assert.deepEqual(called('windows.create'), [['windows.create', dialog]]);

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
  // content scripts are classic scripts, not modules
  execFileSync(process.execPath, ['--check', '--input-type=commonjs'], { input: fs.readFileSync(new URL('./extension/content.js', import.meta.url)) });
  const m = JSON.parse(fs.readFileSync(new URL('./extension/manifest.json', import.meta.url), 'utf8'));
  assert.equal(m.manifest_version, 3);
  assert.equal(m.background.type, 'module');
  assert.deepEqual(m.content_scripts, [{ matches: ['<all_urls>'], js: ['content.js'], all_frames: true, run_at: 'document_idle' }]);
  for (const f of [m.background.service_worker, m.action.default_popup, ...m.content_scripts[0].js]) {
    assert.ok(fs.existsSync(new URL('./extension/' + f, import.meta.url)), f);
  }
  assert.match(fs.readFileSync(new URL('./extension/popup.html', import.meta.url), 'utf8'), /<script type="module" src="popup.js">/);
});

test('the extension shows the app name from brand.json', () => {
  const read = f => fs.readFileSync(new URL(f, import.meta.url), 'utf8');
  const { name } = JSON.parse(read('./brand.json'));
  const m = JSON.parse(read('./extension/manifest.json'));
  assert.deepEqual([m.name, m.action.default_title], [name, name]);
  const files = ['popup.html', 'popup.js', 'background.js', 'content.js'].map(f => read('./extension/' + f)).join('\n');
  for (const text of [`<h1>${name}</h1>`, `>Open ${name}<`, `'Download with ${name}'`, `Is ${name} running?`, `'${name} is not running.'`, `'Download with ${name}'`, `'Sent to ${name} ✓'`,
    `'${name} could not list the formats.'`]) assert.ok(files.includes(text), text);
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

test('send() returns the id of the app\'s download, or null', async () => {
  reset();
  assert.equal(await send({ url: 'https://x.com/a.zip', confirm: true }), 'dl-1');
  assert.equal(requests.at(-1).body.confirm, true);
  reset('error');
  assert.equal(await send({ url: 'https://x.com/a.zip' }), null);
  reset('down');
  assert.equal(await send({ url: 'https://x.com/a.zip' }), null);
});

test('send() passes quality and keeps YouTube cookies in the browser', async () => {
  reset();
  assert.equal(await send({ url: 'https://www.youtube.com/watch?v=dQw4w9WgXcQ', referrer: 'https://www.youtube.com/', quality: '720' }), 'dl-1');
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

test('popup YouTube button opens the Download File Info window; no quality picker any more', async () => {
  reset();
  let els = await openPopup('https://www.youtube.com/shorts/dQw4w9WgXcQ');
  await els['#ytdl'].onclick();
  assert.deepEqual(requests.at(-1).body, { url: 'https://www.youtube.com/shorts/dQw4w9WgXcQ', headers: {}, confirm: true });
  assert.deepEqual(called('windows.create'), [['windows.create', dialog]]);
  assert.equal(els['#ytdl'].textContent, 'Sent ✓');
  assert.equal(els['#ytdl'].disabled, true);
  assert.ok(!('#quality' in els), 'popup.js must not touch a quality select');
  assert.ok(!called('local.get').some(c => [c[1]].flat().includes('quality')), 'no remembered quality');
  const html = fs.readFileSync(new URL('./extension/popup.html', import.meta.url), 'utf8');
  assert.doesNotMatch(html, /<select|quality/i);

  reset('down');
  els = await openPopup('https://youtu.be/dQw4w9WgXcQ');
  await els['#ytdl'].onclick();
  assert.equal(els['#ytdl'].textContent, 'Failed. Is Arrow running?');
  assert.equal(els['#ytdl'].disabled, false);
  assert.equal(called('windows.create').length, 0);
});

test('popup media rows open the Download File Info window too', async () => {
  reset();
  session.tab9 = [{ url: 'https://cdn.x.com/v.mp4', type: 'video/mp4', size: 5e6 }];
  let els = await openPopup('https://x.com/watch');
  const [li] = els['#media'].children, b = li.children[2];
  await b.onclick();
  assert.deepEqual(requests.at(-1).body, { url: 'https://cdn.x.com/v.mp4', title: 'Tab', headers: { referer: 'https://x.com/watch', cookie: 'a=1; b=2' }, confirm: true });
  assert.deepEqual(called('windows.create'), [['windows.create', dialog]]);
  assert.equal(b.textContent, 'Sent ✓');

  reset('error');
  session.tab9 = [{ url: 'https://cdn.x.com/v.mp4', type: 'video/mp4', size: 5e6 }];
  els = await openPopup('https://x.com/watch');
  const b2 = els['#media'].children[0].children[2];
  await b2.onclick();
  assert.equal(b2.textContent, 'Failed');
  assert.equal(called('windows.create').length, 0);
});

test('popup "Show download button on videos" defaults on and saves to overlay', async () => {
  reset();
  let els = await openPopup('https://x.com/');
  assert.equal(els['#overlay'].checked, true);
  els['#overlay'].onchange({ target: { checked: false } });
  assert.equal(local.overlay, false);
  els = await openPopup('https://x.com/');
  assert.equal(els['#overlay'].checked, false);
});

test('pickMedia: YouTube page, http src, then the grabbed HLS or biggest video, else an error', () => {
  const yt = 'https://www.youtube.com/watch?v=dQw4w9WgXcQ', page = 'https://x.com/watch/1';
  // YouTube: the page, with the picked format and the title passed through (the app names the file from yt-dlp's title)
  assert.deepEqual(pickMedia({ pageUrl: yt, src: 'blob:https://www.youtube.com/1', title: 'T', quality: '720', media: [] }), { url: yt, quality: '720', format: undefined, title: 'T' });
  assert.deepEqual(pickMedia({ pageUrl: yt, src: '', title: 'Me at the zoo', format: 'mp3' }), { url: yt, quality: undefined, format: 'mp3', title: 'Me at the zoo' });
  assert.deepEqual(pickMedia({ pageUrl: 'https://www.youtube.com/embed/dQw4w9WgXcQ', src: '' }), { url: 'https://www.youtube.com/embed/dQw4w9WgXcQ', quality: undefined, format: undefined, title: undefined });
  assert.deepEqual(pickMedia({ pageUrl: page, src: 'https://cdn.x.com/v.mp4', title: 'T', quality: '720', media: [{ url: 'https://x.com/a.m3u8', type: 'HLS stream' }] }),
    { url: 'https://cdn.x.com/v.mp4', referrer: page, title: 'T' });
  const media = [
    { url: 'blob:https://x.com/9', type: 'HLS stream', size: 0 },
    { url: 'https://x.com/small.mp4', type: 'video/mp4', size: 1e6 },
    { url: 'https://x.com/live.m3u8', type: 'HLS stream', size: 0 },
    { url: 'https://x.com/second.m3u8', type: 'HLS stream', size: 0 },
  ];
  assert.deepEqual(pickMedia({ pageUrl: page, src: 'blob:https://x.com/1', title: 'T', media }), { url: 'https://x.com/live.m3u8', referrer: page, title: 'T' });
  const files = [
    { url: 'https://x.com/song.mp3', type: 'audio/mpeg', size: 9e9 },
    { url: 'https://x.com/small.mp4', type: 'video/mp4', size: 1e6 },
    { url: 'data:video/mp4,x', type: 'video/mp4', size: 9e9 },
    { url: 'https://x.com/big.webm', type: 'video/webm', size: 5e6 },
  ];
  assert.deepEqual(pickMedia({ pageUrl: page, src: '', title: 'T', media: files }), { url: 'https://x.com/big.webm', referrer: page, title: 'T' });
  const none = { error: 'No video found yet. Play it for a few seconds, then try again.' };
  assert.deepEqual(pickMedia({ pageUrl: page, src: 'blob:https://x.com/1', media: [] }), none);
  assert.deepEqual(pickMedia({ pageUrl: page, src: '', media: [files[0], files[2]] }), none);
  assert.deepEqual(pickMedia({ pageUrl: page, src: 'javascript:alert(1)' }), none);
});

// The video button's message: returns what the handler replied (undefined when it ignored the message).
async function videoMsg(msg, sender = { id: 'ext-id', tab: { id: 5 } }) {
  let reply;
  const later = listeners['runtime.onMessage'](msg, sender, r => { reply = r; });
  if (later === true) await until(() => reply);
  return reply;
}
const vmsg = (over = {}) => ({ type: 'video', src: 'blob:https://x.com/1', pageUrl: 'https://x.com/p', title: 'Clip', ...over });

test('video button message: sends the picked media with the page as referrer', async () => {
  reset();
  session.tab5 = [{ url: 'https://cdn.x.com/v.mp4', type: 'video/mp4', size: 5e6 }];
  assert.deepEqual(await videoMsg(vmsg()), { ok: true });
  assert.deepEqual(requests.at(-1).body, { url: 'https://cdn.x.com/v.mp4', title: 'Clip', headers: { referer: 'https://x.com/p', cookie: 'a=1; b=2' }, confirm: true });
  assert.deepEqual(called('windows.create'), [['windows.create', dialog]]);

  reset(); // YouTube without a pick ("Download anyway"): the window opens without a preselected format
  assert.deepEqual(await videoMsg(vmsg({ pageUrl: 'https://www.youtube.com/watch?v=dQw4w9WgXcQ' })), { ok: true });
  assert.deepEqual(requests.at(-1).body, { url: 'https://www.youtube.com/watch?v=dQw4w9WgXcQ', title: 'Clip', headers: {}, confirm: true });
  assert.deepEqual(called('windows.create'), [['windows.create', dialog]]);

  reset(); // a pick in the format list: format and title go to the app, the window opens with them
  assert.deepEqual(await videoMsg(vmsg({ pageUrl: 'https://www.youtube.com/watch?v=dQw4w9WgXcQ', title: 'Me at the zoo', format: 'mp4:160+140' })), { ok: true });
  assert.deepEqual(requests.at(-1).body, { url: 'https://www.youtube.com/watch?v=dQw4w9WgXcQ', title: 'Me at the zoo', format: 'mp4:160+140', headers: {}, confirm: true });
  assert.deepEqual(called('windows.create'), [['windows.create', dialog]]);

  reset();
  assert.deepEqual(await videoMsg(vmsg()), { error: 'No video found yet. Play it for a few seconds, then try again.' });
  assert.equal(requests.length, 0);
  assert.equal(called('windows.create').length, 0);

  reset('down');
  assert.deepEqual(await videoMsg(vmsg({ src: 'https://cdn.x.com/v.mp4' })), { error: 'Arrow is not running.' });
  assert.equal(called('windows.create').length, 0);
});

test('video button message: other senders and bad fields are refused', async () => {
  reset();
  session.tab5 = [{ url: 'https://cdn.x.com/v.mp4', type: 'video/mp4', size: 5e6 }];
  assert.equal(await videoMsg(vmsg(), { id: 'other-ext', tab: { id: 5 } }), undefined);
  assert.equal(await videoMsg(vmsg(), { id: 'ext-id' }), undefined);
  assert.equal(await videoMsg({ type: 'other' }), undefined);
  for (const bad of [
    { pageUrl: 'file:///C:/x.html' }, { pageUrl: 'javascript:alert(1)' }, { pageUrl: 42 }, { pageUrl: undefined },
    { src: 42 }, { src: undefined }, { title: { x: 1 } }, { title: 'x'.repeat(1001) }, { quality: 'best"; rm' }, { quality: 720 },
    { format: 'bv*+ba/b' }, { format: 'mp4:137' }, { format: 137 }, { format: 'mp3 --exec x' },
  ]) assert.deepEqual(await videoMsg(vmsg(bad)), { error: 'Bad request.' }, JSON.stringify(bad));
  assert.equal(requests.length, 0);
});

test('formats message: YouTube pages only, proxied to /api/formats, one request per page', async () => {
  reset();
  const yt = 'https://www.youtube.com/watch?v=jNQXAC9IVRw';
  const msg = (pageUrl, sender) => videoMsg({ type: 'formats', pageUrl }, sender);
  assert.deepEqual(await msg(yt), LIST);
  assert.deepEqual(await msg(yt), LIST);
  const asked = requests.filter(r => r.url.endsWith('/api/formats'));
  assert.deepEqual(asked.map(r => [r.url, r.method, r.body]), [['http://127.0.0.1:9614/api/formats', 'POST', { url: yt }]], 'cached per page');
  for (const bad of ['https://x.com/v', 'https://www.youtube.com/@channel', 'javascript:alert(1)', 42]) assert.deepEqual(await msg(bad), { error: 'Bad request.' }, String(bad));
  assert.equal(await msg(yt, { id: 'other-ext', tab: { id: 5 } }), undefined);
  assert.equal(await msg(yt, { id: 'ext-id' }), undefined);

  reset('error'); // the app's error comes through, and is not cached
  const other = 'https://youtu.be/dQw4w9WgXcQ';
  assert.deepEqual(await msg(other), { error: 'Private video' });
  reset('down');
  assert.deepEqual(await msg(other), { error: 'Arrow is not running.' });
  reset();
  assert.deepEqual(await msg(other), LIST);
});

// content.js is a classic script; without `document` it only defines its helpers.
const contentSrc = fs.readFileSync(new URL('./extension/content.js', import.meta.url), 'utf8');
const content = vm.runInNewContext(contentSrc + '\n;({ bigEnough, inside, isYouTubePage, menuGroups, sizeText, step })', { URL });

test('content.js: other sites send on one trusted click; YouTube opens the format list; picks send format and title', () => {
  assert.match(contentSrc, /if \(!e\.isTrusted \|\| busy\) return;\s+if \(!isYouTubePage\(location\.href\)\) grab\(\);/); // \s+: git may check out CRLF
  assert.match(contentSrc, /if \(e\.isTrusted\) onPick\(\);/, 'menu rows: trusted clicks only');
  assert.match(contentSrc, /if \(!e\.isTrusted\) return;\s+e\.stopPropagation\(\);/, 'menu keys: trusted only');
  assert.match(contentSrc, /\{ type: 'formats', pageUrl \}/);
  assert.match(contentSrc, /grab\(\{ pageUrl, title: res\.title \|\| undefined, format: o\.key \}\)/);
  assert.match(contentSrc, /\{ type: 'video', src: video\?\.currentSrc \?\? '', pageUrl: pick\.pageUrl \?\? location\.href, title: pick\.title \?\? document\.title, \.\.\.\(pick\.format && \{ format: pick\.format \}\) \}/);
  assert.match(contentSrc, /'Download anyway'/);
  assert.match(contentSrc, /max-height: 392px; overflow: auto/);
  assert.match(contentSrc, /prefers-reduced-motion: reduce\) \{[^}]*\} \.spin \{ animation: none !important; \}/);
});

test('content.js helpers: the YouTube check agrees with common.js, menu groups, sizes, arrow keys', () => {
  for (const u of ['https://www.youtube.com/watch?v=dQw4w9WgXcQ', 'https://youtu.be/dQw4w9WgXcQ?t=1', 'https://www.youtube.com/embed/dQw4w9WgXcQ',
    'https://m.youtube.com/shorts/dQw4w9WgXcQ/', 'https://www.youtube.com/@channel', 'https://youtube.com.evil.example/watch?v=dQw4w9WgXcQ',
    'https://www.youtube.com/watch?v=short', 'ftp://www.youtube.com/watch?v=dQw4w9WgXcQ', 'https://x.com/', 'nope']) {
    assert.equal(content.isYouTubePage(u), isYouTubeVideo(u), u);
  }
  const o = (group, label) => ({ key: label, group, label, size: 1 });
  assert.deepEqual(JSON.parse(JSON.stringify(content.menuGroups([o('Audio', 'M4A'), o('MP4', '1080p'), o('MP4', '720p'), o('Audio', 'MP3')]))), [
    { title: 'VIDEO · MP4', items: [o('MP4', '1080p'), o('MP4', '720p')] },
    { title: 'AUDIO', items: [o('Audio', 'M4A'), o('Audio', 'MP3')] },
  ]);
  assert.equal(content.menuGroups([]).length, 0);
  assert.deepEqual([content.sizeText(126353408), content.sizeText(456000), content.sizeText(900), content.sizeText(6291456000)], ['120.5 MB', '445.3 KB', '900 B', '5.86 GB']);
  assert.deepEqual([content.step(-1, 3, 'ArrowDown'), content.step(-1, 3, 'ArrowUp'), content.step(2, 3, 'ArrowDown'), content.step(0, 3, 'ArrowUp'), content.step(1, 3, 'ArrowDown')], [0, 2, 0, 2, 2]);
});

test('content.js helpers: size and hit checks', () => {
  assert.ok(content.bigEnough({ width: 200, height: 112 }));
  assert.ok(!content.bigEnough({ width: 199, height: 360 }));
  assert.ok(!content.bigEnough({ width: 640, height: 111 }));
  const r = { left: 10, top: 10, right: 650, bottom: 370 };
  assert.ok(content.inside(r, 10, 370) && content.inside(r, 300, 200));
  assert.ok(!content.inside(r, 651, 200) && !content.inside(r, 300, 9));
});

test('content.js shows the app name from brand.json', () => {
  const { name } = JSON.parse(fs.readFileSync(new URL('./brand.json', import.meta.url), 'utf8'));
  for (const text of [`'Download with ${name}'`, `'Sent to ${name} \u2713'`]) assert.ok(contentSrc.includes(text), text);
});
