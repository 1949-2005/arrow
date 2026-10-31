import { APP, sendConfirm, appAlive, pickMedia, isYouTubeVideo, youTubeId, playerData, optionsFromPlayer } from './common.js';

chrome.runtime.onInstalled.addListener(async ({ reason }) => {
  chrome.contextMenus.create({ id: 'fastdl', title: 'Download with Glide', contexts: ['link', 'video', 'audio', 'image'] });
  if (reason !== 'install' && reason !== 'update') return;
  // Open tabs keep the previous content.js, cut off from the extension: give them the new one, which replaces it in place
  // (no reload). Restricted and discarded tabs refuse; they get it on their next load.
  for (const tab of await chrome.tabs.query({ url: ['http://*/*', 'https://*/*'] }).catch(() => [])) {
    chrome.scripting.executeScript({ target: { tabId: tab.id, allFrames: true }, files: ['content.js'] }).catch(() => {});
  }
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
// ytdlp.js FORMAT_KEY, and hls.js HLS_KEY (ts:<bandwidth>); the app checks it against the video's list too
const FORMAT_KEY = /^(?:(?:mp4|webm):[\w-]+(?:\+[\w-]+)?|m4a:[\w-]+|mp3|ts:\d+)$/;
const isStr = (x, max = 8192) => typeof x === 'string' && x.length <= max;
const httpUrl = x => isStr(x) && /^https?:\/\//i.test(x) && URL.canParse(x);

// The video button's format list, from the app's POST /api/formats: a YouTube or other yt-dlp site's page, or an HLS
// stream's qualities (referrer: its page). One request per URL while this worker lives.
const formatLists = new Map();
function formats(url, referrer) {
  let list = formatLists.get(url);
  if (!list) {
    list = fetch(APP + '/api/formats', { method: 'POST', headers: { 'content-type': 'application/json' }, body: JSON.stringify({ url, ...(referrer && { referrer }) }) })
      .then(r => r.json())
      .then(j => Array.isArray(j?.options) ? { title: String(j.title ?? ''), options: j.options } : { error: String(j?.error || 'Glide could not list the formats.') },
        () => ({ error: 'Glide is not running.' }));
    formatLists.set(url, list);
    list.then(r => { if (r.error) formatLists.delete(url); }); // failures are asked again next time
  }
  return list;
}

chrome.runtime.onMessage.addListener((msg, sender, reply) => {
  if (!['video', 'formats'].includes(msg?.type) || sender.id !== chrome.runtime.id || !sender.tab) return;
  if (msg.type === 'formats') {
    if (!httpUrl(msg.pageUrl) || (msg.src !== undefined && !isStr(msg.src))) return reply({ error: 'Bad request.' });
    if (!isYouTubeVideo(msg.pageUrl)) {
      // another site: what the button would send (pickMedia), with its qualities when that is a video page or an HLS
      // stream; options [] when it is one plain file, which the button then sends at once
      const k = 'tab' + sender.tab.id;
      chain.then(() => chrome.storage.session.get(k)).then(got => {
        const pick = pickMedia({ pageUrl: msg.pageUrl, src: msg.src ?? '', title: '', media: got[k] ?? [] });
        if (pick.error) return pick;
        if (pick.url === msg.pageUrl) return formats(pick.url);
        return /\.m3u8$/i.test(new URL(pick.url).pathname) ? formats(pick.url, msg.pageUrl) : { title: '', options: [] };
      }).catch(() => ({ error: 'Glide is not running.' })).then(reply);
      return true; // reply comes later
    }
    const app = formats(msg.pageUrl); // also warms the app's list for its Download File Info window
    // YouTube's own player in the page already has the formats: at once, instead of yt-dlp's 3 s; the app's list is the fallback
    Promise.resolve().then(() => chrome.scripting.executeScript({
      target: { tabId: sender.tab.id, frameIds: [sender.frameId] }, world: 'MAIN', func: playerData, args: [youTubeId(msg.pageUrl)],
    })).then(([r]) => {
      const options = optionsFromPlayer(r?.result);
      if (!options.length) throw new Error('no formats in the page');
      return { title: String(r.result.title ?? ''), options };
    }).catch(() => app).then(reply);
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
    reply(await sendConfirm(pick) ? { ok: true } : { error: 'Glide is not running.' });
  }).catch(() => reply({ error: 'Glide is not running.' }));
  return true; // reply comes later
});
