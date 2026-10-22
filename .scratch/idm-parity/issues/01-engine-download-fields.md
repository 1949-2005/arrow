# 01 Engine: description, per-file limit, speed-limit switch, connections, when done, duplicates, complete window

Status: in progress
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
