import http from 'node:http';
import { readFileSync, existsSync } from 'node:fs';
import { rm } from 'node:fs/promises';
import path from 'node:path';
import { spawn, execFile } from 'node:child_process';
import { promisify } from 'node:util';
import { fileURLToPath } from 'node:url';
import { Manager } from './engine.js';

const PORT = Number(process.env.FASTDL_PORT) || 9614;
const here = path.dirname(fileURLToPath(import.meta.url));
const mgr = new Manager(process.env.FASTDL_STATE || path.join(here, 'state.json'));
await mgr.load();
const page = readFileSync(path.join(here, 'ui.html'));
// FASTDL_STARTUP: tests point this at a temp folder instead of the real Startup folder
const startupLink = path.join(process.env.FASTDL_STARTUP || path.join(process.env.APPDATA ?? '', 'Microsoft', 'Windows', 'Start Menu', 'Programs', 'Startup'), 'FastDL.lnk');

const run = promisify(execFile);
// Values go in as environment variables, so no path or quote can break out of the command.
const powershell = (script, env) => run('powershell.exe', ['-NoProfile', '-NonInteractive', '-Command', '$ErrorActionPreference = "Stop"; ' + script], { env: { ...process.env, ...env }, windowsHide: true });

// "Start with Windows": a Startup-folder shortcut that runs this server minimized (WindowStyle 7). The shortcut itself is the setting.
async function setStartup(on) {
  if (process.platform !== 'win32') throw new Error('Start with Windows only works on Windows');
  if (!on) return rm(startupLink, { force: true });
  await powershell('$s = (New-Object -ComObject WScript.Shell).CreateShortcut($env:FDL_LINK); $s.TargetPath = $env:FDL_NODE; $s.Arguments = $env:FDL_ARGS; $s.WorkingDirectory = $env:FDL_DIR; $s.WindowStyle = 7; $s.Save()',
    { FDL_LINK: startupLink, FDL_NODE: process.execPath, FDL_ARGS: `"${path.join(here, 'server.js')}"`, FDL_DIR: here })
    .catch(e => { throw new Error('could not create the Startup shortcut: ' + (e.stderr || e.message).trim().split('\n')[0]); });
}

// "When all downloads finish", after the 60-second countdown in engine.js.
// shutdown /t 0 because any other timeout implies /f; without /f, apps with unsaved work can still stop it.
// Sleep goes through .NET: rundll32 powrprof.dll,SetSuspendState hibernates on PCs that have hibernation on.
mgr.power = op => {
  console.log(`All downloads finished: ${op === 'shutdown' ? 'shutting down' : 'going to sleep'}`);
  (op === 'shutdown'
    ? run('shutdown.exe', ['/s', '/t', '0'])
    : powershell('Add-Type -AssemblyName System.Windows.Forms; [void][System.Windows.Forms.Application]::SetSuspendState("Suspend", $false, $false)')
  ).catch(e => console.error(`could not ${op === 'shutdown' ? 'shut down' : 'sleep'}:`, e.message));
};

function send(res, code, body, type = 'application/json') {
  res.writeHead(code, { 'content-type': type, 'cache-control': 'no-store' });
  res.end(type === 'application/json' ? JSON.stringify(body) : body);
}

// Always quoted ourselves: Node leaves space-free paths bare and explorer splits them at commas.
// Windows names can't contain '"', and there is no shell, so nothing can break out.
function explorer(arg) {
  spawn('explorer.exe', [arg], { detached: true, stdio: 'ignore', windowsVerbatimArguments: true }).on('error', () => {}).unref();
}

async function readJson(req) {
  const chunks = [];
  let size = 0;
  for await (const c of req) { // Buffers, decoded once: a chunk can end mid-character
    if ((size += c.length) > 1e6) throw new Error('request too large');
    chunks.push(c);
  }
  return JSON.parse(Buffer.concat(chunks).toString('utf8') || '{}');
}

const server = http.createServer(async (req, res) => {
  // Only our own page and browser extensions may drive the app: blocks other websites and DNS rebinding.
  const { host, origin } = req.headers;
  if (host !== `127.0.0.1:${PORT}` && host !== `localhost:${PORT}`) return send(res, 403, { error: 'bad host' });
  const extension = /^(chrome|moz)-extension:\/\//.test(origin ?? '');
  if (origin && origin !== `http://${host}` && !extension) return send(res, 403, { error: 'bad origin' });
  // Any installed extension gets here, so it may only queue downloads: settings could aim the folder at Startup, "open" runs files.
  const route = `${req.method} ${req.url}`;
  if (extension && route !== 'GET /api/ping' && route !== 'POST /api/add') return send(res, 403, { error: 'not allowed from extensions' });
  try {
    if (req.method === 'GET' && req.url === '/') return send(res, 200, page, 'text/html; charset=utf-8');
    if (req.method === 'GET' && req.url === '/api/ping') return send(res, 200, { app: 'fastdl' });
    if (req.method === 'GET' && req.url === '/api/state') {
      const v = mgr.view();
      return send(res, 200, { ...v, settings: { ...v.settings, startup: existsSync(startupLink) } });
    }
    if (req.method !== 'POST') return send(res, 404, { error: 'not found' });
    const body = await readJson(req);
    if (req.url === '/api/add') return send(res, 200, { id: mgr.add(body).id });
    if (req.url === '/api/settings') {
      mgr.update(body);
      if (body.startup != null && !!body.startup !== existsSync(startupLink)) await setStartup(!!body.startup);
      return send(res, 200, mgr.settings);
    }
    if (req.url === '/api/action' && body.op === 'folder') {
      const d = mgr.list.find(x => x.id === body.id);
      if (!d?.file) throw new Error('file not created yet');
      explorer(d.status === 'done' ? `/select,"${d.file}"` : `"${path.dirname(d.file)}"`);
      return send(res, 200, { ok: true });
    }
    if (req.url === '/api/action' && body.op === 'open') {
      const d = mgr.list.find(x => x.id === body.id);
      if (d?.status !== 'done') throw new Error('only finished downloads can be opened');
      explorer(`"${d.file}"`);
      return send(res, 200, { ok: true });
    }
    if (req.url === '/api/action') { await mgr.act(body.id, body.op, body.url); return send(res, 200, { ok: true }); }
    send(res, 404, { error: 'not found' });
  } catch (e) {
    send(res, 400, { error: e.message });
  }
});

server.on('error', e => {
  console.error(e.code === 'EADDRINUSE' ? `FastDL is already running on port ${PORT}` : e.message);
  process.exit(1);
});
server.listen(PORT, '127.0.0.1', () => console.log(`FastDL running at http://127.0.0.1:${PORT}  (Ctrl+C to quit)`));

for (const sig of ['SIGINT', 'SIGTERM', 'SIGBREAK']) process.on(sig, () => { mgr.saveSync(); process.exit(0); });
