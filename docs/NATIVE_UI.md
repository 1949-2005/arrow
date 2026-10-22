# Native UI design (Arrow.exe, direction "Inspector")

The approved design for the native Windows app that replaces ui.html, confirm.html and progress.html.
Every size is in pixels at 96 DPI (scale with the system DPI). Workers implement this; they do not invent UI.

## Principles
- A modern Windows 11 utility, not an IDM clone: no menu bar, no big icon toolbar, no grid lines.
  Flat surfaces, 1 px hairlines, rounded corners, generous spacing, one primary button per area.
- The logo's colors: graphite `#3B3C3D` and white. Graphite leads in light mode, white in dark mode.
  Green, red, amber and the six category colors keep their meaning (state and file type), nothing else is colored.
- Follows Windows light/dark (`HKCU\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize\AppsUseLightTheme`)
  and switches live (`SystemEvents.UserPreferenceChanged`). Dark title bar in dark mode (DwmSetWindowAttribute 20, fallback 19).
- Everything works from the keyboard, every icon-only button has a tooltip and an AccessibleName, focus is always visible.

## Tokens
| Token | Light | Dark | Use |
|---|---|---|---|
| top | #F6F6F8 | #1E1E20 | top bar, status bar |
| sidebar | #EFEFF2 | #252528 | left column |
| list | #FFFFFF | #1B1B1D | list column |
| inspector | #F7F7F9 | #222225 | right column, dialogs' body |
| field | #FFFFFF | #252528 | text fields, paste bar |
| line | #DCDCE0 | #3A3A3E | hairlines, field borders, secondary button borders |
| text | #1D1D1F | #F5F5F7 | main text |
| muted | #6E6E73 | #A1A1A6 | labels, secondary text |
| faint | #8E8E93 | #8E8E93 | placeholders, meta lines |
| hover | #F0F0F3 | #26262A | row and ghost button hover |
| soft | #E2E2E6 | #38383C | selected sidebar item, pressed ghost button |
| sel / onSel | #3B3C3D / #FFFFFF | #505154 / #FFFFFF | selected list rows |
| accent / onAccent | #3B3C3D / #FFFFFF | #ECECEE / #1D1D1F | primary buttons, focus ring, progress fill, graph line |
| bar | #E2E2E7 | #3A3A3E | progress track |
| done | #1E8E3E | #30D158 | finished, complete bar |
| err | #D70015 | #FF453A | failed, remove |
| warn | #B45309 | #FFB340 | warnings, countdown |
| video / music / docs / programs / zip / other | #AF52DE / #E0245E / #0A6CDB / #5856D6 / #D97706 / #8E8E93 | #BF5AF2 / #FF375F / #4DA3FF / #7D7AFF / #FFB340 / #98989D | file-type glyphs |
| tint | category color at 14% alpha | category color at 22% alpha | file-type tiles, over the surface |

Fonts: text "Segoe UI Semibold" 10.5pt (14 px), Small 9pt (12 px). Emphasis (Bold, SmallBold) is "Segoe UI" in the Bold
style at the same sizes, so it reads stronger than the semibold text; big numbers "Segoe UI" Bold 18pt (24 px). Not "Segoe UI Variable": it has no Arabic, so GDI would draw Arabic names in Tahoma.
Text is drawn by GDI (TextRenderer); text with emoji (U+1F000–1FAFF, U+2600–27BF, U+FE0F, the keycap U+20E3) is drawn
and measured by DirectWrite through a Direct2D DC render target with color fonts on (`ColorText.cs`), at GDI's line height
and baseline, so emoji are in color as in the browser. Text fields (TextBox) stay GDI: emoji there are monochrome or boxes.
Radii: 6 (sidebar items, small buttons), 8 (fields, buttons), 12 (tiles). Spacing steps 4, 8, 12, 16.
Focus ring: 2 px accent, drawn 1 px inside the control, only for keyboard focus.

Icons: "Segoe Fluent Icons" (fallback "Segoe MDL2 Assets"), drawn in the token color, 16 px in lists and buttons, 24 px in tiles:
Download E896, Link E71B, Add E710, List E8FD, Clock E121, Options E9E9, More E712, Pause E769, Play E768, Stop E71A,
Delete E74D, Folder E8B7, Open folder E838, Open file E8E5, Copy E8C8, Search E721, Check circle E930, Warning E7BA,
Error E783, Clipboard E77F, Close E8BB. Categories: All E8B7, Active E896, Finished E930, Failed E783, Scheduled E121,
Video E714, Music EC4F, Documents E8A5, Programs ECAA, Compressed F012, Other E7C3.

File types show the icon Windows shows for them, as File Explorer and IDM do (`FileIcons.cs`): SHGetFileInfo on the
extension alone (SHGFI_USEFILEATTRIBUTES, never the disk), the 16 px image up to 16 px, else the 32 px one scaled
down with high-quality bicubic, cached per extension and size. The extension is the file's own, else its category's
typical one (.mp4, .mp3, .pdf, .exe, .zip; none = the generic file). Sizes: 24 px in list rows, 32 px (native, crisp)
in the Download File Info and progress headers. The sidebar's Types at 28 px: the user's SVG icons
`assets/types/<category>.svg` for Video, Music, Documents, Programs and Compressed, the same on every PC (a typical
file's icon comes from whatever app opens that type: a .pdf showed the Claude app's logo). `Core.ParseSvg` reads any
path data (arcs and quadratics become cubics); a transform, another shape or fill="none" shows the glyph instead. The
path is cached per category and filled with the file's fill rule (nonzero unless evenodd), anti-aliased, in `Theme.Text` at paint
time so it follows the theme. Other has none: a missing or unreadable file shows the category glyph in its color.

## Main window
Default 1180 x 700, minimum 900 x 560; remembers bounds, maximized state and the selected section
(`%LOCALAPPDATA%\<name>\ui.json`). Closing it hides it (the app stays in the tray); the first time, a tray balloon says
"<name> keeps running in the tray. Right-click its icon to quit."

```
+------------------------------------------------------------------------------------------+
| [logo]  [link  Paste a link or drop files            ][ Download ]  (pause)(+)(clock)(⚙)(⋯) |  top bar 56
+------------------------------------------------------------------------------------------+
| strip: clipboard offer / countdown / offline (only when needed)                     36   |
+---------------+------------------------------------------------+-------------------------+
| Library       | [search  Search downloads             ]        | [tile] Name of the file |
|  All       11 | ──────────────────────────────────────────     |        youtube.com  ⧉   |
|  Active     2 | [v] Last War Winter Storm.mp4      45% · 12 MB/s| 45%  974 MB of 2.11 GB  |
|  Finished   8 |     ━━━━━━━━━━━━━━━━━━━━━━━━━━━░░░░░░░░░        | ▮▮▮▮░░▮▮░░▮░░░ (segments)|
|  Failed     1 | [f] speed.png                       Paused · 30%| speed graph (60 s)      |
|  Scheduled  0 |     ━━━━━━━━━░░░░░░░░░░░░░░░░░░░░░░░░░░░        | Status       Downloading|
| Types         | [v] (1) tangerine - Search _ X.mp4    20.4 MB  | Time left    1 min 12 s |
|  Video      6 | [p] idman643build15.exe               12.0 MB  | Connections  8          |
|  Music      0 | [f] edited                             Failed  | Resume       Yes        |
|  Programs   1 |                                                | Added        Today 21:56|
|  Documents  0 |                                                | Saved to     Downloads\…|
|  Compressed 2 |                                                | [ Pause ][ Folder ]     |
|  Other      2 |                                                | [ Copy link ][ Remove ] |
+---------------+------------------------------------------------+-------------------------+
| 2 active · 12.4 MB/s · limit 500 KB/s                         When done: [Do nothing v] |  status bar 30
+------------------------------------------------------------------------------------------+
```
Columns: sidebar 200 (fixed), list fills, inspector 320 (fixed). Hairlines between them.

### Top bar (top, height 56, padding 12)
- Logo 24 px (the brand icon), then the paste bar: height 34, radius 8, field + line border, link glyph in faint at
  the left, a borderless TextBox with the native cue banner (EM_SETCUEBANNER) "Paste a link or drop files".
  Focused: border becomes accent.
- Primary button "Download" (Download glyph + text), height 34, accent / onAccent.
  Enter in the paste bar = Download. One link: POST /api/add {url, confirm: true}; the app then shows its
  Download File Info dialog for that pending download (see Dialogs). Several links or a `[from-to]` range: open the
  Add links dialog prefilled. Invalid text: the paste bar's border turns err and a tooltip says "Paste an http or https link".
- Ghost icon buttons, 34 x 34, radius 8, muted glyphs, hover = hover token, tooltips:
  Pause all (enabled only while something runs), Add several links (List glyph, opens Add links), Scheduler (Clock),
  Options, More (⋯ menu: Delete completed…, About <name>, separator, Quit <name>).
- Ctrl+V anywhere outside a text field puts the clipboard into the paste bar and focuses it.

### Strip (below the top bar, height 36, only when needed, one at a time by priority: offline > countdown > clipboard)
Full-width, inspector background, bottom hairline, glyph + text + text buttons on the right:
- Offline (err glyph): "<name>'s engine isn't running." [Start it]
- Countdown (warn glyph): "All downloads finished. Shutting down in 42 s" / "Going to sleep in 42 s" [Cancel]
  (POST /api/settings {whenDone: ''}). A tray balloon announces it once.
- Clipboard offer (Clipboard glyph, only when settings.clipboard is on): "From clipboard: <url>" [Download] [×].
  Listen with AddClipboardFormatListener; offer a text only once, only when Core.ClipboardLink accepts it and no
  download has that url.

### Sidebar (width 200, sidebar token, padding 8)
Two groups with a 12 px muted heading ("Library", "Types"), items 30 high, radius 6, a 28 px icon slot + label + count
right (muted). Selected: soft background, bold label. Hover: hover token. Keyboard: Up/Down move, the list follows.
Library: All, Active (downloading, queued, paused), Finished (done), Failed (error), Scheduled — glyphs at 18 px, muted
(text when selected).
Types (Core.Category): Video, Music, Programs, Documents, Compressed, Other — the user's SVG icons in the text color at
28 px; Other shows its glyph.

### List (list token)
- Search field on top (height 32, margin 12, field + line, Search glyph, cue "Search downloads", Ctrl+F focuses it,
  Esc clears): filters by name or url, case-insensitive, together with the sidebar section.
- Custom-drawn rows, height 48, padding 12: the file's Windows icon at 24 px (as is, also when selected), 12 px gap,
  name (ellipsis), right-aligned trailing
  text; unfinished rows add a 3 px progress line under the name (radius 2, bar token track, accent fill; paused fill =
  faint; error = err). Trailing text: downloading "45% · 12.4 MB/s" (video phases: "Merging", "Converting to MP3"),
  queued "Queued", paused "Paused · 30%", error "Failed" in err, scheduled "Scheduled", done the size in muted.
- Order: unfinished first (downloading, queued, paused, error, scheduled), newest first within each; then finished,
  newest first. Rows keep their place while you look at them: only re-sort when an item changes group.
- Hover: hover token. Selected: sel / onSel (glyphs and texts turn onSel). Multi-select with Ctrl and Shift, Ctrl+A.
- Keys: Up/Down/Home/End/PageUp/PageDown, Enter (open a finished file, else the progress window), Space (pause or
  resume), Delete (Remove dialog), Ctrl+C (copy the links).
- Double-click: finished = open the file; else the progress window. Right-click menu: Open, Show in folder,
  Copy link, separator, Resume / Pause, Change link…, Progress window, separator, Remove….
- Drag links over the window: a full-window overlay (inspector token at 92% alpha, accent dashed border radius 12,
  Download glyph 32, "Drop to download"). Drop: one link = the paste-bar flow, several = Add links dialog.
- Empty list: centered logo at 64 px (40% alpha), "No downloads yet" (bold), "Paste a link above, drop links here,
  or let the browser extension catch them." (muted), and a secondary button "Add links…".
  Empty section or no search match: "Nothing here" + muted hint.
- Accessibility: the control exposes one AccessibleObject per row (role ListItem, name = file name + trailing text,
  selected state); the list has role List and the name "Downloads".

### Inspector (width 320, inspector token, padding 16, scrolls if short)
Shows the selection:
- One download:
  - Header: 48 px tile (radius 12, category tint, 24 px glyph in the category color), name (bold, max 2 lines),
    host of the url (muted) with a ghost Copy button (Copy glyph, tooltip "Copy link").
  - Progress: big percent ("45%", or "Done" when finished, "Failed" in err) + "974 MB of 2.11 GB" (muted);
    under it an 8 px segmented bar (Core.Pieces: one piece per connection for http, one fill otherwise),
    accent pieces on the bar track; finished = full done color; error = err.
  - Error box when failed: err tint (12%/20% alpha), err glyph, the full error text (wraps), selectable.
  - Speed graph while downloading: 56 high, the last 60 s of this download's speed (one sample per poll),
    1.5 px accent line with a 12% accent fill below, a hairline baseline, current speed top right.
  - Facts (label muted left, value right, rows 22): Status (Core.StatusText), Speed, Time left, Connections,
    Resume (Yes/No/—), Added (Core.DateLabel), Format (YouTube key text) or Type, Saved to (folder, ellipsis,
    tooltip = full path, click = Show in folder).
  - Actions: a 2-column grid of buttons, height 32, radius 8. The first is primary and depends on the state:
    Pause (downloading, queued), Resume (paused, scheduled), Retry (error), Open (done).
    Then: Show in folder, Copy link, Change link… (paused or error, not YouTube), Progress window (unfinished),
    Remove (err text, secondary style).
- Several selected: "3 downloads" (big), total size (muted), up to 5 names, actions Resume, Pause, Remove.
- Nothing selected: "Overview": total speed (big), the 60 s total speed graph, counts (Active, Finished, Failed),
  free space on the download folder's drive ("120 GB free on C:").

### Status bar (height 30, top token, top hairline, 12 px muted)
Left: "2 active · 12.4 MB/s" (+ " · limit 500 KB/s", + " · scheduled start 02:00"). Right: "When done:" and a
small combo (Do nothing, Sleep, Shut down) → POST /api/settings {whenDone}.

### Tray
NotifyIcon with the brand icon, tooltip = the app name + " · 2 active" when something runs. Left click: show and
activate the main window. Menu: Open <name> (bold, default), Pause all, separator, Quit <name>.
Balloon when a download finishes ("Download complete", file name; click = show it selected in the main window).

## Dialogs
All dialogs: inspector token body, top token footer with a top hairline, padding 20, radius-8 fields (field + line,
accent border when focused), primary button accent/onAccent, secondary buttons transparent with a line border,
Enter = primary, Esc = Cancel, dark title bar in dark mode, the brand icon, FormBorderStyle FixedDialog, no
min/max boxes, centered on the main window (or the screen when it is hidden).
- Download File Info (one per pending download, appears in front even when Arrow is in the tray; same rules as
  confirm.html): header (the Save As name's Windows icon at 32 px, centered in the 48 px tile's place, "Download File
  Info", Core.Subtitle), URL (read-only, selectable), amber note when
  the probe failed, Format (YouTube only; while probe.listing it shows "Getting formats…" with a small spinner and only
  Cancel works; items are Core.FormatText grouped MP4, WEBM, Audio; hidden with its gap closed when the download's
  own Download.Format is set and the probe is listing or offers that key: the extension's format menu already
  picked it), Category, Save As + Browse… (native SaveFileDialog, no overwrite prompt; tooltip "Missing folders are
  created."), FlatCheck "Remember this folder for <Category>" ("for other files" for Other; follows Category,
  unchecked on open; checked → Start/Later send remember: <Category>), inline error, Size (bold). Tab order:
  Save As, Browse…, the checkbox, then the footer. Save As's folder is Core.FolderFor (the remembered folder first).
  Footer: Download Later (left), Cancel, Start Download (primary). Start → POST /api/confirm start, then this dialog
  becomes the progress window for that download.
- Progress window (IDM's, restyled like the inspector): the file's Windows icon at 32 px (in the tile's place) + title
  ("Downloading · 45%", Core.Heading) + name,
  big percent, segmented bar, speed graph, facts (URL, Status, File size, Downloaded, Transfer rate, Time left,
  Resume capability), footer Pause/Resume and Cancel ("Cancel this download? The partial file is deleted.").
  Done: green check tile, "Download complete", Saved to, File size, footer Open folder, Close, Open (primary).
  Removed elsewhere: "This download was removed." + Close. Window title "45% · name".
- Add links: Links (multiline, cue "One link per line. img[001-120].jpg adds 120 links."), Quality (only when a
  YouTube link is in it: Best quality, 1080p, 720p, 480p, MP3), Save as (single link only), Start later (scheduled).
  Batch ranges: Core.ExpandBatch; more links than typed → confirm "Add 120 downloads?" with the first and last link.
- Options: sections "Downloads" (Save to folder + Browse…, Connections per download 1–32, Downloads at the same
  time 1–20, Speed limit KB/s 0 = unlimited), "Schedule" (Start scheduled downloads at HH:MM, empty = off),
  "Behavior" (Sort into folders, Offer links I copy, Start with Windows). The Scheduler button opens it focused on
  the schedule field. Numbers and times are validated inline.
- Remove: "Remove 3 downloads from the list?" + checkbox "Also delete the downloaded files" (only when some are
  finished) → Cancel / Remove (err fill, white text).
- Change link: "New link for the same file", text field, Cancel / Save.
- About: logo 64, name, version, "A download manager for Windows."
