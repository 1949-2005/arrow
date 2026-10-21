// Installs the desktop app in place (this folder):
//   node install.js             (install.cmd)    build <name>.exe and <name> Engine.exe here, add Start menu and desktop shortcuts
//   node install.js uninstall   (uninstall.cmd)  remove them again; downloads and state.json stay
// <name>.exe is the native app (app/, compiled by app/build.js). <name> Engine.exe is Node's own single executable
// application: a copy of node.exe whose embedded app loads desktop.js from this folder (so code changes need no rebuild),
// with the brand icon, version info and no console window.
// No npm tools: kernel32's UpdateResource (through PowerShell) writes the resources.
import { readFileSync, existsSync } from 'node:fs';
import fs from 'node:fs/promises';
import os from 'node:os';
import path from 'node:path';
import { spawn, execFile } from 'node:child_process';
import { promisify } from 'node:util';
import { fileURLToPath } from 'node:url';
import { here, readBrand, url, startupDir, powershell, shortcut } from './desktop.js';
import { compileApp } from './app/build.js';

const run = promisify(execFile);

// An .ico file as exe resources: each image becomes an RT_ICON, and the directory an RT_GROUP_ICON
// (the same 12-byte entries, with a resource id where the file has the image's offset).
export function iconResources(ico, file = 'the icon') {
  if (ico.readUInt16LE(0) !== 0 || ico.readUInt16LE(2) !== 1) throw new Error(`${file} is not an .ico file`);
  const n = ico.readUInt16LE(4), group = Buffer.alloc(6 + 14 * n), icons = [];
  ico.copy(group, 0, 0, 6);
  for (let i = 0; i < n; i++) {
    const e = 6 + 16 * i, at = ico.readUInt32LE(e + 12);
    ico.copy(group, 6 + 14 * i, e, e + 12);
    group.writeUInt16LE(i + 1, 6 + 14 * i + 12);
    icons.push({ type: 3, name: i + 1, data: ico.subarray(at, at + ico.readUInt32LE(e + 8)) });
  }
  return [{ type: 14, name: 1, data: group }, ...icons];
}

// VS_VERSIONINFO, shown by Explorer (Properties > Details) and Task Manager.
// Each block: length, value length, type (1 = text), key, then the value and the children, each aligned to 4 bytes.
function block(key, value, children = []) {
  const text = typeof value === 'string', v = text ? Buffer.from(value + '\0', 'utf16le') : value ?? Buffer.alloc(0);
  let b = Buffer.concat([Buffer.alloc(6), Buffer.from(key + '\0', 'utf16le')]);
  for (const part of [v, ...children]) b = Buffer.concat([b, Buffer.alloc(-b.length & 3), part]);
  b.writeUInt16LE(b.length, 0); b.writeUInt16LE(text ? v.length / 2 : v.length, 2); b.writeUInt16LE(Buffer.isBuffer(value) ? 0 : 1, 4);
  return b;
}
// file: the exe's own name ("<name> Engine"), which Task Manager shows; name stays the product
export function versionInfo(name, version, file = name) {
  const [a, b, c] = `${version}.0.0`.split('.').map(n => Number(n) & 0xffff);
  const fixed = Buffer.alloc(52);
  fixed.writeUInt32LE(0xfeef04bd, 0); fixed.writeUInt32LE(0x10000, 4);
  for (const at of [8, 16]) { fixed.writeUInt32LE((a << 16 | b) >>> 0, at); fixed.writeUInt32LE(c << 16 >>> 0, at + 4); } // file, product version a.b.c.0
  fixed.writeUInt32LE(0x3f, 24); fixed.writeUInt32LE(0x40004, 32); fixed.writeUInt32LE(1, 36); // flags mask, VOS_NT_WINDOWS32, VFT_APP
  const strings = { FileDescription: file, ProductName: name, InternalName: file, OriginalFilename: file + '.exe', FileVersion: version, ProductVersion: version };
  return block('VS_VERSION_INFO', fixed, [
    block('StringFileInfo', undefined, [block('040904B0', undefined, Object.entries(strings).map(([k, v]) => block(k, v)))]),
    block('VarFileInfo', undefined, [block('Translation', Buffer.from([0x09, 0x04, 0xb0, 0x04]))]), // en-US, UTF-16
  ]);
}

// The few PE (exe) header fields this build reads or changes.
export function pe(b) {
  const at = b.readUInt32LE(0x3c), opt = at + 24;
  if (b.toString('latin1', at, at + 4) !== 'PE\0\0' || b.readUInt16LE(opt) !== 0x20b) throw new Error('not a 64-bit Windows program');
  const table = opt + b.readUInt16LE(at + 20);
  const sections = Array.from({ length: b.readUInt16LE(at + 6) }, (_, i) => table + 40 * i)
    .map(s => ({ rva: b.readUInt32LE(s + 12), size: b.readUInt32LE(s + 16), raw: b.readUInt32LE(s + 20) }));
  const file = rva => { const s = sections.find(s => rva >= s.rva && rva < s.rva + s.size); return rva - s.rva + s.raw; };
  return { subsystem: opt + 68, dirs: opt + 112, file };
}

// Resources as { type, name, data }, in any language.
export function resources(b) {
  const { dirs, file } = pe(b), base = file(b.readUInt32LE(dirs + 16)), out = [];
  const id = v => v & 0x80000000 ? b.toString('utf16le', base + (v & 0x7fffffff) + 2, base + (v & 0x7fffffff) + 2 + 2 * b.readUInt16LE(base + (v & 0x7fffffff))) : v;
  (function walk(dir, keys) {
    for (let i = 0; i < b.readUInt16LE(dir + 12) + b.readUInt16LE(dir + 14); i++) {
      const e = dir + 16 + 8 * i, to = b.readUInt32LE(e + 4), key = [...keys, id(b.readUInt32LE(e))];
      if (to & 0x80000000) walk(base + (to & 0x7fffffff), key);
      else { const at = file(b.readUInt32LE(base + to)); out.push({ type: key[0], name: key[1], data: b.subarray(at, at + b.readUInt32LE(base + to + 4)) }); }
    }
  })(base, []);
  return out;
}

// node.exe's signature no longer matches once the resources change: drop it (its certificate table ends the file).
export function unsign(b) {
  const { dirs } = pe(b), at = b.readUInt32LE(dirs + 32);
  if (!at) return b;
  b.writeUInt32LE(0, dirs + 32); b.writeUInt32LE(0, dirs + 36);
  return b.subarray(0, at);
}

const FUSE = 'NODE_SEA_FUSE_fce680ab2cc467b6e072b8b5df1996b2:0'; // node.exe runs its embedded app when this ends in 1
// Turn on the embedded app, and the Windows (GUI) subsystem, which starts without a console window.
export function finish(b) {
  const at = b.indexOf(FUSE);
  if (at < 0 || b.indexOf(FUSE, at + 1) >= 0) throw new Error(`${process.execPath} cannot hold an app: Node.js 20 or newer is needed`);
  b[at + FUSE.length - 1] = 0x31; // '1'
  b.writeUInt16LE(2, pe(b).subsystem);
  return b;
}

// The exe's embedded app: load desktop.js from the exe's folder. There is no console, so startup errors go to a message box.
const launcher = name => `const path = require('node:path');
import(require('node:url').pathToFileURL(path.join(path.dirname(process.execPath), 'desktop.js')).href)
  .then(app => app.start())
  .catch(e => {
    require('node:child_process').spawnSync('powershell.exe', ['-NoProfile', '-NonInteractive', '-Command',
      'Add-Type -AssemblyName System.Windows.Forms; [void][System.Windows.Forms.MessageBox]::Show($env:FDL_ERROR, $env:FDL_NAME, "OK", "Error")'],
      { env: { ...process.env, FDL_ERROR: String(e?.message ?? e), FDL_NAME: ${JSON.stringify(name)} }, windowsHide: true });
    process.exit(1);
  });
`;

// Windows' own resource editor (kernel32 UpdateResource): drop node.exe's resources, write ours.
const UPDATE = `Add-Type -TypeDefinition @'
using System; using System.Runtime.InteropServices;
public static class Res {
  [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)] public static extern IntPtr BeginUpdateResource(string file, bool deleteExisting);
  [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)] public static extern bool UpdateResource(IntPtr h, IntPtr type, IntPtr name, ushort lang, byte[] data, int size);
  [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)] public static extern bool UpdateResource(IntPtr h, IntPtr type, string name, ushort lang, byte[] data, int size);
  [DllImport("kernel32.dll", SetLastError = true)] public static extern bool EndUpdateResource(IntPtr h, bool discard);
}
'@
$h = [Res]::BeginUpdateResource($env:FDL_EXE, $true)
if ($h -eq [IntPtr]::Zero) { throw [ComponentModel.Win32Exception]::new() }
foreach ($r in (Get-Content -Raw $env:FDL_RES | ConvertFrom-Json)) {
  $d = [Convert]::FromBase64String($r.data)
  $ok = if ($r.name -is [string]) { [Res]::UpdateResource($h, [IntPtr]$r.type, $r.name, 1033, $d, $d.Length) } else { [Res]::UpdateResource($h, [IntPtr]$r.type, [IntPtr]$r.name, 1033, $d, $d.Length) }
  if (!$ok) { throw [ComponentModel.Win32Exception]::new() }
}
if (![Res]::EndUpdateResource($h, $false)) { throw [ComponentModel.Win32Exception]::new() }`;

// <name> Engine.exe in dir, from dir's brand.json.
export async function build(dir = here) {
  const { name, icon } = readBrand(dir);
  const { version } = JSON.parse(readFileSync(path.join(here, 'package.json'), 'utf8'));
  const tmp = await fs.mkdtemp(path.join(os.tmpdir(), 'fastdl-build-')), file = f => path.join(tmp, f);
  try {
    await fs.writeFile(file('main.cjs'), launcher(name));
    await fs.writeFile(file('sea.json'), JSON.stringify({ main: file('main.cjs'), output: file('app.blob'), disableExperimentalSEAWarning: true }));
    await run(process.execPath, ['--experimental-sea-config', file('sea.json')]);
    const node = readFileSync(process.execPath);
    const res = [
      ...resources(node).filter(r => r.type === 24), // node.exe's manifest: Windows 10/11 support, long paths
      { type: 16, name: 1, data: versionInfo(name, version, name + ' Engine') },
      ...iconResources(readFileSync(path.resolve(dir, icon)), icon),
      { type: 10, name: 'NODE_SEA_BLOB', data: readFileSync(file('app.blob')) },
    ];
    await fs.writeFile(file('res.json'), JSON.stringify(res.map(r => ({ ...r, data: r.data.toString('base64') }))));
    await fs.writeFile(file('app.exe'), unsign(node));
    await powershell(UPDATE, { FDL_EXE: file('app.exe'), FDL_RES: file('res.json') });
    await fs.writeFile(path.join(dir, name + ' Engine.exe'), finish(readFileSync(file('app.exe'))));
  } finally {
    await fs.rm(tmp, { recursive: true, force: true });
  }
}

// The name last installed: after a rename, the old shortcuts and exe go too.
const record = dir => path.join(dir, 'installed.json');
const installed = dir => existsSync(record(dir)) ? JSON.parse(readFileSync(record(dir), 'utf8')).name : readBrand(dir).name;

// The Start menu, desktop (it may be in OneDrive) and Startup folders. UTF-8: folder names may not be English.
async function folders() {
  const { stdout } = await powershell("[Console]::OutputEncoding = [Text.Encoding]::UTF8; [Environment]::GetFolderPath('Programs'); [Environment]::GetFolderPath('Desktop')");
  const [programs, desktop] = stdout.trim().split(/\r?\n/);
  return { programs, desktop, startup: startupDir };
}

export async function install(dir = here, where) {
  where ??= await folders();
  const { name, icon } = readBrand(dir), old = installed(dir), exe = path.join(dir, name + '.exe');
  // Start with Windows stays on, now through the native app (its shortcut may still start node.exe or the engine, or have the old name).
  const startup = [old, name].some(n => existsSync(path.join(where.startup, n + '.lnk')));
  if (old !== name) await uninstall(dir, where);
  await build(dir);
  const { version } = JSON.parse(readFileSync(path.join(here, 'package.json'), 'utf8'));
  await compileApp({ out: exe, name, version, icon: path.resolve(dir, icon) });
  for (const folder of [where.programs, where.desktop]) await shortcut(path.join(folder, name + '.lnk'), exe);
  if (startup) await shortcut(path.join(where.startup, name + '.lnk'), exe, '--tray');
  await fs.writeFile(record(dir), JSON.stringify({ name }));
}

export async function uninstall(dir = here, where) {
  where ??= await folders();
  const name = installed(dir);
  for (const f of [...Object.values(where).map(folder => path.join(folder, name + '.lnk')), path.join(dir, name + '.exe'), path.join(dir, name + ' Engine.exe'), record(dir)]) {
    await fs.rm(f, { force: true });
  }
}

// CSV: the table format cuts long image names.
const appRunning = name => run('tasklist.exe', ['/FI', `IMAGENAME eq ${name}.exe`, '/FO', 'CSV', '/NH'], { windowsHide: true })
  .then(r => r.stdout.toLowerCase().includes(`"${name}.exe"`.toLowerCase()), () => false);

if (process.argv[1] && path.resolve(process.argv[1]) === fileURLToPath(import.meta.url)) {
  const remove = process.argv[2] === 'uninstall', name = installed(here);
  try {
    if (process.platform !== 'win32') throw new Error('the desktop app needs Windows.');
    // Windows can't replace or delete a running exe: the engine answers on its port, the native app shows in tasklist
    if (await appRunning(name) || await appRunning(readBrand(here).name) || await fetch(url + '/api/ping', { signal: AbortSignal.timeout(5000) }).then(r => r.ok, () => false)) {
      throw new Error(`${name} is running. Quit it first: right-click its icon next to the clock and click Quit (or close its console window). Then try again.`);
    }
    if (remove) {
      await uninstall();
      console.log(`${name} is uninstalled. Your downloaded files and state.json (the download list and settings) are still here.`);
    } else {
      const { name: now } = readBrand(here);
      console.log(`Building ${now}.exe and ${now} Engine.exe. This takes a few seconds...`);
      await install();
      await run('ie4uinit.exe', ['-show']).catch(() => {}); // refresh Windows' icon cache, for a new logo
      spawn(path.join(here, now + '.exe'), [], { detached: true, stdio: 'ignore' }).unref();
      console.log(`${now} is installed and starting. Next time, start it from the Start menu or the desktop.`);
    }
  } catch (e) {
    console.error('Error: ' + e.message);
    process.exitCode = 1;
  }
}
