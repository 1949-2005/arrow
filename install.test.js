import { test } from 'node:test';
import assert from 'node:assert/strict';
import fs from 'node:fs/promises';
import { readFileSync } from 'node:fs';
import os from 'node:os';
import path from 'node:path';
import { execFileSync } from 'node:child_process';
import { fileURLToPath } from 'node:url';
import { iconResources, versionInfo, pe, resources, unsign, finish, install, uninstall } from './install.js';

const win = { skip: process.platform !== 'win32' };
const repo = path.dirname(fileURLToPath(import.meta.url));
const brandIcon = path.join(repo, JSON.parse(readFileSync(path.join(repo, 'brand.json'), 'utf8')).icon);

test('the brand .ico becomes one RT_ICON per image and an RT_GROUP_ICON pointing at them', () => {
  const ico = readFileSync(brandIcon), n = ico.readUInt16LE(4);
  const [group, ...icons] = iconResources(ico);
  assert.deepEqual([group.type, group.name, group.data.length, icons.length], [14, 1, 6 + 14 * n, n]);
  icons.forEach((icon, i) => {
    const e = 6 + 16 * i, at = ico.readUInt32LE(e + 12);
    assert.deepEqual(group.data.subarray(6 + 14 * i, 6 + 14 * i + 12), ico.subarray(e, e + 12)); // same size, colors, bits, length
    assert.equal(group.data.readUInt16LE(6 + 14 * i + 12), icon.name);
    assert.deepEqual([icon.type, icon.name, icon.data], [3, i + 1, ico.subarray(at, at + ico.readUInt32LE(e + 8))]);
  });
  assert.throws(() => iconResources(readFileSync(path.join(repo, 'assets', 'logo.png')), 'logo.png'), /logo\.png is not an \.ico file/);
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

test('install builds the native app and the engine with the icon, and shortcuts to the app, after removing a renamed install; uninstall removes it all', win, async () => {
  const tmp = await fs.mkdtemp(path.join(os.tmpdir(), 'fastdl-install-'));
  const dir = path.join(tmp, 'app'), where = { programs: path.join(tmp, 'programs'), desktop: path.join(tmp, 'desktop'), startup: path.join(tmp, 'startup') };
  const links = Object.values(where).map(d => path.join(d, 'TestDL.lnk'));
  try {
    for (const d of [dir, ...Object.values(where)]) await fs.mkdir(d);
    await fs.copyFile(brandIcon, path.join(dir, 'logo.ico'));
    await fs.writeFile(path.join(dir, 'brand.json'), JSON.stringify({ name: 'TestDL', icon: 'logo.ico' }));
    // stands in for desktop.js and records how the engine exe called it
    await fs.writeFile(path.join(dir, 'desktop.js'), "import { writeFileSync } from 'node:fs';\nexport function start() { writeFileSync(new URL('./started.json', import.meta.url), JSON.stringify(process.argv.slice(2))); }\n");
    // an earlier install under another name, with Start with Windows on
    await fs.writeFile(path.join(dir, 'installed.json'), JSON.stringify({ name: 'OldDL' }));
    for (const f of [path.join(dir, 'OldDL.exe'), path.join(dir, 'OldDL Engine.exe'), ...Object.values(where).map(d => path.join(d, 'OldDL.lnk'))]) await fs.writeFile(f, '');

    await install(dir, where);
    const exe = path.join(dir, 'TestDL.exe'), engine = path.join(dir, 'TestDL Engine.exe');
    assert.deepEqual((await fs.readdir(dir)).sort(), ['TestDL Engine.exe', 'TestDL.exe', 'brand.json', 'desktop.js', 'installed.json', 'logo.ico']);
    for (const d of Object.values(where)) assert.deepEqual(await fs.readdir(d), ['TestDL.lnk']);
    const read = '[Console]::OutputEncoding = [Text.Encoding]::UTF8; $w = New-Object -ComObject WScript.Shell; $env:L -split "\\|" | % { $s = $w.CreateShortcut($_); "$($s.TargetPath)>$($s.Arguments)" }; $env:E -split "\\|" | % { $v = (Get-Item $_).VersionInfo; "$($v.FileDescription)>$($v.ProductName)>$($v.FileVersion)" }';
    const lines = execFileSync('powershell.exe', ['-NoProfile', '-NonInteractive', '-Command', read], { env: { ...process.env, L: links.join('|'), E: `${exe}|${engine}` } }).toString().trim().split(/\r?\n/);
    const { version } = JSON.parse(readFileSync(path.join(repo, 'package.json'), 'utf8'));
    assert.deepEqual(lines.map(l => l.toLowerCase()), [`${exe}>`, `${exe}>`, `${exe}>--tray`, `testdl>testdl>${version}.0`, `testdl engine>testdl>${version}`].map(l => l.toLowerCase()));

    for (const f of [exe, engine]) {
      const b = readFileSync(f), res = resources(b); // pe() inside: a 64-bit (PE32+) exe
      assert.equal(b.readUInt16LE(pe(b).subsystem), 2, f); // no console window
      assert.equal(res.filter(r => r.type === 3).length, readFileSync(brandIcon).readUInt16LE(4), f); // every size in the .ico
      assert.equal(res.some(r => r.type === 10 && r.name === 'NODE_SEA_BLOB'), f === engine, f); // only the engine is node.exe
    }
    execFileSync(engine, []); // the embedded app loads desktop.js from the exe's folder
    assert.deepEqual(JSON.parse(readFileSync(path.join(dir, 'started.json'), 'utf8')), []);

    await uninstall(dir, where);
    assert.deepEqual((await fs.readdir(dir)).sort(), ['brand.json', 'desktop.js', 'logo.ico', 'started.json']);
    for (const d of Object.values(where)) assert.deepEqual(await fs.readdir(d), []);
  } finally {
    await fs.rm(tmp, { recursive: true, force: true, maxRetries: 3 });
  }
});
