import { test } from 'node:test';
import assert from 'node:assert/strict';
import http from 'node:http';
import fs from 'node:fs/promises';
import os from 'node:os';
import path from 'node:path';
import { execFile } from 'node:child_process';
import { fileURLToPath } from 'node:url';
import { readBrand } from './desktop.js';

const desktopJs = fileURLToPath(new URL('./desktop.js', import.meta.url));

test('brand.json: the name must be safe as a file name and as window text', async () => {
  const dir = await fs.mkdtemp(path.join(os.tmpdir(), 'fastdl-brand-'));
  const name = async n => { await fs.writeFile(path.join(dir, 'brand.json'), JSON.stringify({ name: n, icon: 'logo.ico' })); return readBrand(dir).name; };
  try {
    for (const ok of ['FastDL', 'Téléchargeur Pro', 'Fast-DL (beta) 2']) assert.equal(await name(ok), ok);
    for (const bad of ['', ' Lead', 'Trail.', 'a/b', 'x"y', '<b>', "it's", 'x'.repeat(41), undefined]) await assert.rejects(name(bad), /brand\.json/, String(bad));
  } finally {
    await fs.rm(dir, { recursive: true, force: true });
  }
});

// `node desktop.js` with a stand-in for whatever already answers on its port
async function startWith(answer) {
  const server = http.createServer((req, res) => res.end(answer));
  await new Promise(r => server.listen(0, '127.0.0.1', r));
  const dir = await fs.mkdtemp(path.join(os.tmpdir(), 'fastdl-desktop-'));
  try {
    const env = { ...process.env, FASTDL_PORT: String(server.address().port), FASTDL_STATE: path.join(dir, 'state.json'), FASTDL_STARTUP: dir };
    return await new Promise(resolve => execFile(process.execPath, [desktopJs], { env, timeout: 15000 }, (e, stdout, stderr) => resolve({ code: e?.code ?? 0, stderr })));
  } finally {
    server.close();
    await fs.rm(dir, { recursive: true, force: true });
  }
}

test('a second start leaves the running engine alone', async () => {
  assert.deepEqual(await startWith(JSON.stringify({ app: 'fastdl' })), { code: 0, stderr: '' });
});

test('another program on the port: a clear error, not a second server', async () => {
  const { code, stderr } = await startWith('something else');
  assert.equal(code, 1);
  assert.match(stderr, /Port \d+ is used by another program/);
});
