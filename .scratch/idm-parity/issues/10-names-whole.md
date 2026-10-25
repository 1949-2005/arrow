# 10 Names whole

Status: resolved
Blocked by: 09
Tier: careful

The final screenshots cut the long Arabic title in the list's Name column (an ellipsis after "في فصل ال"). The user's
standing rule: a download's name always shows whole, wrapped onto the lines it needs, never cut or clamped. Apply it
wherever a download's name shows: the list (rows grow, 48 at least), the Scheduler's files, the details panel, the
progress and Download complete windows, Properties.

Done when: no download name is cut anywhere, GLIDE_SHOTS shows the long Arabic title whole on each of those screens in
both themes, and `npm test` passes.

## Comments
Done (careful worker, reviewed by the orchestrator). The list's rows are 48 or as tall as the wrapped name (cached per
name for the Name column's width; tops and a binary search in Core.RowTops / RowAt, tested); the Scheduler's files,
the details panel, progress, Download complete, Properties, Download File Info's header and "Link already in the
list" show the name whole. The Scheduler's file table keeps its fixed height and scrolls: a partly shown last row
says there is more. Known limit: queue and category names (typed by the user, 40 and 30 characters at most) can
still end in "…" in the sidebar and pickers.
