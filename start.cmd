@echo off
cd /d "%~dp0"
start "FastDL (close this window to quit)" /min node server.js
timeout /t 1 /nobreak >nul
start "" msedge --app=http://127.0.0.1:9614
