# Changelog

## 1.0.14 — 2026-11-23

- MVP 27 overhaul support: after Open Game, an `MVP27-Overhaul.mvpmod` beside MVP06.exe is validated and layered as a read-only base so its teams, rosters, uniforms, stadiums and UI appear in the normal categories. Edits stay in the project; Tools → Build overhaul… writes a new package, and Tools → Locate overhaul… picks one manually. The game, mod install and launcher enablement are never touched.
- Overhaul files are organized into the school and category browser (preorganized school indexing and uniform routing).
- Added an optional NASCAR-Workshop build variant (`NASCAR_WORKSHOP`) with its own categories, APT artwork, embedded model and map viewers, build/verify scripts and self-tests.

## 1.0.13 — 2026-11-19

- Fixed Xbox compact index-buffer decoding that produced incorrect triangles and gaps in player pants and other optimized render methods.
- Uniform editing defaults to one large edited player, with an optional Compare with original toggle. Added a selected-texture thumbnail and readable texture/region labels. Player text, dirt and technical details are collapsed until needed.
- Simplified the main toolbar; raw export, extraction, dependencies and format survey remain in the menus.

## 1.0.12 — 2026-11-19

- After school-only texture replacement, the active viewer follows the edited school copy rather than staying on the unchanged shared source.
- Texture replacement reviews use decoded school assignments without waiting for the full game's dependency scan. Other unresolved runtime consumers remain explicitly noted.
- Dressed-player texture replacement supports DDS/TGA imports and the same dimension/resampling review as the texture editor. Import failures are logged and displayed.

## 1.0.11 — 2026-11-19

- Opening a school uniform texture now selects the dressed-player workspace and matching variant. The standalone uniform texture editor also has a Dressed player shortcut.
- Installed mod discovery searches both the game folder's mods directory and the executable folder's mods directory, even when both exist. Added direct Open Mod and Installed Mods toolbar actions; diagnostics and base-mod selection use the combined catalog.

## 1.0.10 — 2026-11-19

- Restored school-by-school browsing, school asset groups, and direct toolbar actions while keeping initial source categories available immediately. Start and the explorer share school indexing instead of duplicating it.
- Name enumeration skips payload probing for known leaf formats, including loose textures and models.
- Installed inventory manifests larger than 16 MiB are streamed for compatibility metadata instead of rejected. The smaller release manifest retains its size limit.

## 1.0.9 — 2026-11-19

- Removed the full archive and school-indexing barrier from the MVP browser. Predefined source categories use filenames and routing directories; archives and texture folders load their children only when expanded. Search and school indexing run independently of browser readiness.

## 1.0.8 — 2026-11-19

- Fixed category matching for MVP 06 archive folders, including databases, frontend resources, and animations. Known file types use metadata for classification.
- Game opening defaults to Categories with cleared filters and completes the browser setup before reporting the game loaded. School-indexing failures no longer block all categories. Base files still require an index pass.
- Automatically discovers installed folder mods and `.mvpmod` packages in the mods directory. File > Open installed mod opens a selected mod as an editable project.
- Recovered two corrupted APT viewer source files from their last committed versions to restore buildability.

## Unreleased — 2026-10-30

### Added
- MicroTalk 10:1 revision-3 per-block budgeted encoder (opt-in API, not yet in the editor): a byte-exact port of the public-domain utkencode with per-SCDl rate search and committed decoder-consistent state. It fits all 11 studied native streams with exact framing, agrees with the independent vgmstream decoder to 1 PCM unit and averages about 19 dB SNR. Listening and native-game playback checks remain before replacement is enabled.
- Game-dependent TeamAssignments, PlayerEditor and LooseTextures checks pass on the loose Xbox install. The checks now use the roster platform's bit layout, resolve loose `name_big/` mod output and accept both known LSU roster revisions.
- Read-only runtime/codec research probes inventory real speech and stadium bindings, locate native string/call references, and test pinned public MicroTalk encoding against an independent decoder. Revision-3 repack experiments preserve native framing when every block fits; most 32 kb/s examples exceed individual block budgets. Xbox school/nickname audio selectors are traced, but production MicroTalk replacement and additional school-only scopes remain incomplete.
- Explorer and search-result menus add read-only **Open original / sources** snapshots, separating vanilla game bytes, selected base-mod bytes and current project/preview bytes without editable controls. Added files have no invented vanilla layer. **Format import / export** routes textures, audio, models and text/CSV to their existing operations; unsupported layouts explain their limits and retain raw interchange.
- Audio viewers add **Import folder…**: files named as Export All writes them (`crowd_Sound_3.wav`, `intro_Stream_0.ogg`, `sfx_Sony_sample_12.mp3`) are converted through the single-sound pipeline and shown in a review with input and native rate/channels/codec/duration, encoded previews and per-row results. Unmatched, duplicate, corrupt, unsupported-input and MicroTalk rows stay visible and are never guessed. Accepted sounds pass impact review and a stale-source check, then save as one undoable edit that preserves offsets, wrappers and untouched sounds.
- Audio now plays stream packs and paired Sony HD/BD samples, imports WAV/MP3/OGG (ffmpeg for compressed inputs), preserves native rate/channels/duration and encodes VAG, EA-XA or PCM in place. Encoded preview, impact review, shared-payload/stale-source guards and undo protect replacements. Confirmed consumers are shown; unresolved bindings stay labelled. MicroTalk playback/export remain supported, but replacement needs a verified encoder.
- Phase 6 verification adds model interchange, audio encoding and structure suites plus a desktop preview/cancel/commit/undo check. All 17 synthetic desktop checks pass; real Xbox mesh/clip and PS2 audio checks pass separately.
- Every asset has Structure tables and Hex region navigation. Known archive, image, executable, ELF and audio layouts highlight bounded byte regions; unknown formats expose raw words without guessed semantics. Format survey and Markdown reports show viewer coverage and rank missing specialized viewers by frequency.
- Animation playback exports selected body/root clips to glTF with sampled local bone tracks and the current model skin. Lowercase loose-install bank names now select the default gameplay bank correctly, controls scroll on short windows, and playback timers stop when the viewer closes. Native bank editing is deferred.
- Model inspection adds sibling LOD selection, textured material lists, wireframe, normals and UV layouts. Self-contained glTF includes materials, textures and available skin/bones. Xbox FLOAT3 position replacement preserves native topology and metadata, validates shared vertices/bounds and uses impact review and undo; PS2/quantized layouts remain export-only.
- School-only/shared replacement review uses confirmed dependency relationships. Uniform/logo isolation copies complete families, reserves an unused slot and repoints one school in a single undoable transaction. Added copies resolve in the school workspace, projects and exports; undecoded audio/model bindings explain why isolation is unavailable.
- Dressed uniforms now have editable Home/Away/Alternate preview mappings and texture-derived colour swatches. Name/number, variant and camera settings survive refresh; real-model checks verify live texture replacement and undo for LSU, Alabama and Stanford. Role defaults retain native suffixes without asserting unverified runtime meaning.
- Phase 4 in MVP06-Base: Test in Game reloads supported resident textures through frame-boundary renderer replacement, retains original identities for revert, and safely requests restart for loaded databases or ambiguous/unsupported texture changes. Added native loader/renderer regression coverage.
- Test in Game launch picker: main menu, school, stadium and two-team exhibition, with defaults from recent school edits. Native flags drive and verify the actual screens; test runs use isolated user settings, logs and temporary venue assignments.
- The earlier top-level port implements atomic reload of unopened assets with revision acknowledgements and VFS transaction locking. Already-open assets/archives and cached-directory topology changes return restart-required while preserving the old layer. The native Release build and 14 no-game regression checks pass; MVP06-Base adds live replacement and gameplay validation in the Phase 4 entries above. A reviewable port patch and `scripts/verify-port.ps1` accompany the changes.
- Test in Game publishes immutable temporary mod generations. Ports advertising `workshopReload: 1` can reuse the running process through an atomic reload pointer and revision acknowledgements; missing/rejected responses appear in Diagnostics. Revert-all publishes an empty layer. Legacy ports retain launch-only behavior. The runtime contract and current limits are documented in `docs/HOT_RELOAD_PROTOCOL.md`.
- Tools > Installed mods as base selects compatible ZIP/folder mods in launcher order without changing launcher settings. Tree rows and viewers identify source mods; project/autosave files retain selections and detect changed sources. Editing a base-added asset makes an undoable project copy that survives deselection. Export includes only project edits, including explicit edits over a base removal.
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
