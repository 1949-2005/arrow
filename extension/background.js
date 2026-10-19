import { send, appAlive } from './common.js';

chrome.runtime.onInstalled.addListener(() => {
  chrome.contextMenus.create({ id: 'fastdl', title: 'Download with FastDL', contexts: ['link', 'video', 'audio', 'image'] });
});

chrome.contextMenus.onClicked.addListener((info, tab) => {
  send({ url: info.linkUrl || info.srcUrl, referrer: tab?.url });
});

// Take over downloads the browser starts. When the app is closed, the browser keeps the download.
chrome.downloads.onCreated.addListener(async item => {
  const url = item.finalUrl || item.url;
  if (item.byExtensionId === chrome.runtime.id || !/^https?:/i.test(url)) return;
  const { intercept = true } = await chrome.storage.local.get('intercept');
  if (!intercept || !(await appAlive())) return;
  await chrome.downloads.cancel(item.id);
  await chrome.downloads.erase({ id: item.id });
  const ok = await send({ url, referrer: item.referrer, name: item.filename?.split(/[\\/]/).pop() || undefined });
  if (!ok) chrome.downloads.download({ url }); // app refused it: give it back to the browser
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
