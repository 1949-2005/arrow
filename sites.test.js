import { test } from 'node:test';
import assert from 'node:assert/strict';
import fs from 'node:fs/promises';
import os from 'node:os';
import path from 'node:path';
import { Manager, cleanSites, siteFor } from './engine.js';

test('sites: hosts cleaned (scheme, path, port, *. and case dropped), duplicates and junk out, numbers clamped', () => {
  assert.deepEqual(cleanSites([
    { host: 'https://Files.Example.com:8443/path', connections: 4, limitKB: 500 },
    { host: '*.cdn.net', connections: 99, limitKB: -5 },
    { host: 'files.example.com', connections: 2 },
    { host: 'bad host', connections: 2 },
    { host: '', connections: 2 },
    null,
  ]), [
    { host: 'files.example.com', connections: 4, limitKB: 500 },
    { host: 'cdn.net', connections: 32, limitKB: 0 },
  ]);
  assert.deepEqual(cleanSites('nope'), []);
});

test('sites: a host matches itself and its subdomains, the longest entry wins', () => {
  const sites = cleanSites([{ host: 'example.com', connections: 2 }, { host: 'dl.example.com', connections: 6 }]);
  assert.equal(siteFor(sites, 'example.com').connections, 2);
  assert.equal(siteFor(sites, 'www.example.com').connections, 2);
  assert.equal(siteFor(sites, 'a.dl.example.com').connections, 6);
  assert.equal(siteFor(sites, 'notexample.com'), null);
  assert.equal(siteFor([], 'example.com'), null);
});

test('sites: saved with the settings; a new download from the site gets its speed limit', async () => {
  const dir = await fs.mkdtemp(path.join(os.tmpdir(), 'arrow-sites-'));
  const mgr = new Manager(path.join(dir, 'state.json'));
  await mgr.load();
  mgr.update({ sites: [{ host: 'slow.example.com', connections: 1, limitKB: 300 }] });
  assert.deepEqual(mgr.settings.sites, [{ host: 'slow.example.com', connections: 1, limitKB: 300 }]);
  const d = mgr.add({ url: 'http://slow.example.com/a.zip', scheduled: true });
  assert.equal(d.limitKB, 300);
  assert.equal(mgr.add({ url: 'http://other.example.com/a.zip', scheduled: true }).limitKB, undefined);
  mgr.saveSync();
  const again = new Manager(path.join(dir, 'state.json'));
  await again.load();
  assert.deepEqual(again.settings.sites, mgr.settings.sites);
  await fs.rm(dir, { recursive: true, force: true });
});
