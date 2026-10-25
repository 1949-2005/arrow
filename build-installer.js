// Builds dist\<name>Setup.exe, one file that installs Glide for the user's account, and its .sha256 checksum:
//   node build-installer.js
// A clean copy of what runs (the native app, the engine exe and the .js files it loads, brand.json, assets, the
// extension, licenses) is zipped into the setup (installer/*.cs, compiled with the app's own sources and look).
// No npm tools: Node's zlib makes the zip, Windows' own csc.exe the setup.
import { readFileSync, writeFileSync, openSync, writeSync, closeSync, readdirSync, statSync, existsSync } from 'node:fs';
import fs from 'node:fs/promises';
import os from 'node:os';
import path from 'node:path';
import zlib from 'node:zlib';
import crypto from 'node:crypto';
import { fileURLToPath } from 'node:url';
import { here, readBrand, powershell } from './desktop.js';
import { build } from './install.js';
import { compileApp, compileSetup } from './app/build.js';

// The engine's modules: desktop.js and every './x.js' it imports, followed through
export function engineFiles(dir = here, start = 'desktop.js') {
  const seen = new Set(), todo = [start];
  while (todo.length) {
    const f = todo.pop();
    if (seen.has(f)) continue;
    seen.add(f);
    for (const m of readFileSync(path.join(dir, f), 'utf8').matchAll(/\bimport\s*(?:[^'"()]*?\bfrom\s*)?\(?\s*['"]\.\/([\w.-]+\.js)['"]/g)) todo.push(m[1]);
  }
  return [...seen].sort();
}

const CRC = Array.from({ length: 256 }, (_, n) => { for (let k = 0; k < 8; k++) n = n & 1 ? 0xedb88320 ^ (n >>> 1) : n >>> 1; return n >>> 0; });
function crc32(b) {
  let c = 0xffffffff;
  for (let i = 0; i < b.length; i++) c = CRC[(c ^ b[i]) & 0xff] ^ (c >>> 8);
  return (c ^ 0xffffffff) >>> 0;
}

// The files under dir as zip entries ('a/b.txt'), deflated; no zip64, so each file and the zip stay under 4 GB
export function zipFolder(dir, out) {
  const list = (d, rel = '') => readdirSync(d, { withFileTypes: true }).flatMap(e =>
    e.isDirectory() ? list(path.join(d, e.name), rel + e.name + '/') : [rel + e.name]);
  const fd = openSync(out, 'w'), central = [];
  let at = 0;
  const put = b => { writeSync(fd, b); at += b.length; };
  try {
    for (const name of list(dir).sort()) {
      const file = path.join(dir, ...name.split('/')), data = readFileSync(file), packed = zlib.deflateRawSync(data, { level: 9 });
      const t = statSync(file).mtime, n = Buffer.from(name, 'utf8');
      const time = t.getHours() << 11 | t.getMinutes() << 5 | t.getSeconds() >> 1, date = (t.getFullYear() - 1980) << 9 | (t.getMonth() + 1) << 5 | t.getDate();
      const head = Buffer.alloc(30);
      head.writeUInt32LE(0x04034b50, 0); head.writeUInt16LE(20, 4); head.writeUInt16LE(0x800, 6); head.writeUInt16LE(8, 8); // UTF-8 names, deflate
      head.writeUInt16LE(time, 10); head.writeUInt16LE(date, 12); head.writeUInt32LE(crc32(data), 14);
      head.writeUInt32LE(packed.length, 18); head.writeUInt32LE(data.length, 22); head.writeUInt16LE(n.length, 26);
      const cd = Buffer.alloc(46);
      cd.writeUInt32LE(0x02014b50, 0); cd.writeUInt16LE(20, 4); head.copy(cd, 6, 4, 30); cd.writeUInt32LE(at, 42);
      central.push(cd, n);
      put(head); put(n); put(packed);
    }
    const cdStart = at, cd = Buffer.concat(central), end = Buffer.alloc(22);
    put(cd);
    end.writeUInt32LE(0x06054b50, 0); end.writeUInt16LE(central.length / 2, 8); end.writeUInt16LE(central.length / 2, 10);
    end.writeUInt32LE(cd.length, 12); end.writeUInt32LE(cdStart, 16);
    put(end);
  } finally {
    closeSync(fd);
  }
}

const README = name => `${name}
${'='.repeat(name.length)}

${name} is a download manager for Windows. It makes downloads faster by using several connections for each file.

Start it from the Start menu or the desktop. Its icon sits next to the clock; right-click it to quit.

Add ${name} to your browser (Brave, Chrome or Edge):
  1. Open the browser's extensions page: brave://extensions, chrome://extensions or edge://extensions
  2. Turn on "Developer mode".
  3. Click "Load unpacked" and choose the "extension" folder in this folder.

YouTube and video downloads need yt-dlp, FFmpeg and Deno. The setup gets them with winget; to update them later:
  winget upgrade yt-dlp.yt-dlp

Your download list and settings are in state.json in this folder. Uninstall from Settings > Apps > Installed apps;
your downloaded files are never removed.

${name} is open source (MIT license, LICENSE). It carries Node.js (licenses\\node-LICENSE.txt).
`;

// Node.js's license for the node.exe the engine is built from: its own folder's LICENSE, else the copy for this
// version in licenses/ (some installs leave LICENSE out; --fetch-node-license gets it)
export const nodeLicenseCopy = (v = process.version) => path.join(here, 'licenses', `node-${v}-LICENSE.txt`);
export function nodeLicense() {
  const f = [path.join(path.dirname(process.execPath), 'LICENSE'), nodeLicenseCopy()].find(existsSync);
  if (!f) throw new Error(`Node.js's license is missing (no LICENSE next to ${process.execPath} and no ${path.relative(here, nodeLicenseCopy())}): the engine carries Node.js, so it must ship with it. Run: node build-installer.js --fetch-node-license`);
  return f;
}

async function fetchNodeLicense() {
  const url = `https://raw.githubusercontent.com/nodejs/node/${process.version}/LICENSE`;
  const res = await fetch(url);
  if (!res.ok) throw new Error(`${url}: HTTP ${res.status}`);
  const text = await res.text();
  if (!text.startsWith('Node.js is licensed')) throw new Error(`${url} is not Node.js's license`);
  await fs.mkdir(path.dirname(nodeLicenseCopy()), { recursive: true });
  await fs.writeFile(nodeLicenseCopy(), text);
  return nodeLicenseCopy();
}

// Code signing, when GLIDE_SIGN_PFX names a code-signing certificate (.pfx; its password in GLIDE_SIGN_PASSWORD):
// Windows' own Set-AuthenticodeSignature, SHA-256, timestamped (GLIDE_SIGN_TIMESTAMP, default DigiCert's) so the
// signature outlives the certificate. Without GLIDE_SIGN_PFX nothing is signed. Returns whether it signed.
export async function sign(files) {
  const pfx = process.env.GLIDE_SIGN_PFX;
  if (!pfx) return false;
  if (!existsSync(pfx)) throw new Error(`GLIDE_SIGN_PFX: ${pfx} does not exist`);
  for (const f of files) {
    const { stdout } = await powershell(
      '$c = New-Object Security.Cryptography.X509Certificates.X509Certificate2($env:FDL_PFX, $env:FDL_PW); ' +
      '$s = Set-AuthenticodeSignature -FilePath $env:FDL_FILE -Certificate $c -HashAlgorithm SHA256 -TimestampServer $env:FDL_TS; ' +
      'if (-not $s.SignerCertificate) { throw "not signed: $($s.StatusMessage)" }; $s.Status',
      { FDL_PFX: pfx, FDL_PW: process.env.GLIDE_SIGN_PASSWORD ?? '', FDL_FILE: f, FDL_TS: process.env.GLIDE_SIGN_TIMESTAMP || 'http://timestamp.digicert.com' });
    console.log(`Signed ${path.basename(f)} (${stdout.trim()})`);
  }
  return true;
}

// The folder the setup installs, in staging (empty)
export async function stage(staging) {
  const { name, icon } = readBrand(here);
  const { version } = JSON.parse(readFileSync(path.join(here, 'package.json'), 'utf8'));
  // the logo's render script is for building, not for the installed app
  const copy = (from, to = from) => fs.cp(path.join(here, from), path.join(staging, to), { recursive: true, filter: f => !f.endsWith('.ps1') });
  for (const f of [...engineFiles(), 'package.json', 'brand.json', 'LICENSE', 'assets', 'extension', 'remote']) await copy(f); // package.json: "type": "module"; remote: the phone's page
  const license = nodeLicense();
  await fs.mkdir(path.join(staging, 'licenses'));
  await fs.copyFile(license, path.join(staging, 'licenses', 'node-LICENSE.txt'));
  await fs.writeFile(path.join(staging, 'README.txt'), README(name).replace(/\n/g, '\r\n'));
  await compileApp({ out: path.join(staging, name + '.exe'), name, version, icon: path.join(staging, icon) });
  await build(staging); // <name> Engine.exe: node.exe whose embedded app loads desktop.js from its folder
  await sign([path.join(staging, name + '.exe'), path.join(staging, name + ' Engine.exe')]);
  return { name, version, icon };
}

export async function buildSetup(dist = path.join(here, 'dist')) {
  const tmp = await fs.mkdtemp(path.join(os.tmpdir(), 'glide-setup-'));
  try {
    const staging = path.join(tmp, 'app');
    await fs.mkdir(staging);
    const { name, version, icon } = await stage(staging);
    zipFolder(staging, path.join(tmp, 'payload.zip'));
    await fs.mkdir(dist, { recursive: true });
    const file = name.replace(/[^\w.-]/g, '') + 'Setup.exe', out = path.join(dist, file);
    await compileSetup({ out, name, version, icon: path.join(here, icon), payload: path.join(tmp, 'payload.zip'), logo: path.join(here, 'assets', 'logo.png') });
    await sign([out]); // before the hash: the checksum is of the signed file
    const hash = crypto.createHash('sha256').update(readFileSync(out)).digest('hex');
    writeFileSync(out + '.sha256', `${hash}  ${file}`); // as limewheel's: lower case, two spaces, no newline
    return { out, hash, size: statSync(out).size };
  } finally {
    await fs.rm(tmp, { recursive: true, force: true, maxRetries: 3 });
  }
}

if (process.argv[1] && path.resolve(process.argv[1]) === fileURLToPath(import.meta.url)) {
  try {
    if (process.argv.includes('--fetch-node-license')) {
      console.log('Saved ' + await fetchNodeLicense());
      process.exit(0);
    }
    if (process.platform !== 'win32') throw new Error('the setup needs Windows.');
    console.log('Building the setup. This takes about 15 seconds...');
    const { out, hash, size } = await buildSetup();
    console.log(`Built: ${out}  (${(size / 1048576).toFixed(1)} MB, sha256 ${hash})`);
  } catch (e) {
    console.error('Error: ' + e.message);
    process.exitCode = 1;
  }
}
