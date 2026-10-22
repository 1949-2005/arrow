# 02 Engine: queues and scheduler

Status: open
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
