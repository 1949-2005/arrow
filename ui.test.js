import { test } from 'node:test';
import assert from 'node:assert/strict';
import fs from 'node:fs';
import { Manager } from './engine.js';

// ui.html keeps its DOM-free logic in <script id="helpers">: run exactly that code.
const html = fs.readFileSync(new URL('./ui.html', import.meta.url), 'utf8');
const helpers = /<script id="helpers">([\s\S]*?)<\/script>/.exec(html)[1];
const { expandBatch, clipboardLink, category, dateLabel, selectRows, keyTarget, selectAll } = new Function(helpers + '\nreturn { expandBatch, clipboardLink, category, dateLabel, selectRows, keyTarget, selectAll };')();
// confirm.html (Download File Info) and progress.html keep theirs the same way
const helpersOf = (file, names) => new Function(/<script id="helpers">([\s\S]*?)<\/script>/.exec(fs.readFileSync(new URL(file, import.meta.url), 'utf8'))[1] + `\nreturn { ${names} };`)();
const confirmJs = helpersOf('./confirm.html', 'folderFor, splitPath, joinPath, withExt, size, subtitle, formatText, formatExt, formatCategory');
const progressJs = helpersOf('./progress.html', 'size, time, total, fraction, heading, statusText, pieces, canPause');

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
  assert.equal(cat({ kind: 'video', quality: 'best', format: 'm4a:140' }), 'Music');
  assert.equal(cat({ kind: 'video', quality: 'best', format: 'mp3' }), 'Music');
  assert.equal(cat({ kind: 'video', quality: 'mp3', format: 'webm:248+251' }), 'Video', 'the format key wins');
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

const V = ['a', 'b', 'c', 'd', 'e'];
const sel = (selected, anchor) => ({ selected: new Set(selected), anchor });
const ids = s => [...s.selected].sort();

test('selection: only replaces the selection and moves the anchor', () => {
  const r = selectRows(sel(['a', 'b'], 'a'), V, 'd', 'only');
  assert.deepEqual(ids(r), ['d']);
  assert.equal(r.anchor, 'd');
  assert.equal(r.cursor, 'd');
});

test('selection: toggle adds or removes one row and moves the anchor', () => {
  const added = selectRows(sel(['a'], 'a'), V, 'c', 'toggle');
  assert.deepEqual(ids(added), ['a', 'c']);
  assert.equal(added.anchor, 'c');
  const removed = selectRows(sel(['a', 'c'], 'c'), V, 'a', 'toggle');
  assert.deepEqual(ids(removed), ['c']);
  assert.deepEqual([removed.anchor, removed.cursor], ['a', 'a']);
});

test('selection: range runs from the anchor, either way, and keeps the anchor', () => {
  const fwd = selectRows(sel(['a'], 'b'), V, 'd', 'range');
  assert.deepEqual(ids(fwd), ['b', 'c', 'd']);
  assert.deepEqual([fwd.anchor, fwd.cursor], ['b', 'd']);
  const back = selectRows(sel([], 'd'), V, 'a', 'range');
  assert.deepEqual(ids(back), ['a', 'b', 'c', 'd']);
  assert.deepEqual([back.anchor, back.cursor], ['d', 'a']);
});

test('selection: a range with a hidden anchor or a hidden target acts like only', () => {
  for (const [anchor, id] of [['x', 'c'], [null, 'c'], ['b', 'x']]) {
    const r = selectRows(sel(['a', 'e'], anchor), V, id, 'range');
    assert.deepEqual(ids(r), [id], `${anchor} → ${id}`);
    assert.deepEqual([r.anchor, r.cursor], [id, id]);
  }
});

test('selection: move changes the cursor only', () => {
  const r = selectRows(sel(['a', 'b'], 'a'), V, 'e', 'move');
  assert.deepEqual(ids(r), ['a', 'b']);
  assert.deepEqual([r.anchor, r.cursor], ['a', 'e']);
});

test('selection: inputs are never mutated', () => {
  for (const how of ['only', 'toggle', 'range', 'move']) {
    const state = sel(['a', 'b'], 'b'), visible = [...V];
    const r = selectRows(state, visible, 'd', how);
    assert.deepEqual([...state.selected], ['a', 'b'], how);
    assert.equal(state.anchor, 'b', how);
    assert.deepEqual(visible, V, how);
    assert.notEqual(r.selected, state.selected, how);
  }
  const before = new Set(['x']);
  assert.notEqual(selectAll(before, V), before);
  assert.deepEqual([...before], ['x']);
});

test('keyTarget: moves within the list, clamped at both ends', () => {
  assert.equal(keyTarget('ArrowDown', V, 'b'), 'c');
  assert.equal(keyTarget('ArrowUp', V, 'b'), 'a');
  assert.equal(keyTarget('ArrowDown', V, 'e'), 'e');
  assert.equal(keyTarget('ArrowUp', V, 'a'), 'a');
  assert.equal(keyTarget('Home', V, 'c'), 'a');
  assert.equal(keyTarget('End', V, 'c'), 'e');
});

test('keyTarget: no cursor starts at the first row, other keys and an empty list give null', () => {
  for (const key of ['ArrowDown', 'ArrowUp', 'Home']) assert.equal(keyTarget(key, V, null), 'a', key);
  assert.equal(keyTarget('End', V, null), 'e');
  for (const key of [' ', 'Enter', 'Delete', 'a', 'ArrowLeft']) assert.equal(keyTarget(key, V, 'b'), null, key);
  for (const key of ['ArrowDown', 'ArrowUp', 'Home', 'End']) assert.equal(keyTarget(key, [], null), null, key);
});

test('selectAll: keeps the selection and adds only the visible rows', () => {
  assert.deepEqual([...selectAll(new Set(['b']), ['a', 'b', 'c'])].sort(), ['a', 'b', 'c']);
  assert.deepEqual([...selectAll(new Set(['hidden']), ['a'])].sort(), ['a', 'hidden']);
  assert.deepEqual([...selectAll(new Set(), [])], []);
});

test('Download File Info: Save As folder follows the category, Other and categories off use the folder itself', () => {
  const { folderFor, joinPath, splitPath, withExt } = confirmJs;
  const on = { dir: 'C:\\Users\\PC\\Downloads', categories: true };
  assert.equal(folderFor(on, 'Video'), 'C:\\Users\\PC\\Downloads\\Video');
  assert.equal(folderFor(on, 'Other'), 'C:\\Users\\PC\\Downloads');
  assert.equal(folderFor({ ...on, categories: false }, 'Music'), 'C:\\Users\\PC\\Downloads');
  assert.equal(folderFor({ dir: 'D:\\', categories: true }, 'Programs'), 'D:\\Programs');
  assert.equal(joinPath(folderFor(on, 'Compressed'), 'a.iso'), 'C:\\Users\\PC\\Downloads\\Compressed\\a.iso');
  assert.equal(joinPath('D:\\', 'a.iso'), 'D:\\a.iso');
  assert.deepEqual(splitPath('C:\\a\\b c\\Me at the zoo.mp4'), ['C:\\a\\b c', 'Me at the zoo.mp4']);
  assert.deepEqual(splitPath('C:/a/f.zip'), ['C:/a', 'f.zip']);
  assert.deepEqual(splitPath('f.zip'), ['', 'f.zip']);
  assert.equal(withExt('Me at the zoo.mp4', '.mp3'), 'Me at the zoo.mp3');
  assert.equal(withExt('clip', '.mp4'), 'clip.mp4');
  assert.equal(withExt('v1.2 final.mp4', ''), 'v1.2 final');
});

test('Download File Info: Format option text, extension and category of a key', () => {
  const { formatText, formatExt, formatCategory } = confirmJs;
  assert.equal(formatText({ key: 'mp4:137+140', group: 'MP4', label: '1080p', tag: 'HD', size: 126353408 }), 'MP4 · 1080p · 120.5 MB');
  assert.equal(formatText({ key: 'webm:303+251', group: 'WEBM', label: '1080p60', size: 102860390 }), 'WEBM · 1080p60 · 98.1 MB');
  assert.equal(formatText({ key: 'm4a:140', group: 'Audio', label: 'M4A', size: 3565158 }), 'M4A · audio · 3.4 MB');
  assert.equal(formatText({ key: 'mp3', group: 'Audio', label: 'MP3', size: 456000 }), 'MP3 · audio · 445.3 KB');
  assert.deepEqual(['mp4:137+140', 'webm:248+251', 'm4a:140-drc', 'mp3', 'best'].map(formatExt), ['.mp4', '.webm', '.m4a', '.mp3', '.mp4']);
  assert.deepEqual(['mp4:137+140', 'webm:248+251', 'm4a:140', 'mp3', 'best'].map(formatCategory), ['Video', 'Video', 'Music', 'Music', 'Video']);
});

test('Download File Info: header line and size text', () => {
  const { subtitle, size } = confirmJs;
  assert.equal(subtitle({ kind: 'video', url: 'https://www.youtube.com/watch?v=jNQXAC9IVRw' }, 'Me at the zoo.mp4'), 'Me at the zoo · youtube.com');
  assert.equal(subtitle({ kind: 'http', url: 'https://releases.ubuntu.com/24.04/u.iso' }, 'u.iso'), 'u.iso · releases.ubuntu.com');
  assert.equal(subtitle({ kind: 'http', url: 'bad' }, 'u.iso'), 'u.iso');
  assert.equal(size(6291456000), '5.86 GB');
  assert.equal(size(10), '10 B');
});

test('progress window: header, status line and bar pieces', () => {
  const { heading, statusText, pieces, fraction, canPause, size, time } = progressJs;
  const http = { kind: 'http', status: 'downloading', size: 1000, got: 452, connections: 8, segments: [[0, 500, 300], [500, 1000, 652]] };
  assert.equal(heading(http), 'Downloading · 45%');
  assert.equal(statusText(http), 'Receiving data… (8 connections)');
  assert.equal(statusText({ ...http, connections: 1 }), 'Receiving data… (1 connection)');
  assert.deepEqual(pieces(http), [[0, 0.3], [0.5, 0.152]]);
  assert.equal(heading({ ...http, status: 'paused' }), 'Paused · 45%');
  assert.equal(statusText({ ...http, status: 'paused' }), 'Paused');
  assert.equal(statusText({ ...http, status: 'queued' }), 'Queued');
  assert.equal(heading({ ...http, status: 'queued' }), 'Queued');
  assert.equal(heading({ ...http, status: 'error', error: 'HTTP 404' }), 'Error');
  assert.equal(statusText({ ...http, status: 'error', error: 'HTTP 404' }), 'Error: HTTP 404');
  assert.equal(heading({ ...http, status: 'done' }), 'Download complete');
  assert.equal(fraction({ ...http, status: 'done' }), 1);
  const video = { kind: 'video', status: 'downloading', size: 200, got: 50, phase: 'audio' };
  assert.equal(statusText(video), 'Downloading audio');
  assert.equal(statusText({ ...video, phase: 'converting' }), 'Converting to MP3');
  assert.equal(heading({ ...video, phase: 'merging' }), 'Merging');
  assert.deepEqual(pieces(video), [[0, 0.25]], 'one fill for video');
  const hls = { kind: 'hls', status: 'downloading', got: 300, hls: { done: 3, total: 6, bytes: 300 }, connections: 0 };
  assert.equal(statusText(hls), 'Receiving data…');
  assert.deepEqual(pieces(hls), [[0, 0.5]]);
  assert.ok(canPause(http) && canPause({ status: 'queued' }) && !canPause({ status: 'paused' }) && !canPause({ status: 'error' }));
  assert.equal(size(null), 'Unknown');
  assert.equal(time(190), '3 min 10 s');
});
