# FastDL

FastDL is a download manager that runs on your PC. It makes downloads faster by using several connections for each file, like IDM.

## Requirements

- Windows 10 or 11.
- Node.js 20.3 or newer. To check, open a command window and type `node --version`.
- Brave, Google Chrome or Microsoft Edge, for the browser extension.

## Start and stop

- Double-click `start.cmd`. It starts the app in a small, minimized console window and opens the FastDL window in Brave, Chrome or Edge (the first one it finds), or else in your default browser.
- Other way: run `npm start` in this folder, then open http://127.0.0.1:9614 in your browser.
- To quit, close the minimized "FastDL" console window, or press Ctrl+C in it.

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
- Start with Windows (off by default): FastDL starts minimized when you sign in to Windows.
- Sleep or shut down the PC when all downloads finish, after a countdown you can cancel.

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
| Start with Windows (minimized) | off |

## Limits

- No sites other than YouTube that send audio and video separately (DASH).
- No DRM-protected video.
- No torrents and no FTP.
- No proxy settings.
- No tray icon.
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
- See `CLAUDE.md`, `docs/SPEC.md` and `filemap.md`.
