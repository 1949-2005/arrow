// The engine launcher. <name> Engine.exe (built by install.js) runs start(); `node desktop.js` does the same.
// The native app (<name>.exe, app/) starts it when the engine does not answer, and shows the windows and the tray icon.
import { readFileSync } from 'node:fs';
import path from 'node:path';
import { execFile, spawn } from 'node:child_process';
import { promisify } from 'node:util';
import { fileURLToPath } from 'node:url';

export const here = path.dirname(fileURLToPath(import.meta.url));

// brand.json is the one place for the app's name and icon (.ico): exes, file properties, window titles, tray, shortcuts.
// The extension can't read it: extension.test.js checks that its files use the same name.
export function readBrand(dir) {
  const brand = JSON.parse(readFileSync(path.join(dir, 'brand.json'), 'utf8'));
  // The name becomes file names, window text and PowerShell input, so only plain characters.
  if (!/^(?![ .])[\p{L}\p{N} ._()+!-]{1,40}(?<![ .])$/u.test(brand.name ?? '')) {
    throw new Error('brand.json: "name" must be 1 to 40 letters, digits, spaces or . _ ( ) + ! - and must not start or end with a space or a dot');
  }
  return brand;
}
export const brand = readBrand(here);
export const exe = path.join(here, brand.name + '.exe'); // the native app
export const engineExe = path.join(here, brand.name + ' Engine.exe');
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

// Errors are thrown: the engine exe shows them in a message box (it has no console).
// The proxy (settings.proxy) is Node's own: NODE_USE_ENV_PROXY with HTTP(S)_PROXY, read once when the process starts.
// The variables this process needs for proxy ('' = none), or null when it already runs with them.
export function proxyEnv(proxy, env = process.env) {
  const want = proxy ? { NODE_USE_ENV_PROXY: '1', HTTP_PROXY: proxy, HTTPS_PROXY: proxy, NO_PROXY: '127.0.0.1,localhost,::1' }
    : { NODE_USE_ENV_PROXY: undefined, HTTP_PROXY: undefined, HTTPS_PROXY: undefined, NO_PROXY: undefined };
  const has = k => env[k] ?? env[k.toLowerCase()];
  return Object.entries(want).every(([k, v]) => (has(k) || undefined) === v) ? null : want;
}

export const stateFile = () => process.env.FASTDL_STATE || path.join(here, 'state.json');
function savedProxy() {
  try { return JSON.parse(readFileSync(stateFile(), 'utf8')).settings?.proxy || ''; } catch { return ''; }
}

// This process again with env (the proxy's variables), detached: it outlives this one, which then ends
export function respawn(env) {
  const next = { ...process.env };
  for (const [k, v] of Object.entries(env)) { delete next[k]; delete next[k.toLowerCase()]; if (v !== undefined) next[k] = v; }
  spawn(process.execPath, process.argv.slice(1), { env: next, detached: true, stdio: 'ignore', windowsHide: true }).unref();
}

export async function start() {
  const ping = await fetch(url + '/api/ping', { signal: AbortSignal.timeout(5000) }).then(r => r.json()).catch(e => e);
  if (ping?.app === 'fastdl') process.exit(0); // already running
  if (ping?.cause?.code !== 'ECONNREFUSED') throw new Error(`Port ${port} is used by another program, so ${brand.name} cannot start.`);
  const env = proxyEnv(savedProxy());
  if (env) { respawn(env); process.exit(0); } // not listening yet: the new one finds the port free
  await import('./server.js');
}

// No top-level await: server.js imports this module while start() is still running.
if (process.argv[1] && path.resolve(process.argv[1]) === fileURLToPath(import.meta.url)) start();
