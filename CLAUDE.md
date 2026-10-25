# FastDL

IDM-style download manager: a Node.js >= 20.3 engine (ESM, zero npm dependencies) with a local HTTP API, a native Windows app (C# WinForms, `app/`) and an MV3 browser extension. Windows-first.

## Commands
- Install the desktop app: `install.cmd` (= `node install.js`): builds `<name>.exe` (the native app) and `<name> Engine.exe` (the engine) here, adds Start menu and desktop shortcuts. `uninstall.cmd` undoes it.
- Build the release setup: `node build-installer.js` → `dist\GlideSetup.exe` + `.sha256` (per-user installer, `installer/`). Its tests install into temp folders through the `GLIDE_SETUP_*` hooks: never the real Programs, Start menu, desktop, Startup folder, Uninstall key or winget.
- Run the engine alone: `node desktop.js` or `npm start` → http://127.0.0.1:9614. The native app is built by `app/build.js` `compileApp()`; `node --test app.test.js` builds and checks it.
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
- The app's name and icon come only from `brand.json`: the engine uses `brand.name`, the native app gets the name at build time (`Application.ProductName`) and reads the icon file at run time. The extension can't read it: its copies of the name are checked by `extension.test.js`.
- `app/*.cs` is C# 5 for Windows' own `csc.exe` (.NET Framework 4.8): no `$""`, `?.`, `nameof`, expression-bodied members, tuples or pattern matching. Its UI follows `docs/NATIVE_UI.md`.
- Child processes on Windows: console tools get `windowsHide: true` (the exes have no console to share). The native app starts the engine without a job object, so downloads outlive its window; the engine's children (yt-dlp, ffmpeg) end with the engine.

Docs: `docs/SPEC.md` (behaviour and decisions), `docs/NATIVE_UI.md` (the native app's design), `docs/PLAN.md` (tasks), `filemap.md` (where things live).

## Agent skills

### Issue tracker

Local markdown files under `.scratch/<feature>/` (the repo has no remote). See `docs/agents/issue-tracker.md`.

### Triage labels

The five default labels, unchanged. See `docs/agents/triage-labels.md`.

### Domain docs

Single-context: `GLOSSARY.md` and `docs/adr/` at the root, created when first needed. See `docs/agents/domain.md`.
