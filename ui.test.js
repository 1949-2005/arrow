import { test } from 'node:test';
import assert from 'node:assert/strict';
import fs from 'node:fs';
import { Manager } from './engine.js';

// ui.html keeps its DOM-free logic in <script id="helpers">: run exactly that code.
const html = fs.readFileSync(new URL('./ui.html', import.meta.url), 'utf8');
const helpers = /<script id="helpers">([\s\S]*?)<\/script>/.exec(html)[1];
const { expandBatch, clipboardLink, category, dateLabel } = new Function(helpers + '\nreturn { expandBatch, clipboardLink, category, dateLabel };')();

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

test('view(): categories and the date each download was added', () => {
  const mgr = new Manager('unused.json');
  const before = Date.now();
  const d = mgr.add({ url: 'https://x.com/a.zip', scheduled: true }); // scheduled: nothing starts
  const v = mgr.view();
  assert.deepEqual(Object.keys(v.categories).sort(), ['Compressed', 'Documents', 'Music', 'Programs', 'Video']);
  assert.deepEqual(Object.values(v.categories).flat(), v.exts);
  const added = v.downloads.find(x => x.id === d.id).added;
  assert.ok(added >= before && added <= Date.now());
});

test('category: by file extension, YouTube by quality, HLS as Video, else Other', () => {
  const { categories } = new Manager('unused.json').view();
  const cat = d => category({ kind: 'http', url: 'https://x.com/', ...d }, categories);
  assert.equal(cat({ url: 'https://x.com/dl/Setup.EXE?v=2#x' }), 'Programs');
  assert.equal(cat({ name: 'report.pdf' }), 'Documents');
  assert.equal(cat({ name: 'old.bin', file: 'C:\Downloads\Compressed\a.tar.gz' }), 'Compressed', 'the file on disk wins');
  assert.equal(cat({ name: 'song.flac' }), 'Music');
  assert.equal(cat({ url: 'https://x.com/download.php?id=4' }), 'Other');
  assert.equal(cat({ name: 'README' }), 'Other');
  assert.equal(cat({ kind: 'video', quality: 'best' }), 'Video');
  assert.equal(cat({ kind: 'video', quality: 'mp3' }), 'Music');
  assert.equal(cat({ kind: 'hls', url: 'https://x.com/live/index.m3u8' }), 'Video');
});

test('date added: today with the time, yesterday, else a short date', () => {
  const now = new Date(2026, 9, 19, 10, 0).getTime();
  assert.equal(dateLabel(new Date(2026, 9, 19, 0, 0).getTime(), now), 'Today 00:00');
  assert.equal(dateLabel(new Date(2026, 9, 19, 9, 5).getTime(), now), 'Today 09:05');
  assert.equal(dateLabel(new Date(2026, 9, 18, 23, 59).getTime(), now), 'Yesterday');
  assert.equal(dateLabel(new Date(2026, 9, 1, 0, 0).getTime(), new Date(2026, 9, 2, 8, 0).getTime()), 'Yesterday');
  assert.equal(dateLabel(new Date(2026, 8, 30, 12, 0).getTime(), new Date(2026, 9, 1, 8, 0).getTime()), 'Yesterday', 'across a month');
  const older = new Date(2026, 9, 17, 12, 0);
  assert.equal(dateLabel(older.getTime(), now), older.toLocaleDateString(undefined, { day: 'numeric', month: 'short' }));
  assert.match(dateLabel(new Date(2025, 9, 17).getTime(), now), /2025/, 'another year shows the year');
  assert.equal(dateLabel(undefined, now), '', 'downloads from before "added" existed');
});
