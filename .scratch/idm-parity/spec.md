# IDM parity: IDM's features in Glide's own look

Status: approved by the user on 2026-10-22 (engine, and the screens from mockups/).

The user asked for "the same UX as IDM" and chose: keep Glide's modern Windows 11 look (docs/NATIVE_UI.md), add what
IDM has and Glide lacks, in four areas: main window, download progress, queues and scheduler, dialogs and options.
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
downloads run. `POST /api/settings {whenDone: ''}` (the strip's Cancel) cancels any running countdown. view():
`whenDone` stays the status bar's choice, `powerOp` names what the running countdown does (null when none),
`powerAll` says it is "When all downloads finish" (not one download's or queue's own), `powerIn` as today; the app's
strip reads `powerOp` and `powerAll` ("All downloads finished. Shutting down in 42 s" only for `powerAll`).

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

## Screens (approved 2026-10-22 from the mockups in mockups/; the user chose "details panel hidden by default")
Glide's look stays: docs/NATIVE_UI.md's tokens, fonts, radii, controls (FlatButton, TextField, Picker, FlatCheck,
…), no grid lines, keyboard and accessibility rules. Pixel sizes at 96 DPI. Every new dialog also gets an GLIDE_SHOTS
picture (Dialogs.Shots) so the orchestrator can review real screenshots. The mockup numbers below match
mockups/m1–m5.

### 1 Main window (mockups/m1-main.png)
- Top bar: logo, paste bar, Download, then ghost buttons Add several links, Options, More. Pause all and Scheduler
  move to the command bar. More: About <name>, separator, Quit <name> (Delete completed moves to the command bar).
- Command bar (new, under the top bar): height 44, top token, bottom hairline, padding 0 8. Ghost buttons 32 high,
  radius 6, glyph (16 px, muted) + text, hover = hover token, disabled = faint text and glyph, tooltips. Left to right:
  Resume (E768), Pause (E769), Delete (E74D) for the selection · Pause all (E769), Delete completed (E74D) · Start
  queue ▾ (E768; one queue: starts it; more: a menu of the queues that are not running), Stop queue (E71A; enabled
  while a queue runs; more than one running: a menu) · Scheduler (E121). At the right: Speed limit ▾ (EC4A; text
  "Speed limit: Off" or "Speed limit: 500 KB/s"; menu: Turn on (500 KB/s), Turn off, Settings… → Options ›
  Downloads). Vertical separators: 1 x 20 line token, 6 px margins. When the labels don't fit, the whole bar shows
  glyphs only (tooltips keep the names).
- Above the list: the search field (flex) and, at its right, ghost buttons Sort ▾ (E8CB) and View ▾ (E8A9).
- List header (new): height 32, muted 12 px titles, bottom hairline. Click = sort by that column; again = reverse;
  a third time = back to the default order. The sorted column's title is text color with a 9 px chevron (E70E up /
  E70D down).
- Columns, in order: Name (the rest, at least 220: icon 24, name, progress line under it, as rows are now), Size 86,
  Status 120, Time left 92, Speed 96, Last try 112, Description 150, Save to 160, Added 112. Shown by default: Size,
  Status, Time left, Speed, Last try, Description. Columns that don't fit are left out from the right; Name keeps 220.
- Cells (one line, ellipsis): Size = Core.Size (empty when unknown); Status = "45%", the video phases ("Merging",
  "Converting to MP3"), "Queued", "In <queue name>" (scheduled), "Paused · 30%", "Failed" (err), "Complete" (muted);
  Time left and Speed only while downloading; Last try = Core.DateLabel(lastTry); Description; Save to = folder;
  Added = Core.DateLabel(added). Finished rows: everything but the name in muted.
- Default order stays as today (unfinished first, newest first); in a queue section, the queue's order.
- Sort ▾: Default, then each column; the current one checked; ascending / descending entries. View ▾: each column as
  a checkable item, separator, "Details panel" (checkable, default off). Sort, columns and the details panel are
  remembered in ui.json.
- Details panel (the inspector) hidden by default; when shown, it works as now, with new facts Description, Queue,
  Last try.
- Sidebar: a "Queues" group under Types: each queue with the List glyph (E8FD, 18 px, muted), its name ("Main
  queue" for the main one), a done-color 8 px dot when it runs, and its count. Selecting it lists its downloads in
  queue order.
- Row menu: Open, Open with…, Show in folder, Copy link · Resume / Pause, Add to queue ▸ (each queue), Remove from
  queue (when in one), Move up / Move down (in a queue section) · Progress window, Properties… · Remove….
  "Change link…" moves into Properties.
- Status bar: left "1 active · 2.4 MB/s" + a chip (line border, radius 10) "Speed limit: Off" / "Speed limit: 500
  KB/s"; click toggles `speedLimitOn`. Then " · Main queue at 02:00" for the next scheduled queue start. Right: When
  done as now. Tray menu gets "Speed limit" (checked when on) above the separator.

### 2 Download progress window (mockups/m2-progress.png)
As now, plus two disclosure rows (chevron E70D/E70E 10 px, text, collapsed state remembered in ui.json):
- "Connections (n)" (HTTP downloads with pieces only): a 3-column table, rows 26, 1 px dividers: # · Downloaded
  ("126 MB of 140 MB") · Info: "Receiving data…" (4th segment value 1), "Done" (piece complete, muted), "Waiting"
  (muted). Open by default.
- "Options": FlatCheck "Limit this download to" + number field (1–10 000 000) + "KB/s" (unchecked = 0) →
  `op: 'limit'`; "When this download is done" + Picker: Nothing / Open the file / Sleep / Shut down → `op: 'whenDone'`.
  Both apply at once. Open by default.

### 3 Download complete window (mockups/m2-progress.png, right)
Green check tile + "Download complete" + name; facts Saved to (folder), File size, Address (ellipsis); FlatCheck
"Don't show this window again" (closing with it checked → `showComplete: false`). Footer: Open with… (left; Windows'
Open with dialog for the file), Open folder, Close, Open (primary). It opens by itself, once, for a download with
`notify` that completes, when `showComplete` is on; when that download's progress window is open, that window
turns into this view instead (as today).

### 5 Download File Info (mockups/m4-fileinfo.png, left)
As now, plus: a "+" button (36 wide) right of Category: opens the category editor for a new category and selects
it; a Description field (cue "Optional, shown in the list", 300 characters) after the remember checkbox →
`confirm({description})`; "Download Later ▾" opens a popover "Add to queue": Picker of the queues + "+" (asks for a
new queue's name), FlatCheck "Start the queue now", primary "Add to queue" → `confirm({action: 'later', queue,
startQueue})`.

### 6 Link already in the list (mockups/m4-fileinfo.png, right)
Before Download File Info, when the pending download has `probe.duplicate` and `settings.duplicates` is 'ask':
"This link is already in your list" + the existing name and status; radios "Download it again with a new name"
(sub-line: the numbered name), "Replace the existing file", "Resume the existing download" (sub-line "If it's
finished, show it instead"); FlatCheck "Remember my choice (change it in Options)"; Cancel / OK. Cancel cancels the
pending download. A saved choice skips the window and acts the same way.

### 7 Properties (mockups/m5-options.png, left)
From the row menu and the details panel. Header (32 px file icon, name, status line); Address (editable when the
download is paused, failed or in a queue and not YouTube; else read-only) → `op: 'url'`; Description (editable) →
`op: 'describe'`; facts Saved to, Referer (when known), Added, Last try, Queue. Footer: Show in folder (left), Cancel,
Save (sends only what changed).

### 4 Scheduler (mockups/m3-scheduler.png)
Opened from the command bar, Options › Queues and the sidebar's queue menu. Left column (sidebar token, 230): the
queues (List glyph, name, running dot) + New queue / Delete (Delete disabled for the main queue; deleting asks, and
its downloads go to the main queue). Right (inspector token), for the selected queue, label column 196: Name
(read-only for the main queue), "Download at the same time" [N] files, Start: radios "By hand only" / "At" [HH:MM],
"On these days" Mon–Sun toggles (none = every day; a faint hint says so), Stop: FlatCheck "At" [HH:MM], "If a file
fails" Try again [N] times, "When the queue is done" Picker Nothing / Sleep / Shut down; then the queue's files
(name, size, status) in order with Move up, Move down, Remove from queue. Footer: Start now, Stop (for the selected
queue), Close (primary). Each change is sent at once (`POST /api/queues update`), validated inline (invalid fields get
the err border and are not sent).

### 8 Options (mockups/m5-options.png, right)
A left navigation (sidebar token, 170, items 32): General, Downloads, Categories, Queues; the page at the right;
footer Cancel / Save.
- General: Start with Windows; Offer links I copy; Show the Download complete window; "When a link is already in the
  list" Picker: Ask me / Download it again with a new name / Replace the existing file / Resume the existing download.
- Downloads: Save to folder + Browse…; Sort into category folders; Connections per download (1–32); Downloads at the
  same time (1–20); FlatCheck "Limit all downloads to" [N] KB/s (`speedLimitOn`, `speedLimitKB`).
- Categories: a table (icon, name, file types, folder) + Add, Edit…, Remove (disabled for the built-ins) and the note
  "The five built-in categories keep their names and icons; you can change their file types and folders. Files that
  match no category go to Other." Add / Edit open the category editor: Name (read-only for built-ins), File types
  (space separated), Folder + Browse…, Save / Cancel. Custom categories show the Other glyph in the sidebar and here.
- Queues: "Queues and their schedules are in the Scheduler." + Open Scheduler.
- The old "Schedule" section goes (it lives in the Scheduler now).

## Tasks
See issues/. Engine first (01–03), screens after the mockups (04–08), docs and screenshots last (09).
