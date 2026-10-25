# 04 Screens: main window

Status: resolved
Blocked by: 03
Tier: careful

Implement spec.md "Screens › 1 Main window" (mockups/m1-main.png) in the native app (app/*.cs), plus the app-side
model for everything the engine now sends: Core.cs `Download` (description, limitKB, whenDone, lastTry, notify,
queue, the 4th segment value), `Settings` (speedLimitOn, duplicates, showComplete, queues, types), `State` (queues,
powerOp) and Api calls for the new actions and /api/queues. The countdown strip reads `powerOp`. The sidebar's Types
come from `state.categories` in order, plus Other (custom categories show the Other glyph).

Done when: everything in that section works, Tests.cs covers the new pure helpers (status texts such as "In <queue>",
sorting, which columns fit a width), GLIDE_SHOTS pictures show the new main window (columns, command bar, queues) and
`npm test` passes.

## Comments
Done (careful worker, two review rounds). Review fixes: the command bar's labels fit at the default 1180 width
(ghost buttons padded 8 instead of 12; glyphs only below that); the countdown strip reads the engine's new
`powerAll`; Move up/down skip finished downloads (engine) and are offered only for unfinished rows.
Choices: first click sorts ascending, empty cells last both ways; Status sorts by group then progress; a column sort
also applies in a queue section; Start queue starts at once with one queue, else a menu; the header is mouse-only
(Sort ▾ is the keyboard path); section keys "type:<name>" / "queue:<id>" (old saved ones are converted).
