import { test } from 'node:test';
import assert from 'node:assert/strict';
import fs from 'node:fs/promises';
import { existsSync, readFileSync, writeFileSync } from 'node:fs';
import os from 'node:os';
import path from 'node:path';
import net from 'node:net';
import { spawn } from 'node:child_process';
import { fileURLToPath } from 'node:url';
import { log, MAX } from './log.js';

const here = path.dirname(fileURLToPath(import.meta.url));
const sleep = ms => new Promise(r => setTimeout(r, ms));

test('log: one line per entry with time and source; more lines indent', async () => {
  const dir = await fs.mkdtemp(path.join(os.tmpdir(), 'glide-log-'));
  const file = path.join(dir, 'log.txt');
  log(file, 'engine', 'started');
  log(file, 'app', 'Error: boom\n   at x');
  const lines = readFileSync(file, 'utf8').split('\n');
  assert.match(lines[0], /^\d{4}-\d\d-\d\dT\d\d:\d\d:\d\d\.\d{3}Z \[engine\] started$/);
  assert.match(lines[1], / \[app\] Error: boom$/);
  assert.equal(lines[2], '       at x');
  await fs.rm(dir, { recursive: true, force: true });
});

test('log: past 1 MB the oldest half goes, at a line start', async () => {
  const dir = await fs.mkdtemp(path.join(os.tmpdir(), 'glide-log-'));
  const file = path.join(dir, 'log.txt');
  writeFileSync(file, Array.from({ length: 20000 }, (_, i) => `old line ${i} ${'x'.repeat(50)}`).join('\n') + '\n');
  log(file, 'engine', 'newest');
  const text = readFileSync(file, 'utf8');
  assert.ok(text.length <= MAX / 2 + 100, `kept ${text.length}`);
  assert.match(text, /^old line \d+ x+\n/);
  assert.match(text, /\[engine\] newest\n$/);
  await fs.rm(dir, { recursive: true, force: true });
});

test('log: an unwritable file is ignored', () => {
  assert.doesNotThrow(() => log(path.join(os.tmpdir(), 'no-such-dir-glide', 'x', 'log.txt'), 'engine', 'lost'));
});

test('engine: an uncaught exception lands in log.txt next to state.json, which is saved; exit code 1', async () => {
  const dir = await fs.mkdtemp(path.join(os.tmpdir(), 'glide-crash-'));
  const probe = net.createServer();
  await new Promise(r => probe.listen(0, '127.0.0.1', r));
  const port = probe.address().port;
  await new Promise(r => probe.close(r));
  const child = spawn(process.execPath, [path.join(here, 'server.js')], {
    env: { ...process.env, FASTDL_PORT: String(port), FASTDL_STATE: path.join(dir, 'state.json'), FASTDL_STARTUP: dir, FASTDL_CRASH: '1' },
    stdio: 'ignore',
  });
  const code = await new Promise(r => child.on('exit', r));
  assert.equal(code, 1);
  const text = readFileSync(path.join(dir, 'log.txt'), 'utf8');
  assert.match(text, /\[engine\] started on port \d+/);
  assert.match(text, /\[engine\] crash: Error: FASTDL_CRASH/);
  for (let t = Date.now(); !existsSync(path.join(dir, 'state.json')) && Date.now() - t < 2000; await sleep(50));
  assert.ok(existsSync(path.join(dir, 'state.json')), 'state.json saved');
  await fs.rm(dir, { recursive: true, force: true, maxRetries: 3 });
});
