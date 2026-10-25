// log.txt: what the engine (and the native app, from C#) leave behind, since neither exe has a console.
// One line per entry: ISO time, the source in brackets, the message. Past 1 MB the oldest half goes.
import { appendFileSync, readFileSync, statSync, writeFileSync } from 'node:fs';

export const MAX = 1 << 20;

export function log(file, source, message) {
  const line = `${new Date().toISOString()} [${source}] ${String(message).replace(/\r?\n/g, '\n    ')}\n`;
  try {
    appendFileSync(file, line);
    if (statSync(file).size > MAX) {
      const text = readFileSync(file, 'utf8');
      const cut = text.indexOf('\n', text.length - MAX / 2);
      writeFileSync(file, text.slice(cut + 1));
    }
  } catch { } // a log that cannot be written must never stop the engine
}

// Uncaught errors go to the log; an uncaught exception also saves the state and ends the process (exit code 1)
export function catchCrashes(file, save) {
  process.on('uncaughtException', e => {
    log(file, 'engine', 'crash: ' + (e && e.stack || e));
    try { save(); } catch { }
    process.exit(1);
  });
  process.on('unhandledRejection', e => log(file, 'engine', 'unhandled rejection: ' + (e && e.stack || e)));
}
