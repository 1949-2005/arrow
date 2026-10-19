@echo off
rem Builds the app (an exe with the logo from brand.json) and adds Start menu and desktop shortcuts.
rem Run it again after you change brand.json or the logo.
node "%~dp0install.js"
pause
