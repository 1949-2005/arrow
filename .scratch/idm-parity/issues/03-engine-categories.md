# 03 Engine: editable categories

Status: resolved
Blocked by: 02
Tier: medium

Implement the "Categories" section of `.scratch/idm-parity/spec.md`: `settings.types` replaces the `CATEGORIES`
constant everywhere in the engine, with validation and the rules for built-in and custom categories.

Done when: covered by tests (defaults and order, editing a built-in's types, adding / renaming / removing a custom one,
a type moving between categories, invalid names and types rejected, downloads of a removed category become Other,
view's exts and categories follow the settings); `npm test` passes.

## Comments
Done (medium worker, reviewed by the orchestrator). Choices made where the spec was open:
- A rename is a custom name that disappears while a new name takes its place in the order; its folder entry follows
  (unless the same POST sends `folders`). The app's Options page sends the whole `types` in order, so this fits.
- A built-in left out of a POST keeps its types; a bad file type is dropped on its own (no fixing); names may use any
  Unicode letters; leading or trailing spaces are rejected.
- Only an open Download File Info window stores a category (`probe.category`); it follows new types at once.
127 tests pass.
