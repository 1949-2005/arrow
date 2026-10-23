# 06 Screens: Download File Info, duplicate link, Properties, category editor

Status: resolved
Blocked by: 05
Tier: medium

Implement spec.md "Screens › 5 Download File Info", "6 Link already in the list" and "7 Properties"
(mockups/m4-fileinfo.png, m5-options.png left), and the category editor dialog described under "8 Options ›
Categories" (it is opened from Download File Info's "+" too). Properties replaces "Change link…".

Done when: each works as specified (including a duplicate that becomes known after the window opened, and a saved
duplicates choice that skips the window), ARROW_SHOTS has a picture of each, and `npm test` passes.

## Comments
Done (medium worker, reviewed by the orchestrator: pictures match mockups m4 and m5 left). Review fix: the engine's
view() now sends each download's `referer` (never the headers), so Properties shows it. Choices: the duplicate
question opens over Download File Info (the HTTP probe usually arrives after it opened); `replace` is sent only while
Save As is still the existing file; file types are lower-cased, a leading dot is rejected; Change link is gone
(Properties edits the address).
