@echo off
rem Removes the shortcuts, the exe and its icon. Your downloads and state.json stay.
node "%~dp0install.js" uninstall
pause
