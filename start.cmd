@echo off
cd /d "%~dp0"
start "FastDL (close this window to quit)" /min node server.js
timeout /t 1 /nobreak >nul

rem Edge by full path: "msedge" alone only works when Edge registered its App Path.
set "EDGE=%ProgramFiles(x86)%\Microsoft\Edge\Application\msedge.exe"
if not exist "%EDGE%" set "EDGE=%ProgramFiles%\Microsoft\Edge\Application\msedge.exe"
if not exist "%EDGE%" set "EDGE=%LocalAppData%\Microsoft\Edge\Application\msedge.exe"
if not exist "%EDGE%" goto browser
start "" "%EDGE%" --app=http://127.0.0.1:9614
goto :eof

:browser
rem no Edge: open the app in the default browser instead
start "" http://127.0.0.1:9614
