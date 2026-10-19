# FastDL spec

Personal download manager modelled on how Internet Download Manager works (from public knowledge; no reverse engineering, no license bypass).

## Non-goals
YouTube/DASH audio+video merging, DRM, torrents, FTP, site grabber, tray icon, proxy settings, clipboard monitoring.

## Download engine (`engine.js`)
- **Probe = first connection.** `GET` with `Range: bytes=0-`. 206 + `Content-Range` total, or 200 + `Accept-Ranges: bytes` + length → resumable. The probe's body keeps streaming as segment #1. 416 → asked again without `Range` and downloaded as not resumable (zero-byte files). 5xx and 429 are retried up to 4 times; a numeric `Retry-After` (capped at 30 s) replaces the default backoff.
- **Dynamic segmentation.** Up to `settings.connections` connections per download. A free connection first takes an unstarted/unfinished piece; otherwise it splits the largest active piece in half (only if each half keeps ≥ 512 KB). A piece shrinks under a running connection, which stops at the new end.
- **Resume.** Each retry asks for `bytes=pos-end` and must get 206 with `Content-Range` starting at `pos` and the same total; anything else is an error (file changed on the server).
- **Not resumable.** One connection; every retry, pause or restart starts over from byte 0.
- **Failures.** A connection retries up to 5 times (2 while others are alive). When it gives up while others are alive, it is retired and the download carries on with fewer connections (servers that cap connections per user). No data for `stallMs` (30 s) cuts a connection off.
- **Files.** Written into `<name>.fdl` (preallocated), renamed when complete; `segments` are then dropped from the state. Names: `Content-Disposition` → URL → `download`; `sanitize()`; duplicates get ` (n)`; optional category subfolders (Video, Music, Compressed, Documents, Programs).
- **Queue.** At most `settings.maxActive` downloads run; others wait as `queued`. `scheduled` downloads become `queued` when the clock hits `settings.scheduleAt` (daily, app must be running).
- **Speed limit.** One global debt-based token bucket across all connections.
- **State.** `state.json`: settings + list. Saved atomically every second while anything runs or changed. `downloading` loads back as `queued`.

## HLS (`hls.js`)
Master playlist → highest `BANDWIDTH`. AES-128 with explicit IV or media-sequence IV; other methods are refused as DRM. `#EXT-X-BYTERANGE` playlists are refused. Segment bodies are read chunk by chunk under the stall watchdog. `#EXT-X-MAP` → `.mp4` output, else `.ts`. Segments fetched `connections` at a time, written in order; resume continues at the first unwritten segment.

## Data model
Download: `id, url, name, title, headers{cookie,referer,user-agent}, cookieHost, kind(http|hls), status(queued|downloading|paused|done|error|scheduled|removed), file, tmp, size, resumable, segments[{start,end,pos}], hls{done,bytes,total}, error, added, finished`.
Settings: `dir, connections(1–32), maxActive(1–20), speedLimitKB(0 = off), scheduleAt("HH:MM" or ""), categories(bool)`.

## HTTP API (`server.js`, 127.0.0.1:9614)
`GET /` (UI) · `GET /api/ping` · `GET /api/state` · `POST /api/add {url, name?, title?, headers?, scheduled?}` · `POST /api/settings {...}` · `POST /api/action {id, op: pause|resume|url|remove|removeFile|folder|open, url?}` (`open` runs a finished file with its default app)

## Security model
- Binds 127.0.0.1 only. `Host` must be `127.0.0.1:PORT` or `localhost:PORT` (DNS rebinding).
- `Origin`, when present, must be the app's own page or a browser extension. Other websites get 403.
- Extension origins may only use `GET /api/ping` and `POST /api/add`; anything else is 403. Any installed extension can reach the app, and settings (download folder) or `open` would let it plant and run files.
- Finished files get the Mark-of-the-Web (`:Zone.Identifier` stream, `ZoneId=3`, referrer, URL without credentials) on Windows, like browsers do, so SmartScreen and Office Protected View still apply. Write errors are ignored (FAT/exFAT).
- Cookies come from the extension, are sent only to the host they were captured for, also across redirects, and are dropped from the state once the download is done.
- Header allowlist: cookie, referer, user-agent. The default User-Agent is honest.
- File names can't leave the download folder (`sanitize()`).

## UI (`ui.html`)
Single table: name, size, progress (one bar per connection), speed + connection count, time left, status, actions (Pause/Resume/Start now, Change link, Open, Folder, Remove). Settings dialog. Drop links on the window. Desktop notification on completion. Light/dark via `prefers-color-scheme`.

## Extension (`extension/`)
Takes over browser downloads when the app is running (toggle in popup), "Download with FastDL" context menu, media grabber (video/audio responses > 512 KB and HLS playlists per tab, badge count), sends cookies + referrer, never the browser UA.
