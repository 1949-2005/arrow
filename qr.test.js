// qr.js against a reader written from the standard here: format bits (both copies, BCH-valid), function patterns,
// every Reed-Solomon block's syndromes zero, and the byte-mode text read back.
import { test } from 'node:test';
import assert from 'node:assert/strict';
import { encodeQr } from './qr.js';

// GF(2^8) with the QR polynomial 0x11D, by log tables (the encoder multiplies bit by bit)
const EXP = new Array(512), LOG = new Array(256);
for (let i = 0, x = 1; i < 255; i++) { EXP[i] = x; LOG[x] = i; x <<= 1; if (x & 0x100) x ^= 0x11D; }
for (let i = 255; i < 512; i++) EXP[i] = EXP[i - 255];
const mul = (a, b) => a && b ? EXP[LOG[a] + LOG[b]] : 0;

const BLOCKS_M = [0, 1, 1, 1, 2, 2, 4, 4, 4, 5, 5]; // versions 1-10
const ECC_M = [0, 10, 16, 26, 18, 24, 16, 18, 22, 22, 26];

function read(m) {
  const size = m.length, ver = (size - 17) / 4;
  assert.ok(Number.isInteger(ver) && ver >= 1 && ver <= 10, 'size ' + size);
  // finder patterns in three corners, with their separators
  for (const [cx, cy] of [[3, 3], [size - 4, 3], [3, size - 4]])
    for (let dy = -4; dy <= 4; dy++)
      for (let dx = -4; dx <= 4; dx++) {
        const x = cx + dx, y = cy + dy, d = Math.max(Math.abs(dx), Math.abs(dy));
        if (x >= 0 && y >= 0 && x < size && y < size) assert.equal(m[y][x], d !== 2 && d !== 4, `finder at ${x},${y}`);
      }
  for (let i = 8; i < size - 8; i++) { assert.equal(m[6][i], i % 2 === 0, 'timing row'); assert.equal(m[i][6], i % 2 === 0, 'timing column'); }
  assert.equal(m[size - 8][8], true, 'dark module');
  // format: copy 1 around the top-left finder, copy 2 split between the other two
  const f1 = [], f2 = [];
  for (let i = 0; i <= 5; i++) f1.push(m[i][8]);
  f1.push(m[7][8], m[8][8], m[8][7]);
  for (let i = 9; i < 15; i++) f1.push(m[8][14 - i]);
  for (let i = 0; i < 8; i++) f2.push(m[8][size - 1 - i]);
  for (let i = 8; i < 15; i++) f2.push(m[size - 15 + i][8]);
  const val = bits => bits.reduce((a, b, i) => a | ((b ? 1 : 0) << i), 0);
  assert.equal(val(f1), val(f2), 'both format copies agree');
  const fmt = val(f1) ^ 0x5412;
  let rem = fmt;
  for (let i = 14; i >= 10; i--) if (rem & (1 << i)) rem ^= 0x537 << (i - 10);
  assert.equal(rem, 0, 'format bits are a BCH codeword');
  const level = fmt >>> 13, mask = (fmt >>> 10) & 7;
  assert.equal(level, 0, 'level M');
  // which modules carry data: not finder+separator+format, timing, alignment, dark module, version
  const fn = Array.from({ length: size }, () => new Array(size).fill(false));
  const mark = (x, y) => { if (x >= 0 && y >= 0 && x < size && y < size) fn[y][x] = true; };
  for (let y = 0; y < 9; y++) for (let x = 0; x < 9; x++) mark(x, y);
  for (let y = 0; y < 9; y++) for (let x = size - 8; x < size; x++) mark(x, y);
  for (let y = size - 8; y < size; y++) for (let x = 0; x < 9; x++) mark(x, y);
  for (let i = 0; i < size; i++) { mark(6, i); mark(i, 6); }
  if (ver >= 2) {
    const n = Math.floor(ver / 7) + 2, last = size - 7, step = n === 2 ? last - 6 : Math.ceil((last - 6) / (n - 1) / 2) * 2;
    const pos = [6]; for (let i = n - 2; i >= 0; i--) pos.splice(1, 0, last - (n - 2 - i) * step);
    const uniq = [...new Set(pos)].sort((a, b) => a - b);
    for (const ax of uniq) for (const ay of uniq) {
      if ((ax === 6 && ay === 6) || (ax === 6 && ay === last) || (ax === last && ay === 6)) continue;
      for (let dy = -2; dy <= 2; dy++) for (let dx = -2; dx <= 2; dx++) mark(ax + dx, ay + dy);
    }
  }
  if (ver >= 7) for (let i = 0; i < 6; i++) for (let j = 0; j < 3; j++) { mark(size - 11 + j, i); mark(i, size - 11 + j); }
  const masked = (x, y) => [
    (x + y) % 2 === 0, y % 2 === 0, x % 3 === 0, (x + y) % 3 === 0,
    (Math.floor(y / 2) + Math.floor(x / 3)) % 2 === 0, (x * y) % 2 + (x * y) % 3 === 0,
    ((x * y) % 2 + (x * y) % 3) % 2 === 0, ((x + y) % 2 + (x * y) % 3) % 2 === 0][mask];
  // the bits, column pairs from the right, up then down, skipping column 6
  const bits = [];
  let up = true;
  for (let col = size - 1; col > 0; col -= 2) {
    if (col === 6) col--;
    for (let k = 0; k < size; k++) {
      const y = up ? size - 1 - k : k;
      for (const x of [col, col - 1]) if (!fn[y][x]) bits.push(m[y][x] !== masked(x, y));
    }
    up = !up;
  }
  const total = Math.floor(bits.length / 8), words = [];
  for (let i = 0; i < total; i++) words.push(bits.slice(i * 8, i * 8 + 8).reduce((a, b) => (a << 1) | (b ? 1 : 0), 0));
  // de-interleave: short blocks first, then long ones one data codeword longer
  const nb = BLOCKS_M[ver], ecc = ECC_M[ver], short = Math.floor(total / nb), longCount = total % nb;
  const blocks = Array.from({ length: nb }, (_, i) => ({ data: [], ecc: [], len: short - ecc + (i >= nb - longCount ? 1 : 0) }));
  let k = 0;
  for (let i = 0; i < short - ecc + 1; i++) for (const b of blocks) if (i < b.len) b.data.push(words[k++]);
  for (let i = 0; i < ecc; i++) for (const b of blocks) b.ecc.push(words[k++]);
  // every block: all syndromes zero (no errors), the generator's roots being 2^0 .. 2^(ecc-1)
  for (const b of blocks) {
    const cw = b.data.concat(b.ecc);
    for (let r = 0; r < ecc; r++) {
      let s = 0;
      for (const c of cw) s = mul(s, EXP[r]) ^ c;
      assert.equal(s, 0, 'syndrome ' + r);
    }
  }
  // byte mode: 0100, the count, the bytes
  const data = blocks.flatMap(b => b.data);
  const stream = data.flatMap(w => [7, 6, 5, 4, 3, 2, 1, 0].map(i => (w >>> i) & 1));
  let p = 0;
  const take = n => { let v = 0; for (let i = 0; i < n; i++) v = (v << 1) | stream[p++]; return v; };
  assert.equal(take(4), 4, 'byte mode');
  const len = take(ver <= 9 ? 8 : 16);
  return Buffer.from(Array.from({ length: len }, () => take(8))).toString('utf8');
}

test('QR codes read back: the pairing link, short and long text, accents, versions 1 to 10', () => {
  for (const text of ['http://192.168.1.23:9615/pair#Ab3dEf6hIj9kLm2nOp5qRs', 'HELLO', 'x', 'é ü ✓ Glide', 'x'.repeat(150) + ' version 7+', 'y'.repeat(200)]) {
    const m = encodeQr(text);
    assert.equal(read(m), text, text.slice(0, 30));
  }
});

test('QR codes: the smallest version that fits', () => {
  assert.equal(encodeQr('x'.repeat(14)).length, 21); // version 1-M holds 14 bytes
  assert.equal(encodeQr('x'.repeat(15)).length, 25);
  assert.equal(encodeQr('http://192.168.1.23:9615/pair#Ab3dEf6hIj9kLm2nOp5qRs').length, 33); // version 4: 62 bytes
});
