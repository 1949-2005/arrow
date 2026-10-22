# 01 Engine: description, per-file limit, speed-limit switch, connections, when done, duplicates, complete window

Status: resolved
Tier: careful

Implement in engine.js and server.js exactly as `.scratch/idm-parity/spec.md` says in these sections:
"Downloads: new fields" (all except `queue`, which is issue 02), "Settings: new fields" (`speedLimitOn`,
`duplicates`, `showComplete`; not `queues`/`types`), "Power (shared countdown)" and "Duplicates".

Done when: every rule there is implemented and covered by tests in engine.test.js / server.test.js (new fields set
through their API and validated, the per-file limit actually slows one download while another runs at full speed,
the 4th segment value, 'open' calling the recorder, a per-download shutdown starting the countdown while another
download still runs and Cancel stopping it, each duplicate choice, speedLimitOn migration); `npm test` passes; no
other behavior changes.

## Comments
Done (careful worker, reviewed by the orchestrator). Choices made where the spec was open:
- `speedLimitOn` is false on a new install; sending only `speedLimitKB` no longer limits (the app sends both).
- 'replace': the old file stays until the new download finishes, whose final rename writes over it. With another
  Save As path the old entry still goes and the new file is numbered as usual.
- `rememberDuplicates` is saved also on 'cancel' (the app's answer for 'resume').
- A download's own countdown is stopped only by Cancel, not by new work; the global choice stays when it fires.
Review fix: view() `whenDone` stays the status bar's choice and the new `powerOp` names the running countdown (the
picker would otherwise show a download's own op). 113 tests pass.
