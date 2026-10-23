# 04 Screens: main window

Status: in progress
Blocked by: 03
Tier: careful

Implement spec.md "Screens › 1 Main window" (mockups/m1-main.png) in the native app (app/*.cs), plus the app-side
model for everything the engine now sends: Core.cs `Download` (description, limitKB, whenDone, lastTry, notify,
queue, the 4th segment value), `Settings` (speedLimitOn, duplicates, showComplete, queues, types), `State` (queues,
powerOp) and Api calls for the new actions and /api/queues. The countdown strip reads `powerOp`. The sidebar's Types
come from `state.categories` in order, plus Other (custom categories show the Other glyph).

Done when: everything in that section works, Tests.cs covers the new pure helpers (status texts such as "In <queue>",
sorting, which columns fit a width), ARROW_SHOTS pictures show the new main window (columns, command bar, queues) and
`npm test` passes.

## Comments
