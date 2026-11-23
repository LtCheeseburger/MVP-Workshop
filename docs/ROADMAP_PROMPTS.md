# Roadmap prompts

One prompt per item in [ROADMAP.md](ROADMAP.md), to paste into a fresh Claude Code session in `E:\Tool Source\MVP-Tool`.
Every prompt assumes this shared context, so paste the block below first, followed by the item's prompt.

> **Context:** MVP Workshop (WPF, .NET, `src/MVPWorkshop.App` + `src/MVPWorkshop.Core`) edits the MVP 06 recomp's
> loose install and packs edits as `.mvpmod` files (`MvpModExporter`, `MainWindow.Mvpmod.cs`). Edits go through
> `EditSession` (overrides keyed by `AssetNode.Key`, one undo history, `Added` files, SAVED vs PREVIEW ONLY). The game
> folder is never written. The goal is Frosty-level ease of use without copying Frosty's UI; keep the app's
> existing look (`Ui.cs`, theme resources) and code style. Build with `dotnet build src/MVPWorkshop.App`, update
> `docs/ROADMAP.md` (tick the item) and `CHANGELOG.md`, and check the change in the running app. One commit per item.

Items in the same phase touch the same files (mostly `MainWindow*`), so run them one at a time; different phases can
run in parallel when noted.

---

## Phase 1: Explorer and project basics

### 1.1 Right-click menu (done, needs a check in the app)
Verify the expanded asset right-click menu in `MainWindow.Mvpmod.cs` (`Tree_ContextMenuOpening`) works in the
running app, on both the tree and search results: Open, Replace image, Import file over this, Add new file here,
Export raw, Extract folder, Revert, Revert everything in here, Show in Changes, Dependencies, Copy name/path/SHA-1.
Also check the "Edited only" button beside Collapse all. Fix anything broken, then commit.

### 1.2 Edited / Added filter
Extend the "Edited only" list (`EditedOnly_Click` in `MainWindow.xaml.cs`) into a proper filter: checkboxes for
Modified and Added above the data explorer that filter the tree itself (Categories and Files views), keeping
parents of matching assets. Added files (`EditSession.Added`) should appear in the tree as new nodes with their
own marker, with Remove on right-click.

### 1.3 Untitled project, autosave, recent projects, save prompt
Opening a game should start an untitled project automatically. Add autosave on a timer into rotating backups
(`%APPDATA%\MVP-Workshop\Autosave\<game>\Project_001..005.mvpwproj`), a Recent projects list on the Start page and
File menu (`Settings` in `Models.cs`), a dirty marker (`*`) in the title bar from `EditSession.IsDirty`, and a
Save / Don't save / Cancel prompt on close and when opening another game or project. Offer to restore the newest
autosave after a crash.

### 1.4 Menu bar and toolbar
Replace the 15-icon toolbar in `MainWindow.xaml` with a menu bar (File / Edit / View / Tools / Help) that shows
every command with its shortcut, plus a slim toolbar: Open, Save, Undo, Redo, and a prominent **Test in Game**
button and **Export .mvpmod** button. Add Close all tabs, tab drag to reorder, and Ctrl+Shift+W. Keep every
existing command reachable.

### 1.5 Bookmarks
Add a Bookmarks pane (beside the data explorer, switchable) with folders, rename, a filter box, double-click to
open, "Find in explorer", Ctrl+B to bookmark the selection and Ctrl+Shift+B to remove. Bookmarks are stored per
game in settings by `AssetNode.Key`; missing ones show greyed out.

### 1.6 Options window and crash handler
Add an Options window: game exe path, autosave interval and count, default mod output folder, log level, and
a reset-layout button. Add a global crash handler (`App.xaml.cs`: dispatcher, AppDomain, TaskScheduler) that saves
an autosave first, writes a report (version, log tail, exception, open project) to `%APPDATA%\MVP-Workshop\crashes`,
and shows a dialog with "Copy report" and "Open folder".

## Phase 2: The mod is the project

### 2.1 Open a .mvpmod for editing
Add File > Open mod… which reads a `.mvpmod` (format in `MvpModExporter`: zip with `mod.json` and files at loose
paths) and applies its files as overrides on the open loose install (new paths become `Added` files). Fill the
saved `ModInfo` from its `mod.json` so re-export keeps id/name and bumps the version. Warn when its XBE SHA-1
doesn't match the install. Add a round-trip test in `tests/`: export, re-open, compare overrides.

### 2.2 Installed mods as a read-only base layer
Read the launcher's installed mod list (find where the port's launcher stores it; ask me for the port repo path
if it isn't obvious) and let the user enable mods as a base layer: their files show through in viewers and the
tree, marked "from <mod>", but aren't packed into the new mod. Editing one copies it into this project.

### 2.3 Conflict check at export
At export, compare the mod's file list against every other mod in the launcher's list and show "these files are
also changed by <mod>", with load-order notes. Not blocking; list it in the export summary and in Diagnostics.

## Phase 3: Diagnostics

### 3.1 Diagnostics panel and checks
Add a Diagnostics pane with a badge count on its button. Run checks whenever the session changes (debounced, in
the background). Errors (block export and Test in Game): textures the runtime won't take (format/size), files
`MvpModExporter.Collect` rejects (packed archives), invalid `ModInfo` (`Problem()`), bad icon. Warnings:
preview-only changes left out, references to missing loose files (use `DependencyIndex`), a shared asset changed
for every school that uses it, `AffectsSaves` off when roster/save data changed. Each row: severity, asset,
message, and a "Go to" action.

### 3.2 Game log after Test in Game
After Test in Game, capture or read the game's log (find where the port writes it) and show mod-related load
errors and warnings in Diagnostics, linked to the asset.

## Phase 4: Test in Game (needs port changes; ask me for the port repo first)

### 4.1 Hot reload
Design and implement hot reload: Test in Game keeps the game running, and re-exporting rewrites the temp mod
folder; the port watches it (or gets a reload signal over a named pipe) and reloads changed textures/data.
Implement the tool side and write up the port side precisely, or implement it there if I give you the repo.

### 4.2 Launch to a target
Add launch targets to Test in Game (a dropdown: main menu, a team, a stadium, an exhibition with two teams) passed
as port command-line flags; default to the school/stadium of the asset you last edited. Needs the flags in the
port.

## Phase 5: Team workspaces

### 5.1 Task-first start page
When a loose install is open, the Start page (`Viewers/StartPage.cs`) shows a school picker and cards: Uniforms,
Logos, Stadium, Roster, Menus/UI, Audio, Textures. Each opens the right existing editor already scoped to the
school (`TeamWorkspaceView`, `UniformWorkspaceView`, `StadiumView`, `TeamView`, etc.). Show recent edits per school.

### 5.2 "This team only" vs "everyone" on replace
On every replace action (textures, audio, models), use `DependencyIndex` "Used by" to show how many schools use
the asset, and offer "Replace for <school> only" (write a new loose file via `EditSession.AddFile` and repoint that
school's assignment with `TeamAssignmentEditor`) or "Replace for all N". Preview the impact before applying.

### 5.3 Uniform on the player model
Show the selected school's uniform on the 3D player model (`ModelView.Uniform.cs`, `UniformWorkspaceView`) with
home/away/alt switching, team colours, number and name preview, and live update when a texture is replaced.

## Phase 6: Viewers and editors for every file type

### 6.1 Models
Improve `ModelView`: LOD switching, per-material list with textures, wireframe/normals/UV views, bone display,
and export to glTF (and OBJ). Then import: replace a mesh from glTF where the format allows, with clear limits.

### 6.2 Animations
Add an animation viewer (`ModelView.Anim.cs`, `EaglAnimNames`): pick a model and an animation, timeline with
play/pause/scrub/speed/loop, frame counter, bone overlay, and export to glTF. Editing comes later; document what
the format allows.

### 6.3 Hex view and a viewer for every format
Add a hex view tab for every asset with structure overlays where the format is known (`FormatDetector`, survey).
Use the format survey (`SurveyView`) to list every format with no viewer, and add a structured viewer (tables,
headers, fields) for each, in order of how many files use it.

### 6.4 Audio
Make `AudioView` play every audio format in the game, and add Replace audio: pick a WAV/MP3/OGG, convert it to the
game's format (sample rate, channels, codec) automatically, preview, and commit. Show which schools/moments use it.

## Phase 7: Batch and round-trip

### 7.1 Texture pack export to PNGs
Add "Export pack as PNGs" for texture packs and school uniform sets: one PNG per slot, named so
`TextureFolderImport` re-imports the folder unchanged. Add a round-trip test in `tests/LooseTextures`.

### 7.2 Roster CSV
Export a school's (or every school's) roster to CSV with readable column names, and import it back through the
same value-span editing the roster editor uses, with a preview of changed rows and validation of ranges.

### 7.3 Bulk audio replace
Replace many audio files from a folder of files named after the targets, with conversion and a review screen
like `FolderImportReview`.

## Phase 8: Release

### 8.1 CI
Add a GitHub Actions workflow that builds the solution and runs `tests/*` and the self-tests that don't need game
files, plus a synthetic `.mvpmod` round-trip test (build a tiny fake loose install, export, apply, diff).

### 8.2 Tool / port version check
Read the installed port's version and layout (`version.txt`, `port-manifest.json`) and compare with
`MvpModExporter.Format`/`Layout`; warn on the Start page and at export when the port expects a newer layout, with a
link to update.
