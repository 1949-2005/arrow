# 08 Screens: Options pages

Status: resolved
Blocked by: 07
Tier: medium

Implement spec.md "Screens › 8 Options" (mockups/m5-options.png right): the page navigation, General, Downloads
(speed limit with `speedLimitOn`), Categories (with the editor from issue 06) and Queues; the old Schedule section
goes.

Done when: every setting saves and reloads, GLIDE_SHOTS has a picture of each page, and `npm test` passes.

## Comments
Done (medium worker, reviewed by the orchestrator: matches mockup m5). Review fixes: a row grows so its name, file
types and folder wrap whole (never cut); file types in the text color, not faint; the mockup's "yours" tag on custom
categories; the Remove question says no file is moved; the shots use the engine's default file types. Choices: Open
Scheduler saves General and Downloads, closes Options, then opens the Scheduler (not while a field is invalid); a
failed Save shows the Downloads page; an unticked limit keeps the stored KB/s, an empty one starts at 500.
