// The phone (an iPhone, or any phone or tablet on the same Wi-Fi): a second HTTP server, on the local network, for
// paired devices only. Pairing: Options > iPhone shows a QR code with a one-time secret in the link's #fragment (never
// sent in a request line); the phone's page posts it once and gets a device token, kept in its localStorage. Every API
// call carries the token (Authorization: Bearer); state.json keeps only its SHA-256. Host must be an IP address, so a
// web page can't reach it through a DNS name of its own (DNS rebinding). Off unless settings.remote.enabled.
import http from 'node:http';
import crypto from 'node:crypto';
import os from 'node:os';
import path from 'node:path';
import { createReadStream, existsSync, readFileSync, statSync } from 'node:fs';
import { fileURLToPath } from 'node:url';
import { encodeQr } from './qr.js';

const here = path.dirname(fileURLToPath(import.meta.url));
export const REMOTE_PORT = Number(process.env.FASTDL_REMOTE_PORT) || 9615;
const PAIR_MS = 10 * 60000, TICKET_MS = 60000, MAX_BODY = 64 << 10;
const OPS = ['pause', 'resume', 'remove'];

// A link's last path part (decoded), else the link: what a download not named yet shows
const fileOfUrl = u => { try { return decodeURIComponent(new URL(u).pathname.split('/').pop()) || u; } catch { return u; } };
const sha = s => crypto.createHash('sha256').update(s).digest('hex');
const token = n => crypto.randomBytes(n).toString('base64url');

// settings.remote as state.json may hold it: on/off and the paired devices (id, name, token hash, dates)
export function cleanRemote(r) {
  const devices = (Array.isArray(r?.devices) ? r.devices : []).filter(d => typeof d?.id === 'string' && /^[0-9a-f]{64}$/.test(d?.hash ?? ''))
    .map(d => ({ id: d.id, name: String(d.name ?? 'Phone').slice(0, 60), hash: d.hash, added: Number(d.added) || 0, seen: Number(d.seen) || 0 }));
  return { enabled: !!r?.enabled, devices };
}

// This PC's addresses on private networks (home Wi-Fi or Ethernet), the likeliest first
export function lanAddresses(ifaces = os.networkInterfaces()) {
  const all = [];
  for (const [name, list] of Object.entries(ifaces))
    for (const a of list ?? [])
      if ((a.family === 'IPv4' || a.family === 4) && !a.internal && /^(10\.|192\.168\.|172\.(1[6-9]|2\d|3[01])\.)/.test(a.address))
        all.push({ name, address: a.address });
  const rank = x => /vEthernet|VirtualBox|VMware|Hyper-V|WSL|Docker|vpn|tailscale/i.test(x.name) ? 2 : /^192\.168\./.test(x.address) ? 0 : 1;
  return all.sort((a, b) => rank(a) - rank(b)).map(x => x.address);
}

// A Host header that is an IP address and our port (no names: DNS rebinding)
export function hostOk(host, port) {
  const m = /^(\d{1,3}(?:\.\d{1,3}){3}|\[[0-9a-f:.]+\])(?::(\d+))?$/i.exec(String(host ?? ''));
  return !!m && Number(m[2] ?? 80) === port;
}

// A device's own name from its User-Agent: "iPhone", "iPad", else "Phone"
export const deviceName = ua => /iPhone/.test(ua ?? '') ? 'iPhone' : /iPad/.test(ua ?? '') ? 'iPad' : /Android/.test(ua ?? '') ? 'Android phone' : 'Phone';

export class Remote {
  constructor(mgr, { port = REMOTE_PORT, log = () => { }, name = 'Glide' } = {}) {
    this.mgr = mgr;
    this.name = name;
    this.port = port;
    this.log = log;
    this.pairing = null; // { hash, expires }: the QR code on screen, one at a time
    this.tickets = new Map(); // one-time file links: ticket → { id, expires }
    this.failures = []; // times of wrong pairing secrets (10 a minute at most)
    this.server = null;
  }

  get settings() { return (this.mgr.settings.remote = cleanRemote(this.mgr.settings.remote)); }

  // Turned on or off (settings.remote.enabled), and listening to match
  async set(enabled) {
    this.settings.enabled = !!enabled;
    this.mgr.dirty = true;
    if (!enabled) this.pairing = null;
    await this.apply();
  }

  async apply() {
    if (this.settings.enabled && !this.server) {
      const server = http.createServer((req, res) => this.handle(req, res).catch(e => this.send(res, 400, { error: e.message })));
      await new Promise((resolve, reject) => { server.once('error', reject); server.listen(this.port, '0.0.0.0', resolve); });
      this.server = server;
      this.log(`phone access on port ${this.port}`);
    } else if (!this.settings.enabled && this.server) {
      const s = this.server;
      this.server = null;
      s.closeAllConnections?.();
      await new Promise(r => s.close(r));
      this.log('phone access off');
    }
  }

  // What Options > iPhone shows: on/off, the addresses, the devices (never their hashes), and an error if it can't listen
  info() {
    const r = this.settings;
    return { enabled: r.enabled, listening: !!this.server, port: this.port, addresses: lanAddresses(), devices: r.devices.map(({ hash, ...d }) => d) };
  }

  // A new QR code: its link and its module grid. The secret is valid 10 minutes, once.
  startPairing(address = lanAddresses()[0]) {
    if (!this.settings.enabled) throw new Error('turn on phone access first');
    if (!address) throw new Error('This PC is not on a local network (Wi-Fi or Ethernet).');
    const secret = token(16);
    this.pairing = { hash: sha(secret), expires: Date.now() + PAIR_MS };
    const url = `http://${address}:${this.port}/#pair=${secret}`;
    return { url, qr: encodeQr(url), expires: this.pairing.expires };
  }

  removeDevice(id) {
    const r = this.settings;
    r.devices = r.devices.filter(d => d.id !== id);
    this.mgr.dirty = true;
  }

  device(req) {
    const m = /^Bearer ([\w-]{20,100})$/.exec(req.headers.authorization ?? '');
    if (!m) return null;
    const h = sha(m[1]);
    const d = this.settings.devices.find(x => crypto.timingSafeEqual(Buffer.from(x.hash, 'hex'), Buffer.from(h, 'hex')));
    if (d && Date.now() - d.seen > 60000) { d.seen = Date.now(); this.mgr.dirty = true; }
    return d ?? null;
  }

  send(res, code, body, headers = {}) {
    if (res.headersSent) return res.end();
    res.writeHead(code, { 'content-type': 'application/json; charset=utf-8', 'cache-control': 'no-store', 'x-content-type-options': 'nosniff', 'referrer-policy': 'no-referrer', ...headers });
    res.end(JSON.stringify(body));
  }

  async body(req) {
    let size = 0;
    const chunks = [];
    for await (const c of req) {
      size += c.length;
      if (size > MAX_BODY) throw new Error('request too large');
      chunks.push(c);
    }
    return chunks.length ? JSON.parse(Buffer.concat(chunks).toString('utf8')) : {};
  }

  static(res, file, type) {
    const f = path.join(here, file);
    if (!existsSync(f)) return this.send(res, 404, { error: 'not found' });
    res.writeHead(200, { 'content-type': type, 'cache-control': 'no-cache', 'x-content-type-options': 'nosniff', 'referrer-policy': 'no-referrer',
      'content-security-policy': "default-src 'self'; script-src 'self' 'unsafe-inline'; style-src 'self' 'unsafe-inline'; img-src 'self' data:; connect-src 'self'; frame-ancestors 'none'" });
    // the page and its manifest carry the app's name from brand.json
    res.end(/^(text|application\/manifest)/.test(type) ? readFileSync(f, 'utf8').replaceAll('__NAME__', this.name) : readFileSync(f));
  }

  async handle(req, res) {
    if (!hostOk(req.headers.host, this.port)) return this.send(res, 421, { error: 'open Glide by its address' });
    const url = new URL(req.url, 'http://x').pathname;
    if (req.method === 'GET' && (url === '/' || url === '/index.html')) return this.static(res, 'remote/index.html', 'text/html; charset=utf-8');
    if (req.method === 'GET' && url === '/manifest.webmanifest') return this.static(res, 'remote/manifest.webmanifest', 'application/manifest+json');
    if (req.method === 'GET' && url === '/icon.png') return this.static(res, 'assets/logo.png', 'image/png');
    if (req.method === 'GET' && url.startsWith('/file/')) return this.file(res, url.slice(6));
    if (req.method !== 'POST' && !(req.method === 'GET' && url === '/api/state')) return this.send(res, 404, { error: 'not found' });

    if (url === '/api/pair') {
      const now = Date.now();
      this.failures = this.failures.filter(t => now - t < 60000);
      if (this.failures.length >= 10) return this.send(res, 429, { error: 'Too many tries. Wait a minute.' });
      const { secret } = await this.body(req);
      const p = this.pairing;
      if (!p || now > p.expires || typeof secret !== 'string' || sha(secret) !== p.hash) {
        this.failures.push(now);
        return this.send(res, 403, { error: 'This QR code has expired or was already used. Show a new one in Glide (Options › iPhone).' });
      }
      this.pairing = null; // once
      const t = token(32), d = { id: crypto.randomUUID(), name: deviceName(req.headers['user-agent']), hash: sha(t), added: now, seen: now };
      this.settings.devices.push(d);
      this.mgr.dirty = true;
      this.log(`paired ${d.name}`);
      return this.send(res, 200, { token: t, name: d.name });
    }

    const device = this.device(req);
    if (!device) return this.send(res, 401, { error: 'This phone is not paired with Glide. Scan the QR code in Glide › Options › iPhone.' });

    if (url === '/api/state') {
      const v = this.mgr.view();
      return this.send(res, 200, {
        device: device.name,
        downloads: v.downloads.filter(d => d.status !== 'pending').map(d => ({
          id: d.id, name: d.name || path.basename(d.file ?? '') || fileOfUrl(d.url), status: d.status, size: d.size ?? null, got: d.got ?? 0, speed: d.speed ?? 0,
          error: d.error ?? null, video: d.kind === 'video',
        })),
      });
    }
    const body = await this.body(req);
    if (url === '/api/add') {
      const quality = body.quality == null ? undefined : String(body.quality);
      const d = this.mgr.add({ url: String(body.url ?? ''), quality });
      return this.send(res, 200, { id: d.id });
    }
    if (url === '/api/action') {
      if (!OPS.includes(body.op)) throw new Error('unknown action');
      await this.mgr.act(String(body.id ?? ''), body.op);
      return this.send(res, 200, { ok: true });
    }
    if (url === '/api/ticket') {
      const d = this.mgr.list.find(x => x.id === body.id);
      if (d?.status !== 'done' || !d.file || !existsSync(d.file)) throw new Error('only finished downloads can be saved');
      const t = token(24);
      this.tickets.set(t, { id: d.id, expires: Date.now() + TICKET_MS });
      return this.send(res, 200, { url: '/file/' + t });
    }
    this.send(res, 404, { error: 'not found' });
  }

  // A finished file, once, by a ticket from /api/ticket (a link can't carry the Authorization header)
  file(res, t) {
    const k = this.tickets.get(t);
    this.tickets.delete(t);
    for (const [key, v] of this.tickets) if (Date.now() > v.expires) this.tickets.delete(key);
    const d = k && Date.now() <= k.expires && this.mgr.list.find(x => x.id === k.id);
    if (!d || !d.file || !existsSync(d.file)) return this.send(res, 404, { error: 'This link has expired. Tap Save again.' });
    const name = path.basename(d.file);
    res.writeHead(200, {
      'content-type': 'application/octet-stream', 'content-length': statSync(d.file).size, 'cache-control': 'no-store', 'x-content-type-options': 'nosniff',
      'content-disposition': `attachment; filename="${name.replace(/[^\x20-\x7e]|["\\]/g, '_')}"; filename*=UTF-8''${encodeURIComponent(name)}`,
    });
    createReadStream(d.file).pipe(res);
  }

  close() {
    const s = this.server;
    this.server = null;
    if (!s) return Promise.resolve();
    s.closeAllConnections?.();
    return new Promise(r => s.close(r));
  }
}
