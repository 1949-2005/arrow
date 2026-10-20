// Fake yt-dlp for ytdlp.test.js: no network. Prints the lines our --print/--progress-template
// arguments make the real one print. The video ID picks the behaviour:
//   err…  ERROR line, exit 1     live…  filtered out as live, exit 0
//   slow… slow, with a child process (like the PyInstaller launcher), continues its .part files.
//         A fresh run waits 10 s at 50000 bytes, so a pause or remove always lands mid-stream.
import fs from 'node:fs';
import path from 'node:path';
import { spawn } from 'node:child_process';

const args = process.argv.slice(2);
const dir = args[args.indexOf('-P') + 1];
const url = args.at(-1);
const id = new URL(url).searchParams.get('v');
const mp3 = args.includes('-x');
const slow = id.startsWith('slow');
const out = s => process.stdout.write(s + '\n');
const log = s => process.env.FAKE_YTDLP_LOG && fs.appendFileSync(process.env.FAKE_YTDLP_LOG, s + '\n');
const sleep = ms => new Promise(r => setTimeout(r, ms));
const bytes = (seed, from, n) => Buffer.from(Array.from({ length: n }, (_, i) => (from + i + seed) % 251));

log(`run ${process.pid}`);
out(`[youtube] Extracting URL: ${url}`);
if (id.startsWith('err')) {
  process.stderr.write(`ERROR: [youtube] ${id}: Private video. Sign in if you've been granted access to this video\n`);
  process.exitCode = 1;
} else if (id.startsWith('live')) {
  out(`[download] Some stream does not pass filter (!is_live), skipping ..`);
} else {
  // detached: Node would otherwise kill it with us (job object); PyInstaller's child is not tied like that.
  // It inherits our stdout on Windows, so on a normal end it must stop with us, as the launcher's child does.
  const child = slow && spawn(process.execPath, ['-e', 'setTimeout(() => {}, 60000)'], { stdio: 'ignore', detached: true });
  if (child) log(`child ${child.pid}`);
  const fresh = !fs.readdirSync(dir).length; // no .part files to continue
  const streams = mp3 ? [['a1', 'none', 30000, 'webm']] : [['v1', 'avc1', slow ? 200000 : 40000, 'mp4'], ['a1', 'none', slow ? 100000 : 20000, 'm4a']];
  // mp3 prints NA as the expected size, so the engine has to add up the streams
  out(`FDLINFO ${streams.map(s => s[0]).join('+')} ${mp3 ? 'NA' : streams.reduce((a, s) => a + s[2], 0)} "Fake vid\\u00e9o: ${id}"`);
  const files = [];
  for (const [fid, vcodec, size, ext] of streams) {
    const file = path.join(dir, `media.f${fid}.${ext}`), part = file + '.part';
    files.push(file);
    if (fs.existsSync(file)) { out(`[download] ${file} has already been downloaded`); out(`FDLP ${fid} ${vcodec} finished NA ${size}`); continue; }
    let got = fs.existsSync(part) ? fs.statSync(part).size : 0;
    if (got) log(`resume ${fid} ${got}`);
    out(`[download] Destination: ${file}`);
    while (got < size) {
      const n = Math.min(10000, size - got);
      fs.appendFileSync(part, bytes(fid.charCodeAt(0), got, n));
      got += n;
      out(`FDLP ${fid} ${vcodec} downloading ${got} ${size}`);
      // taskkill needs 0.3 s or more (seconds when busy): without the wait the stream could end before the kill lands
      if (slow) await sleep(fresh && got === 50000 ? 10000 : 40);
    }
    fs.renameSync(part, file);
    out(`FDLP ${fid} ${vcodec} finished ${got} ${size}`);
  }
  const final = path.join(dir, mp3 ? 'media.mp3' : 'media.mp4');
  out(`FDLPP ${mp3 ? 'ExtractAudio' : 'Merger'}`);
  fs.writeFileSync(final, Buffer.concat(files.map(f => fs.readFileSync(f))));
  for (const f of files) fs.rmSync(f);
  out(`FDLPP MoveFiles`);
  out(`FDLFILE ${JSON.stringify(final)}`);
  if (child) child.kill();
}
