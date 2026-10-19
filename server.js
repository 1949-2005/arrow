import http from 'node:http';
import { readFileSync, existsSync } from 'node:fs';
import { rm, readFile } from 'node:fs/promises';
import path from 'node:path';
import { spawn, execFile } from 'node:child_process';
import { promisify } from 'node:util';
import { Manager } from './engine.js';
import { here, brand, exe, port as PORT, startupDir, powershell, shortcut, openWindow } from './desktop.js';

const mgr = new Manager(process.env.FASTDL_STATE || path.join(here, 'state.json'));
await mgr.load();
const page = readFileSync(path.join(here, 'ui.html'), 'utf8').replaceAll('FastDL', brand.name);
const startupLink = path.join(startupDir, brand.name + '.lnk');
const run = promisify(execFile);

// "Start with Windows": a Startup-folder shortcut that starts the exe in the tray. The shortcut itself is the setting.
async function setStartup(on) {
  if (process.platform !== 'win32') throw new Error('Start with Windows only works on Windows');
  if (!on) return rm(startupLink, { force: true });
  await shortcut(startupLink, exe, '--tray')
    .catch(e => { throw new Error('could not create the Startup shortcut: ' + (e.stderr || e.message).trim().split('\n')[0]); });
}

// "When all downloads finish", after the 60-second countdown in engine.js.
// shutdown /t 0 because any other timeout implies /f; without /f, apps with unsaved work can still stop it.
// Sleep goes through .NET: rundll32 powrprof.dll,SetSuspendState hibernates on PCs that have hibernation on.
mgr.power = op => {
  console.log(`All downloads finished: ${op === 'shutdown' ? 'shutting down' : 'going to sleep'}`);
  (op === 'shutdown'
    ? run('shutdown.exe', ['/s', '/t', '0'], { windowsHide: true })
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
    if (req.method === 'GET' && req.url === '/favicon.ico') { // the logo, so the app window and its taskbar button show it
      return readFile(path.join(here, brand.logo)).then(b => send(res, 200, b, 'image/png'), () => send(res, 404, { error: 'not found' }));
    }
    if (req.method === 'GET' && req.url === '/api/ping') return send(res, 200, { app: 'fastdl' });
    if (req.method === 'GET' && req.url === '/api/state') {
      const v = mgr.view();
      return send(res, 200, { ...v, settings: { ...v.settings, startup: existsSync(startupLink) } });
    }
    if (req.method !== 'POST') return send(res, 404, { error: 'not found' });
    const body = await readJson(req);
    if (req.url === '/api/add') return send(res, 200, { id: mgr.add(body).id });
    // The tray menu (tray.ps1)
    if (req.url === '/api/open') { openWindow(); return send(res, 200, { ok: true }); }
    if (req.url === '/api/pause-all') { mgr.pauseAll(); return send(res, 200, { ok: true }); }
    if (req.url === '/api/quit') { res.on('close', quit); return send(res, 200, { ok: true }); }
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
  console.error(e.code === 'EADDRINUSE' ? `${brand.name} is already running on port ${PORT}` : e.message);
  process.exit(1);
});
server.listen(PORT, '127.0.0.1', () => console.log(`${brand.name} running at http://127.0.0.1:${PORT}  (Ctrl+C to quit)`));

// Running yt-dlp processes and the tray end with this process (libuv puts child processes in a job object).
function quit() {
  mgr.saveSync();
  process.exit(0);
}
for (const sig of ['SIGINT', 'SIGTERM', 'SIGBREAK']) process.on(sig, quit);
