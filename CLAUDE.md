# FastDL

IDM-style download manager: Node.js >= 20.3 (ESM), zero npm dependencies, local web UI + MV3 browser extension. Windows-first.

## Commands
- Install the desktop app: `install.cmd` (= `node install.js`): builds `<name>.exe` here, adds Start menu and desktop shortcuts. `uninstall.cmd` undoes it.
- Run: `node desktop.js` (server + tray + app window, like the exe) or `npm start` (server only) → http://127.0.0.1:9614
- Test: `npm test` (node:test, local test servers only, no network)
- Syntax-check a file: `node --check <file>`
- Don't start `server.js`, `desktop.js` or the exe on port 9614 while testing: they would use the user's real `state.json`. Set `FASTDL_PORT`, `FASTDL_STATE` and `FASTDL_STARTUP` to a spare port and temp paths.

## Rules
- No npm dependencies. Node built-ins only (fetch, fs/promises, node:test, crypto).
- Every HTTP request goes through `Manager.get()`: it keeps cookies on `d.cookieHost`, follows redirects by hand, and runs the stall watchdog.
- Treat anything from servers or the extension (file names, headers, URLs, playlists) as untrusted: names go through `sanitize()`, headers through the allowlist in `add()`.
- The User-Agent stays honest (`FastDL`). Never spoof a browser.
- `state.json` is user data: write it atomically (tmp + rename), and never overwrite one that failed to parse.
- yt-dlp and ffmpeg are external tools from winget. Tests never run the real yt-dlp: they use `fixtures/fake-ytdlp.mjs` through `mgr.ytdlp`.
- New logic gets a test in `*.test.js`. Tests must not touch the network or the user's Downloads folder.
- Update `filemap.md` when you add or move files.
- The app's name and icon come only from `brand.json`. Desktop texts use `brand.name`; `ui.html` may say "FastDL", which `server.js` replaces. The extension can't read it: its copies of the name are checked by `extension.test.js`.
- Child processes on Windows: console tools get `windowsHide: true` (the exe has no console to share), and anything that must outlive the app (the browser) gets `detached: true`; everything else ends with the app.

Docs: `docs/SPEC.md` (behaviour and decisions), `docs/PLAN.md` (tasks), `filemap.md` (where things live).

## Agent skills

### Issue tracker

Local markdown files under `.scratch/<feature>/` (the repo has no remote). See `docs/agents/issue-tracker.md`.

### Triage labels

The five default labels, unchanged. See `docs/agents/triage-labels.md`.

### Domain docs

Single-context: `GLOSSARY.md` and `docs/adr/` at the root, created when first needed. See `docs/agents/domain.md`.
