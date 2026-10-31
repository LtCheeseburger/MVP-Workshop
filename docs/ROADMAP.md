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
- [ ] Menu bar (File / Edit / View / Tools / Help) with shortcuts; Test in Game as the main button;
      a slimmer toolbar
- [ ] Bookmarks (folders, filter, Ctrl+B)
- [ ] Options window (game exe, autosave interval, default folders), crash handler with "copy report"

## 2. The mod is the project

- [ ] Open an existing `.mvpmod` for editing (its files become overrides on the loose install)
- [ ] Other installed mods as a read-only base layer
- [ ] Conflict check against the launcher's mod list at export

## 3. Diagnostics before export

- [ ] Errors block export: texture format/size the runtime won't take, bad `mod.json`, packed archives,
      icon problems
- [ ] Warnings: preview-only changes left out, missing loose references, shared assets changed for
      everyone, `AffectsSaves` not set for roster/save data
- [ ] Badge count on the Diagnostics button; game log read back after Test in Game

## 4. Test in Game

- [ ] Hot reload of the `--mod` folder (needs port support)
- [ ] Launch to a target (`--team`, `--stadium`; needs port support)

## 5. Team workspaces

- [ ] Task-first start page: Uniforms, Logos, Stadium, Roster, Menus/UI, Audio, Textures, scoped to a school
- [ ] "This team only" vs "everyone" on every replace (fork a loose copy and repoint the assignment)
- [ ] Uniform editor on the player model with team colours

## 6. Viewers and editors for every file type

- [ ] Models: skinned preview, LODs, materials, wireframe, export (glTF/OBJ), import
- [ ] Animations: timeline, play/scrub, bone view, export
- [ ] Hex view with format overlays for every file; a structured viewer for each format the survey identifies
- [ ] Audio: play, replace with automatic encoding

## 7. Batch and round-trip

- [ ] Export a texture pack to slot-named PNGs (pairs with folder import)
- [ ] Roster CSV export / import
- [ ] Bulk replace from a folder for audio

## 8. Release

- [ ] CI runs the self-tests and `tests/*`, plus a `.mvpmod` round-trip test
- [ ] Tool / port layout version check
