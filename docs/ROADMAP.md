# Roadmap: a production-ready MVP 06 editor

Goal: an editor for the MVP 06 recomp's loose install and `.mvpmod` files that is as easy to use as Frosty,
and deep enough that every asset in the game can be read, previewed and edited. Steps are in build order;
each lands as its own commit and is checked in the running app.

## 1. Explorer and project basics (the Frosty shell)

- [x] Asset right-click menu in the tree and search results: Open, Replace image, Import file over this,
      Add new file here, Export raw, Extract folder, Revert, Revert everything in here, Show in Changes,
      Dependencies, Copy name / path / SHA-1
- [ ] Additional asset actions: Open original, format-specific Import / Export, Bookmark
- [x] Explorer filter: Modified / Added checkboxes filter Categories and Files trees, keep matching parents,
      refresh after edits / revert / undo / redo, and show added nodes with right-click Remove
- [x] An untitled project starts when a game opens; autosave into rotating backups; recent projects;
      dirty marker in the title; Save / Don't save / Cancel on close and source switches; crash recovery
- [x] Menu bar (File / Edit / View / Tools / Help) with shortcuts; Test in Game as the main button;
      a slimmer toolbar; Close all tabs (Ctrl+Shift+W) and drag tabs to reorder
- [x] Bookmarks (folders, rename/move, filter, Ctrl+B / Ctrl+Shift+B, missing assets, Find in explorer)
- [x] Options window (game exe, autosave interval/count, default mod folder, log level, reset layout),
      dispatcher/AppDomain/task crash reports with autosave, Copy report and Open folder

## 2. The mod is the project

- [x] Open an existing `.mvpmod` for editing (overrides and Added files, texture-folder reconstruction,
      metadata/icon retention, version increment, XBE mismatch warning, synthetic round-trip coverage)
- [x] Other installed mods as a read-only base layer: launcher-order selector, source markers,
      project/autosave references, changed-source warnings and project-owned copies on edit
- [x] Nonblocking conflict checks against ZIP/folder mods and launcher enable/order settings at export;
      Diagnostics and the export summary include overlapping files and later-wins load-order notes

## 3. Diagnostics before export

- [x] Errors block export and Test in Game: invalid texture storage/size, bad `mod.json`, packed archives,
      icon problems
- [x] Warnings: preview-only changes left out, missing loose references, shared assets changed for
      everyone, `AffectsSaves` not set for roster/save data
- [x] Debounced background Diagnostics pane with a badge count and Go to actions
- [x] Game log read back after Test in Game: portable/Documents paths, release/debug files,
      incremental mod/texture warnings and errors linked to assets in Diagnostics

## 4. Test in Game

- [x] Hot reload: immutable generations, atomic publication, process reuse and acknowledgements;
      MVP06-Base replaces supported resident textures at renderer frame boundaries and remounts
      unopened data. Loaded databases, ambiguous/shared texture identities and unsupported texture
      layouts request restart atomically; see HOT_RELOAD_PROTOCOL.md for limits and verification.
- [x] Launch targets: main menu, school, stadium and two-team exhibition; automatic default follows
      the last school/stadium edit. MVP06-Base flags drive native menus and verify the selected venue.

## 5. Team workspaces

- [x] Task-first start page: school picker and Uniforms, Logos, Stadium, Roster, Menus/UI, Audio, Textures
      tasks open scoped workspaces; recent edits match the school's roster/player records
- [ ] "This team only" vs "everyone" on every replace (fork a loose copy and repoint the assignment)
- [ ] Uniform editor on the player model with team colours

## 6. Viewers and editors for every file type

- [ ] Models: skinned preview, LODs, materials, wireframe, export (glTF/OBJ), import
- [ ] Animations: timeline, play/scrub, bone view, export
- [ ] Hex view with format overlays for every file; a structured viewer for each format the survey identifies
- [ ] Audio: play, replace with automatic encoding

## 7. Batch and round-trip

- [x] Export texture packs and school uniform sets as slot-named PNGs; duplicate/unsafe tags are
      index-qualified, unchanged folder re-import preserves native bytes, with round-trip tests
- [x] School/all-school roster CSV export/import with readable columns, source identities,
      changed-row review, DAT value-span validation, known jersey ranges and stale-review protection
- [ ] Bulk replace from a folder for audio

## 8. Release

- [x] Windows CI workflow builds every project and runs the self-contained console/WPF suites,
      synthetic install/player checks, and a `.mvpmod` round-trip test (`scripts/verify.ps1`)
- [ ] Run the dataset-specific TeamAssignments and older game-dependent editor checks against their
      supported validation installations; these are explicitly excluded from no-game CI
- [x] Tool/port version and layout checks on Start and export, with update links; absent advertised
      capabilities stay explicitly unknown and release-manifest schema is not treated as mod format
