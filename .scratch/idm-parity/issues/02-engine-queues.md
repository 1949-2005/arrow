# 02 Engine: queues and scheduler

Status: resolved
Blocked by: 01
Tier: careful

Implement the "Queues" section of `.scratch/idm-parity/spec.md` in engine.js and server.js, plus the `queue` field
of downloads, replacing today's single `scheduleAt` scheduler (with its migration).

Done when: every rule there is covered by tests (start and stop by hand and at HH:MM on allowed days only, once a
day; per-queue maxActive together with the global one; order and up/down; stop puts downloads back to 'scheduled';
retries after 10 s up to N per run; a done queue's whenDone starting the countdown; remove sends downloads to the
main queue; Later with queue/startQueue; migration of scheduleAt and of old 'scheduled' downloads; running not
saved); the existing scheduler tests are updated to the new model; `npm test` passes.

## Comments
Done (careful worker, reviewed by the orchestrator). Choices made where the spec was open:
- After a restart, a queue's queued or downloading downloads wait in it again ('scheduled'), resumed-by-hand ones too.
- `POST /api/settings {scheduleAt}` still sets the main queue's start time; `{queues}` there is ignored.
- Names are unique regardless of case; the main queue's name is fixed ("Main download queue"; the app shows "Main queue").
- Retries count per download per run; a queue that started nothing ends without its whenDone (no daily shut down on an
  empty queue); a waiting retry keeps the queue running.
- stopAt applies every day (days only limit starts); each start and stop fires once a day.
- Moving into a queue: paused, failed or queued become 'scheduled', a running one keeps running; finished keep their
  queue (view's `total` counts them; `waiting` = scheduled + queued, `active` = downloading).
- Removing a running queue stops it first; a bad queue id in add/confirm is an error.
122 tests pass.
