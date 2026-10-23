# 05 Screens: download progress and download complete

Status: resolved
Blocked by: 04
Tier: medium

Implement spec.md "Screens › 2 Download progress window" and "3 Download complete window" (mockups/m2-progress.png):
the Connections and Options disclosures, and the Download complete window with Open with…, "Don't show this window
again" and the rule for opening by itself.

Done when: both work as specified, ARROW_SHOTS has pictures of the progress window (both disclosures open) and the
complete window, and `npm test` passes.

## Comments
Done (medium worker, reviewed by the orchestrator: pictures match mockup m2). Choices: with no limit the field shows
500 (like the main window's "Turn on (500 KB/s)"); typing a valid number ticks the box and sends after 0.6 s (Enter at
once); polls don't overwrite a change on its way; the complete view hides the segment bar; the Connections table
scrolls on small screens. The complete window opens once for a download the app saw unfinished and then done.
