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

## Known gaps (not planned)
- popup.js has no automated test; the extension was never loaded in a real browser.
- HLS playlist bodies and `#EXT-X-MAP` byte ranges are not covered by the stall watchdog / byte-range refusal.
- `Retry-After` as an HTTP date is ignored (default backoff).
