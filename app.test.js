import { test, after } from 'node:test';
import assert from 'node:assert/strict';
import net from 'node:net';
import fs from 'node:fs/promises';
import { existsSync, readFileSync } from 'node:fs';
import os from 'node:os';
import path from 'node:path';
import { spawn, spawnSync } from 'node:child_process';
import { fileURLToPath } from 'node:url';
import { compileApp, compileTests } from './app/build.js';
import { resources } from './install.js';

// The native app (app/*.cs) is compiled with Windows' own C# compiler: Windows only.
const win = { skip: process.platform !== 'win32' };
const repo = path.dirname(fileURLToPath(import.meta.url));
const sleep = ms => new Promise(r => setTimeout(r, ms));
let dir, engine;

const temp = async () => dir ??= await fs.mkdtemp(path.join(os.tmpdir(), 'arrow-app-'));
after(async () => {
  engine?.kill();
  if (dir) await fs.rm(dir, { recursive: true, force: true, maxRetries: 5, retryDelay: 200 });
});

test('the ported helpers pass Tests.cs', win, async () => {
  const exe = path.join(await temp(), 'Tests.exe');
  await compileTests(exe);
  const r = spawnSync(exe, [], { encoding: 'utf8', windowsHide: true, timeout: 60000 });
  assert.equal(r.status, 0, r.stdout + r.stderr);
});

test('the app builds into an exe with the brand icon', win, async () => {
  const exe = path.join(await temp(), 'TestDL.exe');
  await compileApp({ out: exe, name: 'TestDL', version: '1.0.0', icon: path.join(repo, 'assets', 'logo.ico') });
  assert.ok(existsSync(exe));
  assert.ok(resources(readFileSync(exe)).some(r => r.type === 3), 'RT_ICON resources');
});

test('--check reads the state from a running engine', win, async () => {
  const exe = path.join(await temp(), 'TestDL.exe');
  if (!existsSync(exe)) await compileApp({ out: exe, name: 'TestDL', version: '1.0.0', icon: path.join(repo, 'assets', 'logo.ico') });
  await fs.copyFile(path.join(repo, 'brand.json'), path.join(dir, 'brand.json'));
  const probe = net.createServer();
  await new Promise(r => probe.listen(0, '127.0.0.1', r));
  const port = probe.address().port;
  await new Promise(r => probe.close(r));
  // never the user's port 9614, state.json, Startup folder or Save dialog
  const env = { ...process.env, FASTDL_PORT: String(port), FASTDL_STATE: path.join(dir, 'state.json'), FASTDL_STARTUP: dir, FASTDL_BROWSE_STUB: path.join(dir, 'picked') };
  const check = () => spawnSync(exe, ['--check', '--no-engine'], { env, encoding: 'utf8', windowsHide: true, timeout: 30000 });

  const offline = check();
  assert.equal(offline.status, 1, offline.stdout);
  assert.match(offline.stdout, /TestDL's engine is not running\./);

  engine = spawn(process.execPath, [path.join(repo, 'server.js')], { env, stdio: 'ignore' });
  for (const t = Date.now(); ; await sleep(50)) {
    try { if ((await fetch(`http://127.0.0.1:${port}/api/ping`)).ok) break; } catch {}
    if (Date.now() - t > 10000) throw new Error('engine did not start');
  }
  const r = check();
  assert.equal(r.status, 0, r.stdout + r.stderr);
  assert.match(r.stdout, /engine ok, 0 downloads/);
});
