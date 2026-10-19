# Plan

## Done
- Engine: segmentation, resume, retries, connection-limit tolerance, stall watchdog, speed limit, queue, scheduler, categories, state persistence (engine.test.js: 10 tests)
- HLS with AES-128
- Server + web UI
- MV3 extension (untested in a real browser)

## Tasks
1. **Harden engine and server, add Open button** — Careful. Extension privileges, Mark-of-the-Web, Open action, zero-byte files, 429 on probe, HLS byte-range refusal, HLS stall watchdog, smaller state, UTF-8-safe request bodies, testable server. Verify: `npm test`. Status: pending
2. **Test extension with stubbed browser APIs** — Medium. `extension.test.js` driving background.js with fake `chrome`/`fetch`; fix what it finds. Verify: `npm test`. Status: pending
3. **Write README** — Light, after 1 and 2. Status: pending
