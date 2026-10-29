# Changelog

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
