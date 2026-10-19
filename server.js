import http from 'node:http';
import { readFileSync } from 'node:fs';
import path from 'node:path';
import { spawn } from 'node:child_process';
import { fileURLToPath } from 'node:url';
import { Manager } from './engine.js';

const PORT = Number(process.env.FASTDL_PORT) || 9614;
const here = path.dirname(fileURLToPath(import.meta.url));
const mgr = new Manager(path.join(here, 'state.json'));
await mgr.load();
const page = readFileSync(path.join(here, 'ui.html'));

function send(res, code, body, type = 'application/json') {
  res.writeHead(code, { 'content-type': type, 'cache-control': 'no-store' });
  res.end(type === 'application/json' ? JSON.stringify(body) : body);
}

async function readJson(req) {
  let s = '';
  for await (const c of req) {
    s += c;
    if (s.length > 1e6) throw new Error('request too large');
  }
  return JSON.parse(s || '{}');
}

const server = http.createServer(async (req, res) => {
  // Only our own page and browser extensions may drive the app: blocks other websites and DNS rebinding.
  const { host, origin } = req.headers;
  if (host !== `127.0.0.1:${PORT}` && host !== `localhost:${PORT}`) return send(res, 403, { error: 'bad host' });
  if (origin && origin !== `http://${host}` && !/^(chrome|moz)-extension:\/\//.test(origin)) return send(res, 403, { error: 'bad origin' });
  try {
    if (req.method === 'GET' && req.url === '/') return send(res, 200, page, 'text/html; charset=utf-8');
    if (req.method === 'GET' && req.url === '/api/ping') return send(res, 200, { app: 'fastdl' });
    if (req.method === 'GET' && req.url === '/api/state') return send(res, 200, mgr.view());
    if (req.method !== 'POST') return send(res, 404, { error: 'not found' });
    const body = await readJson(req);
    if (req.url === '/api/add') return send(res, 200, { id: mgr.add(body).id });
    if (req.url === '/api/settings') { mgr.update(body); return send(res, 200, mgr.settings); }
    if (req.url === '/api/action' && body.op === 'folder') {
      const d = mgr.list.find(x => x.id === body.id);
      if (!d?.file) throw new Error('file not created yet');
      const args = d.status === 'done' ? ['/select,', d.file] : [path.dirname(d.file)];
      spawn('explorer.exe', args, { detached: true, stdio: 'ignore' }).on('error', () => {}).unref();
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
