# Arrow

Arrow is a download manager that runs on your PC. It makes downloads faster by using several connections for each file, like IDM.

## Requirements

- Windows 10 or 11.
- Node.js 20.3 or newer, to install Arrow. To check, open a command window and type `node --version`.
- For the browser extension: Brave, Google Chrome or Microsoft Edge.

## Install

- Double-click `install.cmd`. Wait about 15 seconds.
- It makes two programs in this folder, puts Arrow in the Start menu and on the desktop, and starts Arrow:
  - `Arrow.exe` is the app you see: a normal Windows app with its own windows.
  - `Arrow Engine.exe` does the downloads in the background (about 90 MB: it carries its own copy of Node.js). Arrow starts it for you.
- Arrow stays in this folder. Do not move the folder after you install. If you must move it, uninstall first, move it, then install again.
- If Arrow is running, `install.cmd` asks you to quit it first.

## Start

- Click Arrow in the Start menu, or double-click the Arrow icon on the desktop.
- The Arrow window opens, and the Arrow icon appears next to the clock.
- On Windows 11, a new icon may be hidden under the **^** arrow next to the clock. Drag it to the taskbar to always see it.
- Start Arrow again while it runs: it only shows its window again.

## Close and quit

- Close the window: Arrow keeps running next to the clock, like IDM. Downloads go on.
- Click the icon next to the clock to open the window again.
- Right-click the icon for the menu:
  - **Open Arrow** opens the window.
  - **Pause all** pauses the downloads that run or wait. Scheduled downloads keep their start time.
  - **Quit** stops Arrow. Unfinished downloads continue the next time you start it.

## Uninstall

- Quit Arrow first (right-click the icon next to the clock, then Quit).
- Double-click `uninstall.cmd`.
- It removes the Start menu and desktop shortcuts, the Start with Windows shortcut, `Arrow.exe` and `Arrow Engine.exe`.
- Your downloaded files stay. `state.json` (the download list and settings) stays too. Delete the folder if you want it all gone.

## Name and icon

- `brand.json` holds the app's name (Arrow) and its icon file (`assets/logo.ico`).
- They are used for both programs, the window titles, the window and taskbar icon, the icon next to the clock, the shortcuts and the file properties.
- The browser extension has the name in its own files (`manifest.json`, `popup.html`, `popup.js`, `background.js`). A test checks that they match `brand.json`. Its icon is in `extension/icons/`.
- To change the name or icon: quit the app, edit `brand.json` (and the extension's files), then double-click `install.cmd` again. It removes the shortcuts with the old name.
- The name may use letters, digits, spaces and `. _ ( ) + ! -` (40 at most).

## Updates

- After you update the engine's files (the `.js` files), quit Arrow and start it again. No new install is needed.
- Run `install.cmd` again after you update the app's files (the `app` folder), change `brand.json` or the icon, or update Node.js.

## Install the browser extension

- Brave: open `brave://extensions`, turn on "Developer mode", click "Load unpacked" and choose the `extension` folder.
- Chrome: the same at `chrome://extensions`. Edge: the same at `edge://extensions`.
- Then pin the Arrow icon to the toolbar.

## How to use

- Paste a link in the bar at the top of the window and click **Download** (or press Enter). Ctrl+V anywhere in the window pastes there too. You can also drop links on the window.
- The **Download File Info** window opens (see below). Pick where the file goes, then click **Start Download**.
- To add several links at once, click **Add several links**. "Save as" is used only for a single link. If you tick "Start later (scheduled)", the downloads are added as scheduled.
- Batch links: for numbered files, put the numbers in square brackets. `https://site.com/img[001-120].jpg` adds img001.jpg to img120.jpg (the leading zeros are kept). Arrow tells you how many links that makes before it adds them. At most 1000 at a time.
- The left side shows the downloads by status (All, Active, Finished, Failed, Scheduled) and by file type (Video, Music, Documents, Programs, Compressed, Other). The search field filters by name or link.
- Select like in a file list: click, Ctrl+click, Shift+click, Ctrl+A. Enter or a double-click opens a finished file, or the progress window of an unfinished one. Space pauses or resumes. Delete removes. Right-click for more.
- The right side shows the selected download: progress, speed, time left, connections, and buttons for Pause or Resume, Open, Show in folder, Copy link, Change link (for a paused or failed download whose link expired) and Remove. When you remove finished downloads, Arrow asks if it should also delete the files.
- **Options** holds the settings. **Scheduler** opens them at "Start scheduled downloads at".
- Clipboard monitoring (turn it on in Options): copy a link to a file, for example a .zip or .mp4. Arrow offers "From clipboard" at the top of its window and adds the file only if you click Download.
- When all downloads finish: at the bottom right, choose Do nothing, Sleep or Shut down. When the last download ends, a 60-second countdown appears at the top of the window. Click Cancel to stop it. The choice is used once: after the countdown, or after Cancel, it goes back to Do nothing. It is not kept when the app restarts.
- The **Download File Info** window is Arrow's own window, like IDM's. It opens for a pasted link and for every download from the browser:
  - It shows the link, the size (when the server tells it) and where the file goes.
  - For YouTube, choose the **Format** (MP4, WEBM, M4A or MP3). Each one shows its real size. The file name is the video's title.
  - **Category** changes the folder. Type another path in **Save As**, or click **Browse…** to pick it in Windows. Missing folders are created. If a file with that name exists, Arrow adds a number.
  - **Start Download** (or Enter) starts it, and the window shows its progress. **Download Later** adds it as paused: start it later with Resume. **Cancel** (or Esc) drops it.
  - If the window says "Couldn't read the file details", you can still try to start it.
  - The download shows in the list only after you click Start Download or Download Later. A window left open for 30 minutes forgets its download.
- The progress window shows the status, size, speed, time left and one bar piece per connection. Pause, Resume or Cancel the download there. When it is done, click **Open**, **Open folder** or **Close**.
- With the extension:
  - While the app runs, browser downloads go to Arrow automatically. To stop this, open the extension popup and switch off "Take over browser downloads".
  - Right-click a link or a video and choose "Download with Arrow".
  - Point at a video on a web page. The Arrow panel appears at its top right, like IDM's. Click **Download this video**. If it says "No video found yet", let the video play for a few seconds, then click again.
    - **?** explains the panel. **×** hides it on that video until you reload the page.
    - Drag the panel to move it. Double-click its edge (the dots on the left) to put it back.
  - Each of these opens Arrow's **Download File Info** window. It comes from Arrow, not from the browser, so Arrow must be running. If it is not, the browser keeps its own download, and the extension's other buttons say that Arrow is not running.
  - Play a video, then click the extension icon. It shows the media it found (the number on the icon tells how many). Click Download. The Download File Info window opens for these too.
  - **Open Arrow** in the extension popup shows Arrow's window.
  - To hide that button, open the extension popup and switch off "Show download button on videos".

## YouTube videos

- Arrow needs two free tools for this: yt-dlp and ffmpeg. Install them once, in a command window:
  - `winget install yt-dlp.yt-dlp`
  - `winget install yt-dlp.FFmpeg`
  - If one is already installed, winget tells you so.
- Ways to start:
  - Point at the YouTube video and click **Download this video**. A list of formats opens, with the real size of each:
    - **VIDEO · MP4** and **VIDEO · WEBM**: one line per resolution. The size counts the video and the sound.
    - **AUDIO**: M4A and MP3.
    - Click one. The Download File Info window opens with that format.
    - Getting the list takes yt-dlp about 3 seconds (about 8 after YouTube updates its player). Arrow starts it as soon as you point at the video, so it is often ready when you click. It keeps the list for 15 minutes.
  - In Brave, open a YouTube video page. Click the Arrow icon and click **Download this video**.
  - Right-click a YouTube link and choose "Download with Arrow".
  - Paste the link in the bar at the top of Arrow's window. The Download File Info window lists the formats.
- The Download File Info window has one **Format** list, with real sizes. While Arrow reads the list, it says "Getting formats…". Only Cancel works until then.
- Sizes are real: Arrow asks YouTube. A format without a known size is not shown. The final file can be a few KB bigger (merging, MP3 header).
- MP3 is made at 192 kbps. So Arrow knows its size before the download.
- The file name is the video's title: `<title>.mp4`, `.webm`, `.m4a` or `.mp3`.
- Videos go to the `Video` folder. M4A and MP3 files go to the `Music` folder.
- If Arrow can't read the format list, the window offers only "MP4 · best quality" and "MP3 · audio".
- Arrow prefers H.264 video. It plays everywhere. Very high resolutions (like 4K) may use VP9 or AV1. VLC plays them.
- Pause and Resume work. One progress bar covers video and sound. "Merging" is the last step.
- If YouTube downloads start to fail, run `winget upgrade yt-dlp.yt-dlp`.
- Limits:
  - One video per link. No playlists or channels.
  - No live streams.
  - No private, members-only or age-restricted videos. Arrow never sends your YouTube login.
  - The speed limit applies to each video. A new limit takes effect when a video starts.
- YouTube's terms only allow saving videos through YouTube's own app. Download your own videos, or videos you are allowed to save.

## Features

- Several connections per file, with dynamic splitting like IDM.
- Pause and resume, also after you close the app or restart the PC (when the server allows resuming; otherwise the download starts again from zero).
- Automatic retries.
- Works with servers that limit connections.
- A connection that stops sending data is cut after 30 seconds.
- Queue: you choose how many downloads run at the same time.
- Scheduler: scheduled downloads start every day at the time you choose. The app must be running.
- Global speed limit.
- Auto-sorting into Video, Music, Compressed, Documents and Programs folders.
- If a file name already exists, the new file gets (1), (2) and so on.
- HLS video streams (.m3u8), also AES-128 encrypted ones. They are saved as .ts (or .mp4).
  - Some sites (like X) send the sound as a separate track. Arrow downloads it too and joins it to the video with ffmpeg, into one .mp4. This needs ffmpeg: `winget install yt-dlp.FFmpeg`.
- "Change link" for links that have expired.
- Open and Show in folder buttons for finished files.
- A desktop notification when a download finishes.
- YouTube videos (MP4, WEBM) and audio (M4A, MP3), with real sizes (needs yt-dlp and ffmpeg).
- Browser cookies are passed along, so downloads that need a login work.
- Batch links with number ranges, like `img[001-120].jpg`.
- Clipboard monitoring (off by default).
- Start with Windows (off by default): Arrow starts next to the clock when you sign in to Windows, without opening its window.
- Sleep or shut down the PC when all downloads finish, after a countdown you can cancel.
- A real Windows app: `Arrow.exe` with its own windows and icon, Start menu and desktop shortcuts, and an icon next to the clock with Open, Pause all and Quit. It follows Windows' light or dark mode.

## Settings

| Setting | Default |
|---|---|
| Save to folder | Your Downloads folder |
| Connections per download (1–32) | 8 |
| Downloads at the same time (1–20) | 3 |
| Speed limit in KB/s (0 = unlimited) | 0 |
| Start scheduled downloads at (HH:MM) | empty (off) |
| Sort into folders by type | on |
| Offer download links I copy (clipboard monitoring) | off |
| Start with Windows (in the tray) | off |

## Limits

- No sites other than YouTube that send audio and video separately (DASH).
- No DRM-protected video.
- No torrents and no FTP.
- No proxy settings.
- `Arrow.exe` and `Arrow Engine.exe` are not signed. If Windows Smart App Control is on, it may block Arrow.
- If Arrow stops in a wrong way (for example from Task Manager), its icon can stay next to the clock until you move the mouse over it.
- Clipboard monitoring only offers links that end in a file type from the sorting folders (Video, Music, Compressed, Documents, Programs).
- Sleep and shut down only happen while the app runs. Shut down does not force apps to close: an app with unsaved work can stop it.
- HLS video without a separate sound track is saved as .ts (or .mp4). Most players (VLC, MPC-HC) can play it. ffmpeg can convert it.
- The extension logic is tested with fake browser parts, but it has not yet been tried in a real browser.

## Privacy and safety

- Arrow runs only on your computer (127.0.0.1). Other websites cannot control it.
- The browser extension can only add downloads and show Arrow's window. It cannot change settings, turn on Start with Windows, or open files.
- Start with Windows puts an "Arrow" shortcut in your Startup folder. Turning the setting off deletes it.
- Clipboard monitoring reads the clipboard when it changes, only while the setting is on. If the clipboard holds anything other than one download link, Arrow ignores it. Nothing from the clipboard is saved.
- Browser cookies are only sent to the site they belong to.
- Your YouTube login cookies are never sent to Arrow or yt-dlp.
- Finished files are marked as "downloaded from the internet", so Windows SmartScreen still checks programs before they run.
- The download list and settings are saved in `state.json` next to the app. This file holds the links and login cookies of unfinished downloads. Do not share it.

## For developers

- `npm test` runs all tests. No internet is needed.
- `npm start` or `node desktop.js` runs only the engine (no window, no icon next to the clock), at http://127.0.0.1:9614.
- The app's sources are in `app/`. Started without `Arrow Engine.exe` next to it, `Arrow.exe` runs `node desktop.js`.
- The design of the app's windows is in `docs/NATIVE_UI.md`.
- See `CLAUDE.md`, `docs/SPEC.md` and `filemap.md`.
