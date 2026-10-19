import { test } from 'node:test';
import assert from 'node:assert/strict';
import fs from 'node:fs';
import { Manager } from './engine.js';

// ui.html keeps its DOM-free logic in <script id="helpers">: run exactly that code.
const html = fs.readFileSync(new URL('./ui.html', import.meta.url), 'utf8');
const helpers = /<script id="helpers">([\s\S]*?)<\/script>/.exec(html)[1];
const { expandBatch, clipboardLink } = new Function(helpers + '\nreturn { expandBatch, clipboardLink };')();

test('batch links: a numeric range becomes every link in it, zero padding kept', () => {
  assert.deepEqual(expandBatch(['https://s.com/img[001-003].jpg']), ['https://s.com/img001.jpg', 'https://s.com/img002.jpg', 'https://s.com/img003.jpg']);
  assert.deepEqual(expandBatch(['https://s.com/[8-10].zip']), ['https://s.com/8.zip', 'https://s.com/9.zip', 'https://s.com/10.zip']);
  assert.deepEqual(expandBatch(['https://s.com/p[099-101].png']), ['https://s.com/p099.png', 'https://s.com/p100.png', 'https://s.com/p101.png']);
  assert.deepEqual(expandBatch(['https://s.com/[03-01].jpg']), ['https://s.com/03.jpg', 'https://s.com/02.jpg', 'https://s.com/01.jpg']);
  const imgs = expandBatch(['https://s.com/img[001-120].jpg']);
  assert.equal(imgs.length, 120);
  assert.equal(imgs.at(-1), 'https://s.com/img120.jpg');
});

test('batch links: other links stay as they are, several ranges multiply', () => {
  assert.deepEqual(expandBatch(['https://s.com/a.zip', 'https://s.com/[1-2]/[01-02].jpg']),
    ['https://s.com/a.zip', 'https://s.com/1/01.jpg', 'https://s.com/1/02.jpg', 'https://s.com/2/01.jpg', 'https://s.com/2/02.jpg']);
  const plain = ['http://[::1]:8080/f.zip', 'https://s.com/[a-z].zip', 'https://s.com/[5].zip'];
  assert.deepEqual(expandBatch(plain), plain);
});

test('batch links: at most 1000, refused before more are built', () => {
  assert.equal(expandBatch(['https://s.com/[0001-1000].jpg']).length, 1000);
  for (const links of [
    ['https://s.com/[1-1001].jpg'],
    ['https://s.com/a.zip', 'https://s.com/[1-1000].jpg'],
    ['https://s.com/[1-40]/[1-40].jpg'],
    ['https://s.com/[1-99999999999999999999].jpg'],
  ]) assert.throws(() => expandBatch(links), /at most 1000 links/, links.join(' '));
});

test('clipboard: only a lone http(s) link to a known file type is offered', () => {
  const { exts } = new Manager('unused.json').view(); // what /api/state sends the page
  assert.ok(exts.includes('zip') && exts.includes('mp4') && exts.includes('exe'));
  assert.equal(clipboardLink('  https://x.com/a/File.ZIP \n', exts), 'https://x.com/a/File.ZIP');
  assert.equal(clipboardLink('HTTPS://X.com/v.mp4?token=1#t', exts), 'https://x.com/v.mp4?token=1#t');
  for (const text of [
    'https://x.com/page.html', 'https://x.com/', 'https://x.com/zip', 'https://x.com/a.zip/', 'ftp://x.com/a.zip',
    'see https://x.com/a.zip', 'https://x.com/a.zip https://x.com/b.zip', 'C:\\Downloads\\a.zip', '', 'https://[bad/a.zip',
  ]) assert.equal(clipboardLink(text, exts), null, JSON.stringify(text));
});
