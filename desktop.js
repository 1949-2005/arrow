// The Windows desktop app. <name>.exe (built by install.js) runs start(); `node desktop.js [--tray]` does the same.
// start(): one instance, the tray icon (tray.ps1), and the UI as an app window in Brave, Chrome or Edge.
import { readFileSync, existsSync } from 'node:fs';
import path from 'node:path';
import { spawn, execFile } from 'node:child_process';
import { promisify } from 'node:util';
import { fileURLToPath } from 'node:url';

export const here = path.dirname(fileURLToPath(import.meta.url));

// brand.json is the one place for the app's name and logo: exe, file properties, window title, tray, shortcuts.
export function readBrand(dir) {
  const brand = JSON.parse(readFileSync(path.join(dir, 'brand.json'), 'utf8'));
  // The name becomes file names, page text and PowerShell input, so only plain characters.
  if (!/^(?![ .])[\p{L}\p{N} ._()+!-]{1,40}(?<![ .])$/u.test(brand.name ?? '')) {
    throw new Error('brand.json: "name" must be 1 to 40 letters, digits, spaces or . _ ( ) + ! - and must not start or end with a space or a dot');
  }
  return brand;
}
export const brand = readBrand(here);
export const exe = path.join(here, brand.name + '.exe');
export const port = Number(process.env.FASTDL_PORT) || 9614;
export const url = `http://127.0.0.1:${port}`;
// FASTDL_STARTUP: tests point this at a temp folder instead of the real Startup folder
export const startupDir = process.env.FASTDL_STARTUP || path.join(process.env.APPDATA ?? '', 'Microsoft', 'Windows', 'Start Menu', 'Programs', 'Startup');

const run = promisify(execFile);
// Values go in as environment variables, so no path or quote can break out of the command.
export const powershell = (script, env) => run('powershell.exe', ['-NoProfile', '-NonInteractive', '-Command', '$ErrorActionPreference = "Stop"; ' + script], { env: { ...process.env, ...env }, windowsHide: true });

// A .lnk file that starts target (with args) in target's folder.
export const shortcut = (link, target, args = '') => powershell('$s = (New-Object -ComObject WScript.Shell).CreateShortcut($env:FDL_LINK); $s.TargetPath = $env:FDL_TARGET; $s.Arguments = $env:FDL_ARGS; $s.WorkingDirectory = $env:FDL_DIR; $s.Save()',
  { FDL_LINK: link, FDL_TARGET: target, FDL_ARGS: args, FDL_DIR: path.dirname(target) });

// Brave, then Chrome, then Edge (full paths: a bare "chrome" only works when the browser registered an App Path).
// null: none of them, so the default browser.
export function browser(env = process.env, exists = existsSync) {
  const { LOCALAPPDATA: local, ProgramFiles: pf, 'ProgramFiles(x86)': pf86 } = env;
  const brave = 'BraveSoftware\\Brave-Browser\\Application\\brave.exe', chrome = 'Google\\Chrome\\Application\\chrome.exe', edge = 'Microsoft\\Edge\\Application\\msedge.exe';
  return [[local, brave], [pf, brave], [local, chrome], [pf, chrome], [pf86, chrome], [pf86, edge], [pf, edge]]
    .map(([dir, file]) => dir && path.join(dir, file)).find(p => p && exists(p)) ?? null;
}

// detached: the browser must outlive the app (Windows ends the app's other child processes with it).
export function openWindow() {
  const b = browser();
  spawn(b ?? 'explorer.exe', [b ? `--app=${url}` : url], { detached: true, stdio: 'ignore' }).on('error', () => {}).unref();
}

// Errors are thrown: the exe shows them in a message box (it has no console).
export async function start(args = process.argv.slice(2)) {
  const quiet = args.includes('--tray'); // Start with Windows: only the tray icon, no window
  const ping = await fetch(url + '/api/ping', { signal: AbortSignal.timeout(5000) }).then(r => r.json()).catch(e => e);
  if (ping?.app === 'fastdl') { // already running: only show the window
    if (!quiet) openWindow();
    process.exit(0);
  }
  if (ping?.cause?.code !== 'ECONNREFUSED') throw new Error(`Port ${port} is used by another program, so ${brand.name} cannot start.`);
  await import('./server.js');
  // Not detached, so Windows ends it when the app exits for any reason (libuv's job object).
  spawn('powershell.exe', ['-NoProfile', '-NonInteractive', '-ExecutionPolicy', 'Bypass', '-File', path.join(here, 'tray.ps1')],
    { windowsHide: true, stdio: 'ignore', env: { ...process.env, FDL_NAME: brand.name, FDL_ICON: path.join(here, brand.name + '.ico'), FDL_URL: url } }).on('error', () => {});
  if (!quiet) openWindow();
}

// No top-level await: server.js imports this module while start() is still running.
if (process.argv[1] && path.resolve(process.argv[1]) === fileURLToPath(import.meta.url)) start();
