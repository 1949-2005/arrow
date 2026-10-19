import { APP, send, appAlive, isYouTubeVideo } from './common.js';

const $ = s => document.querySelector(s);
const size = n => !n ? '' : n < 1048576 ? (n / 1024).toFixed(0) + ' KB' : (n / 1048576).toFixed(1) + ' MB';

const alive = await appAlive();
$('#status').textContent = alive ? 'App is running.' : 'App is not running. Start it from the Start menu.';
$('#status').className = alive ? '' : 'off';

const { intercept = true } = await chrome.storage.local.get('intercept');
$('#intercept').checked = intercept;
$('#intercept').onchange = e => chrome.storage.local.set({ intercept: e.target.checked });
$('#open').onclick = () => chrome.tabs.create({ url: APP });

const [tab] = await chrome.tabs.query({ active: true, currentWindow: true });

$('#yt').hidden = !isYouTubeVideo(tab.url);
if (!$('#yt').hidden) {
  const { quality = 'best' } = await chrome.storage.local.get('quality');
  $('#quality').value = quality;
  $('#quality').onchange = e => chrome.storage.local.set({ quality: e.target.value });
  $('#ytdl').onclick = async () => {
    const b = $('#ytdl');
    b.disabled = true;
    const ok = await send({ url: tab.url, quality: $('#quality').value });
    b.textContent = ok ? 'Sent ✓' : 'Failed. Is FastDL running?';
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
    b.textContent = await send({ url: m.url, referrer: tab.url, title: tab.title }) ? 'Sent ✓' : 'Failed';
  };
  li.append(label, info, b);
  $('#media').append(li);
}
