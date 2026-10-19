@echo off
cd /d "%~dp0"
start "FastDL (close this window to quit)" /min node server.js
timeout /t 1 /nobreak >nul

rem Open the UI as an app window in the first Chromium browser found: Brave, Chrome, then Edge.
rem Full paths on purpose: a bare "msedge"/"chrome" only works when the browser registered an App Path.
set "B=%LocalAppData%\BraveSoftware\Brave-Browser\Application\brave.exe"
if not exist "%B%" set "B=%ProgramFiles%\BraveSoftware\Brave-Browser\Application\brave.exe"
if not exist "%B%" set "B=%LocalAppData%\Google\Chrome\Application\chrome.exe"
if not exist "%B%" set "B=%ProgramFiles%\Google\Chrome\Application\chrome.exe"
if not exist "%B%" set "B=%ProgramFiles(x86)%\Google\Chrome\Application\chrome.exe"
if not exist "%B%" set "B=%ProgramFiles(x86)%\Microsoft\Edge\Application\msedge.exe"
if not exist "%B%" set "B=%ProgramFiles%\Microsoft\Edge\Application\msedge.exe"
if not exist "%B%" goto default
start "" "%B%" --app=http://127.0.0.1:9614
goto :eof

:default
rem none of those: open the app in the default browser instead
start "" http://127.0.0.1:9614
