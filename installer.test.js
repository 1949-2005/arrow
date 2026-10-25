import { test, before, after } from 'node:test';
import assert from 'node:assert/strict';
import net from 'node:net';
import fs from 'node:fs/promises';
import { existsSync, readFileSync, readdirSync } from 'node:fs';
import os from 'node:os';
import path from 'node:path';
import crypto from 'node:crypto';
import zlib from 'node:zlib';
import { spawn, spawnSync, execFileSync } from 'node:child_process';
import { fileURLToPath } from 'node:url';
import { engineFiles, zipFolder, buildSetup } from './build-installer.js';

const repo = path.dirname(fileURLToPath(import.meta.url));
const win = { skip: process.platform !== 'win32' };
const sleep = ms => new Promise(r => setTimeout(r, ms));

test('the engine files are desktop.js and what it imports, followed through', () => {
  assert.deepEqual(engineFiles(), ['desktop.js', 'engine.js', 'hls.js', 'server.js', 'ytdlp.js']);
});

test('zipFolder writes a zip that unpacks to the same files', async () => {
  const tmp = await fs.mkdtemp(path.join(os.tmpdir(), 'arrow-zip-'));
  try {
    await fs.mkdir(path.join(tmp, 'in', 'a', 'b'), { recursive: true });
    const files = { 'x.txt': 'hello', 'a/b/é.bin': crypto.randomBytes(70000), 'a/empty.txt': '' };
    for (const [f, d] of Object.entries(files)) await fs.writeFile(path.join(tmp, 'in', f), d);
    zipFolder(path.join(tmp, 'in'), path.join(tmp, 'out.zip'));
    // read it back by its central directory, as ZipArchive does
    const z = readFileSync(path.join(tmp, 'out.zip')), end = z.length - 22, got = {};
    assert.equal(z.readUInt32LE(end), 0x06054b50);
    for (let i = 0, at = z.readUInt32LE(end + 16); i < z.readUInt16LE(end + 10); i++) {
      const name = z.toString('utf8', at + 46, at + 46 + z.readUInt16LE(at + 28)), local = z.readUInt32LE(at + 42);
      const data = z.subarray(local + 30 + z.readUInt16LE(local + 26), local + 30 + z.readUInt16LE(local + 26) + z.readUInt32LE(at + 20));
      got[name] = zlib.inflateRawSync(data);
      assert.equal(got[name].length, z.readUInt32LE(at + 24), name);
      at += 46 + z.readUInt16LE(at + 28);
    }
    assert.deepEqual(Object.keys(got).sort(), Object.keys(files).sort());
    for (const [f, d] of Object.entries(files)) assert.ok(got[f].equals(Buffer.from(d)), f);
  } finally {
    await fs.rm(tmp, { recursive: true, force: true });
  }
});

// A real setup, installed and uninstalled in temp folders: never the real Programs folder, Start menu, desktop,
// Startup folder, Uninstall key, winget or port 9614
let tmp, setup, env, dir, key, engine;
const freePort = () => new Promise(r => { const s = net.createServer().listen(0, '127.0.0.1', () => { const p = s.address().port; s.close(() => r(p)); }); });
const run = (exe, args = [], extra = {}) => spawnSync(exe, args, { env: { ...env, ...extra }, encoding: 'utf8', windowsHide: true, timeout: 120000 });
const links = () => ['start', 'desktop', 'startup'].map(f => path.join(tmp, f, 'Arrow.lnk'));
const regQuery = () => spawnSync('reg.exe', ['query', 'HKCU\\' + key + '\\Arrow'], { encoding: 'utf8', windowsHide: true });
const shortcut = link => execFileSync('powershell.exe', ['-NoProfile', '-NonInteractive', '-Command',
  '$s = (New-Object -ComObject WScript.Shell).CreateShortcut($env:L); "$($s.TargetPath)|$($s.Arguments)"'], { env: { ...process.env, L: link } }).toString().trim();

before(async () => {
  if (process.platform !== 'win32') return;
  tmp = await fs.mkdtemp(path.join(os.tmpdir(), 'arrow-setup-test-'));
  ({ out: setup } = await buildSetup(path.join(tmp, 'dist')));
  dir = path.join(tmp, 'Programs', 'Arrow');
  key = 'Software\\ArrowSetupTest-' + crypto.randomBytes(4).toString('hex');
  // winget stand-in: puts the tool where winget would (WinGet\Links, WinGet\Packages), or fails
  await fs.writeFile(path.join(tmp, 'winget.mjs'), `import fs from 'node:fs';
const id = process.argv[process.argv.indexOf('--id') + 1], w = process.env.LOCALAPPDATA + '/Microsoft/WinGet';
fs.appendFileSync(process.env.FAKE_WINGET_LOG, id + '\\n');
process.stdout.write('Found ' + id + '\\r  ██████▒▒▒▒  1 MB / 2 MB\\r');
if (process.env.FAKE_WINGET === 'fail') { console.log('An unexpected error occurred while executing the command: 0x80072ee7'); process.exit(5); }
const put = f => { fs.mkdirSync(f.replace(/[\\\\/][^\\\\/]+$/, ''), { recursive: true }); fs.writeFileSync(f, ''); };
if (id === 'yt-dlp.yt-dlp') { put(w + '/Links/yt-dlp.exe'); put(w + '/Links/deno.exe'); put(w + '/Packages/yt-dlp.FFmpeg_Microsoft.Winget.Source_8wekyb3d8bbwe/ffmpeg-8.0-full_build/bin/ffmpeg.exe'); }
if (id === 'DenoLand.Deno') put(w + '/Links/deno.exe');
console.log('Successfully installed');
`);
  await fs.writeFile(path.join(tmp, 'winget.cmd'), `@"${process.execPath}" "%~dp0winget.mjs" %*\r\n`);
  env = {
    ...process.env, ARROW_SETUP_DIR: dir, ARROW_SETUP_PROGRAMS: path.join(tmp, 'start'), ARROW_SETUP_DESKTOP: path.join(tmp, 'desktop'),
    ARROW_SETUP_STARTUP: path.join(tmp, 'startup'), FASTDL_STARTUP: path.join(tmp, 'startup'), ARROW_SETUP_REGKEY: key,
    ARROW_SETUP_WINGET: path.join(tmp, 'winget.cmd'), FAKE_WINGET_LOG: path.join(tmp, 'winget.log'), FASTDL_PORT: String(await freePort()),
    LOCALAPPDATA: path.join(tmp, 'local'), // no tools found there, until the stand-in puts them
    PATH: [path.join(process.env.SystemRoot, 'System32'), process.env.SystemRoot].join(';'), FASTDL_YTDLP: '', FASTDL_FFMPEG: '',
  };
});

after(async () => {
  engine?.kill();
  if (key) spawnSync('reg.exe', ['delete', 'HKCU\\' + key, '/f'], { windowsHide: true });
  if (tmp) await fs.rm(tmp, { recursive: true, force: true, maxRetries: 5, retryDelay: 300 });
});

test('build-installer writes the setup and its sha256 file', win, () => {
  const hash = crypto.createHash('sha256').update(readFileSync(setup)).digest('hex');
  assert.equal(readFileSync(setup + '.sha256', 'utf8'), `${hash}  ArrowSetup.exe`);
});

test('install: files, tools through winget, shortcuts, Settings > Apps; the engine runs from the install folder', win, async () => {
  const r = run(setup, ['--quiet']);
  assert.equal(r.status, 0, r.stderr);
  for (const f of ['Arrow.exe', 'Arrow Engine.exe', 'Uninstall Arrow.exe', ...engineFiles(), 'package.json', 'brand.json', 'assets/logo.ico',
    'assets/types/video.svg', 'assets/icons/active.svg', 'extension/manifest.json', 'extension/icons/icon-128.png', 'LICENSE', 'README.txt', 'licenses/node-LICENSE.txt'])
    assert.ok(existsSync(path.join(dir, f)), f);
  const all = readdirSync(dir, { recursive: true }).map(f => f.replaceAll('\\', '/'));
  assert.deepEqual(all.filter(f => /\.test\.js$|^app\/|^fixtures|^docs|^\.scratch|state\.json|installed\.json|\.cs$/.test(f)), []);
  assert.equal(readFileSync(path.join(tmp, 'winget.log'), 'utf8'), 'yt-dlp.yt-dlp\n'); // it brings FFmpeg and Deno
  assert.deepEqual(links().map(shortcut), [`${dir}\\Arrow.exe|`, `${dir}\\Arrow.exe|`, `${dir}\\Arrow.exe|--tray`]);
  const reg = regQuery().stdout;
  for (const v of ['DisplayName    REG_SZ    Arrow', 'DisplayVersion    REG_SZ    1.0.0', 'Publisher    REG_SZ    1949-2005', `InstallLocation    REG_SZ    ${dir}`,
    `UninstallString    REG_SZ    "${dir}\\Uninstall Arrow.exe"`, `DisplayIcon    REG_SZ    ${dir}\\Arrow.exe`, 'NoModify    REG_DWORD    0x1', 'NoRepair    REG_DWORD    0x1'])
    assert.ok(reg.includes(v), v + '\n' + reg);
  assert.ok(Number(/EstimatedSize\s+REG_DWORD\s+(0x[0-9a-f]+)/.exec(reg)[1]) > 30000, 'EstimatedSize in KB');

  // the engine exe loads desktop.js and its modules from the install folder; the app finds it
  engine = spawn(path.join(dir, 'Arrow Engine.exe'), [], { env, stdio: 'ignore' });
  for (const t = Date.now(); ; await sleep(100)) {
    try { if ((await fetch(`http://127.0.0.1:${env.FASTDL_PORT}/api/ping`)).ok) break; } catch {}
    if (Date.now() - t > 15000) throw new Error('the installed engine did not start');
  }
  const check = run(path.join(dir, 'Arrow.exe'), ['--check', '--no-engine']);
  assert.equal(check.status, 0, check.stdout + check.stderr);
  assert.match(check.stdout, /engine ok, 0 downloads/);
});

test('reinstall: refused while Arrow runs, --quit-arrow quits it cleanly; state.json is kept', win, async () => {
  assert.equal(run(setup, ['--quiet']).status, 2, 'Arrow is running');
  const r = run(setup, ['--quiet', '--quit-arrow']);
  assert.equal(r.status, 0, r.stderr);
  const code = engine.exitCode ?? await Promise.race([new Promise(r => engine.once('exit', r)), sleep(5000).then(() => 'still running')]);
  assert.equal(code, 0, 'the engine quit cleanly');
  const state = readFileSync(path.join(dir, 'state.json'), 'utf8'); // saved by the engine on quit
  await fs.writeFile(path.join(dir, 'state.json'), state.replace('{', '{"marker":1,'));
  assert.equal(run(setup, ['--quiet', '--no-desktop']).status, 0);
  assert.match(readFileSync(path.join(dir, 'state.json'), 'utf8'), /"marker":1/);
  assert.ok(!existsSync(links()[1]), 'the desktop shortcut went with --no-desktop');
});

test('tools: a failing or missing winget still installs Arrow (exit 3)', win, async () => {
  await fs.rm(path.join(tmp, 'local'), { recursive: true, force: true });
  assert.equal(run(setup, ['--quiet'], { FAKE_WINGET: 'fail' }).status, 3);
  assert.equal(run(setup, ['--quiet'], { ARROW_SETUP_WINGET: path.join(tmp, 'no-winget.exe') }).status, 3);
  assert.ok(existsSync(path.join(dir, 'Arrow.exe')));
});

test('uninstall: shortcuts, files and the key go; state.json and downloads stay, unless asked', win, async () => {
  const inside = path.join(dir, 'Downloads', 'video.mp4'); // a download folder inside the install folder
  await fs.mkdir(path.dirname(inside), { recursive: true });
  await fs.writeFile(inside, 'mine');
  const r = run(path.join(dir, 'Uninstall Arrow.exe'), ['--quiet']);
  assert.equal(r.status, 0, r.stderr);
  await sleep(4000); // cmd deletes the uninstaller once it has exited
  assert.deepEqual(links().filter(existsSync), []);
  assert.notEqual(regQuery().status, 0, 'the Uninstall key is gone');
  assert.deepEqual(readdirSync(dir, { recursive: true }).map(f => f.replaceAll('\\', '/')).sort(), ['Downloads', 'Downloads/video.mp4', 'state.json']);

  assert.equal(run(setup, ['--quiet', '--no-tools']).status, 0);
  assert.equal(run(setup, ['--quiet', '--uninstall', '--remove-data']).status, 0);
  assert.deepEqual(readdirSync(dir, { recursive: true }).map(f => f.replaceAll('\\', '/')).sort(), ['Downloads', 'Downloads/video.mp4']);
  assert.equal(readFileSync(inside, 'utf8'), 'mine');
});
