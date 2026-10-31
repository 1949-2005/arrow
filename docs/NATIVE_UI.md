# Native UI design (Glide.exe, direction "Inspector")

The approved design for the native Windows app that replaces ui.html, confirm.html and progress.html, with the IDM
parity screens approved on 2026-10-22 (`.scratch/idm-parity/spec.md`, mockups m1–m5).
Every size is in pixels at 96 DPI (scale with the system DPI). Workers implement this; they do not invent UI.

## Principles
- IDM's features in Glide's own look: what IDM offers (columns, a command bar, queues and a scheduler, the progress
  and complete windows, properties, categories), drawn as a Windows 11 utility. No menu bar, no big icon toolbar (the
  command bar's ghost buttons are 32 high with 16 px glyphs), no grid lines in the download list (small tables in
  dialogs use 1 px row dividers). Flat surfaces, 1 px hairlines, rounded corners, generous spacing, one primary
  button per area.
- The logo's colors: graphite `#3B3C3D` and white. Graphite leads in light mode, white in dark mode.
  Green, red, amber and the six category colors keep their meaning (state and file type), nothing else is colored.
- Follows Windows light/dark (`HKCU\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize\AppsUseLightTheme`)
  and switches live (`SystemEvents.UserPreferenceChanged`). Dark title bar in dark mode (DwmSetWindowAttribute 20, fallback 19).
- Everything works from the keyboard (the list header is mouse-only: Sort ▾ is its keyboard path), every icon-only
  button has a tooltip and an AccessibleName, focus is always visible.
- A download's name always shows whole: wrapped onto the lines it needs, never cut with "…" or clamped to N lines
  (the list, the Scheduler's files, the details panel, the progress and Download complete windows, Properties,
  Download File Info's header, Link already in the list). Only an editable field (Save As) stays one line.
- Every window and dialog has an GLIDE_SHOTS picture (`MainForm.Shots`, `Dialogs.Shots`) for visual review.

## Tokens
| Token | Light | Dark | Use |
|---|---|---|---|
| top | #F6F6F8 | #1E1E20 | top bar, command bar, status bar, dialog footers |
| sidebar | #EFEFF2 | #252528 | left column, the Scheduler's queues, Options' page list |
| list | #FFFFFF | #1B1B1D | list column |
| inspector | #F7F7F9 | #222225 | details panel, dialogs' body |
| field | #FFFFFF | #252528 | text fields, paste bar |
| line | #DCDCE0 | #3A3A3E | hairlines, field borders, secondary button borders |
| text | #1D1D1F | #F5F5F7 | main text |
| muted | #6E6E73 | #A1A1A6 | labels, secondary text |
| faint | #8E8E93 | #8E8E93 | placeholders, meta lines, disabled buttons |
| hover | #F0F0F3 | #26262A | row and ghost button hover |
| soft | #E2E2E6 | #38383C | selected sidebar item, pressed ghost button |
| sel / onSel | #3B3C3D / #FFFFFF | #505154 / #FFFFFF | selected list rows |
| accent / onAccent | #3B3C3D / #FFFFFF | #ECECEE / #1D1D1F | primary buttons, focus ring, progress fill, graph line, chosen day chips |
| bar | #E2E2E7 | #3A3A3E | progress track |
| done | #1E8E3E | #30D158 | finished, complete bar, a running queue's dot |
| err | #D70015 | #FF453A | failed, remove, delete |
| warn | #B45309 | #FFB340 | warnings, countdown |
| video / music / docs / programs / zip / other | #AF52DE / #E0245E / #0A6CDB / #5856D6 / #D97706 / #8E8E93 | #BF5AF2 / #FF375F / #4DA3FF / #7D7AFF / #FFB340 / #98989D | file-type glyphs |
| tint | category color at 14% alpha | category color at 22% alpha | file-type tiles, over the surface |

Fonts: text "Segoe UI Semibold" 10.5pt (14 px), Small 9pt (12 px). Emphasis (Bold, SmallBold) is "Segoe UI" in the Bold
style at the same sizes, so it reads stronger than the semibold text; big numbers "Segoe UI" Bold 18pt (24 px). Not "Segoe UI Variable": it has no Arabic, so GDI would draw Arabic names in Tahoma.
Text is drawn by GDI (TextRenderer); text with emoji (U+1F000–1FAFF, U+2600–27BF, U+FE0F, the keycap U+20E3) is drawn
and measured by DirectWrite through a Direct2D DC render target with color fonts on (`ColorText.cs`), at GDI's line height
and baseline, so emoji are in color as in the browser. Text fields (TextBox) stay GDI: emoji there are monochrome or boxes.
Radii: 6 (sidebar items, small and command bar buttons), 8 (fields, buttons), 10 (the status bar's speed limit chip), 12 (tiles). Spacing steps 4, 8, 12, 16.
Focus ring: 2 px accent, drawn 1 px inside the control, only for keyboard focus.

Icons: "Segoe Fluent Icons" (fallback "Segoe MDL2 Assets"), drawn in the token color, 16 px in lists and buttons, 24 px in tiles:
Download E896, Link E71B, Add E710, List E8FD, Clock E121, Options E9E9, More E712, Pause E769, Play E768, Stop E71A,
Delete E74D, Folder E8B7, Open folder E838, Open file E8E5, Copy E8C8, Search E721, Check circle E930, Warning E7BA,
Error E783, Clipboard E77F, Close E8BB, Chevron down E70D, Chevron up E70E, Check mark E73E, Sort E8CB, View E8A9,
Speed limit EC4A, Move up E74A, Move down E74B, Minimize E921. Categories: All E8B7, Active E896, Finished E930, Failed E783,
Scheduled E121, Video E714, Music EC4F, Documents E8A5, Programs ECAA, Compressed F012, Other E7C3.

File types show the icon Windows shows for them, as File Explorer and IDM do (`FileIcons.cs`): SHGetFileInfo on the
extension alone (SHGFI_USEFILEATTRIBUTES, never the disk), the 16 px image up to 16 px, else the 32 px one scaled
down with high-quality bicubic, cached per extension and size. The extension is the file's own, else its category's
typical one (.mp4, .mp3, .pdf, .exe, .zip; none = the generic file). Sizes: 24 px in list rows, 32 px (native, crisp)
in the headers of the details panel, Download File Info, the progress window and Properties. The sidebar's Types at 28 px: the user's SVG icons
`assets/types/<category>.svg` for Video, Music, Documents, Programs and Compressed, the same on every PC (a typical
file's icon comes from whatever app opens that type: a .pdf showed the Claude app's logo). `Core.ParseSvg` reads any
path data (arcs and quadratics become cubics); a transform, another shape or fill="none" shows the glyph instead. The
path is cached per category and filled with the file's fill rule (nonzero unless evenodd), anti-aliased, in `Theme.Text` at paint
time so it follows the theme. Other uses assets/types/other.svg like the others; a missing or unreadable file shows the category glyph in its color.
Custom categories use Other's icon everywhere (sidebar, Options › Categories).
The main window's other icons are the user's SVGs too, `assets/icons/<name>.svg`, read and drawn the same way
(`FileIcons.DrawIcon`); one file can serve several places:
- `all`, `active`, `finished`, `failed`: the sidebar's Library; `active` is also the Download button
- `scheduler`: Scheduled (sidebar) and Scheduler (command bar)
- `play`: Resume and Start queue; `pause`: Pause and Pause all; `delete`: Delete and Delete completed; `stop`: Stop queue
- `mainqueue`: the sidebar's queues and the top bar's Add several links button
- `link`: the paste bar; `search`: the search field; `sort`: Sort; `view`: View; `speed`: Speed limit; `settings`: Options; `more`: More

Each icon has a white file `<name>.svg` for the dark theme and `<name>_black.svg` for the light one, drawn in the
file's own fill color (no fill: black, SVG's default). The Download button sits on the accent, the opposite shade, so
it takes the other file. A disabled button's icon keeps its full color (only its text greys). Sidebar items at 28 px,
fields and the icon-only buttons at 16 px, the command bar, Sort and Download at 20 px. A missing `_black` file: the white one in `Theme.Text`; a missing or unreadable file: the glyph.

## Main window
Default 1180 x 700, minimum 900 x 560; remembers bounds, maximized state, the selected section, the sort, the shown
columns, the details panel and the progress window's two disclosures (`%LOCALAPPDATA%\<name>\ui.json`). Closing it
hides it (the app stays in the tray); the first time, a tray balloon says
"<name> keeps running in the tray. Right-click its icon to quit."

```
+------------------------------------------------------------------------------------------+
| [logo] [link  Paste a link or drop files                    ][ Download ]  (≡)(⚙)(⋯)     |  top bar 56
+------------------------------------------------------------------------------------------+
| ▷Resume ‖Pause 🗑Delete | ‖Pause all 🗑Delete completed | ▷Start queue▾ □Stop queue | ⏱Scheduler   Speed limit: Off▾ |  44
+------------------------------------------------------------------------------------------+
| strip: clipboard offer / countdown / offline (only when needed)                     36   |
+---------------+--------------------------------------------------------------------------+
| Library       | [search  Search downloads                          ]  Sort▾  View▾       |
|  All        7 | Name                      Size    Status       Time left Speed Last try  Description |
|  Active     1 | [i] photos-ete.zip        500 MB  Paused · 30%                Today 12:00          |
|  Finished   4 |     ━━━━━━━━░░░░░░░░                                                      |
|  Failed     1 | [i] wallpaper.png         3,0 MB  Failed                      Today 14:00          |
|  Scheduled  1 | [i] setup-2.4.1.exe       100 MB  In Main queue                                    |
| Types         | [i] Podcast episode 12…   46 MB   Complete                    Today 15:00 Episode 12 |
|  Video …      |                                                          (details panel, 320,    |
| Queues        |                                                           hidden by default)     |
|  Main queue ● 1                                                                          |
|  Night      0 |                                                                          |
+---------------+--------------------------------------------------------------------------+
| 1 active · 2.4 MB/s (Speed limit: Off) · Night at 23:30            When done: [Do nothing v] |  status bar 30
+------------------------------------------------------------------------------------------+
```
Columns: sidebar 200 (fixed), list fills, details panel 320 (fixed, hidden by default). Hairlines between them.

### Top bar (top, height 56, padding 12)
- Logo 24 px (the brand icon), then the paste bar: height 34, radius 8, field + line border, link glyph in faint at
  the left, a borderless TextBox with the native cue banner (EM_SETCUEBANNER) "Paste a link or drop files".
  Focused: border becomes accent.
- Primary button "Download" (Download glyph + text), height 34, accent / onAccent.
  Enter in the paste bar = Download. One link: POST /api/add {url, confirm: true}; the app then shows its
  Download File Info dialog for that pending download (see Dialogs). Several links or a `[from-to]` range: open the
  Add links dialog prefilled. Invalid text: the paste bar's border turns err and a tooltip says "Paste an http or https link".
- Ghost icon buttons, 34 x 34, radius 8, muted glyphs, hover = hover token, tooltips, 4 apart:
  Add several links (List glyph, opens Add links), Options (opens Options › General), More (⋯ menu: About <name>,
  separator, Quit <name>).
- Ctrl+V anywhere outside a text field puts the clipboard into the paste bar and focuses it.

### Command bar (under the top bar, height 44, top token, bottom hairline, padding 0 8)
Ghost buttons 32 high, radius 6, padding 8, glyph (16 px, muted) + text, hover = hover token, disabled = faint text
and glyph, each with its name as tooltip and AccessibleName. Left to right:
- Resume (Play), Pause, Delete (the Remove dialog) for the selection;
- Pause all (enabled while something runs or waits), Delete completed (asks "Remove N finished downloads from the
  list? The files stay on disk.");
- Start queue (Play; one queue: starts it; more: a ▾ menu of the queues that are not running), Stop queue (Stop;
  enabled while a queue runs; more than one running: a ▾ menu);
- Scheduler (Clock, opens the Scheduler).
- At the right: Speed limit ▾ (Speed limit glyph, "Speed limit: Off" or "Speed limit: 500 KB/s"); menu: "Turn on
  (<n> KB/s)" with the stored number (when there is none: "Set a limit…" → Options › Downloads), Turn off,
  Settings… (Options › Downloads).
Groups are split by 1 x 20 vertical lines (line token) with 6 px margins. When the labels don't fit (below about
1180), the whole bar shows glyphs only; the tooltips keep the names.

### Strip (below the command bar, height 36, only when needed, one at a time by priority: offline > countdown > clipboard)
Full-width, inspector background, bottom hairline, glyph + text + text buttons on the right:
- Offline (err glyph): "<name>'s engine isn't running." [Start it]
- Countdown (warn glyph), named after `powerOp`: "All downloads finished. Shutting down in 42 s" (only for
  `powerAll`), else "Shutting down in 42 s" / "Going to sleep in 42 s" (a download's or a queue's own) [Cancel]
  (POST /api/settings {whenDone: ''}). A tray balloon announces it once.
- Clipboard offer (Clipboard glyph, only when settings.clipboard is on): "From clipboard: <url>" [Download] [×].
  Listen with AddClipboardFormatListener; offer a text only once, only when Core.ClipboardLink accepts it (a file
  type of any category, `state.exts`) and no download has that url.

### Sidebar (width 200, sidebar token, padding 8)
Three groups with a 12 px muted heading ("Library", "Types", "Queues"), items 30 high, radius 6, a 28 px icon slot +
label + count right (muted). Selected: soft background, bold label. Hover: hover token. Keyboard: Up/Down move, the list follows.
Library: All, Active (downloading, queued, paused), Finished (done), Failed (error), Scheduled — glyphs at 18 px, muted
(text when selected).
Types: `state.categories` in their order, then Other — the user's SVG icons in the text color at 28 px for the five
built-ins and Other; custom categories show Other's icon.
Queues: each queue with the List glyph (18 px, muted), its name ("Main queue" for the main one), an 8 px done-color dot
before the count while it runs, and its count (waiting + active). Selecting it lists its unfinished downloads in queue
order. Its context menu (right-click, the menu key, Shift+F10): Open Scheduler (on that queue).

### List (list token)
- Above it: the search field (height 32, margin 12, field + line, Search glyph, cue "Search downloads", Ctrl+F
  focuses it, Esc clears; filters by name or url, case-insensitive, together with the sidebar section) and, at its
  right, ghost buttons Sort ▾ and View ▾ (4 apart).
- Header: height 32, muted 12 px titles over the cells, bottom hairline. Click = sort by that column ascending; again
  = descending; a third time = back to the default order. The sorted column's title is in the text color with a
  9 px chevron (up ascending, down descending). Right-click = the View menu.
- Columns, in order: Name (the rest, at least 220), Size 86, Status 120, Time left 92, Speed 96, Last try 112,
  Description 150, Save to 160, Added 112. Shown by default: Size, Status, Time left, Speed, Last try, Description.
  Columns that don't fit are left out from the right (the first that doesn't fit and every one after it); Name
  keeps 220.
- Custom-drawn rows, padding 12, no grid lines: the file's Windows icon at 24 px (as is, also when
  selected), 12 px gap, the name, always whole: wrapped onto the lines it needs, never cut. A row is 48 high, or the
  name's height + 28 when it wraps; with a progress line the name starts 7 from the top, else the name is centered.
  A 3 px progress line 5 under the name's last line (radius 2, bar token
  track, accent fill; paused and scheduled fill = faint; error = err) for downloading, queued and paused rows, and
  for failed or scheduled ones that have progress. The icon and the cells sit on the name's first line. Cells: one
  line, ellipsis. Size = Core.Size (empty when
  unknown); Status = "45%", "Merging", "Converting to MP3", "Queued", "In <queue name>" (scheduled), "Paused · 30%",
  "Failed" (in err), "Complete"; Time left and Speed only while downloading; Last try and Added = Core.DateLabel;
  Description; Save to = the folder. Finished rows: everything but the name in muted.
- Order: by default unfinished first (downloading, queued, paused, error, scheduled), newest first within each; then
  finished, newest first. In a queue section, the queue's order. A column sort applies in every section; empty
  cells stay last both ways; Status sorts by that group order, then by progress.
- Sort ▾: Default, then each column (the current one checked), separator, Ascending, Descending. View ▾: each column
  but Name as a checkable item, separator, "Details panel" (checkable, default off).
- Hover: hover token. Selected: sel / onSel (glyphs and texts turn onSel). Multi-select with Ctrl and Shift, Ctrl+A.
- Keys: Up/Down/Home/End/PageUp/PageDown, Enter (open a finished file, else the progress window), Space (pause or
  resume), Delete (Remove dialog), Ctrl+C (copy the links), Ctrl+N (Add links), Ctrl+, (Options).
- Double-click: finished = open the file; else the progress window. Right-click menu: Open, Open with… (Windows'
  Open with dialog), Show in folder, Copy link · Resume / Pause, Add to queue ▸ (each queue, checked when the
  selection is in it), Remove from queue (when in one), Move up / Move down (in a queue section, unfinished rows
  only) · Progress window, Properties… · Remove….
- Drag links over the window: a full-window overlay (inspector token at 92% alpha, accent dashed border radius 12,
  Download glyph 32, "Drop to download"). Drop: one link = the paste-bar flow, several = Add links dialog.
- Empty list: centered logo at 64 px (40% alpha), "No downloads yet" (bold), "Paste a link above, drop links here,
  or let the browser extension catch them." (muted), and a secondary button "Add links…".
  Empty section: "Nothing here" + "No downloads in this section."; no search match: "No download matches your search."
- Accessibility: the control exposes one AccessibleObject per row (role ListItem, name = file name + trailing text,
  selected state); the list has role List and the name "Downloads".

### Details panel (the inspector; width 320, inspector token, padding 16, scrolls if short; hidden by default, View ▾ › Details panel)
Shows the selection:
- One download:
  - Header: the file's Windows icon at 32 px in a 48 px slot, name (bold, whole: as many lines as it needs),
    host of the url (muted) with a ghost Copy button (Copy glyph, tooltip "Copy link").
  - Progress: big percent ("45%", or "Done" when finished, "Failed" in err) + "974 MB of 2.11 GB" (muted);
    under it an 8 px segmented bar (Core.Pieces: one piece per connection for http, one fill otherwise),
    accent pieces on the bar track; finished = full done color; error = err.
  - Error box when failed: err tint (12%/20% alpha), err glyph, the full error text (wraps), selectable.
  - Speed graph while downloading: 56 high, the last 60 s of this download's speed (one sample per poll),
    1.5 px accent line with a 12% accent fill below, a hairline baseline, current speed top right.
  - Facts (label muted left, value right, rows 22): Status (Core.StatusText), Speed, Time left, Connections,
    Resume (Yes/No/—), Added, Last try (Core.DateLabel), Queue (when in one), Format (YouTube key text) or Type,
    Description (when set), Saved to (folder, ellipsis, tooltip = full path, click = Show in folder).
  - Actions: a 2-column grid of buttons, height 32, radius 8. The first is primary and depends on the state:
    Pause (downloading, queued), Resume (paused, scheduled), Retry (error), Open (done).
    Then: Show in folder, Copy link, Progress window (unfinished), Properties…, Remove (err text, secondary style).
- Several selected: "3 downloads" (big), total size (muted), up to 5 names, actions Resume, Pause, Remove.
- Nothing selected: "Overview": total speed (big), the 60 s total speed graph, counts (Active, Finished, Failed),
  free space on the download folder's drive ("120 GB free on C:").

### Status bar (height 30, top token, top hairline, 12 px muted)
Left: "1 active · 2.4 MB/s", then a chip (line border, radius 10, 20 high) "Speed limit: Off" / "Speed limit: 500
KB/s": click turns `speedLimitOn` on or off (no number yet: Options › Downloads); then " · Main queue at 02:00", the
next start of a queue that is not running (Core.NextStart). Right: "When done:" and a small picker (110 x 22, radius
6: Do nothing, Sleep, Shut down) → POST /api/settings {whenDone}; it shows the engine's `whenDone`.

### Updates
Strip (lowest priority, after the clipboard offer): Download glyph, "<name> 1.2.0 is available. You have 1.0.0.", Update and × (hides that
version: ui.json `skipVersion`). While the setup downloads: "Downloading <name> 1.2.0… 45%. <name> restarts when it's installed.", no
buttons. Update asks first when downloads run (they stop while the setup replaces the app). Checks: 10 s after start, then
every 24 h, when ui.json `updates` (⋯ › Check for updates automatically, default on); ⋯ › Check for updates says when it
finds nothing (balloon) or fails (error dialog).

### Tray
NotifyIcon with the brand icon, tooltip = the app name + " · 2 active" when something runs. Left click: show and
activate the main window. Menu: Open <name> (bold, default), Pause all, Speed limit (checked when on; click toggles
it like the chip), separator, Quit <name>.
Balloon when a download finishes ("Download complete", file name; click = show it selected in the main window).

## Dialogs
All dialogs: inspector token body, padding 20, top token footer 64 high with a top hairline and buttons 32 high
(at least 88 wide, 8 apart), radius-8 fields (field + line, accent border when focused, err border when invalid),
primary button accent/onAccent, secondary buttons transparent with a line border, destructive ones in err text,
Enter = primary, Esc = Cancel, dark title bar in dark mode, the brand icon, FormBorderStyle FixedDialog, no min/max
boxes, centered on the screen's working area (the main window's screen, or the mouse's when it is hidden), at any
resolution; a change of height (Connections, Options) keeps the middle in place until the user moves the window. Engine calls run off the UI thread; their
errors show inline or in an Error dialog.
- Download File Info (560 wide; one per pending download, appears in front even when Glide is in the tray; same
  rules as confirm.html): header (the Save As name's Windows icon at 32 px, centered in a 48 px slot, "Download File
  Info", Core.Subtitle), then label column 86: URL (read-only, selectable), amber note when the probe failed,
  Format (YouTube only, 320 wide; while probe.listing it shows "Getting formats…" with a small spinner and only
  Cancel works; items are Core.FormatText grouped MP4, WEBM, Audio; hidden with its gap closed when the download's
  own Download.Format is set and the probe is listing or offers that key: the extension's format menu already
  picked it), Category (picker 180, the categories by name then Other) + a "+" button 36 wide (the category editor
  for a new category, which is then selected and sets the folder), Save As + Browse… (native SaveFileDialog, no
  overwrite prompt; tooltip "Missing folders are created."), FlatCheck "Remember this folder for <Category>" ("for
  other files" for Other; follows Category, unchecked on open; checked → Start/Later send remember: <Category>),
  Description (cue "Optional, shown in the list", 300 characters), inline error, Size (bold). Save As's folder is
  Core.FolderFor (the remembered folder first).
  Footer: Download Later ▾ (left), Cancel, Start Download (primary). Start → POST /api/confirm start, then this
  dialog becomes the progress window for that download. Download Later opens the Add to queue popover under the
  button (above it near the screen's bottom): a borderless window 300 wide, padding 16, with a shadow and Windows
  11's round corners: "Add to queue" (bold), a Picker of the queues + "+" 36 wide (asks "Name of the new queue" and
  creates it), FlatCheck "Start the queue now", primary "Add to queue" → POST /api/confirm {action: 'later', queue,
  startQueue}. Esc or a click elsewhere closes it.
  When the probe brings `probe.duplicate` (often after the window opened): with `duplicates` 'ask' the Link already
  in the list window opens over it; a saved choice acts the same way without asking. Number: nothing changes;
  Replace: Save As becomes the existing file and Start/Later send `replace`; Resume: this download is cancelled and
  the existing one resumes, or its Download complete or progress window shows. Cancel there cancels this download.
- Link already in the list (460 wide): "This link is already in your list" (bold) + the existing name and status
  (small, muted); radios "Download it again with a new name" (sub-line: the numbered name, as uniqueFile names it),
  "Replace the existing file", "Resume the existing download" (sub-line "If it's finished, show it instead"), the
  sub-lines indented 26; FlatCheck "Remember my choice (change it in Options)" → `rememberDuplicates`. Footer:
  Cancel, OK (primary).
- Progress window (460 wide; one per download; IDM's, restyled like the details panel): the file's Windows icon at
  32 px (in the tile's place) + title ("Downloading · 45%", Core.Heading) + name, big percent, segmented bar, speed
  graph (56 high), facts (URL, Status, File size, Downloaded, Transfer rate, Time left, Resume capability), then two
  disclosure rows (bold ghost buttons 28 high with a 10 px chevron, down when open; open by default, remembered in
  ui.json):
  - "Connections (n)" (HTTP downloads with pieces only): a table with rows 26 and 1 px dividers: # · Downloaded
    ("126 MB of 140 MB") · Info: "Receiving data…" (the 4th segment value is 1), "Done" (muted), "Waiting" (muted).
    It scrolls on small screens.
  - "Options": FlatCheck "Limit this download to" + a number field 88 wide (1–10 000 000; 500 when there is no limit)
    + "KB/s": a valid number ticks the box and is sent after 0.6 s (Enter at once), unticked = 0 → op 'limit';
    "When this download is done" + a Picker 180 wide: Nothing / Open the file / Sleep / Shut down → op 'whenDone'.
    Polls don't overwrite a change on its way.
  Footer: Pause/Resume and Cancel ("Cancel this download? The partial file is deleted."). Window title "45% · name".
  - Minimize to system tray: its own button left of the title bar's close (`TrayButton`; the window has no minimize or maximize), the tray icon's three chevrons
    side by side, apart and stepping down like stairs, in its grey and bold rounded strokes, still (the download may not have started yet); dimmer while the window
    is inactive, Windows' hover and press tints; tooltip "Minimize to tray"). Windows has no API for an extra caption
    button, so it is a small owned layered window (no activation, not in Alt+Tab) one caption button wide, placed from
    DWMWA_CAPTION_BUTTON_BOUNDS and moved with the window. It shows only while the download runs (not failed,
    Download complete or removed) and the window is shown. To the tray: the window
    hides (it keeps updating) and the download gets
    its own tray icon, IDM's running arrows: three chevrons down the square, no logo (drawn as shapes, so they read at 16 px), in grey of the taskbar's shade (`SystemUsesLightTheme`:
    #3B3C3D on a light taskbar, #D8D8DC on a dark one). While it downloads the lit chevron moves down every 330 ms;
    paused or queued, all three stay dim. It takes the id of Glide's own tray
    icon (still a separate icon, with its own window): Windows keys tray places by app and id, so it sits right after
    Glide's icon instead of at the far left, where a new icon goes. Its tooltip is `Core.TrayTip`: "45% · 2.4 MB/s" (else the heading, e.g.
    "Paused · 45%"), then the name on a second line, within 127 characters (NotifyIcon's text field set by
    reflection; 63 if that fails): the only place a name is cut, its end giving way to "…". Left, right or double
    click: the window shows again where it was, in front, and the icon goes. When the download finishes, fails or is
    removed, the window shows again (Download complete, the error, "This download was removed."); closing the window
    or quitting the app removes the icon.
- Download complete (the progress window's done view, also opening by itself once for a download with `notify` that
  the app saw unfinished and then done, when `showComplete` is on; an open progress window turns into it): green
  check tile, "Download complete", the name; facts Saved to (folder), File size, Address (ellipsis); no segment bar;
  FlatCheck "Don't show this window again" (closing with it checked → POST /api/settings {showComplete: false}).
  Footer: Open with… (left; Windows' Open with dialog for the file), Open folder, Close, Open (primary).
  Removed elsewhere: "This download was removed." + Close.
- Properties (460 wide; from the row menu and the details panel): header (32 px file icon, name, status line
  Core.PropertiesStatus, e.g. "Paused · 30% of 500 MB"); Address: an editable field, labeled "Address (change it if
  the link expired)", while the download is paused, failed or in a queue and not YouTube, else read-only
  selectable text → op 'url'; Description (editable, 300 characters) → op 'describe'; facts Saved to, Referer (when
  known), Added, Last try, Queue ("Not in a queue" when none). Footer: Show in folder (left), Cancel, Save (primary;
  sends only what changed). It replaces the old Change link dialog.
- Category editor (480 wide; "New category" / "Edit category"; from Download File Info's "+" and Options ›
  Categories): label column 86: Name (read-only text for the five built-ins), File types (separated by spaces,
  without the dot; lower-cased; a leading dot or a bad type is an inline error), Folder + Browse… (empty = the
  default folder, shown as its cue; a full path or empty). Footer: Cancel, Save (primary): POST /api/settings {types,
  folders} at once, the edited category's file types taken from the others.
- Scheduler (880 wide; one at a time; from the command bar, Options › Queues and a sidebar queue's menu): a left
  column 230 (sidebar token, a hairline at its right) with the queues (List glyph, name, done-color dot while it
  runs) and at its bottom New queue (+; asks for the name) and Delete (err text; disabled for the main queue; asks
  "Delete the queue "<name>"? Its files move to the main queue."). At the right, for the selected queue, a label
  column of 196 (wider when a label needs it) and rows 32 high every 44: Name (260 wide, read-only for the main
  queue), "Download at the same time" [N] files (1–20), Start: radios "By hand only" / "At" [HH:MM] (typing a time
  picks At), "On these days" Mon–Sun chips (28 high; chosen = accent fill, onAccent text) + faint "None = every day",
  Stop: FlatCheck "At" [HH:MM], "If a file fails" Try again [N] times (0–10), "When the queue is done" Picker 200
  wide: Nothing / Sleep / Shut down. Then the queue's unfinished files in order ("Files in this queue (in order)",
  Size, Status "Waiting" or the list's status; header 32, rows 36 or as tall as the wrapped name with Size and Status on its first line,
  three rows of 36 high, it scrolls) with Move up, Move down, Remove
  from queue. Each change is validated inline (an invalid field gets the err border and is not sent) and sent at
  once, merged per queue and in order (POST /api/queues update); polls skip a focused or invalid field, and the form
  for 1 s after a send. Footer: Start now (left), Stop (for the selected queue), Close (primary).
- Options (700 wide): a left navigation 170 wide (sidebar token, items 32): General, Downloads, Sites, iPhone, Categories, Queues;
  iPhone: "Let my iPhone use <name> on this Wi-Fi" (applied at once, POST /api/remote), its state under it (Off / On, at http://ip:port / the port is taken, in err / the network is Public and how to make it Private, in err), a note on what it allows and the firewall, Pair an iPhone… (primary; PhoneDialog: the QR code 232 px on white with its quiet zone, three steps, "Waiting for your iPhone…", the link small; it closes itself 1.4 s after "<phone> is paired ✓"), Paired phones (name · paired <date>, a delete button asking first).
  the page at the right; footer Cancel / Save (primary; validates and sends only what changed, POST /api/settings;
  with an invalid field Save shows the Downloads page, where every field that can be wrong is).
  - General: Start with Windows; Offer links I copy; Show the Download complete window; "When a link is already in
    the list" Picker (320): Ask me / Download it again with a new name / Replace the existing file / Resume the
    existing download.
  - Downloads: Save to folder + Browse…; Sort into category folders; Connections per download (1–32); Downloads at
    the same time (1–20); FlatCheck "Limit all downloads to" [N] KB/s (`speedLimitOn`, `speedLimitKB`; unticked
    keeps the stored number, an empty one starts at 500). Numbers are validated inline.
  - Categories: a table (header 32, rows 40 that grow so the name, file types and folder wrap whole: icon, name with a
    "yours" tag on custom ones, file types in the text color, folder) + Add, Edit… (the category editor; a
    double-click edits too) and Remove (err text; disabled for the built-ins; asks "Remove the category <name>? Its
    downloads go to Other; no file is moved.") and the note "The five built-in categories keep their names and
    icons; you can change their file types and folders. Files that match no category go to Other." Add, Edit and
    Remove save at once, not through Save.
  - Queues: "Queues and their schedules are in the Scheduler." + Open Scheduler (saves General and Downloads, closes
    Options, then opens the Scheduler; not while a field is invalid).
- Add links (520 wide): Links (multiline, cue "One link per line. img[001-120].jpg adds 120 links."), Quality (only
  when a YouTube link is in it: Best quality, 1080p, 720p, 480p, MP3), Save as (single link only), Start later (into
  the main queue as scheduled). Batch ranges: Core.ExpandBatch; more links than typed → confirm "Add 120
  downloads?" with the first and last link. Footer: Cancel, Download (primary).
- Remove (420 wide): "Remove 3 downloads from the list?" + checkbox "Also delete the downloaded files" (only when
  some are finished) → Cancel / Remove (err fill, white text).
- A one-line prompt (420 wide, e.g. "New queue": "Name of the new queue", up to 40 characters) → Cancel / Add.
- About (400 wide): logo 64, name, version, "A download manager for Windows."

## Setup (GlideSetup.exe, `installer/`)
One window in the dialogs' look (the Dialog base: inspector body, top footer, the same buttons, light or dark like
Windows), 600 x 440, its own taskbar button and the exe's icon. Each page has a header: the logo (56 px), a Big title,
a muted line.
- Options: "Install <name>" / "A fast download manager for Windows"; Location (text field + Change…; a picked folder
  gets its own <name> folder); FlatChecks "Desktop shortcut", "Start <name> with Windows" (both on) and "Get yt-dlp,
  FFmpeg and Deno (needed for YouTube and video downloads)" (on; disabled with a green "Already installed" when they are
  found). Footer: Cancel, Install (primary).
- Progress: "Installing <name>" / the folder; three steps "Copying <name>", "Getting yt-dlp, FFmpeg and Deno", "Adding
  shortcuts", each with a ring (waiting), a turning accent arc (running, bold title), a done check or an err icon, and a
  small muted line under it (winget's last line, "Already installed", "Skipped", the error); the progress bar under
  them. No buttons while it runs; closing waits.
- A failed winget: the explanation under the bar (winget missing: get App Installer; else check the connection), a
  ghost link "App Installer in the Microsoft Store", footer Skip and Try again (primary).
- Glide running (install or uninstall): "<name> is running. Quit it from its icon next to the clock, then click Try
  again." Footer: Back (left), Quit <name>, Try again (primary).
- Done: "<name> is installed" / version; a warn line when the tools are missing; "Add <name> to your browser" (bold) and
  3 numbered steps: 1 "Open your browser's extensions page" with a button per installed browser (Brave, Chrome, Edge:
  registered in App Paths) that opens it and copies its extensions address (browsers ignore chrome:// addresses on
  their command line), with a small muted hint; 2 "Turn on Developer mode"; 3 "Click Load unpacked and choose this
  folder:" with the read-only folder and Copy. Footer: Close, Open <name> (primary; started with today's PATH).
- Uninstall: "Uninstall <name>" / the folder, what goes and that downloaded files stay, FlatCheck "Also remove the
  download list and settings" (off). Footer: Cancel, Uninstall (danger). Then the steps "Removing shortcuts", "Removing
  <name>", and "<name> is uninstalled" (where state.json stays, when kept) with Close.
