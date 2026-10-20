import { APP, sendConfirm, appAlive, pickMedia, isYouTubeVideo } from './common.js';

chrome.runtime.onInstalled.addListener(() => {
  chrome.contextMenus.create({ id: 'fastdl', title: 'Download with Arrow', contexts: ['link', 'video', 'audio', 'image'] });
});

chrome.contextMenus.onClicked.addListener((info, tab) => sendConfirm({ url: info.linkUrl || info.srcUrl, referrer: tab?.url }));

// Take over downloads the browser starts. When the app is closed, the browser keeps the download.
chrome.downloads.onCreated.addListener(async item => {
  const url = item.finalUrl || item.url;
  if (item.byExtensionId === chrome.runtime.id || !/^https?:/i.test(url)) return;
  const { intercept = true } = await chrome.storage.local.get('intercept');
  if (!intercept || !(await appAlive())) return;
  await chrome.downloads.cancel(item.id);
  await chrome.downloads.erase({ id: item.id });
  const id = await sendConfirm({ url, referrer: item.referrer, name: item.filename?.split(/[\\/]/).pop() || undefined });
  if (!id) chrome.downloads.download({ url }); // app refused it: give it back to the browser
});

// Video grabber: remember media and HLS playlists each tab loads.
const SEGMENT = /\.(ts|m4s|aac)(\?|$)/i;
let chain = Promise.resolve();

chrome.webRequest.onHeadersReceived.addListener(info => {
  // YouTube's split audio/video pieces and manifests are useless on their own
  if (info.tabId < 0 || /googlevideo\.com/.test(info.url)) return;
  const h = {};
  for (const x of info.responseHeaders ?? []) h[x.name.toLowerCase()] = x.value;
  const type = (h['content-type'] ?? '').split(';')[0].trim().toLowerCase();
  const size = Number(h['content-range']?.split('/')[1] ?? h['content-length']) || 0;
  const hls = /mpegurl/.test(type) || /\.m3u8(\?|$)/i.test(info.url);
  // skip stream fragments, which are useless on their own
  const media = /^(video|audio)\//.test(type) && type !== 'video/mp2t' && !SEGMENT.test(info.url)
    && (size === 0 || size > 512 * 1024);
  if (hls || media) remember(info.tabId, { url: info.url, type: hls ? 'HLS stream' : type, size });
}, { urls: ['<all_urls>'], types: ['media', 'xmlhttprequest', 'other'] }, ['responseHeaders']);

function remember(tabId, item) {
  chain = chain.then(async () => {
    const k = 'tab' + tabId;
    const list = (await chrome.storage.session.get(k))[k] ?? [];
    if (list.some(x => x.url === item.url)) return;
    list.push(item);
    await chrome.storage.session.set({ [k]: list });
    await chrome.action.setBadgeText({ tabId, text: String(list.length) });
  }).catch(() => {});
}

// queued on the same chain, so a remember() still in flight cannot re-add the old page's media
function forget(tabId) {
  chain = chain.then(async () => {
    await chrome.storage.session.remove('tab' + tabId);
    await chrome.action.setBadgeText({ tabId, text: '' });
  }).catch(() => {});
}

chrome.tabs.onUpdated.addListener((tabId, change) => { if (change.url) forget(tabId); });
chrome.tabs.onRemoved.addListener(forget);

// The video button (content.js). Only our own content scripts can send this, but the frame may be hostile,
// so every field is checked before it reaches the app.
const QUALITIES = ['best', '1080', '720', '480', 'mp3'];
const FORMAT_KEY = /^(?:(?:mp4|webm):[\w-]+\+[\w-]+|m4a:[\w-]+|mp3)$/; // the app checks it against the video's list too
const isStr = (x, max = 8192) => typeof x === 'string' && x.length <= max;
const httpUrl = x => isStr(x) && /^https?:\/\//i.test(x) && URL.canParse(x);

// The video button's format list (YouTube), from the app's POST /api/formats; one request per page while this worker lives.
const formatLists = new Map();
function formats(url) {
  let list = formatLists.get(url);
  if (!list) {
    list = fetch(APP + '/api/formats', { method: 'POST', headers: { 'content-type': 'application/json' }, body: JSON.stringify({ url }) })
      .then(r => r.json())
      .then(j => Array.isArray(j?.options) ? { title: String(j.title ?? ''), options: j.options } : { error: String(j?.error || 'Arrow could not list the formats.') },
        () => ({ error: 'Arrow is not running.' }));
    formatLists.set(url, list);
    list.then(r => { if (r.error) formatLists.delete(url); }); // failures are asked again next time
  }
  return list;
}

chrome.runtime.onMessage.addListener((msg, sender, reply) => {
  if (!['video', 'formats'].includes(msg?.type) || sender.id !== chrome.runtime.id || !sender.tab) return;
  if (msg.type === 'formats') {
    if (!httpUrl(msg.pageUrl) || !isYouTubeVideo(msg.pageUrl)) return reply({ error: 'Bad request.' });
    formats(msg.pageUrl).then(reply);
    return true; // reply comes later
  }
  const { src, pageUrl, title, quality, format } = msg;
  if (!isStr(src) || !httpUrl(pageUrl) || !isStr(title, 1000) || (quality !== undefined && !QUALITIES.includes(quality))
    || (format !== undefined && !(isStr(format, 200) && FORMAT_KEY.test(format)))) {
    reply({ error: 'Bad request.' });
    return;
  }
  const k = 'tab' + sender.tab.id;
  chain.then(() => chrome.storage.session.get(k)).then(async got => {
    const pick = pickMedia({ pageUrl, src, title, quality, format, media: got[k] ?? [] });
    if (pick.error) return reply(pick);
    reply(await sendConfirm(pick) ? { ok: true } : { error: 'Arrow is not running.' });
  }).catch(() => reply({ error: 'Arrow is not running.' }));
  return true; // reply comes later
});
