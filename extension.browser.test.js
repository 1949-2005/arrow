// The extension in a real browser: headless Microsoft Edge loads a copy of extension/ whose APP points at a test engine
// (never port 9614 or the user's browsers), opens a local page and clicks its download link. Skipped without Edge.
import { test, before, after } from 'node:test';
import assert from 'node:assert/strict';
import http from 'node:http';
import net from 'node:net';
import fs from 'node:fs/promises';
import { existsSync, readdirSync } from 'node:fs';
import os from 'node:os';
import path from 'node:path';
import { spawn, spawnSync } from 'node:child_process';
import { fileURLToPath } from 'node:url';

const repo = path.dirname(fileURLToPath(import.meta.url));
const EDGE = ['C:\\Program Files (x86)\\Microsoft\\Edge\\Application\\msedge.exe', 'C:\\Program Files\\Microsoft\\Edge\\Application\\msedge.exe'].find(existsSync);
// IDM's browser integration grabs downloads from every browser, this one included (and shows its own window): skip then
const idm = process.platform === 'win32' && /IDMan\.exe|IEMonitor\.exe/i.test(spawnSync('tasklist.exe', [], { encoding: 'utf8', windowsHide: true }).stdout ?? '');
const skip = { skip: process.platform !== 'win32' || !EDGE ? 'needs Microsoft Edge on Windows' : idm ? 'Internet Download Manager is running: it would take the test downloads' : false };
const sleep = ms => new Promise(r => setTimeout(r, ms));
const freePort = () => new Promise(r => { const s = net.createServer().listen(0, '127.0.0.1', () => { const p = s.address().port; s.close(() => r(p)); }); });

let tmp, engine, edge, site, siteUrl, appUrl, cdp, polling;

// The Chrome DevTools Protocol over the browser's WebSocket: send(method, params, sessionId) → result
async function connect(port) {
  let info;
  for (const t = Date.now(); !info; await sleep(200)) {
    try { info = await (await fetch(`http://127.0.0.1:${port}/json/version`)).json(); } catch { if (Date.now() - t > 20000) throw new Error('Edge did not start'); }
  }
  const ws = new WebSocket(info.webSocketDebuggerUrl);
  await new Promise((r, j) => { ws.onopen = r; ws.onerror = j; });
  let next = 1;
  const waiting = new Map();
  ws.onmessage = m => {
    const msg = JSON.parse(m.data);
    if (msg.id && waiting.has(msg.id)) { const { r, j } = waiting.get(msg.id); waiting.delete(msg.id); msg.error ? j(new Error(msg.error.message)) : r(msg.result); }
  };
  return {
    send: (method, params = {}, sessionId) => new Promise((r, j) => { const id = next++; waiting.set(id, { r, j }); ws.send(JSON.stringify({ id, method, params, sessionId })); }),
    close: () => ws.close(),
  };
}

const state = async () => (await fetch(appUrl + '/api/state')).json();

before(async () => {
  if (skip.skip) return;
  tmp = await fs.mkdtemp(path.join(os.tmpdir(), 'glide-browser-'));
  // the test site: a page with a download link, and the file
  site = http.createServer((req, res) => {
    if (req.url === '/page.html') return res.writeHead(200, { 'content-type': 'text/html' }).end('<a id="dl" href="/files/report.zip">Report</a>');
    if (req.url.startsWith('/files/')) return res.writeHead(200, { 'content-type': 'application/zip', 'content-length': 4, 'content-disposition': 'attachment; filename="report.zip"' }).end('PK..');
    res.writeHead(404).end();
  });
  await new Promise(r => site.listen(0, '127.0.0.1', r));
  siteUrl = `http://127.0.0.1:${site.address().port}`;
  // the engine, on its own port with its state in tmp
  const port = await freePort();
  appUrl = `http://127.0.0.1:${port}`;
  engine = spawn(process.execPath, [path.join(repo, 'server.js')], {
    env: { ...process.env, FASTDL_PORT: String(port), FASTDL_STATE: path.join(tmp, 'state.json'), FASTDL_STARTUP: tmp }, stdio: 'ignore',
  });
  for (const t = Date.now(); ; await sleep(100)) {
    try { if ((await fetch(appUrl + '/api/ping')).ok) break; } catch { }
    if (Date.now() - t > 10000) throw new Error('engine did not start');
  }
  // the extension, pointed at that engine
  const ext = path.join(tmp, 'extension');
  await fs.cp(path.join(repo, 'extension'), ext, { recursive: true });
  const common = path.join(ext, 'common.js');
  await fs.writeFile(common, (await fs.readFile(common, 'utf8')).replace("'http://127.0.0.1:9614'", `'${appUrl}'`));
  const debug = await freePort();
  edge = spawn(EDGE, ['--headless=new', `--user-data-dir=${path.join(tmp, 'profile')}`, `--remote-debugging-port=${debug}`, '--no-first-run',
    '--no-default-browser-check', `--load-extension=${ext}`, 'about:blank'], { stdio: 'ignore' });
  cdp = await connect(debug);
  await cdp.send('Browser.setDownloadBehavior', { behavior: 'allow', downloadPath: path.join(tmp, 'browser-downloads'), eventsEnabled: true });
});

after(async () => {
  clearInterval(polling);
  try { await cdp?.send('Browser.close'); } catch { }
  cdp?.close();
  edge?.kill();
  engine?.kill();
  site?.close();
  await sleep(500);
  if (tmp) await fs.rm(tmp, { recursive: true, force: true, maxRetries: 10, retryDelay: 300 });
});

// Opens the test page in a new tab and clicks its link
async function clickDownload() {
  const { targetId } = await cdp.send('Target.createTarget', { url: siteUrl + '/page.html' });
  const { sessionId } = await cdp.send('Target.attachToTarget', { targetId, flatten: true });
  for (const t = Date.now(); ; await sleep(100)) {
    const { result } = await cdp.send('Runtime.evaluate', { expression: '!!document.getElementById("dl")', returnByValue: true }, sessionId);
    if (result.value) break;
    if (Date.now() - t > 10000) throw new Error('the test page did not load');
  }
  await cdp.send('Runtime.evaluate', { expression: 'document.getElementById("dl").click()', userGesture: true }, sessionId);
}

test('the extension loads in Edge as a service worker', skip, async () => {
  let worker;
  for (const t = Date.now(); !worker && Date.now() - t < 10000; await sleep(200)) {
    const { targetInfos } = await cdp.send('Target.getTargets');
    worker = targetInfos.find(x => x.type === 'service_worker' && x.url.endsWith('/background.js'));
  }
  assert.ok(worker, 'background.js runs');
});

test('with the app open, a download the page starts goes to Glide as pending, not to the browser', skip, async () => {
  polling = setInterval(() => state().catch(() => { }), 1000); // the native app polls /api/state: it counts as open
  await state();
  await clickDownload();
  let d;
  for (const t = Date.now(); !d && Date.now() - t < 15000; await sleep(200)) d = (await state()).downloads.find(x => x.url === siteUrl + '/files/report.zip');
  assert.ok(d, 'Glide got the download');
  assert.equal(d.status, 'pending', 'it waits for the Download File Info window');
  const dir = path.join(tmp, 'browser-downloads');
  assert.ok(!existsSync(dir) || !readdirSync(dir).some(f => f.startsWith('report')), 'the browser did not keep it');
});

test('with the app closed, the browser keeps its download', skip, async () => {
  clearInterval(polling);
  await sleep(6000); // the engine counts the app as gone 5 s after its last poll
  await clickDownload(); // nothing reads /api/state now: the engine answers but says the app is gone (503)
  // the extension gives it back: a download of its own in the browser's list (Edge may then hold a plain-http one for a click)
  const { targetInfos } = await cdp.send('Target.getTargets');
  const worker = targetInfos.find(x => x.type === 'service_worker' && x.url.endsWith('/background.js'));
  const { sessionId } = await cdp.send('Target.attachToTarget', { targetId: worker.targetId, flatten: true });
  let mine = [];
  for (const t = Date.now(); !mine.length && Date.now() - t < 15000; await sleep(300)) {
    const { result } = await cdp.send('Runtime.evaluate', { expression: `chrome.downloads.search({ url: '${siteUrl}/files/report.zip' }).then(l => l.filter(d => d.byExtensionId === chrome.runtime.id).map(d => d.state))`, awaitPromise: true, returnByValue: true }, sessionId);
    mine = result.value ?? [];
  }
  assert.ok(mine.length > 0, 'the browser downloads it again');
  assert.equal((await state()).downloads.filter(d => d.url === siteUrl + '/files/report.zip').length, 1, 'Glide did not take a second one');
});
