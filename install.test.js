import { test } from 'node:test';
import assert from 'node:assert/strict';
import fs from 'node:fs/promises';
import { readFileSync } from 'node:fs';
import os from 'node:os';
import path from 'node:path';
import { execFileSync } from 'node:child_process';
import { fileURLToPath } from 'node:url';
import { dib, icoFile, iconGroup, versionInfo, pe, resources, unsign, finish, install, uninstall } from './install.js';

const win = { skip: process.platform !== 'win32' };
const repo = path.dirname(fileURLToPath(import.meta.url));

test('icon images are 32-bit BMPs, bottom row first, with a mask for transparent pixels', () => {
  const px = Buffer.from([1, 2, 3, 255, 0, 0, 0, 0, 5, 6, 7, 128, 9, 9, 9, 255]); // 2x2 BGRA; top row: opaque, transparent
  const img = dib(px);
  assert.deepEqual([img.readUInt32LE(0), img.readInt32LE(4), img.readInt32LE(8), img.readUInt16LE(14)], [40, 2, 4, 32]); // height counts the mask
  assert.deepEqual(img.subarray(40, 56), Buffer.concat([px.subarray(8), px.subarray(0, 8)]));
  assert.deepEqual(img.subarray(56), Buffer.from([0, 0, 0, 0, 0x40, 0, 0, 0])); // one 4-byte mask row each: only the top-right pixel
});

test('.ico file and icon group: one entry per size, 256 written as 0', () => {
  const small = dib(Buffer.alloc(16 * 16 * 4)), big = dib(Buffer.alloc(256 * 256 * 4));
  const ico = icoFile([small, big]);
  assert.deepEqual([...ico.subarray(0, 6)], [0, 0, 1, 0, 2, 0]);
  assert.deepEqual([ico[6], ico[22]], [16, 0]);
  assert.deepEqual([ico.readUInt32LE(6 + 12), ico.readUInt32LE(22 + 12)], [38, 38 + small.length]); // images follow the directory
  assert.deepEqual(ico.subarray(38 + small.length), big);
  const group = iconGroup([small, big]);
  assert.equal(group.length, 6 + 2 * 14);
  assert.deepEqual([group.readUInt32LE(6 + 8), group.readUInt16LE(6 + 12), group.readUInt16LE(20 + 12)], [small.length, 1, 2]); // size, resource ids
});

test('version info holds the name and the version', () => {
  const v = versionInfo('TestDL', '1.2.3');
  assert.equal(v.readUInt16LE(0), v.length);
  assert.equal(v.toString('utf16le', 6, 36), 'VS_VERSION_INFO');
  assert.deepEqual([v.readUInt32LE(40).toString(16), v.readUInt32LE(48), v.readUInt32LE(52)], ['feef04bd', 0x10002, 0x30000]); // 1.2.3.0
  for (const s of ['FileDescription', 'TestDL.exe', '1.2.3']) assert.ok(v.toString('utf16le').includes(s), s);
});

test('exe edits on a copy of node.exe: signature dropped, embedded app on, no console', win, () => {
  const node = readFileSync(process.execPath);
  assert.ok(resources(node).some(r => r.type === 24), 'node.exe has a manifest to keep');
  const signed = node.readUInt32LE(pe(node).dirs + 32);
  const b = finish(unsign(Buffer.from(node)));
  assert.equal(b.readUInt32LE(pe(b).dirs + 32), 0);
  assert.equal(b.length, signed || node.length);
  assert.equal(b.readUInt16LE(pe(b).subsystem), 2);
  assert.ok(b.includes('NODE_SEA_FUSE_fce680ab2cc467b6e072b8b5df1996b2:1'));
  assert.throws(() => finish(b), /cannot hold an app/);
});

test('install builds a working exe with the logo and shortcuts, after removing a renamed install; uninstall removes it all', win, async () => {
  const tmp = await fs.mkdtemp(path.join(os.tmpdir(), 'fastdl-install-'));
  const dir = path.join(tmp, 'app'), where = { programs: path.join(tmp, 'programs'), desktop: path.join(tmp, 'desktop'), startup: path.join(tmp, 'startup') };
  const links = Object.values(where).map(d => path.join(d, 'TestDL.lnk'));
  try {
    for (const d of [dir, ...Object.values(where)]) await fs.mkdir(d);
    await fs.copyFile(path.join(repo, JSON.parse(readFileSync(path.join(repo, 'brand.json'), 'utf8')).logo), path.join(dir, 'logo.png'));
    await fs.writeFile(path.join(dir, 'brand.json'), JSON.stringify({ name: 'TestDL', logo: 'logo.png' }));
    // stands in for desktop.js and records how the exe called it
    await fs.writeFile(path.join(dir, 'desktop.js'), "import { writeFileSync } from 'node:fs';\nexport function start() { writeFileSync(new URL('./started.json', import.meta.url), JSON.stringify(process.argv.slice(2))); }\n");
    // an earlier install under another name, with Start with Windows on
    await fs.writeFile(path.join(dir, 'installed.json'), JSON.stringify({ name: 'OldDL' }));
    for (const f of [path.join(dir, 'OldDL.exe'), ...Object.values(where).map(d => path.join(d, 'OldDL.lnk'))]) await fs.writeFile(f, '');

    await install(dir, where);
    const exe = path.join(dir, 'TestDL.exe');
    assert.deepEqual((await fs.readdir(dir)).sort(), ['TestDL.exe', 'TestDL.ico', 'brand.json', 'desktop.js', 'installed.json', 'logo.png']);
    for (const d of Object.values(where)) assert.deepEqual(await fs.readdir(d), ['TestDL.lnk']);
    const read = '[Console]::OutputEncoding = [Text.Encoding]::UTF8; $w = New-Object -ComObject WScript.Shell; $env:L -split "\\|" | % { $s = $w.CreateShortcut($_); "$($s.TargetPath)>$($s.Arguments)" }; $v = (Get-Item $env:E).VersionInfo; "$($v.FileDescription)>$($v.ProductName)>$($v.FileVersion)"';
    const lines = execFileSync('powershell.exe', ['-NoProfile', '-NonInteractive', '-Command', read], { env: { ...process.env, L: links.join('|'), E: exe } }).toString().trim().split(/\r?\n/);
    const { version } = JSON.parse(readFileSync(path.join(repo, 'package.json'), 'utf8'));
    assert.deepEqual(lines.map(l => l.toLowerCase()), [`${exe}>`, `${exe}>`, `${exe}>--tray`, `testdl>testdl>${version}`].map(l => l.toLowerCase()));

    const b = readFileSync(exe), res = resources(b);
    assert.equal(b.readUInt16LE(pe(b).subsystem), 2); // no console window
    assert.equal(res.filter(r => r.type === 3).length, 8); // icon sizes 16 to 256
    assert.ok(res.some(r => r.type === 10 && r.name === 'NODE_SEA_BLOB'));
    execFileSync(exe, ['--tray']); // the embedded app loads desktop.js from the exe's folder
    assert.deepEqual(JSON.parse(readFileSync(path.join(dir, 'started.json'), 'utf8')), ['--tray']);

    await uninstall(dir, where);
    assert.deepEqual((await fs.readdir(dir)).sort(), ['brand.json', 'desktop.js', 'logo.png', 'started.json']);
    for (const d of Object.values(where)) assert.deepEqual(await fs.readdir(d), []);
  } finally {
    await fs.rm(tmp, { recursive: true, force: true, maxRetries: 3 });
  }
});
