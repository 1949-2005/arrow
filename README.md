# FastDL

FastDL is a download manager that runs on your PC. It makes downloads faster by using several connections for each file, like IDM.

## Requirements

- Windows 10 or 11.
- Node.js 20.3 or newer, to install FastDL. To check, open a command window and type `node --version`.
- Brave, Google Chrome or Microsoft Edge. FastDL opens its window in the first one it finds (else in your default browser), and the browser extension needs one of them.

## Install

- Double-click `install.cmd`. Wait about 10 seconds.
- It makes `FastDL.exe` in this folder (about 90 MB: it carries its own copy of Node.js), puts FastDL in the Start menu and on the desktop, and starts FastDL.
- FastDL stays in this folder. Do not move the folder after you install. If you must move it, uninstall first, move it, then install again.
- If FastDL is running, `install.cmd` asks you to quit it first.

## Start

- Click FastDL in the Start menu, or double-click the FastDL icon on the desktop.
- The FastDL window opens, and the FastDL icon appears next to the clock.
- On Windows 11, a new icon may be hidden under the **^** arrow next to the clock. Drag it to the taskbar to always see it.
- Start FastDL again while it runs: it only opens its window again.

## Close and quit

- Close the window: FastDL keeps running next to the clock, like IDM. Downloads go on.
- Click the icon next to the clock to open the window again.
- Right-click the icon for the menu:
  - **Open FastDL** opens the window.
  - **Pause all** pauses the downloads that run or wait. Scheduled downloads keep their start time.
  - **Quit** stops FastDL. Unfinished downloads continue the next time you start it.

## Uninstall

- Quit FastDL first (right-click the icon next to the clock, then Quit).
- Double-click `uninstall.cmd`.
- It removes the Start menu and desktop shortcuts, the Start with Windows shortcut, and `FastDL.exe`.
- Your downloaded files stay. `state.json` (the download list and settings) stays too. Delete the folder if you want it all gone.

## Your own name and logo

- `brand.json` holds the app's name and its logo file. The logo is a PNG, best square and 256 × 256 pixels or bigger.
- The name and logo are used for the exe, the window title, the window and taskbar icon, the icon next to the clock, the shortcuts and the file properties.
- To change them: quit the app, edit `brand.json` or replace the PNG, then double-click `install.cmd` again. It removes the shortcuts with the old name.
- The name may use letters, digits, spaces and `. _ ( ) + ! -` (40 at most).

## Updates

- After you update FastDL's files, quit FastDL and start it again. No new install is needed.
- Run `install.cmd` again only after you change `brand.json` or the logo, or after you update Node.js.

## Install the browser extension

- Brave: open `brave://extensions`, turn on "Developer mode", click "Load unpacked" and choose the `extension` folder.
- Chrome: the same at `chrome://extensions`. Edge: the same at `edge://extensions`.
- Then pin the FastDL icon to the toolbar.

## How to use

- Paste one or more links into the box (or drop links on the window). Click Download.
- "Save as" is optional. If you tick "Schedule", the download is added as scheduled.
- Batch links: for numbered files, put the numbers in square brackets. `https://site.com/img[001-120].jpg` adds img001.jpg to img120.jpg (the leading zeros are kept). FastDL tells you how many links that makes before it adds them. At most 1000 at a time.
- Clipboard monitoring (turn it on in Settings): copy a link to a file, for example a .zip or .mp4, then switch to the FastDL window. FastDL asks "Download?" and adds the file only if you click Download. The first time, the browser asks if FastDL may see the clipboard. Click Allow.
- When all downloads finish: at the bottom right, choose Do nothing, Sleep or Shut down. When the last download ends, a 60-second countdown appears at the top of the window. Click Cancel to stop it. The choice is used once: after the countdown, or after Cancel, it goes back to Do nothing. It is not kept when the app restarts.
- With the extension:
  - While the app runs, browser downloads go to FastDL automatically. To stop this, open the extension popup and switch off "Take over browser downloads".
  - Right-click a link or a video and choose "Download with FastDL".
  - Play a video, then click the extension icon. It shows the media it found (the number on the icon tells how many). Click Download.

## YouTube videos

- FastDL needs two free tools for this: yt-dlp and ffmpeg. Install them once, in a command window:
  - `winget install yt-dlp.yt-dlp`
  - `winget install yt-dlp.FFmpeg`
  - If one is already installed, winget tells you so.
- Three ways to start:
  - In Brave, open a YouTube video page. Click the FastDL icon, choose the quality, and click **Download this video**.
  - Paste the link in FastDL. A quality picker appears next to the box.
  - Right-click a YouTube link and choose "Download with FastDL". This uses the best quality.
- Qualities: Best quality, 1080p, 720p, 480p, MP3 (audio only).
- Videos are saved as MP4 in the `Video` folder. MP3 files go to the `Music` folder.
- FastDL prefers H.264 video. It plays everywhere. Very high resolutions (like 4K) may use VP9 or AV1. VLC plays them.
- Pause and Resume work. One progress bar covers video and sound. "Merging" is the last step.
- If YouTube downloads start to fail, run `winget upgrade yt-dlp.yt-dlp`.
- Limits:
  - One video per link. No playlists or channels.
  - No live streams.
  - No private, members-only or age-restricted videos. FastDL never sends your YouTube login.
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
- Open and Folder buttons for finished files.
- A desktop notification when a download finishes.
- YouTube videos and MP3 audio, with a quality choice (needs yt-dlp and ffmpeg).
- Browser cookies are passed along, so downloads that need a login work.
- Batch links with number ranges, like `img[001-120].jpg`.
- Clipboard monitoring (off by default).
- Start with Windows (off by default): FastDL starts next to the clock when you sign in to Windows, without opening its window.
- Sleep or shut down the PC when all downloads finish, after a countdown you can cancel.
- A real Windows app: `FastDL.exe` with its own icon, no console window, Start menu and desktop shortcuts, and an icon next to the clock with Open, Pause all and Quit.

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
- `FastDL.exe` is not signed. If Windows Smart App Control is on, it may block FastDL.
- The browser extension keeps the name FastDL. `brand.json` does not change it.
- If FastDL stops in a wrong way (for example from Task Manager), its icon can stay next to the clock until you move the mouse over it.
- Clipboard monitoring only looks at the clipboard when you switch to the FastDL window, not while you work in other windows. It only offers links that end in a file type from the sorting folders (Video, Music, Compressed, Documents, Programs).
- Sleep and shut down only happen while the app runs. Shut down does not force apps to close: an app with unsaved work can stop it.
- HLS video is saved as .ts. Most players (VLC, MPC-HC) can play it. ffmpeg can convert it.
- The extension logic is tested with fake browser parts, but it has not yet been tried in a real browser.

## Privacy and safety

- FastDL runs only on your computer (127.0.0.1). Other websites cannot control it.
- The browser extension can only add downloads. It cannot change settings, turn on Start with Windows, or open files.
- Start with Windows puts a "FastDL" shortcut in your Startup folder. Turning the setting off deletes it.
- Clipboard monitoring reads the clipboard only when you switch to the FastDL window. If the clipboard holds anything other than one download link, FastDL ignores it. Nothing from the clipboard is saved.
- Browser cookies are only sent to the site they belong to.
- Your YouTube login cookies are never sent to FastDL or yt-dlp.
- Finished files are marked as "downloaded from the internet", so Windows SmartScreen still checks programs before they run.
- The download list and settings are saved in `state.json` next to the app. This file holds the links and login cookies of unfinished downloads. Do not share it.

## For developers

- `npm test` runs all tests. No internet is needed.
- `npm start` runs only the server (no window, no icon next to the clock). Then open http://127.0.0.1:9614.
- `node desktop.js` runs the whole app without `FastDL.exe`.
- See `CLAUDE.md`, `docs/SPEC.md` and `filemap.md`.
