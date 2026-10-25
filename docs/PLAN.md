# Plan

## Done
- Engine: segmentation, resume, retries, connection-limit tolerance, stall watchdog, speed limit, queue, scheduler, categories, state persistence (engine.test.js: 10 tests)
- HLS with AES-128
- Server + web UI
- YouTube downloads (yt-dlp + ffmpeg)
- MV3 extension, logic tested with fake browser APIs (not yet loaded in a real browser)

## Tasks
1. **Harden engine and server, add Open button** — Careful. Extension privileges, Mark-of-the-Web, Open action, zero-byte files, 429 on probe, HLS byte-range refusal, HLS stall watchdog, smaller state, UTF-8-safe request bodies, testable server. Status: done (30 tests across engine/server/extension)
2. **Test extension with stubbed browser APIs** — Medium. `extension.test.js` driving background.js with fake `chrome`/`fetch`. Fixed: googlevideo HLS manifests were remembered; tab cleanup raced with in-flight `remember()`. Status: done
3. **Write README** — Light, after 1 and 2. Status: done
4. **Most-missed IDM features** — Medium. Batch links (`[001-120]`, max 1000, count shown first), clipboard monitoring (page reads the clipboard on focus, offers only), Start with Windows (Startup-folder shortcut, minimized server), sleep/shut down when all downloads finish (60 s countdown, Cancel, used once). Status: done (7 new tests; the new `ui.test.js` runs the UI's helper script)
5. **Real Windows desktop app** — Careful. `<name>.exe` as a Node single executable app built without npm tools (UpdateResource via PowerShell, fuse and GUI-subsystem patch), brand icon and version info from `brand.json` (name Arrow, icon `assets/logo.ico`), tray icon (Open / Pause all / Quit), one instance, one window, install/uninstall with Start menu and desktop shortcuts, Start with Windows through the exe. `start.cmd` removed. Status: done (15 new tests, including a real build in a temp folder; the exe was also run by hand on an isolated port)
6. **IDM parity: IDM's features in Arrow's own look** — Careful. `.scratch/idm-parity/` (spec, mockups, issues 01–09). Engine: per-download description, speed limit and "when done", the speed-limit switch, last try, the Download complete flag, live connections in `segments`, duplicates (number / replace / resume, remembered), named queues with start and stop times, days, files at the same time, retries and "when done", editable categories. App: command bar, list columns with sorting and a column chooser, details panel hidden by default, queues in the sidebar, Connections and Options in the progress window, the Download complete window, Download File Info's + / Description / Add to queue, Link already in the list, Properties, the category editor, the Scheduler, Options pages. Status: done (`npm test`: 127 tests, 127 pass, 0 fail; ARROW_SHOTS pictures of every screen in light and dark)

## Known gaps (not planned)
- popup.js has no automated test; the extension was never loaded in a real browser.
- The real sleep and shutdown commands never run in tests (a recorder stands in for `Manager.power`). Only the countdown logic is tested, plus a manual check that the .NET sleep method resolves.
- Clipboard monitoring depends on the browser's clipboard permission and on the window getting focus; it is not tested in Firefox.
- The shortcuts point at the exe in the app folder: after moving the folder, uninstall and install again.
- The tray menu was never clicked on screen (a test clicks its items without showing them), and the "older windows close" step was not tried in a real Brave window.
- HLS playlist bodies and `#EXT-X-MAP` byte ranges are not covered by the stall watchdog / byte-range refusal.
- `Retry-After` as an HTTP date is ignored (default backoff).
- Never tried by hand: saving Options and the Scheduler against a live engine (only the tests' engine and the ARROW_SHOTS pictures), a real queue starting at its time (tests use their own clock), and the real sleep or shutdown after a queue is done (the recorder stands in).