import { APP, sendConfirm, appAlive, isYouTubeVideo } from './common.js';

const $ = s => document.querySelector(s);
const size = n => !n ? '' : n < 1048576 ? (n / 1024).toFixed(0) + ' KB' : (n / 1048576).toFixed(1) + ' MB';

const alive = await appAlive();
$('#status').textContent = alive ? 'App is running.' : 'App is not running. Start it from the Start menu.';
$('#status').className = alive ? '' : 'off';

const { intercept = true } = await chrome.storage.local.get('intercept');
$('#intercept').checked = intercept;
$('#intercept').onchange = e => chrome.storage.local.set({ intercept: e.target.checked });
const { overlay = true } = await chrome.storage.local.get('overlay');
$('#overlay').checked = overlay;
$('#overlay').onchange = e => chrome.storage.local.set({ overlay: e.target.checked });
$('#open').onclick = () => fetch(APP + '/api/open', { method: 'POST' }).catch(() => {}); // the app shows its window

const [tab] = await chrome.tabs.query({ active: true, currentWindow: true });

$('#yt').hidden = !isYouTubeVideo(tab.url);
if (!$('#yt').hidden) {
  $('#ytdl').onclick = async () => { // type and quality are picked in the Download File Info window
    const b = $('#ytdl');
    b.disabled = true;
    const ok = !!await sendConfirm({ url: tab.url, page: tab.url });
    b.textContent = ok ? 'Sent ✓' : 'Failed. Is Glide running?';
    b.disabled = ok;
  };
}

const key = 'tab' + tab.id;
const list = (await chrome.storage.session.get(key))[key] ?? [];
if (!list.length) $('#media').innerHTML = '<li class="empty">No video or audio found yet. Play the video, then open this again.</li>';
for (const m of list) {
  const li = document.createElement('li');
  const label = document.createElement('span');
  const file = new URL(m.url).pathname.split('/').pop();
  try { label.textContent = decodeURIComponent(file) || m.url; } catch { label.textContent = file; }
  label.title = m.url;
  const info = document.createElement('small');
  info.textContent = [m.type, size(m.size)].filter(Boolean).join(' · ');
  const b = document.createElement('button');
  b.textContent = 'Download';
  b.onclick = async () => {
    b.disabled = true;
    b.textContent = await sendConfirm({ url: m.url, referrer: tab.url, page: tab.url, title: tab.title }) ? 'Sent ✓' : 'Failed';
  };
  li.append(label, info, b);
  $('#media').append(li);
}
