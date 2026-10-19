# FastDL

IDM-style download manager: Node.js >= 20.3 (ESM), zero npm dependencies, local web UI + MV3 browser extension. Windows-first.

## Commands
- Run: `npm start` (or `start.cmd`, which also opens an app window in Brave, Chrome or Edge, else the default browser) → http://127.0.0.1:9614
- Test: `npm test` (node:test, local test servers only, no network)
- Syntax-check a file: `node --check <file>`
- Don't start `server.js` on port 9614 while testing: tests use their own ports and temp folders.

## Rules
- No npm dependencies. Node built-ins only (fetch, fs/promises, node:test, crypto).
- Every HTTP request goes through `Manager.get()`: it keeps cookies on `d.cookieHost`, follows redirects by hand, and runs the stall watchdog.
- Treat anything from servers or the extension (file names, headers, URLs, playlists) as untrusted: names go through `sanitize()`, headers through the allowlist in `add()`.
- The User-Agent stays honest (`FastDL`). Never spoof a browser.
- `state.json` is user data: write it atomically (tmp + rename), and never overwrite one that failed to parse.
- New logic gets a test in `*.test.js`. Tests must not touch the network or the user's Downloads folder.
- Update `filemap.md` when you add or move files.

Docs: `docs/SPEC.md` (behaviour and decisions), `docs/PLAN.md` (tasks), `filemap.md` (where things live).
