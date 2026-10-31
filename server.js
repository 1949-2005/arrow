import http from 'node:http';
import { existsSync } from 'node:fs';
import { rm } from 'node:fs/promises';
import path from 'node:path';
import { spawn, execFile } from 'node:child_process';
import { promisify } from 'node:util';
import { Manager } from './engine.js';
import { here, brand, exe, port as PORT, startupDir, powershell, shortcut, proxyEnv, respawn } from './desktop.js';
import { log, catchCrashes } from './log.js';
import { Remote } from './remote.js';

const stateFile = process.env.FASTDL_STATE || path.join(here, 'state.json');
const logFile = path.join(path.dirname(stateFile), 'log.txt'); // the native app writes to the same file
const mgr = new Manager(stateFile);
mgr.log = m => log(logFile, 'engine', m);
catchCrashes(logFile, () => mgr.saveSync());
await mgr.load();
const remote = new Remote(mgr, { log: m => log(logFile, 'engine', m), name: brand.name });
// phone access, when on; a port another program holds is logged, not fatal
await remote.apply().catch(e => log(logFile, 'engine', 'phone access could not start: ' + e.message));
let opens = 0, appSeen = 0; // POST /api/open count, and when the native app last polled /api/state
const startupLink = path.join(startupDir, brand.name + '.lnk');
const run = promisify(execFile);

// "Start with Windows": a Startup-folder shortcut that starts the native app in the tray. The shortcut itself is the setting.
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

function send(res, code, body) {
  res.writeHead(code, { 'content-type': 'application/json', 'cache-control': 'no-store' });
  res.end(JSON.stringify(body));
}

// Always quoted ourselves: Node leaves space-free paths bare and explorer splits them at commas.
// Windows names can't contain '"', and there is no shell, so nothing can break out.
function explorer(arg) {
  spawn('explorer.exe', [arg], { detached: true, stdio: 'ignore', windowsVerbatimArguments: true }).on('error', () => {}).unref();
}
// The Open action, and a download's own "when done: open the file"
mgr.opener = d => explorer(`"${d.file}"`);

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
  // Only the native app (no Origin) and browser extensions may drive the engine: blocks other websites and DNS rebinding.
  const { host, origin } = req.headers;
  if (host !== `127.0.0.1:${PORT}` && host !== `localhost:${PORT}`) return send(res, 403, { error: 'bad host' });
  const extension = /^(chrome|moz)-extension:\/\//.test(origin ?? '');
  if (origin && origin !== `http://${host}` && !extension) return send(res, 403, { error: 'bad origin' });
  // Any installed extension gets here, so it may only queue downloads, list a YouTube video's formats and show the window: settings could aim the folder at Startup, "open" runs files.
  const url = req.url.split('?')[0];
  const route = `${req.method} ${url}`;
  if (extension && !['GET /api/ping', 'POST /api/add', 'POST /api/formats', 'POST /api/open'].includes(route)) return send(res, 403, { error: 'not allowed from extensions' });
  try {
    if (req.method === 'GET' && url === '/api/ping') return send(res, 200, { app: 'fastdl' });
    if (req.method === 'GET' && url === '/api/state') {
      if (!origin) appSeen = Date.now();
      const v = mgr.view();
      // proxyActive: this process runs with settings.proxy (it restarts to take a new one)
      return send(res, 200, { ...v, open: opens, proxyActive: process.env.NODE_USE_ENV_PROXY === '1', settings: { ...v.settings, remote: undefined, startup: existsSync(startupLink) } });
    }
    if (req.method !== 'POST') return send(res, 404, { error: 'not found' });
    const body = await readJson(req);
    // A pending download waits for the Download File Info window, which only the native app shows.
    if (url === '/api/add' && body.confirm && Date.now() - appSeen > 5000) return send(res, 503, { error: `${brand.name} is not open. Start it from the Start menu.` });
    if (url === '/api/add') return send(res, 200, { id: mgr.add(body).id });
    // The video button's format list: YouTube single-video links only (youtubeId())
    if (url === '/api/formats') return send(res, 200, await mgr.formats(String(body.url ?? ''), body.referrer));
    // The Download File Info window
    if (url === '/api/confirm') { await mgr.confirm(body.id, body); return send(res, 200, { ok: true }); }
    // The native app shows its window when `open` in /api/state rises
    if (url === '/api/open') { opens++; return send(res, 200, { ok: true }); }
    if (url === '/api/pause-all') { mgr.pauseAll(); return send(res, 200, { ok: true }); }
    // The Scheduler: add, update, remove, start and stop queues (not for extensions: a queue's whenDone can shut down)
    if (url === '/api/queues') return send(res, 200, mgr.queues(body));
    if (url === '/api/quit') { res.on('close', quit); return send(res, 200, { ok: true }); }
    // Options > iPhone: phone access on or off, a new pairing QR code, a device removed (the token hashes never leave)
    if (url === '/api/remote') {
      if (body.enabled != null) await remote.set(!!body.enabled);
      return send(res, 200, await remote.info());
    }
    if (url === '/api/remote/pair') return send(res, 200, remote.startPairing(body.address));
    if (url === '/api/remote/remove') { remote.removeDevice(String(body.id ?? '')); return send(res, 200, await remote.info()); }
    if (url === '/api/settings') {
      mgr.update(body);
      if (body.startup != null && !!body.startup !== existsSync(startupLink)) await setStartup(!!body.startup);
      send(res, 200, { ...mgr.settings, remote: undefined });
      // a new proxy needs a new process (Node reads it at start): this one stops listening, starts the next and ends
      const env = body.proxy != null && proxyEnv(mgr.settings.proxy);
      if (env) {
        log(logFile, 'engine', 'restarting for the proxy setting');
        server.close();
        setTimeout(() => { respawn(env); quit(); }, 200);
      }
      return;
    }
    if (url === '/api/action' && body.op === 'folder') {
      const d = mgr.list.find(x => x.id === body.id);
      if (!d?.file) throw new Error('file not created yet');
      explorer(d.status === 'done' ? `/select,"${d.file}"` : `"${path.dirname(d.file)}"`);
      return send(res, 200, { ok: true });
    }
    if (url === '/api/action' && body.op === 'open') {
      const d = mgr.list.find(x => x.id === body.id);
      if (d?.status !== 'done') throw new Error('only finished downloads can be opened');
      mgr.opener(d);
      return send(res, 200, { ok: true });
    }
    if (url === '/api/action') { await mgr.act(body.id, body.op, body.url, body); return send(res, 200, { ok: true }); }
    send(res, 404, { error: 'not found' });
  } catch (e) {
    send(res, 400, { error: e.message });
  }
});

server.on('error', e => {
  const why = e.code === 'EADDRINUSE' ? `${brand.name} is already running on port ${PORT}` : e.message;
  console.error(why);
  log(logFile, 'engine', 'could not start: ' + why);
  process.exit(1);
});
server.listen(PORT, '127.0.0.1', () => {
  console.log(`${brand.name} running at http://127.0.0.1:${PORT}  (Ctrl+C to quit)`);
  log(logFile, 'engine', `started on port ${PORT}, Node ${process.versions.node}`);
  if (process.env.FASTDL_CRASH) setTimeout(() => { throw new Error('FASTDL_CRASH'); }); // tests: an uncaught exception
});

// Running yt-dlp processes end with this process (libuv puts child processes in a job object).
function quit() {
  for (const id of mgr.held.keys()) mgr.release(id); // the Download File Info probes' open connections
  mgr.saveSync();
  log(logFile, 'engine', 'quit');
  process.exit(0);
}
for (const sig of ['SIGINT', 'SIGTERM', 'SIGBREAK']) process.on(sig, quit);
