# 03 Engine: editable categories

Status: in progress
Blocked by: 02
Tier: medium

Implement the "Categories" section of `.scratch/idm-parity/spec.md`: `settings.types` replaces the `CATEGORIES`
constant everywhere in the engine, with validation and the rules for built-in and custom categories.

Done when: covered by tests (defaults and order, editing a built-in's types, adding / renaming / removing a custom one,
a type moving between categories, invalid names and types rejected, downloads of a removed category become Other,
view's exts and categories follow the settings); `npm test` passes.

## Comments
