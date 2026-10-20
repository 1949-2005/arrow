# Arrow

Arrow is a download manager that runs on your PC. It makes downloads faster by using several connections for each file, like IDM.

## Requirements

- Windows 10 or 11.
- Node.js 20.3 or newer, to install Arrow. To check, open a command window and type `node --version`.
- Brave, Google Chrome or Microsoft Edge. Arrow opens its window in the first one it finds (else in your default browser), and the browser extension needs one of them.

## Install

- Double-click `install.cmd`. Wait about 10 seconds.
- It makes `Arrow.exe` in this folder (about 90 MB: it carries its own copy of Node.js), puts Arrow in the Start menu and on the desktop, and starts Arrow.
- Arrow stays in this folder. Do not move the folder after you install. If you must move it, uninstall first, move it, then install again.
- If Arrow is running, `install.cmd` asks you to quit it first.

## Start

- Click Arrow in the Start menu, or double-click the Arrow icon on the desktop.
- The Arrow window opens, and the Arrow icon appears next to the clock.
- On Windows 11, a new icon may be hidden under the **^** arrow next to the clock. Drag it to the taskbar to always see it.
- Start Arrow again while it runs: it only opens its window again.

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
- It removes the Start menu and desktop shortcuts, the Start with Windows shortcut, and `Arrow.exe`.
- Your downloaded files stay. `state.json` (the download list and settings) stays too. Delete the folder if you want it all gone.

## Name and icon

- `brand.json` holds the app's name (Arrow) and its icon file (`assets/logo.ico`).
- They are used for the exe, the window title, the window and taskbar icon, the icon next to the clock, the shortcuts and the file properties.
- The browser extension has the name in its own files (`manifest.json`, `popup.html`, `popup.js`, `background.js`). A test checks that they match `brand.json`.
- To change the name or icon: quit the app, edit `brand.json` (and the extension's files), then double-click `install.cmd` again. It removes the shortcuts with the old name.
- The name may use letters, digits, spaces and `. _ ( ) + ! -` (40 at most).

## Updates

- After you update Arrow's files, quit Arrow and start it again. No new install is needed.
- Run `install.cmd` again only after you change `brand.json` or the icon, or after you update Node.js.

## Install the browser extension

- Brave: open `brave://extensions`, turn on "Developer mode", click "Load unpacked" and choose the `extension` folder.
- Chrome: the same at `chrome://extensions`. Edge: the same at `edge://extensions`.
- Then pin the Arrow icon to the toolbar.

## How to use

- Click **Add URL**. Paste one or more links into "Links" (or drop links on the window). Click Download.
- "Save as" is optional and used only for a single link. If you tick "Start later (scheduled)", the download is added as scheduled.
- The toolbar at the top works on the downloads you select in the list:
  - **Resume** and **Stop** start or pause them. **Stop all** pauses everything that runs or waits.
  - **Delete** (or the Delete key) removes them from the list. For finished ones, Arrow asks if it should also delete the files.
  - **Delete completed** removes all finished downloads from the list. The files stay on disk.
  - **Options** opens the settings. **Scheduler** opens them at "Start scheduled downloads at".
- Select like in a file list: click, Ctrl+click to add or remove one, Shift+click for a range. With the keyboard: Up and Down move, Shift+Up/Down extend, Ctrl+A selects all, Space adds or removes one. Enter or a double-click opens a finished file. A double-click on an unfinished download opens its progress window.
- The left side sorts the list by category (Compressed, Documents, Music, Programs, Video, Other, by file type) and by status (Unfinished, Finished, Scheduled). Click one to show only those downloads. Arrow remembers your choice. In a narrow window the left side is hidden and the list shows everything.
- The pane under the list shows the selected download: its link and folder, how much is downloaded, speed, time left, connections, whether it can resume, and one bar piece per connection. It has Open, Show in folder and Change link (for a paused or failed download whose link expired).
- Batch links: for numbered files, put the numbers in square brackets. `https://site.com/img[001-120].jpg` adds img001.jpg to img120.jpg (the leading zeros are kept). Arrow tells you how many links that makes before it adds them. At most 1000 at a time.
- Clipboard monitoring (turn it on in Settings): copy a link to a file, for example a .zip or .mp4, then switch to the Arrow window. Arrow asks "Download?" and adds the file only if you click Download. The first time, the browser asks if Arrow may see the clipboard. Click Allow.
- When all downloads finish: at the bottom right, choose Do nothing, Sleep or Shut down. When the last download ends, a 60-second countdown appears at the top of the window. Click Cancel to stop it. The choice is used once: after the countdown, or after Cancel, it goes back to Do nothing. It is not kept when the app restarts.
- With the extension:
  - While the app runs, browser downloads go to Arrow automatically. To stop this, open the extension popup and switch off "Take over browser downloads".
  - Right-click a link or a video and choose "Download with Arrow".
  - Point at a video on a web page. A "Download with Arrow" button appears at its top right. Click it. If it says "No video found yet", let the video play for a few seconds, then click again.
  - Each of these opens the **Download File Info** window, like IDM:
    - It shows the link, the size (when the server tells it) and where the file goes.
    - For YouTube, choose the **Format** (MP4, WEBM, M4A or MP3). Each one shows its real size. The file name is the video's title.
    - **Category** changes the folder. Type another path in **Save As**, or click **Browse…** to pick it in Windows. Missing folders are created. If a file with that name exists, Arrow adds a number.
    - **Start Download** (or Enter) starts it, and the window shows its progress. **Download Later** adds it as paused: start it later with Resume. **Cancel** (or Esc) drops it.
    - If the window says "Couldn't read the file details", you can still try to start it.
    - The download shows in the list only after you click Start Download or Download Later. A window left open for 30 minutes forgets its download.
  - The progress window shows the status, size, speed, time left and one bar piece per connection. Pause, Resume or Cancel the download there. When it is done, click **Open**, **Open folder** or **Close**.
  - Play a video, then click the extension icon. It shows the media it found (the number on the icon tells how many). Click Download. The Download File Info window opens for these too.
  - To hide that button, open the extension popup and switch off "Show download button on videos".

## YouTube videos

- Arrow needs two free tools for this: yt-dlp and ffmpeg. Install them once, in a command window:
  - `winget install yt-dlp.yt-dlp`
  - `winget install yt-dlp.FFmpeg`
  - If one is already installed, winget tells you so.
- Ways to start:
  - Point at the YouTube video and click **Download with Arrow**. A list of formats opens, with the real size of each:
    - **VIDEO · MP4** and **VIDEO · WEBM**: one line per resolution. The size counts the video and the sound.
    - **AUDIO**: M4A and MP3.
    - Click one. The Download File Info window opens with that format.
  - In Brave, open a YouTube video page. Click the Arrow icon and click **Download this video**.
  - Right-click a YouTube link and choose "Download with Arrow".
  - Paste the link in Arrow's Add URL window. A quality picker appears under the links.
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
- "Change link" for links that have expired.
- Open and Show in folder buttons for finished files.
- A desktop notification when a download finishes.
- YouTube videos (MP4, WEBM) and audio (M4A, MP3), with real sizes (needs yt-dlp and ffmpeg).
- Browser cookies are passed along, so downloads that need a login work.
- Batch links with number ranges, like `img[001-120].jpg`.
- Clipboard monitoring (off by default).
- Start with Windows (off by default): Arrow starts next to the clock when you sign in to Windows, without opening its window.
- Sleep or shut down the PC when all downloads finish, after a countdown you can cancel.
- A real Windows app: `Arrow.exe` with its own icon, no console window, Start menu and desktop shortcuts, and an icon next to the clock with Open, Pause all and Quit.

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
- `Arrow.exe` is not signed. If Windows Smart App Control is on, it may block Arrow.
- If Arrow stops in a wrong way (for example from Task Manager), its icon can stay next to the clock until you move the mouse over it.
- Clipboard monitoring only looks at the clipboard when you switch to the Arrow window, not while you work in other windows. It only offers links that end in a file type from the sorting folders (Video, Music, Compressed, Documents, Programs).
- Sleep and shut down only happen while the app runs. Shut down does not force apps to close: an app with unsaved work can stop it.
- HLS video is saved as .ts. Most players (VLC, MPC-HC) can play it. ffmpeg can convert it.
- The extension logic is tested with fake browser parts, but it has not yet been tried in a real browser.

## Privacy and safety

- Arrow runs only on your computer (127.0.0.1). Other websites cannot control it.
- The browser extension can only add downloads. It cannot change settings, turn on Start with Windows, or open files.
- Start with Windows puts an "Arrow" shortcut in your Startup folder. Turning the setting off deletes it.
- Clipboard monitoring reads the clipboard only when you switch to the Arrow window. If the clipboard holds anything other than one download link, Arrow ignores it. Nothing from the clipboard is saved.
- Browser cookies are only sent to the site they belong to.
- Your YouTube login cookies are never sent to Arrow or yt-dlp.
- Finished files are marked as "downloaded from the internet", so Windows SmartScreen still checks programs before they run.
- The download list and settings are saved in `state.json` next to the app. This file holds the links and login cookies of unfinished downloads. Do not share it.

## For developers

- `npm test` runs all tests. No internet is needed.
- `npm start` runs only the server (no window, no icon next to the clock). Then open http://127.0.0.1:9614.
- `node desktop.js` runs the whole app without `Arrow.exe`.
- See `CLAUDE.md`, `docs/SPEC.md` and `filemap.md`.
