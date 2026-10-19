import { test } from 'node:test';
import assert from 'node:assert/strict';
import http from 'node:http';
import fs from 'node:fs/promises';
import os from 'node:os';
import path from 'node:path';
import { execFile, execFileSync } from 'node:child_process';
import { fileURLToPath } from 'node:url';
import { browser, readBrand } from './desktop.js';

const desktopJs = fileURLToPath(new URL('./desktop.js', import.meta.url));

test('the window opens in Brave, else Chrome, else Edge, else the default browser', () => {
  const env = { LOCALAPPDATA: 'L:\\local', ProgramFiles: 'P:\\pf', 'ProgramFiles(x86)': 'X:\\pf86' };
  const at = (dir, file) => path.join(dir, file);
  const braveUser = at('L:\\local', 'BraveSoftware\\Brave-Browser\\Application\\brave.exe'), brave = at('P:\\pf', 'BraveSoftware\\Brave-Browser\\Application\\brave.exe');
  const chrome = at('X:\\pf86', 'Google\\Chrome\\Application\\chrome.exe'), edge = at('X:\\pf86', 'Microsoft\\Edge\\Application\\msedge.exe');
  const pick = (...installed) => browser(env, p => installed.includes(p));
  assert.equal(pick(edge, chrome, brave, braveUser), braveUser);
  assert.equal(pick(edge, chrome, brave), brave);
  assert.equal(pick(edge, chrome), chrome);
  assert.equal(pick(edge), edge);
  assert.equal(pick(), null);
});

test('brand.json: the name must be safe as a file name and as page text', async () => {
  const dir = await fs.mkdtemp(path.join(os.tmpdir(), 'fastdl-brand-'));
  const name = async n => { await fs.writeFile(path.join(dir, 'brand.json'), JSON.stringify({ name: n, icon: 'logo.ico' })); return readBrand(dir).name; };
  try {
    for (const ok of ['FastDL', 'Téléchargeur Pro', 'Fast-DL (beta) 2']) assert.equal(await name(ok), ok);
    for (const bad of ['', ' Lead', 'Trail.', 'a/b', 'x"y', '<b>', "it's", 'x'.repeat(41), undefined]) await assert.rejects(name(bad), /brand\.json/, String(bad));
  } finally {
    await fs.rm(dir, { recursive: true, force: true });
  }
});

// `node desktop.js --tray` with a stand-in for whatever already answers on its port
async function startWith(answer) {
  const server = http.createServer((req, res) => res.end(answer));
  await new Promise(r => server.listen(0, '127.0.0.1', r));
  const dir = await fs.mkdtemp(path.join(os.tmpdir(), 'fastdl-desktop-'));
  try {
    const env = { ...process.env, FASTDL_PORT: String(server.address().port), FASTDL_STATE: path.join(dir, 'state.json'), FASTDL_STARTUP: dir };
    return await new Promise(resolve => execFile(process.execPath, [desktopJs, '--tray'], { env, timeout: 15000 }, (e, stdout, stderr) => resolve({ code: e?.code ?? 0, stderr })));
  } finally {
    server.close();
    await fs.rm(dir, { recursive: true, force: true });
  }
}

test('a second start leaves the running app alone (and with --tray shows no window)', async () => {
  assert.deepEqual(await startWith(JSON.stringify({ app: 'fastdl' })), { code: 0, stderr: '' });
});

test('another program on the port: a clear error, not a second server', async () => {
  const { code, stderr } = await startWith('something else');
  assert.equal(code, 1);
  assert.match(stderr, /Port \d+ is used by another program/);
});

test('tray menu: Open, Pause all and Quit call the app', { skip: process.platform !== 'win32' }, async () => {
  const calls = [];
  const server = http.createServer((req, res) => { calls.push(`${req.method} ${req.url}`); res.end('{}'); });
  await new Promise(r => server.listen(0, '127.0.0.1', r));
  const dir = await fs.mkdtemp(path.join(os.tmpdir(), 'fastdl-tray-'));
  try {
    // tray.ps1 with nothing on screen: instead of showing the icon and waiting, click each menu item
    const tray = await fs.readFile(new URL('./tray.ps1', import.meta.url), 'utf8');
    const clicks = tray.replace(/\$tray\.Visible = \$true\r?\n\[System\.Windows\.Forms\.Application\]::Run\(\)/,
      '$menu.Items | Where-Object { $_ -is [System.Windows.Forms.ToolStripMenuItem] } | ForEach-Object { "item: $($_.Text)"; $_.PerformClick() }');
    assert.notEqual(clicks, tray, 'tray.ps1 should end by showing the icon and running the message loop');
    await fs.writeFile(path.join(dir, 'tray.ps1'), clicks);
    const env = { ...process.env, FDL_NAME: 'TestDL', FDL_ICON: path.join(dir, 'none.ico'), FDL_URL: `http://127.0.0.1:${server.address().port}` };
    const out = await new Promise((resolve, reject) => execFile('powershell.exe', ['-NoProfile', '-NonInteractive', '-ExecutionPolicy', 'Bypass', '-File', path.join(dir, 'tray.ps1')],
      { env, timeout: 30000 }, (e, stdout) => e ? reject(e) : resolve(stdout)));
    assert.deepEqual(out.trim().split(/\r?\n/), ['item: Open TestDL', 'item: Pause all', 'item: Quit']);
    assert.deepEqual(calls, ['POST /api/open', 'POST /api/pause-all', 'POST /api/quit']);
  } finally {
    server.close();
    await fs.rm(dir, { recursive: true, force: true });
  }
});

test('tray.ps1 is valid PowerShell', { skip: process.platform !== 'win32' }, () => {
  const check = '$t = $null; $e = $null; [void][System.Management.Automation.Language.Parser]::ParseFile($env:F, [ref]$t, [ref]$e); $e | ForEach-Object { $_.ToString() }';
  const errors = execFileSync('powershell.exe', ['-NoProfile', '-NonInteractive', '-Command', check], { env: { ...process.env, F: fileURLToPath(new URL('./tray.ps1', import.meta.url)) } });
  assert.equal(errors.toString().trim(), '');
});
