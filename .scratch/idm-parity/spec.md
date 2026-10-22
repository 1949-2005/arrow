# IDM parity: IDM's features in Arrow's own look

Status: engine part approved (2026-10-22); screens are a draft until the user approves the mockups.

The user asked for "the same UX as IDM" and chose: keep Arrow's modern Windows 11 look (docs/NATIVE_UI.md), add what
IDM has and Arrow lacks, in four areas: main window, download progress, queues and scheduler, dialogs and options.
Not in this round: proxy, site logins, sounds, virus check, site grabber, export/import, browser-extension changes.

## What IDM has (read from IDM 6.43.15's own menus and dialogs, in our words)
- Main window: menu bar, a big labeled toolbar (add, resume, stop, stop all, delete, delete completed, options,
  scheduler, start/stop queue), a categories tree (all / unfinished / finished, each with the file types, plus
  queues), and a download list with columns: file name, size, status, time left, transfer rate, last try date,
  description (also save path, referer, parent page), sortable by any of them, with a column chooser.
- Download progress window: tabs "Download status", "Speed limiter" (per file), "Options on completion"; a "Show
  details" part with one row per connection (number, downloaded, info).
- Download complete window: open, open with…, open folder, close, "don't show again".
- Download file info: URL, category with "+", save as, remember the folder, description; start, later, cancel.
  "Later" asks for a queue (and whether to start it).
- Duplicate link window: add with a numbered name, overwrite the existing file, or resume / show the existing one;
  remember the choice.
- Scheduler: named queues (the main queue always exists); per queue: files at the same time, the files in order
  (move up, move down), start time (once or daily, days of the week), stop time, retries for failed files, and an
  action when the queue is done (shut down, exit).
- File properties: name, folder, address, description, referer, status, size, last try.
- Options: general, file types, save to (categories with types and folders), downloads (show the start and complete
  windows, duplicates), connection, proxy, logins, sounds and more.

## Engine (engine.js, server.js; tested in engine.test.js / server.test.js)

### Downloads: new fields (saved in state.json, all in view())
- `description`: text, at most 300 characters, control characters removed, trimmed; null when empty. Set by
  `add({description})`, `confirm({description})` and `POST /api/action {op: 'describe', id, description}`.
- `limitKB`: this file's own speed limit, 0–10 000 000, 0 = none. `POST /api/action {op: 'limit', id, kb}`. Works
  together with the global limit: a byte waits for both. HTTP and HLS apply it at once; YouTube (yt-dlp
  `--limit-rate`, the lower of the two limits) from its next run.
- `whenDone`: '' | 'open' | 'sleep' | 'shutdown', what to do when this download completes.
  `POST /api/action {op: 'whenDone', id, value}`. 'open' calls `mgr.opener(d)` (server.js opens the file the way the
  Open action does; tests get a recorder). 'sleep' / 'shutdown' start the same 60 s countdown the global
  "When done" uses (see Power below).
- `queue`: the id of the queue it belongs to, or null.
- `lastTry`: time of the last start (`start()` sets it). `finished` stays as it is.
- `notify`: true when the user started it from the Download File Info window ("Start Download"). When it completes,
  the app shows its Download complete window once (if `settings.showComplete`); the app remembers which ones it
  showed, the engine only keeps the flag.
- view() `segments` rows get a 4th value: 1 when a connection is working on that piece now, else 0 (for the
  progress window's connection list).

### Settings: new fields (POST /api/settings; validated like the others; unknown values fall back to the default)
- `speedLimitOn` (bool). The global limiter rate is `speedLimitOn ? speedLimitKB * 1024 : 0`. Migration: a loaded
  state with `speedLimitKB > 0` and no `speedLimitOn` gets `true`. The status bar and the tray can turn it on/off
  without losing the number (IDM: Speed Limiter → Turn on / Turn off / Settings…).
- `duplicates`: 'ask' (default) | 'number' | 'replace' | 'resume' (see Duplicates).
- `showComplete` (bool, default true).
- `queues` (see Queues). `scheduleAt` goes away: on load it moves into the main queue's `startAt`.
- `types`: the categories and their file types, `{ name: [ext, …] }` in sidebar order (see Categories).

### Power (shared countdown)
One countdown, as today (60 s, `powerDelayMs`), for three triggers: the global "When done" (all downloads
finished, used once, as today), a download's own `whenDone` sleep/shutdown when it completes, and a queue's
`whenDone` when the queue is done. The per-download and per-queue triggers start the countdown even while other
downloads run. `POST /api/settings {whenDone: ''}` (the strip's Cancel) cancels any running countdown. view()
`powerIn` and `whenDone` describe the running countdown's op; the app's strip text names the reason ("Shutting down
in 42 s" stays).

### Duplicates
A duplicate is a download whose url equals one already in the list (not pending). For `add({confirm: true})` the
probe adds `probe.duplicate = { id, name, status }` of the existing one. The app asks (when `duplicates` is 'ask')
or applies the saved choice, then answers through `confirm`:
- 'number': as today (the new one gets " (1)" etc.).
- 'replace': `confirm({…, replace: <existing id>})` removes the existing entry (its .fdl / temp files too); the new
  download writes to the existing file's path (allowed even though the file exists; it is replaced when the new one
  starts writing). Save As defaults to the existing file.
- 'resume': `confirm({action: 'cancel'})` for the new one and `act(existing, 'resume')` (a finished one is shown
  instead: the app opens its Download complete window). No engine change beyond `probe.duplicate`.
- `confirm({…, rememberDuplicates: 'number' | 'replace' | 'resume'})` saves the choice in `settings.duplicates`.
- Adds without confirm (Add links, batches): 'ask' and 'resume' act as 'number'; 'replace' replaces.

### Queues
- `settings.queues`: an array, the first always `{ id: 'main', name: 'Main download queue' }` (cannot be removed or
  renamed). Each queue: `id` (made by the engine), `name` (1–40 characters, unique), `maxActive` (1–20, default 1:
  "Download N files at the same time"), `startAt` ('' or HH:MM), `days` (array of 0–6, Sunday = 0; empty = every day),
  `stopAt` ('' or HH:MM), `retries` (0–10, default 0), `whenDone` ('' | 'sleep' | 'shutdown').
- A download in a queue that has not started yet has status 'scheduled'. Its order in the queue is its order in
  `mgr.list` among that queue's downloads.
- Start (manual, or at `startAt` on an allowed day, once per day): the queue runs; its 'scheduled' downloads become
  'queued'; `pump()` keeps at most `maxActive` of that queue's downloads running (and the global `maxActive` for all).
  Downloads started by hand (Resume) are not held back by a queue that is not running.
- Stop (manual, or at `stopAt`): the queue stops; its downloading and queued downloads go back to 'scheduled'
  (paused like Pause, but they stay in the queue).
- Retries: while a queue runs, a failed download of it goes back to 'queued' after 10 s, up to `retries` times per
  queue run.
- Done: a running queue with nothing 'queued' / 'downloading' / 'scheduled' left stops; its `whenDone` starts the
  countdown.
- Running is not saved (after a restart no queue runs until its next start time or a manual start).
- API: `POST /api/queues {op: 'add', name} → {id}`, `{op: 'update', id, …fields}`, `{op: 'remove', id}` (its
  downloads go to the main queue), `{op: 'start', id}`, `{op: 'stop', id}`.
  `POST /api/action {op: 'queue', id, queue}` (queue id, or null = out of every queue: 'scheduled' becomes
  'paused'), `{op: 'up', id}` / `{op: 'down', id}` (swap with the previous / next download of the same queue).
- `add({scheduled: true, queue})` and `confirm({action: 'later', queue, startQueue})`: the download goes into that
  queue (default 'main') as 'scheduled' ("Download Later" used to make it 'paused'); `startQueue` starts the queue.
- view(): `queues` with each queue's fields plus `running` and counts (`waiting`, `active`, `total`).
- Migration on load: `scheduleAt` → main queue `startAt`; 'scheduled' downloads without a queue → 'main'.

### Categories
- `settings.types`: `{ Video: […], Music: […], Programs: […], Documents: […], Compressed: […], …custom }`, default
  from today's `CATEGORIES` in that order. Every place that uses `CATEGORIES` uses the settings instead (probe,
  `assignName`, `isCategory`, view's `exts` and `categories`).
- The five built-in names cannot be removed or renamed (their icons are keyed by name); their file types and folders
  can change. Custom categories: name 1–30 letters, digits, spaces, `-` or `_`, unique (case-insensitive), not
  "Other" or "All"; they can be added, renamed and removed (their downloads become Other; their remembered folder
  goes too).
- File types: 1–10 letters or digits, lower case, without the dot; one category per type (the later one in a POST
  wins, the earlier loses it).
- Folders stay in `settings.folders` per category, as today.

## Screens (draft; the mockups decide)
Arrow's look stays (tokens, fonts, radii, no grid lines). New:
- Command bar under the top bar, like File Explorer's: labeled buttons Resume, Pause, Delete · Pause all, Delete
  completed · Start queue ▾ (choose a queue), Stop queue · Scheduler · Speed limit ▾ (On/Off, Settings…), and at the
  right Sort ▾ and View ▾ (columns, details panel).
- Download list with columns (header row, sortable, chooser): Name (icon, name, progress line), Size, Status, Time
  left, Speed, Last try, Description, Save to. Narrow windows drop columns from the right; the details panel can be
  hidden.
- Sidebar: a "Queues" group (each queue, running ones marked) under Types.
- Progress window: "Details" shows the connections (number, downloaded, info); options "Limit this download to N
  KB/s" and "When done: Nothing / Open the file / Sleep / Shut down".
- Download complete window: Open, Open with…, Open folder, Close, "Don't show this window again"; appears by itself
  for downloads started from Download File Info.
- Download File Info: Description, "+" next to Category (new category), Download Later asks for the queue.
- Duplicate link window; Properties window (replaces Change link); category editor; Scheduler window; Options with
  pages (General, Downloads, Categories, Queues).

## Tasks
See issues/. Engine first (01–03), screens after the mockups (04–08), docs and screenshots last (09).
