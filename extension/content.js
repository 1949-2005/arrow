// "Download with Arrow" button over web video players, like IDM's. Runs in every frame (embedded players
// live in iframes). Content scripts can't be ES modules, so the helpers below are plain functions;
// extension.test.js runs them in Node.

const LABEL = 'Download with Arrow';
const QUALITIES = [['best', 'Best quality'], ['1080', '1080p'], ['720', '720p'], ['480', '480p'], ['mp3', 'MP3 (audio only)']];

// Copy of isYouTubeVideo() in common.js (extension.test.js checks they agree).
const YT_HOSTS = new Set(['youtube.com', 'www.youtube.com', 'm.youtube.com', 'music.youtube.com']);
const YT_ID = /^[\w-]{11}$/;
function isYouTubeVideo(url) {
  let u;
  try { u = new URL(url); } catch { return false; }
  if (!/^https?:$/.test(u.protocol)) return false;
  if (u.hostname === 'youtu.be') return YT_ID.test(u.pathname.slice(1));
  if (!YT_HOSTS.has(u.hostname)) return false;
  if (u.pathname === '/watch') return YT_ID.test(u.searchParams.get('v') ?? '');
  const m = u.pathname.match(/^\/(?:shorts|live|embed)\/([^/]+)\/?$/);
  return !!m && YT_ID.test(m[1]);
}

// Big enough to be a player, not a thumbnail preview or an icon.
const bigEnough = r => r.width >= 200 && r.height >= 112;
const inside = (r, x, y) => x >= r.left && x <= r.right && y >= r.top && y <= r.bottom;

if (typeof document !== 'undefined') main();

function main() {
  const CSS = `
    .box { position: fixed; top: 0; left: 0; transform: translateX(-100%); opacity: 0; visibility: hidden;
      transition: opacity .12s, visibility 0s .12s; font: 600 13px/1 "SF Pro Text", -apple-system, "Segoe UI", system-ui, sans-serif; }
    .box.on { opacity: 1; visibility: visible; transition: opacity .12s; }
    @media (prefers-reduced-motion: reduce) { .box, .box.on { transition: none; } }
    button { all: unset; box-sizing: border-box; display: flex; align-items: center; gap: 6px; height: 30px; padding: 0 12px 0 8px;
      border-radius: 15px; background: rgba(17,24,39,.88); border: 1px solid rgba(255,255,255,.18); color: #fff;
      font: inherit; white-space: nowrap; cursor: pointer; }
    button:hover { background: rgba(17,24,39,.96); }
    button:focus-visible { outline: 2px solid #14B8A6; }
    .menu { position: absolute; top: calc(100% + 4px); right: 0; display: flex; flex-direction: column; align-items: flex-end; gap: 4px; }
    .menu[hidden], .sr { display: none; }`;

  let host, box, btn, label, menu, live, video, hideTimer = 0, busy = false, enabled = false;

  // DOM building without innerHTML, which Trusted Types pages (YouTube) may refuse.
  const make = (ns, tag, attrs = {}, ...kids) => {
    const e = ns ? document.createElementNS(ns, tag) : document.createElement(tag);
    for (const k in attrs) e.setAttribute(k, attrs[k]);
    e.append(...kids);
    return e;
  };
  const h = (...a) => make(null, ...a);
  const s = (...a) => make('http://www.w3.org/2000/svg', ...a);
  // assets/logo.svg at 16 px
  const logo = () => s('svg', { width: 16, height: 16, viewBox: '0 0 256 256', 'aria-hidden': 'true' },
    s('defs', {}, s('linearGradient', { id: 'g', x1: 0, y1: 0, x2: 1, y2: 1 },
      s('stop', { offset: 0, 'stop-color': '#0F766E' }), s('stop', { offset: 1, 'stop-color': '#19C2AE' }))),
    s('rect', { width: 256, height: 256, rx: 58, fill: 'url(#g)' }),
    s('rect', { x: 70, y: 50, width: 14, height: 56, rx: 7, fill: '#fff', opacity: '.5' }),
    s('rect', { x: 172, y: 38, width: 14, height: 44, rx: 7, fill: '#fff', opacity: '.5' }),
    s('rect', { x: 106, y: 38, width: 44, height: 104, rx: 12, fill: '#fff' }),
    s('path', { d: 'M64 130 H192 L128 196 Z', fill: '#fff', stroke: '#fff', 'stroke-width': 12, 'stroke-linejoin': 'round' }),
    s('rect', { x: 70, y: 214, width: 116, height: 16, rx: 8, fill: '#fff' }));

  // Closed shadow root: page CSS can't style it and page scripts can't reach the buttons.
  function build() {
    host = document.createElement('div');
    host.style.cssText = 'all: initial !important; position: fixed !important; top: 0 !important; left: 0 !important; width: 0 !important; height: 0 !important; z-index: 2147483647 !important; display: block !important;';
    const root = host.attachShadow({ mode: 'closed' });
    label = h('span', {}, LABEL);
    btn = h('button', { 'aria-label': LABEL }, logo(), label);
    menu = h('div', { class: 'menu', role: 'group', 'aria-label': 'Quality' },
      ...QUALITIES.map(([q, text]) => h('button', { 'data-q': q, 'aria-label': 'Download ' + text }, text)));
    menu.hidden = true;
    box = h('div', { class: 'box' }, btn, menu);
    live = h('div', { class: 'sr', 'aria-live': 'polite' });
    root.append(h('style', {}, CSS), box, live);
    // keyboard Enter/Space on a <button> fires a trusted click too; scripted clicks are not trusted
    btn.addEventListener('click', e => {
      if (!e.isTrusted || busy) return;
      if (!isYouTubeVideo(location.href)) return grab();
      if (menu.hidden) openMenu(); else closeMenu();
    });
    menu.addEventListener('click', e => {
      const q = e.target.closest?.('button')?.dataset.q;
      if (!e.isTrusted || !q) return;
      closeMenu();
      btn.focus(); // the picked item just disappeared
      chrome.storage.local.set({ quality: q });
      grab(q);
    });
    root.addEventListener('keydown', e => {
      if (e.key === 'Escape' && !menu.hidden) { e.stopPropagation(); closeMenu(); btn.focus(); }
    });
    document.documentElement.append(host);
  }

  async function openMenu() {
    const { quality = 'best' } = await chrome.storage.local.get('quality');
    for (const b of menu.children) {
      b.setAttribute('aria-pressed', String(b.dataset.q === quality));
      b.textContent = (b.dataset.q === quality ? '✓ ' : '') + QUALITIES.find(x => x[0] === b.dataset.q)[1];
    }
    menu.hidden = false;
    btn.setAttribute('aria-expanded', 'true');
    menu.querySelector('[aria-pressed="true"]').focus();
  }
  function closeMenu() {
    if (!menu || menu.hidden) return;
    menu.hidden = true;
    btn.setAttribute('aria-expanded', 'false');
  }

  function say(text) {
    label.textContent = text;
    btn.setAttribute('aria-label', text);
    live.textContent = text === LABEL ? '' : text;
  }

  async function grab(quality) {
    busy = true;
    say('Sending…');
    const msg = { type: 'video', src: video?.currentSrc ?? '', pageUrl: location.href, title: document.title };
    if (quality) msg.quality = quality;
    const res = await chrome.runtime.sendMessage(msg).catch(() => null); // fails after the extension reloads
    say(res?.ok ? 'Sent to Arrow ✓' : res?.error ?? 'Reload the page and try again.');
    setTimeout(() => { busy = false; say(LABEL); }, 2500);
  }

  function place() {
    if (!video || !box?.classList.contains('on')) return;
    const r = video.getBoundingClientRect();
    if (!video.isConnected || !bigEnough(r) || document.fullscreenElement) return hide(true);
    box.style.left = r.right - 8 + 'px';
    box.style.top = r.top + 8 + 'px';
  }

  function show(v) {
    clearTimeout(hideTimer);
    hideTimer = 0;
    if (!host) build();
    if (video !== v) closeMenu();
    video = v;
    box.classList.add('on');
    place();
  }

  // 1.5 s after the pointer left the video and the button (counted from leaving, not from the last move)
  function hide(now) {
    if (!box?.classList.contains('on') || (hideTimer && now !== true)) return;
    clearTimeout(hideTimer);
    hideTimer = setTimeout(() => {
      hideTimer = 0;
      if (busy) return hide(); // keep the result readable
      closeMenu();
      box.classList.remove('on');
      video = null;
    }, now === true ? 0 : 1500);
  }

  // Players often put their controls on top of the <video>, so the pointer is matched by position.
  function videoAt(x, y) {
    for (const v of document.querySelectorAll('video')) {
      const r = v.getBoundingClientRect();
      if (inside(r, x, y) && bigEnough(r) && v.checkVisibility({ opacityProperty: true, visibilityProperty: true })) return v;
    }
  }

  // Chromium already delivers mousemove at most once per frame, so no extra throttling.
  addEventListener('mousemove', e => {
    if (!enabled) return;
    // Not over fullscreen video: only the fullscreen element is drawn, so the button would have to go
    // inside the page's own player, where page scripts and CSS can reach it.
    if (document.fullscreenElement) return hide(true);
    const v = host && e.composedPath().includes(host) ? video : videoAt(e.clientX, e.clientY);
    if (v) show(v);
    else if (video) hide();
  }, { passive: true, capture: true });
  addEventListener('mouseout', e => { if (!e.relatedTarget && video) hide(); }, true); // pointer left the frame
  addEventListener('scroll', place, { passive: true, capture: true });
  addEventListener('resize', place, { passive: true });
  addEventListener('pointerdown', e => { if (host && !e.composedPath().includes(host)) closeMenu(); }, true);

  function setEnabled(on) {
    enabled = on;
    if (on || !host) return;
    clearTimeout(hideTimer);
    hideTimer = 0;
    host.remove();
    host = box = video = null;
    busy = false;
  }
  chrome.storage.local.get('overlay').then(({ overlay = true }) => setEnabled(overlay));
  chrome.storage.onChanged.addListener((change, area) => {
    if (area === 'local' && 'overlay' in change) setEnabled(change.overlay.newValue !== false);
  });
}
