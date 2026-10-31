# Changelog

## Unreleased — 2026-10-30

### Added
- School workspaces and Tools now export/import roster CSVs for one school or all schools. Readable columns retain stable identities; a review window shows before/after field changes and validation errors before applying DAT value-span edits. Unresolved players remain visible as read-only rows; unknown DAT bytes and roster identities are preserved.
- Start and export now report installed port version, advertised mod format/layout and installed loose-layout metadata, with Workshop/port update links. Newer or mixed layouts warn before relying on exports; older manifests without capability fields remain explicitly unconfirmed rather than being assumed compatible.
- Export pack as PNGs in the texture editor and asset menu writes one index-qualified PNG per slot for folder re-import. School and uniform workspaces export every uniform pack into a separate subfolder. Duplicate/unsafe tags remain unambiguous; existing files and the game directory are protected.
- A Windows GitHub Actions workflow and shared PowerShell verification command build the solution and every test project, run synthetic console and desktop checks, and verify a tiny `.mvpmod` round trip. Generated fixtures require no game files; desktop tests isolate their settings. Exact game-data checks remain available through an explicit validation-install argument.
- Export checks installed ZIP and folder mods against the launcher's `mods/mods.json` list. Overlapping files, removals and whole texture-folder replacements appear as nonblocking Diagnostics warnings and in the export summary, with enabled/disabled state and load-order notes. Launcher and game files remain untouched.
- Test in Game now watches the port's portable or Documents logs and adds new mod/texture load errors, warnings and load-order conflicts to Diagnostics with asset navigation. Runtime messages retain their own source label and do not prevent exporting a fix; project switches clear the previous run's log diagnostics.
- Opening a loose installation shows a task-first Start page with a school picker and Uniforms, Logos, Stadium, Roster, Menus/UI, Audio and Textures cards. Tasks open the existing school editor or confirmed source list. Recent edits are attributed to school roster/player records rather than every school sharing a database.
- Diagnostics checks edits in the background after a short debounce and shows errors/warnings with asset navigation and a badge count. Export and Test in Game wait for validation; invalid texture storage, packed archives, mod details, and corrupt PNG icons block output. Preview-only edits, missing references, confirmed shared-school impact, and save-data changes without AffectsSaves produce warnings.
- Exports capture the current edit bytes so background validation and packing use the same changes without altering the viewer overlay.
- File > Open mod opens an existing `.mvpmod` as an editable project over the loose install. Existing files become overrides, new files become Added, DDS folders rebuild as textures, and metadata/icons survive re-export with the patch version incremented. A SHA-1 mismatch prompts before applying; unsafe or duplicate ZIP paths are rejected.
- Tools > Options configures the game executable, autosave interval and backup count, default mod export folder, log level, and restores the default layout.
- Dispatcher, AppDomain and unobserved-task crash handlers attempt an autosave and write versioned reports with project context, exception details and recent logs. The report dialog offers Copy report and Open folder.
- Per-game Bookmarks pane with folders, rename/move, filter, missing-asset markers, open and Find in explorer. Ctrl+B adds and Ctrl+Shift+B removes the selected asset; asset menus also offer Bookmark.
- File / Edit / View / Tools / Help menus expose every toolbar command and its keyboard shortcut. A compact toolbar keeps Open, Save, Undo and Redo beside prominent Test in Game (F5) and Export .mvpmod buttons.
- Drag document tabs to reorder them; Close all tabs (Ctrl+Shift+W) preserves Start and protects unapplied player drafts.
- Opening a game starts an untitled project. Dirty projects autosave every two minutes into five rotating backups under `%APPDATA%\MVP-Workshop\Autosave\<game>`, with a restore offer after an interrupted session. Autosave includes Added files, excludes preview-only edits, and keeps the project unsaved.
- Recent projects appear on Start and in the File menu. The title shows `*` for unsaved edits; closing or opening another game/project offers Save / Don't save / Cancel.
- Modified and Added explorer checkboxes filter both tree views, preserving parents of matching assets. Neither checked shows all assets; both checked combines the two kinds. Added files have a green + marker and right-click Remove.
- Expanded asset right-click menu in the tree and search results: Open, Replace image, Import file over this, Add new file here, Export raw, Extract folder, Revert, Revert everything in here, Show in Changes, Dependencies, and Copy name / path / SHA-1.

### Fixed
- The shared player-field validator enforces jersey numbers from 0 to 99. CSV reviews reject later source/roster changes rather than overwriting newer edits.
- Re-importing an untouched exported PNG folder preserves exact native bytes, palettes, mipmaps and lossy DXT blocks without adding an undo step. A stale import cannot overwrite a newer texture edit.
- A mod shipping `xsh.meta` must include its complete replacement texture folder; missing images are rejected without changing the active project.
- Document tabs show the Added marker only for Added assets, rather than showing it on Start and utility tabs.
- Explorer context menus select and act on the clicked asset, including search results, regardless of the open document. Add and extract keep that asset as their target.
- Extract folder preserves a loose texture pack as `.xsh.raw` alongside its DDS folder, avoiding a file/directory collision.
- Raw export and folder extraction reject destinations inside the game directory.

## 1.0.7 — 2026-10-28

### Added
- **Import folder…** in the texture viewer: replaces every image of a pack at once from a folder of pictures named after the slots (`hat.png`, `merf.tga`, `lega.png`…). A review list shows what each slot gets; the import is one undo step.
- In a uniform, **`torf` and `llod` are made for you** when the folder has none: `torf` is `tors` with its top quadrants mirrored, and `llod` (the low-detail atlas) gets the new torso, legs and hat scaled into place.
- **4K textures:** Xbox textures can be imported at up to 4096×4096, converted to the slot's format with the original mip count.

### Fixed
- Categorizing a loose install took over a minute; it now takes a few seconds.

## 1.0.6 — 2026-10-28

First release with the built-in updater, and the first public build since 1.0.3: it includes everything in 1.0.4 and 1.0.5 below. From here on the Workshop updates itself.

## 1.0.5 — 2026-10-28

### Added
- **Updates:** on launch the Workshop checks GitHub for a newer release and offers to install it. Yes downloads and checks the zip (sha256), closes the Workshop, replaces its files and starts the new version. Click the version number in the title bar to check by hand.
- **Export .mvpmod** from a loose install: only the files that differ from the install go in, at their loose paths (just the changed DDS images of a texture pack). **Test in Game** starts MVP06.exe with the changes as a temporary mod.
- **Mod details** (name, id, version, author, description, affects saves) with an optional **icon** (a square PNG, 64-256 px, saved as `icon.png`).
- New files can be added to a mod; the save shows warnings for anything the port won't take.
- **TGA and DDS import** in the texture viewer's Replace image: the picture is converted to the slot's format with mips rebuilt and alpha kept.
- Right-click menu in the asset tree.

## 1.0.4 — 2026-10-28

### Changed
- **Starts empty and asks for the game:** on launch the tool asks for `MVP06.exe` and opens the `assets\` folder beside it (`game\` also works). The last game is no longer reopened automatically (`--game` still works). The toolbar and Start page say **Open Game**.

### Added
- **Loose install support** for the MVP 06 port (`docs/LOOSE_ASSETS.md` in the port): `name_big/` folders with a `_big.json` open as `name.big` archives (nested too), and `.xsh/` folders of DDS files open as texture packs, rebuilt byte-identical from `xsh.meta` and the DDS files. Categories, search and every viewer work as before.
- **DDS files are browsed directly:** each `.xsh` folder expands to its DDS images, and Categories list them by archive and texture folder. Opening a DDS shows that image in the texture viewer, where it can be replaced as before.
- **Export Mod** writes an edited loose texture pack as a DDS folder in the install layout, after checking it rebuilds exactly.
- `XshFolder`: splits an `.xsh` into DDS files and builds it back (test `tests/LooseTextures`).

### Fixed
- **Xbox school assignments:** the roster was read with the PS2 bit layout, so every Xbox school got the wrong logo and uniform (Alabama showed Loyola Marymount's). On Xbox the logo and uniform start at bit 0, not bit 1; all 128 schools now match `teaminfo.csv`. Assignment edits write the same layout.
- Opening a DDS file selected the wrong image in packs whose directory order differs from file order.

## 1.0.3 — 2026-10-27

### Added
- **Animate tab:** banks have plain names and groups (Gameplay, Signature batting stances, Signature pitching motions, Face & hands, Cutscenes, Crowd), and the main gameplay bank opens by default.
- Each clip shows once, with its body and root halves playing together. It gets a readable title decoded from its name (`r_stepagr135ltostand90l` → "Step aggressive 135° L to stand 90° L"), plus the raw name, frame count and length.
- Clips are grouped by role (Batting, Pitching, Catcher, Fielding, Running, Umpire, Hands, Facial, Dugout, Cutscene, Crowd…), with a category filter and search on the title or raw name.
- **Parts tab on player bodies:** parts are named from the model's name table ("Catcher's mask", "Chest protector", "Fielder's glove"…) and grouped by body area.
- **Batter / Fielder / Catcher / All kit** presets show one role's loadout, so alternate caps, helmets, pants and gear no longer overlap. Fielder is the default.
- Probe: `anim-names` lists every clip in every bank.

## 1.0.2 — 2026-10-27

### Added
- **Models** section in the Categories browser: player bodies, heads and eyes, equipment (bats, ball), crowd, scenery and props, and shadows, with each model in one place.
- **Animations** section listing all 270 animation banks separately from their bank lists.
- Mixed archives (stadiums) are split into **Models / Textures / Data** folders.
- Xbox high-detail player body (`hibody`), `mehead`, `lohead` and the crowd models now open: their `.ord`/`.orl` halves live in different archives (`initstaz.big` / `initprgz.big`) and are joined automatically.

### Fixed
- Xbox rigid models (bats, ball, `misc` props, stadium fields, clouds, mounds) were drawn as one long triangle strip, producing spikes and shredded shapes. They now use their own index buffers.
- Stadium field line draws (`ParentRM_…Line`) were drawn as triangles, making large white spikes across the infield. Line draws are now skipped (the viewer renders triangles only).
- Texture archives formerly labelled "Players/Models and equipment" are now "Players/Uniform and body textures".
- `.orl` link fragments are listed under System instead of next to every model.

### Docs
- `XBOX_MVP06.md`: removed the outdated "no skin-weight animation on Xbox" limitation; documented cross-archive split pairs.

## 1.0.0 — 2026-10-25

First release under the **MVP-Workshop** name (formerly "MVP07 Asset Tool", last version 0.9.0-beta.1).

### Added
- **MVP 06 NCAA Baseball (Xbox) support** alongside MVP 07 NCAA Baseball (PS2). Open an extracted Xbox game folder (`default.xbe`).
  - `.xsh` SHPX texture packs: DXT1/3/5, PAL8, BGRA8888 and 16-bit formats, Xbox Morton (un-)swizzling.
  - **Texture replacement** on Xbox textures with automatic **mip-map regeneration** and DXT re-encode (PAL8 and BGRA8888 are written without DXT loss).
  - `.xbx` XPR0 title/save images, `.xfn` FntX bitmap fonts, `.vp6` MVhd movies (playback through ffmpeg), `.xbe` header/section/library inspector.
  - Xbox EAGL models (`.ord` objects with `__VB` vertex buffers, `__RM` render methods and 16-bit triangle-strip index buffers), with textures and skeletons.
- Version number shown in the title bar, start page and startup log line.
- Project extension `.mvpwproj`; old `.mvp07proj` projects still open (same format).
- Settings migrate once from `%APPDATA%\MVP07AssetTool` to `%APPDATA%\MVP-Workshop`.

### Changed
- Rebranded to MVP-Workshop: executable `MVP-Workshop.exe`, window/title bar, start page, message-box captions, default export names (`MVP-Workshop-modded.iso`), survey titles, docs.
- Projects and namespaces renamed `AssetTool.*` → `MVPWorkshop.*`.
- Temp folder is now `%TEMP%\MVP-Workshop`.
- PS2 (MVP 07) behaviour and file formats are unchanged.

### Known limitations
- Xbox models: **no skin-weight animation/posing** (skin weights are decoded, but the Xbox layout is shown as static geometry).
- Teams workspace: **stadium and uniform tables are not yet resolved for Xbox**.
- **Lossy DXT**: replacing DXT textures re-encodes with a block fit; some logos with fine detail or hard edges show visible artefacts. Texture dimensions must be multiples of 4.
- Movies (MPC and VP6) need `ffmpeg.exe` on `PATH` (not bundled).
- Upscaled textures and APT appended strings are not yet tested in-game (carried over from 0.9.0-beta.1). Back up your files.
