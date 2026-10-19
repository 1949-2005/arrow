# FastDL

FastDL is a download manager that runs on your PC. It makes downloads faster by using several connections for each file, like IDM.

## Requirements

- Windows 10 or 11.
- Node.js 20.3 or newer. To check, open a command window and type `node --version`.
- Microsoft Edge or Google Chrome, for the browser extension.

## Start and stop

- Double-click `start.cmd`. It starts the app in a small, minimized console window and opens the FastDL window in Edge.
- Other way: run `npm start` in this folder, then open http://127.0.0.1:9614 in your browser.
- To quit, close the minimized "FastDL" console window, or press Ctrl+C in it.

## Install the browser extension

- Edge: open `edge://extensions`, turn on "Developer mode", click "Load unpacked" and choose the `extension` folder.
- Chrome: open `chrome://extensions`, turn on "Developer mode", click "Load unpacked" and choose the same `extension` folder.
- Then pin the FastDL icon to the toolbar.

## How to use

- Paste one or more links into the box (or drop links on the window). Click Download.
- "Save as" is optional. If you tick "Schedule", the download is added as scheduled.
- With the extension:
  - While the app runs, browser downloads go to FastDL automatically. To stop this, open the extension popup and switch off "Take over browser downloads".
  - Right-click a link or a video and choose "Download with FastDL".
  - Play a video, then click the extension icon. It shows the media it found (the number on the icon tells how many). Click Download.

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
- Browser cookies are passed along, so downloads that need a login work.

## Settings

| Setting | Default |
|---|---|
| Save to folder | Your Downloads folder |
| Connections per download (1–32) | 8 |
| Downloads at the same time (1–20) | 3 |
| Speed limit in KB/s (0 = unlimited) | 0 |
| Start scheduled downloads at (HH:MM) | empty (off) |
| Sort into folders by type | on |

## Limits

- No YouTube, and no other sites that send audio and video separately (DASH).
- No DRM-protected video.
- No torrents and no FTP.
- No proxy settings.
- No clipboard monitoring and no tray icon.
- HLS video is saved as .ts. Most players (VLC, MPC-HC) can play it. ffmpeg can convert it.
- The extension logic is tested with fake browser parts, but it has not yet been tried in a real browser.

## Privacy and safety

- FastDL runs only on your computer (127.0.0.1). Other websites cannot control it.
- The browser extension can only add downloads. It cannot change settings or open files.
- Browser cookies are only sent to the site they belong to.
- Finished files are marked as "downloaded from the internet", so Windows SmartScreen still checks programs before they run.
- The download list and settings are saved in `state.json` next to the app. This file holds the links and login cookies of unfinished downloads. Do not share it.

## For developers

- `npm test` runs all tests. No internet is needed.
- See `CLAUDE.md`, `docs/SPEC.md` and `filemap.md`.
