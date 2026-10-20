// "Download with Arrow" button over web video players, like IDM's. Runs in every frame (embedded players
// live in iframes). One click sends it; the app's Download File Info window asks the rest.
// On YouTube a click first shows the video's formats with their real sizes (the app's /api/formats, through background.js);
// the picked one opens the Download File Info window with that format.
// Content scripts can't be ES modules, so the helpers below are plain functions; extension.test.js runs them in Node.

const LABEL = 'Download with Arrow';

// Big enough to be a player, not a thumbnail preview or an icon.
const bigEnough = r => r.width >= 200 && r.height >= 112;
const inside = (r, x, y) => x >= r.left && x <= r.right && y >= r.top && y <= r.bottom;

// A single YouTube video page, as common.js isYouTubeVideo() (extension.test.js checks they agree).
function isYouTubePage(href) {
  let u;
  try { u = new URL(href); } catch { return false; }
  if (!/^https?:$/.test(u.protocol)) return false;
  const id = u.hostname === 'youtu.be' ? u.pathname.slice(1)
    : !['youtube.com', 'www.youtube.com', 'm.youtube.com', 'music.youtube.com'].includes(u.hostname) ? ''
    : u.pathname === '/watch' ? u.searchParams.get('v')
    : /^\/(?:shorts|live|embed)\/([^/]+)\/?$/.exec(u.pathname)?.[1];
  return /^[\w-]{11}$/.test(id ?? '');
}

// The format list's groups, in the app's order: [{title, items}], empty groups left out.
const GROUPS = [['MP4', 'VIDEO · MP4'], ['WEBM', 'VIDEO · WEBM'], ['Audio', 'AUDIO']];
const menuGroups = options => GROUPS.map(([g, title]) => ({ title, items: options.filter(o => o.group === g) })).filter(g => g.items.length);
// As confirm.html size(), so the list and the window show the same number
const sizeText = n => n < 1024 ? n + ' B' : n < 1048576 ? (n / 1024).toFixed(1) + ' KB' : n < 1073741824 ? (n / 1048576).toFixed(1) + ' MB' : (n / 1073741824).toFixed(2) + ' GB';
// Up/Down through n items, wrapping; from nothing focused (-1) Down goes to the first, Up to the last.
const step = (i, n, key) => i < 0 ? (key === 'ArrowUp' ? n - 1 : 0) : (i + (key === 'ArrowUp' ? n - 1 : 1)) % n;

if (typeof document !== 'undefined') main();

function main() {
  const CSS = `
    .box { position: fixed; top: 0; left: 0; transform: translateX(-100%); opacity: 0; visibility: hidden;
      display: flex; flex-direction: column; align-items: flex-end; gap: 6px;
      transition: opacity .12s, visibility 0s .12s; font: 600 13px/1 "SF Pro Text", -apple-system, "Segoe UI", system-ui, sans-serif; }
    .box.on { opacity: 1; visibility: visible; transition: opacity .12s; }
    @media (prefers-reduced-motion: reduce) { .box, .box.on { transition: none; } .spin { animation: none !important; } }
    button { all: unset; box-sizing: border-box; cursor: pointer; }
    .pill { display: flex; align-items: center; gap: 6px; height: 30px; padding: 0 12px 0 8px;
      border-radius: 15px; background: rgba(17,24,39,.88); border: 1px solid rgba(255,255,255,.18); color: #fff;
      font: inherit; white-space: nowrap; }
    .pill:hover { background: rgba(17,24,39,.96); }
    .pill:focus-visible { outline: 2px solid #14B8A6; }
    .chev { width: 12px; height: 12px; opacity: .7; margin-left: 2px; }
    .menu { box-sizing: border-box; width: 292px; max-height: 392px; overflow: auto; padding: 6px; border-radius: 12px; background: rgba(17,24,39,.96);
      border: 1px solid rgba(255,255,255,.18); box-shadow: 0 10px 28px rgba(0,0,0,.45); color: #fff; font-weight: 400; }
    .menu[hidden] { display: none; }
    h3 { margin: 8px 10px 4px; font-size: 11px; font-weight: 600; letter-spacing: .3px; color: rgba(255,255,255,.55); }
    .item { display: flex; align-items: center; gap: 10px; width: 100%; padding: 7px 10px; border-radius: 7px; font-size: 13px; }
    .item:hover, .item:focus { background: rgba(255,255,255,.1); }
    .item:focus { outline: 2px solid #14B8A6; outline-offset: -2px; }
    .q { font-weight: 600; min-width: 56px; }
    .tag { font-size: 10.5px; font-weight: 700; padding: 1px 5px; border-radius: 4px; background: rgba(20,184,166,.22); color: #5EEAD4; }
    .sz { margin-left: auto; color: rgba(255,255,255,.6); }
    .note { display: flex; align-items: center; gap: 8px; padding: 10px; color: rgba(255,255,255,.75); line-height: 1.4; }
    .spin { flex: none; box-sizing: border-box; width: 14px; height: 14px; border-radius: 50%; border: 2px solid rgba(255,255,255,.25);
      border-top-color: #14B8A6; animation: spin .8s linear infinite; }
    @keyframes spin { to { transform: rotate(360deg); } }
    .sr { display: none; }`;

  let host, root, box, btn, label, chev, menu, live, video, hideTimer = 0, busy = false, enabled = false, asked = 0;

  // DOM building without innerHTML, which Trusted Types pages (YouTube) may refuse.
  const make = (ns, tag, attrs = {}, ...kids) => {
    const e = ns ? document.createElementNS(ns, tag) : document.createElement(tag);
    for (const k in attrs) e.setAttribute(k, attrs[k]);
    e.append(...kids.filter(k => k != null));
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
    root = host.attachShadow({ mode: 'closed' });
    label = h('span', {}, LABEL);
    chev = s('svg', { class: 'chev', viewBox: '0 0 24 24', fill: 'none', stroke: 'currentColor', 'stroke-width': '2.5', 'stroke-linecap': 'round', 'stroke-linejoin': 'round', 'aria-hidden': 'true' },
      s('path', { d: 'm6 9 6 6 6-6' }));
    btn = h('button', { class: 'pill', 'aria-label': LABEL }, logo(), label, chev);
    menu = h('div', { class: 'menu', role: 'menu', 'aria-label': 'Choose a format', hidden: '' });
    box = h('div', { class: 'box' }, btn, menu);
    live = h('div', { class: 'sr', 'aria-live': 'polite' });
    root.append(h('style', {}, CSS), box, live);
    // keyboard Enter/Space on a <button> fires a trusted click too; scripted clicks are not trusted
    btn.addEventListener('click', e => {
      if (!e.isTrusted || busy) return;
      if (!isYouTubePage(location.href)) grab();
      else if (menu.hidden) openMenu();
      else closeMenu();
    });
    menu.addEventListener('keydown', e => {
      if (!e.isTrusted) return;
      e.stopPropagation(); // the page's own shortcuts (YouTube: arrows seek and change the volume)
      const items = [...menu.querySelectorAll('.item')];
      if ((e.key === 'ArrowDown' || e.key === 'ArrowUp') && items.length) {
        e.preventDefault();
        items[step(items.indexOf(root.activeElement), items.length, e.key)].focus();
      } else if (e.key === 'Escape') {
        e.preventDefault();
        closeMenu();
        btn.focus();
      }
    });
    document.documentElement.append(host);
  }

  function say(text) {
    label.textContent = text;
    btn.setAttribute('aria-label', text);
    live.textContent = text === LABEL ? '' : text;
  }

  async function grab(pick = {}) {
    busy = true;
    say('Sending…');
    const msg = { type: 'video', src: video?.currentSrc ?? '', pageUrl: pick.pageUrl ?? location.href, title: pick.title ?? document.title, ...(pick.format && { format: pick.format }) };
    const res = await chrome.runtime.sendMessage(msg).catch(() => null); // fails after the extension reloads
    say(res?.ok ? 'Sent to Arrow ✓' : res?.error ?? 'Reload the page and try again.');
    setTimeout(() => { busy = false; say(LABEL); }, 2500);
  }

  // A menu row; only trusted clicks (mouse, or Enter on the focused row) pick it.
  const row = (kids, onPick) => {
    const b = h('button', { class: 'item', role: 'menuitem', tabindex: '-1' }, ...kids);
    b.addEventListener('click', e => { if (e.isTrusted) onPick(); });
    return b;
  };

  // The format list under the pill. pageUrl and title are kept from the moment it opened: YouTube changes pages without reloading.
  async function openMenu() {
    const n = ++asked, pageUrl = location.href;
    menu.replaceChildren(h('div', { class: 'note' }, h('span', { class: 'spin' }), 'Getting formats…'));
    menu.hidden = false;
    btn.setAttribute('aria-expanded', 'true');
    live.textContent = 'Getting formats…';
    const res = await chrome.runtime.sendMessage({ type: 'formats', pageUrl }).catch(() => null);
    if (n !== asked || !menu) return; // closed or opened again meanwhile
    const pick = o => { closeMenu(); grab({ pageUrl, title: res.title || undefined, format: o.key }); };
    const groups = menuGroups(res?.options ?? []);
    if (groups.length) {
      menu.replaceChildren(...groups.map(g => h('div', { role: 'group', 'aria-label': g.title }, h('h3', { 'aria-hidden': 'true' }, g.title),
        ...g.items.map(o => row([h('span', { class: 'q' }, o.label), o.tag ? h('span', { class: 'tag' }, o.tag) : null,
          h('span', { class: 'sz' }, sizeText(o.size))], () => pick(o))))));
      live.textContent = '';
    } else {
      const text = res?.error ?? (res ? 'No downloadable formats found.' : 'Reload the page and try again.');
      // the window opens without a preselected format
      menu.replaceChildren(h('div', { class: 'note', role: 'alert' }, text), row([h('span', { class: 'q' }, 'Download anyway')], () => { closeMenu(); grab({ pageUrl }); }));
    }
    menu.querySelector('.item')?.focus();
  }

  function closeMenu() {
    asked++;
    if (!menu || menu.hidden) return;
    menu.hidden = true;
    menu.replaceChildren();
    btn.setAttribute('aria-expanded', 'false');
    live.textContent = '';
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
    if (v !== video) closeMenu();
    video = v;
    const yt = isYouTubePage(location.href);
    chev.style.display = yt ? '' : 'none';
    if (yt) { btn.setAttribute('aria-haspopup', 'menu'); btn.setAttribute('aria-expanded', String(!menu.hidden)); }
    else { btn.removeAttribute('aria-haspopup'); btn.removeAttribute('aria-expanded'); }
    box.classList.add('on');
    place();
  }

  // 1.5 s after the pointer left the video and the button (counted from leaving, not from the last move).
  // An open format list stays until a pick, Esc or a click elsewhere.
  function hide(now) {
    if (!box?.classList.contains('on') || (hideTimer && now !== true)) return;
    clearTimeout(hideTimer);
    if (now === true) closeMenu();
    hideTimer = setTimeout(() => {
      hideTimer = 0;
      if (busy || !menu.hidden) return hide(); // keep the result and the list readable
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
  addEventListener('pointerdown', e => { if (host && !e.composedPath().includes(host)) closeMenu(); }, true); // a click elsewhere
  addEventListener('scroll', place, { passive: true, capture: true });
  addEventListener('resize', place, { passive: true });

  function setEnabled(on) {
    enabled = on;
    if (on || !host) return;
    clearTimeout(hideTimer);
    hideTimer = 0;
    asked++;
    host.remove();
    host = root = box = menu = video = null;
    busy = false;
  }
  chrome.storage.local.get('overlay').then(({ overlay = true }) => setEnabled(overlay));
  chrome.storage.onChanged.addListener((change, area) => {
    if (area === 'local' && 'overlay' in change) setEnabled(change.overlay.newValue !== false);
  });
}
