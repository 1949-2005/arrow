// IDM-style download panel over web video players: "Download this video", ? (what this panel is) and × (hide it on
// this video). Runs in every frame (embedded players live in iframes). One click sends it; the app's Download File Info
// window asks the rest. On YouTube a click first shows the video's formats with their real sizes (the app's /api/formats,
// through background.js); the picked one opens the Download File Info window with that format.
// The panel can be dragged; a double-click on its edge puts it back, as in IDM.
// Content scripts can't be ES modules, so the helpers below are plain functions; extension.test.js runs them in Node.
// An update injects this file again into open tabs (background.js onInstalled). A tab that already runs the new version gets
// it a second time in the same JS world, so the top level uses var and function only: const would throw "already declared".

var NAME = 'Arrow';
var LABEL = 'Download this video';

// Big enough to be a player, not a thumbnail preview or an icon.
var bigEnough = r => r.width >= 200 && r.height >= 112;
var inside = (r, x, y) => x >= r.left && x <= r.right && y >= r.top && y <= r.bottom;

// The panel's top-right corner: 8 px inside the video's, moved by the drag offset, and kept in the window
// (w, h: the panel's size; vw, vh: the window's).
var panelAt = (r, off, w, h, vw, vh) => ({
  left: Math.min(Math.max(r.right - 8 + off.x, w), vw),
  top: Math.max(Math.min(r.top + 8 + off.y, vh - h), 0),
});

// A single YouTube video's clean watch URL (no playlist or tracking parameters), else null.
function ytWatchUrl(href) {
  let u;
  try { u = new URL(href); } catch { return null; }
  if (!/^https?:$/.test(u.protocol)) return null;
  const id = u.hostname === 'youtu.be' ? u.pathname.slice(1)
    : !['youtube.com', 'www.youtube.com', 'm.youtube.com', 'music.youtube.com'].includes(u.hostname) ? ''
    : u.pathname === '/watch' ? u.searchParams.get('v')
    : /^\/(?:shorts|live|embed)\/([^/]+)\/?$/.exec(u.pathname)?.[1];
  return /^[\w-]{11}$/.test(id ?? '') ? 'https://www.youtube.com/watch?v=' + id : null;
}
// A single YouTube video page, as common.js isYouTubeVideo() (extension.test.js checks they agree).
var isYouTubePage = href => !!ytWatchUrl(href);

// The format list's groups, in the app's order: [{title, items}], empty groups left out.
var GROUPS = [['MP4', 'Video · MP4'], ['WEBM', 'Video · WEBM'], ['Audio', 'Audio']];
var menuGroups = options => GROUPS.map(([g, title]) => ({ title, items: options.filter(o => o.group === g) })).filter(g => g.items.length);
// As confirm.html size(), so the list and the window show the same number
var sizeText = (n, approx) => (approx ? '~' : '') + (n < 1024 ? n + ' B' : n < 1048576 ? (n / 1024).toFixed(1) + ' KB' : n < 1073741824 ? (n / 1048576).toFixed(1) + ' MB' : (n / 1073741824).toFixed(2) + ' GB'); // ~: approximate (a recording YouTube is still processing)
// Up/Down through n items, wrapping; from nothing focused (-1) Down goes to the first, Up to the last.
var step = (i, n, key) => i < 0 ? (key === 'ArrowUp' ? n - 1 : 0) : (i + (key === 'ArrowUp' ? n - 1 : 1)) % n;
// The file the app will save for a format key: engine.js sanitize(title + extOf(key)), ported (no imports in a classic script).
function fileName(title, key) {
  let name = (String(title ?? '').trim() ? title : 'download') + '.' + key.split(':')[0];
  name = name.replace(/[<>:"/\\|?*\x00-\x1f]/g, '_').replace(/^[\s.]+|[\s.]+$/g, '');
  const dot = name.lastIndexOf('.'), ext = dot > 0 ? name.slice(dot) : ''; // path.extname
  if (name.length > 180) name = name.slice(0, 180 - ext.length) + ext;
  if (/^(con|prn|aux|nul|com\d|lpt\d)(\.|$)/i.test(name)) name = '_' + name;
  return name || 'download';
}

// chrome.runtime.sendMessage, or null instead of an error. Once the extension reloads, this tab's copy of the script is cut
// off and sendMessage throws at once ("Extension context invalidated"), with no promise for .catch to catch.
var send = msg => { try { return chrome.runtime.sendMessage(msg).catch(() => null); } catch { return Promise.resolve(null); } };

// The second run in the same world (see above) leaves the running copy alone: one panel.
if (typeof document !== 'undefined' && !globalThis.arrowMain) {
  globalThis.arrowMain = true;
  main();
}

function main() {
  // After an update, this tab's previous copy runs in another JS world, cut off from the extension. A new copy says so on the
  // shared document before it listens itself; the old one then removes its panel and stops every handler (life.abort()).
  const START = 'arrow-start-' + chrome.runtime.id, life = new AbortController(), on = { signal: life.signal };
  document.dispatchEvent(new Event(START));
  document.addEventListener(START, () => {
    life.abort();
    clearTimeout(sentTimer);
    setEnabled(false);
    try { chrome.storage.onChanged.removeListener(onStorage); } catch {} // cut off: may throw
  }, on);

  const CSS = `
    .box { position: fixed; top: 0; left: 0; transform: translateX(-100%); opacity: 0; visibility: hidden;
      display: flex; flex-direction: column; align-items: flex-end; gap: 6px;
      transition: opacity .12s, visibility 0s .12s; font: 600 13px/1 "Segoe UI Variable Text", "Segoe UI", system-ui, sans-serif; } /* the app's font (Theme.Body), as its tray menu */
    .box.on { opacity: 1; visibility: visible; transition: opacity .12s; }
    @media (prefers-reduced-motion: reduce) { .box, .box.on { transition: none; } .spin { animation: none !important; } }
    [hidden] { display: none !important; }
    button { all: unset; box-sizing: border-box; cursor: pointer; }
    button:focus-visible { outline: 2px solid #fff; outline-offset: -2px; }
    /* the logo's graphite (#3B3C3D) and white */
    .panel { display: flex; align-items: center; gap: 2px; padding: 3px; border-radius: 10px; background: rgba(59,60,61,.92);
      border: 1px solid rgba(255,255,255,.18); box-shadow: 0 4px 14px rgba(0,0,0,.35); color: #fff;
      cursor: move; user-select: none; touch-action: none; }
    .grip { width: 6px; height: 16px; margin: 0 3px 0 4px; opacity: .45;
      background: radial-gradient(circle, #fff 1px, transparent 1.3px) 0 0 / 3px 4px; }
    .pill { display: flex; align-items: center; gap: 7px; height: 28px; padding: 0 11px 0 8px; border-radius: 7px;
      background: rgba(255,255,255,.1); font: inherit; white-space: nowrap; }
    .pill:hover { background: rgba(255,255,255,.18); }
    .logo { flex: none; width: 18px; height: 18px; }
    .chev { width: 12px; height: 12px; opacity: .7; margin-left: 2px; }
    .icon { display: grid; place-items: center; width: 26px; height: 28px; border-radius: 7px; color: rgba(255,255,255,.7); }
    .icon:hover { background: rgba(255,255,255,.12); color: #fff; }
    .icon svg { width: 14px; height: 14px; }
    .menu, .help { box-sizing: border-box; border-radius: 12px; background: rgba(59,60,61,.97); border: 1px solid rgba(255,255,255,.18);
      box-shadow: 0 10px 28px rgba(0,0,0,.45); color: #fff; font-weight: 400; }
    .menu { width: min(440px, calc(var(--room, 448px) - 8px)); max-height: min(480px, 70vh); overflow: auto; padding: 6px; scroll-padding-top: 40px; scrollbar-width: thin; scrollbar-color: rgba(255,255,255,.28) transparent; }
    /* sticky group header: the format stays in view while its rows scroll under it; the -6px (top and sides) cover the menu's padding */
    h3 { position: sticky; top: -6px; z-index: 1; display: flex; align-items: center; gap: 8px; margin: 0 -6px; padding: 10px 16px 7px;
      background: #3B3C3D; border-bottom: 1px solid rgba(255,255,255,.1); font-size: 12.5px; font-weight: 600; color: rgba(255,255,255,.9); }
    .fmtchip { padding: 2px 7px; border-radius: 5px; background: #fff; color: #3B3C3D; font-size: 11.5px; font-weight: 700; letter-spacing: .4px; }
    .item { display: flex; align-items: center; gap: 10px; width: 100%; padding: 7px 10px; border-radius: 7px; font-size: 13px; }
    .item:hover, .item:focus { background: rgba(255,255,255,.1); }
    .item:focus { outline: 2px solid #fff; outline-offset: -2px; }
    /* a format row: its line, then the whole file name, wrapped */
    .fmt { flex-direction: column; align-items: stretch; gap: 4px; }
    .line { display: flex; align-items: center; gap: 10px; }
    .name { font-size: 13px; line-height: 1.5; color: rgba(255,255,255,.72); overflow-wrap: anywhere; }
    .item:hover .name, .item:focus .name { color: #fff; }
    .q { font-weight: 600; min-width: 56px; }
    .tag { font-size: 10.5px; font-weight: 700; padding: 1px 5px; border-radius: 4px; background: rgba(255,255,255,.16); color: #fff; }
    .sz { margin-left: auto; color: rgba(255,255,255,.6); }
    .note { display: flex; align-items: center; gap: 8px; padding: 10px; color: rgba(255,255,255,.75); line-height: 1.4; }
    .spin { flex: none; box-sizing: border-box; width: 14px; height: 14px; border-radius: 50%; border: 2px solid rgba(255,255,255,.25);
      border-top-color: #fff; animation: spin .8s linear infinite; }
    @keyframes spin { to { transform: rotate(360deg); } }
    .help { width: 300px; padding: 14px 16px 12px; cursor: default; }
    .help h2 { display: flex; align-items: center; gap: 8px; margin: 0 0 8px; font-size: 13px; font-weight: 600; }
    .help p { margin: 0 0 6px; font-size: 12.5px; line-height: 1.45; color: rgba(255,255,255,.8); }
    .help b { font-weight: 600; color: #fff; }
    .ok { display: block; margin: 10px 0 0 auto; height: 28px; padding: 0 18px; border-radius: 7px; background: #fff; color: #3B3C3D; line-height: 28px; font-weight: 600; }
    .ok:hover { background: #E2E2E5; }
    .ok:focus-visible { outline-color: #9A9B9D; outline-offset: 2px; } /* a white ring would vanish on the white button */
    .sr { position: absolute; width: 1px; height: 1px; overflow: hidden; clip-path: inset(50%); white-space: nowrap; } /* heard, not seen */`;

  let host, root, box, panel, grip, btn, label, chev, helpBtn, help, okBtn, menu, live, video, hideTimer = 0, sentTimer = 0, busy = false, enabled = false, asked = 0;
  let offset = { x: 0, y: 0 }, drag = null, dragged = false;
  const prefetched = new Set(); // YouTube videos whose format list was already asked for
  // video → the YouTube video (else page) it was hidden on with ×: YouTube reuses one <video> for every video and preview
  const dismissed = new WeakMap();
  // The YouTube video the shown <video> plays (its watch URL), or null: the format list's pageUrl. Set only by show().
  let target = null, targetOwn = false, seen = {};

  // DOM building without innerHTML, which Trusted Types pages (YouTube) may refuse.
  const make = (ns, tag, attrs = {}, ...kids) => {
    const e = ns ? document.createElementNS(ns, tag) : document.createElement(tag);
    for (const k in attrs) e.setAttribute(k, attrs[k]);
    e.append(...kids.filter(k => k != null));
    return e;
  };
  const h = (...a) => make(null, ...a);
  const s = (...a) => make('http://www.w3.org/2000/svg', ...a);
  // The app icon's tile, as the toolbar and tray show it: assets/logo.svg at 16 px, with thicker strokes. Fixed colors in every state.
  const logo = () => s('svg', { class: 'logo', viewBox: '0 0 256 256', 'aria-hidden': 'true' },
    s('rect', { width: 256, height: 256, rx: 58, fill: '#FFFFFF' }),
    s('g', { transform: 'translate(19.9 7.92) scale(0.47)', fill: 'none', stroke: '#3B3C3D', 'stroke-width': 52, 'stroke-linejoin': 'round', 'stroke-linecap': 'round' },
      s('path', { d: 'M71.5 87.5 L437.5 255.5 L71.5 423.5 Z' }), s('path', { d: 'M71.5 87.5 L183.5 255.5' }),
      s('path', { d: 'M183.5 255.5 L437.5 255.5 L71.5 423.5 Z', fill: '#3B3C3D' })));
  const icon = (...paths) => s('svg', { viewBox: '0 0 24 24', fill: 'none', stroke: 'currentColor', 'stroke-width': '2.5', 'stroke-linecap': 'round',
    'stroke-linejoin': 'round', 'aria-hidden': 'true' }, ...paths.map(d => s('path', { d })));

  // Closed shadow root: page CSS can't style it and page scripts can't reach the buttons.
  function build() {
    host = document.createElement('div');
    host.style.cssText = 'all: initial !important; position: fixed !important; top: 0 !important; left: 0 !important; width: 0 !important; height: 0 !important; z-index: 2147483647 !important; display: block !important;';
    root = host.attachShadow({ mode: 'closed' });
    label = h('span', {}, LABEL);
    chev = icon('m6 9 6 6 6-6');
    chev.setAttribute('class', 'chev');
    btn = h('button', { class: 'pill', 'aria-label': LABEL }, logo(), label, chev);
    const q = icon('M9.1 9a3 3 0 0 1 5.8 1c0 2-3 3-3 3', 'M12 17h.01');
    q.setAttribute('viewBox', '2.5 2 19 19'); // the ? fills little of its 24 box: zoomed in, it matches the ×
    helpBtn = h('button', { class: 'icon', title: 'What is this?', 'aria-label': 'What is this?', 'aria-haspopup': 'dialog', 'aria-expanded': 'false' }, q);
    const closeBtn = h('button', { class: 'icon', title: 'Hide on this video', 'aria-label': 'Hide on this video' }, icon('M18 6 6 18', 'm6 6 12 12'));
    grip = h('span', { class: 'grip', title: 'Drag to move. Double-click to put it back.' });
    panel = h('div', { class: 'panel', role: 'group', 'aria-label': `${NAME} download panel` }, grip, btn, helpBtn, closeBtn);
    menu = h('div', { class: 'menu', role: 'menu', 'aria-label': 'Choose a format', hidden: '' });
    okBtn = h('button', { class: 'ok' }, 'OK');
    help = h('div', { class: 'help', role: 'dialog', 'aria-labelledby': 'help-title', hidden: '' },
      h('h2', { id: 'help-title' }, logo(), `${NAME} download panel`),
      h('p', {}, `To turn it off, click the ${NAME} icon in your browser's toolbar and untick `, h('b', {}, 'Show download button on videos'), '.'),
      h('p', {}, 'Drag the panel to move it. Double-click its edge to put it back. × hides it on this video until you reload the page.'),
      okBtn);
    box = h('div', { class: 'box' }, panel, menu, help);
    live = h('div', { class: 'sr', 'aria-live': 'polite' });
    root.append(h('style', {}, CSS), box, live);
    // keyboard Enter/Space on a <button> fires a trusted click too; scripted clicks are not trusted
    btn.addEventListener('pointerenter', () => prefetch(target));
    btn.addEventListener('click', e => {
      if (!e.isTrusted || busy || dragged) return;
      closeHelp();
      if (!target) grab();
      else if (menu.hidden) openMenu();
      else closeMenu();
    });
    helpBtn.addEventListener('click', e => {
      if (!e.isTrusted || dragged) return;
      if (help.hidden) openHelp();
      else closeHelp();
    });
    closeBtn.addEventListener('click', e => {
      if (!e.isTrusted || dragged) return;
      if (video) dismissed.set(video, target ?? location.href);
      hide(true);
    });
    okBtn.addEventListener('click', () => { closeHelp(); helpBtn.focus(); });
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
    help.addEventListener('keydown', e => {
      if (!e.isTrusted) return;
      e.stopPropagation();
      if (e.key === 'Escape') {
        e.preventDefault();
        closeHelp();
        helpBtn.focus();
      }
    });
    // Drag: past 4 px a press moves the panel instead of clicking. The pointer is captured only then,
    // because a captured press would no longer click the button under it.
    panel.addEventListener('pointerdown', e => {
      dragged = false;
      drag = e.isTrusted && e.button === 0 ? { x: e.clientX, y: e.clientY, from: offset } : null;
    });
    panel.addEventListener('pointermove', e => {
      if (!drag) return;
      if (!(e.buttons & 1)) { drag = null; return; } // released outside the panel before it became a drag
      const dx = e.clientX - drag.x, dy = e.clientY - drag.y;
      if (!dragged && Math.hypot(dx, dy) < 4) return;
      if (!dragged) { dragged = true; panel.setPointerCapture(e.pointerId); closeMenu(); closeHelp(); }
      offset = { x: drag.from.x + dx, y: drag.from.y + dy };
      place();
    });
    panel.addEventListener('pointerup', () => { drag = null; });
    panel.addEventListener('pointercancel', () => { drag = null; });
    panel.addEventListener('dblclick', e => {
      if (!e.isTrusted || (e.target !== panel && e.target !== grip)) return;
      offset = { x: 0, y: 0 };
      place();
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
    const res = await send(msg); // null after the extension reloads
    if (life.signal.aborted) return; // replaced by a newer copy meanwhile
    say(res?.ok ? `Sent to ${NAME} ✓` : res?.error ?? 'Reload the page and try again.');
    sentTimer = setTimeout(() => { busy = false; say(LABEL); }, 2500);
  }

  // A menu row; only trusted clicks (mouse, or Enter on the focused row) pick it.
  const row = (kids, onPick) => {
    const b = h('button', { class: 'item', role: 'menuitem', tabindex: '-1' }, ...kids);
    b.addEventListener('click', e => { if (e.isTrusted) onPick(); });
    return b;
  };

  // The format list under the panel. pageUrl and title are kept from the moment it opened: YouTube changes pages without reloading.
  async function openMenu() {
    const n = ++asked, pageUrl = target;
    menu.setAttribute('aria-label', 'Choose a format');
    menu.replaceChildren(h('div', { class: 'note' }, h('span', { class: 'spin' }), 'Getting formats…'));
    menu.hidden = false;
    btn.setAttribute('aria-expanded', 'true');
    live.textContent = 'Getting formats…';
    const res = await send({ type: 'formats', pageUrl });
    if (n !== asked || !menu) return; // closed or opened again meanwhile
    const pick = o => { closeMenu(); grab({ pageUrl, title: res.title || undefined, format: o.key }); };
    const groups = menuGroups(res?.options ?? []);
    if (groups.length) {
      // each row shows the whole file it saves, as IDM does, wrapped over as many lines as it needs
      const title = res.title || document.title;
      // group header: a white chip per file format the group offers, then its kind: "[MP4] Video", "[M4A] [MP3] Audio"
      const h3 = g => h('h3', { 'aria-hidden': 'true' }, ...[...new Set(g.items.map(o => o.key.split(':')[0].toUpperCase()))].map(f => h('span', { class: 'fmtchip' }, f)), g.title.split(' · ')[0]);
      const item = o => {
        const name = fileName(title, o.key);
        const b = row([h('div', { class: 'line' }, h('span', { class: 'q' }, o.label), o.tag ? h('span', { class: 'tag' }, o.tag) : null, h('span', { class: 'sz' }, sizeText(o.size, o.approx))),
          h('div', { class: 'name', dir: 'auto' }, name)], () => pick(o));
        b.classList.add('fmt');
        b.setAttribute('aria-label', `${o.label}${o.tag ? ' ' + o.tag : ''}, ${sizeText(o.size, o.approx)}, ${name}`); // one name for screen readers: format, size, file
        return b;
      };
      menu.setAttribute('aria-label', `Choose a format for ${title}`);
      menu.replaceChildren(...groups.map(g => h('div', { role: 'group', 'aria-label': g.title }, h3(g), ...g.items.map(item))));
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

  function openHelp() {
    closeMenu();
    help.hidden = false;
    helpBtn.setAttribute('aria-expanded', 'true');
    okBtn.focus();
  }

  function closeHelp() {
    if (!help || help.hidden) return;
    help.hidden = true;
    helpBtn.setAttribute('aria-expanded', 'false');
  }

  function place() {
    if (!video || !box?.classList.contains('on')) return;
    const r = video.getBoundingClientRect();
    if (!video.isConnected || !bigEnough(r) || document.fullscreenElement) return hide(true);
    const { clientWidth, clientHeight } = document.documentElement; // the window without its scrollbars
    const p = panelAt(r, offset, panel.offsetWidth, panel.offsetHeight, clientWidth, clientHeight);
    box.style.left = p.left + 'px';
    box.style.setProperty('--room', p.left + 'px'); // the panel's right edge: the room the menu has on its left
    box.style.top = p.top + 'px';
  }

  // The YouTube video a <video> plays: {url, own}. The page's own player plays the page's video; a feed's inline preview
  // (home, search, channel) sits over its thumbnail, whose link is in the stack under the middle of the preview's visible
  // part. Worked out again only when the <video>, its place or the page changes: YouTube moves one preview <video>
  // between thumbnails. No side effects but its cache.
  function targetOf(v) {
    const r = v.getBoundingClientRect();
    if (seen.v === v && seen.left === r.left && seen.top === r.top && seen.href === location.href) return seen.t;
    const own = isYouTubePage(location.href) ? location.href : null;
    let t = { url: own, own: false };
    if (own && v.closest('#movie_player')) t.own = true;
    else if (/(^|\.)youtube\.com$/.test(location.hostname)) {
      const x = (Math.max(r.left, 0) + Math.min(r.right, innerWidth)) / 2, y = (Math.max(r.top, 0) + Math.min(r.bottom, innerHeight)) / 2;
      for (const el of document.elementsFromPoint(x, y)) {
        const url = ytWatchUrl(el.closest('a[href]')?.href ?? '');
        if (url) { t = { url, own: false }; break; }
      }
    }
    seen = { v, left: r.left, top: r.top, href: location.href, t };
    return t;
  }

  function prefetch(url) {
    if (!url || prefetched.has(url)) return; // once per video; the app keeps the list 15 min
    prefetched.add(url);
    send({ type: 'formats', pageUrl: url }); // extension reloaded: the click says so
  }

  function show(v) {
    clearTimeout(hideTimer);
    hideTimer = 0;
    if (!host) build();
    const t = targetOf(v);
    // another <video>, or the same preview <video> moved to another thumbnail: the open list or help was for the old one
    if (v !== video || t.url !== target) { closeMenu(); closeHelp(); }
    video = v;
    target = t.url;
    targetOwn = t.own;
    const yt = !!target;
    chev.style.display = yt ? '' : 'none';
    if (yt) { btn.setAttribute('aria-haspopup', 'menu'); btn.setAttribute('aria-expanded', String(!menu.hidden)); }
    else { btn.removeAttribute('aria-haspopup'); btn.removeAttribute('aria-expanded'); }
    box.classList.add('on');
    place();
    // yt-dlp needs a few seconds for YouTube's list: start it when the panel shows on the page's own player, so a click
    // often finds it ready. Previews wait for the pointer on the button: a run per hovered thumbnail would be too many.
    if (targetOwn) prefetch(target);
  }

  // 1.5 s after the pointer left the video and the panel (counted from leaving, not from the last move).
  // An open format list or help stays until a pick, OK, Esc or a click elsewhere.
  function hide(now) {
    if (!box?.classList.contains('on') || (hideTimer && now !== true)) return;
    clearTimeout(hideTimer);
    if (now === true) { closeMenu(); closeHelp(); }
    hideTimer = setTimeout(() => {
      hideTimer = 0;
      if (now !== true && (busy || !menu.hidden || !help.hidden)) return hide(); // keep the result, the list and the help readable; × can't wait
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
    // Not over fullscreen video: only the fullscreen element is drawn, so the panel would have to go
    // inside the page's own player, where page scripts and CSS can reach it.
    if (document.fullscreenElement) return hide(true);
    const v = host && e.composedPath().includes(host) ? video : videoAt(e.clientX, e.clientY);
    if (v && (!dismissed.has(v) || dismissed.get(v) !== (targetOf(v).url ?? location.href))) show(v);
    else if (video) hide();
  }, { passive: true, capture: true, ...on });
  addEventListener('mouseout', e => { if (!e.relatedTarget && video) hide(); }, { capture: true, ...on }); // pointer left the frame
  addEventListener('pointerdown', e => { if (host && !e.composedPath().includes(host)) { closeMenu(); closeHelp(); } }, { capture: true, ...on }); // a click elsewhere
  addEventListener('scroll', place, { passive: true, capture: true, ...on });
  addEventListener('resize', place, { passive: true, ...on });

  function setEnabled(want) {
    enabled = want && !life.signal.aborted;
    if (enabled || !host) return;
    clearTimeout(hideTimer);
    hideTimer = 0;
    asked++;
    host.remove();
    host = root = box = panel = menu = help = video = null;
    busy = false;
  }
  chrome.storage.local.get('overlay').then(({ overlay = true }) => setEnabled(overlay));
  function onStorage(change, area) {
    if (area === 'local' && 'overlay' in change) setEnabled(change.overlay.newValue !== false);
  }
  chrome.storage.onChanged.addListener(onStorage);
}
