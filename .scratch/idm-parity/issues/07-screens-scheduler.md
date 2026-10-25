# 07 Screens: Scheduler

Status: resolved
Blocked by: 06
Tier: careful

Implement spec.md "Screens › 4 Scheduler" (mockups/m3-scheduler.png) on top of the queues API (issue 02).

Done when: queues can be created, edited (every field, validated inline, sent at once), deleted, started and stopped
from it, the file list reorders and removes, GLIDE_SHOTS has its picture, and `npm test` passes.

## Comments
Done (careful worker, reviewed by the orchestrator: matches mockup m3). Review fix: a chosen day chip is filled with
the accent (onAccent text, an inner ring when focused); the soft fill was too faint in light mode. Choices: a sidebar
queue opens the Scheduler from its context menu ("Open Scheduler"); typing a time ticks its "At"; changes are merged
per queue and sent in order, and polls skip a focused or invalid field and the form for 1 s after a send.
