# MVP-Workshop

Asset explorer, editor and format-research workbench for **MVP 07 NCAA Baseball (PS2, SLUS-215.82)** and **MVP 06 NCAA Baseball (Xbox, default.xbe)**.
Frosty-style layout: data explorer on the left, tabbed documents in the middle, properties on the right, log below.

The game folder is never written to. Edits live in a project (`.mvpwproj`) and **Export Mod** rebuilds only the
changed archives into a separate output folder (default `mod_output`, next to the game folder). See
[docs/EDITING.md](docs/EDITING.md) for what can be edited, how it is saved, and how it was validated.

## Run

```bash
dotnet run --project src/MVPWorkshop.App -c Release
```

On start it opens, in order: the game given with `--game <folder|iso>`, the last game you opened (kept in
`%APPDATA%\MVP-Workshop\settings.json`), or a sibling `game` folder above the executable. Otherwise use
**Open Game Folder** (Ctrl+O). Supported sources are an extracted PS2 game folder (`SLUS_*`), an Xbox folder
(`default.xbe`), or a PS2 ISO.

## Editing in short

| Asset | What you can change | Saved into the game archives? |
|---|---|---|
| APT UI movie (`.apt`/`.const`) | text fields (text, variable, bounds, font, colour, alignment, flags), display-list placements (position, scale, skew, tint, depth, character), string constants, stage size | yes |
| Team assignments (Teams → school → Edit assignments) | uniform/logo asset families and roster stadium enum, with resolved previews and source offsets | yes; masked roster fields, authentic venue registry retained |
| Players (Teams → school → Roster) | source-labeled names, jersey/position IDs, ratings, appearance and equipment raw values in joined DAT tabs | yes; value spans only, unknown bytes/fields and roster relationships retained |
| SSH texture (`.ssh`) | review/import PNG/BMP/JPG/GIF/TIFF (PAL4, PAL8, RGB888, RGBA8888, GST); retain eligible native upscales or fit with area/bilinear/nearest filters | yes for supported layouts; native resizing is experimental ([details](docs/TEXTURE_IMPORT.md)) |
| LOCH string tables (`FEENG.LOC` ...) | any string | yes |
| Text / CSV / `.fel` / `.loc` text | the text; encoding and line endings kept | yes |

Everything goes through one undo/redo history (Ctrl+Z / Ctrl+Y). A dot marks edited assets (tabs, tree, and the archives
that contain them). **Changes** lists every change as **SAVED** or **PREVIEW ONLY**; preview-only changes (formats the
tool cannot write back, assets inside containers it cannot rebuild) are visible in the viewers but are never saved
in a project or exported. Ctrl+S saves the project, Ctrl+E exports the mod.

The school roster editor retains player drafts while switching players and sections.
Use **Apply player fields to project** for one source or **Apply all player drafts**
for the school before saving/exporting. Search field names/IDs, compare original
values and reset pending edits without reverting applied project changes.

## What it does

- **Dependencies** — asynchronous, cancellable **Depends on** / **Used by** explorer with separate explicit, decoded and candidate evidence, source locations, missing-reference checks and transitive replacement impact. Team/player assignments, stadiums, UI, fonts, textures, localization, models and audio are connected where supported by decoded fields. Texture replacement previews impact before applying. See [dependency evidence and validation](docs/DEPENDENCIES.md).

- **Data Explorer** — the disk tree plus the inside of every BIGF archive (nested archives, RefPack
  decompression handled transparently) and SCHl stream packs. Ctrl+F searches every name in every archive.
  **Categories** is the default view: UI (menus, APT resources, fonts, localization, HUD), Teams (uniforms,
  stadiums, logos, portraits, roster/database files), Players (models, heads, animation), Audio, Video, Textures,
  Data and System. Categories are aliases to the real assets, grouped by source pack; original archive paths,
  viewers and exports are retained. Switch to **Files and archives** to browse the physical layout.
- **Teams → school** — a school workspace with decoded roster/player database joins,
  appearance/equipment/audio fields, source editors and searchable portrait, equipment and audio
  libraries. Confirmed source rows and unresolved runtime associations are labeled explicitly;
  shared assets are never asserted as school-owned. Project edits, undo/redo and project loading
  refresh the workspace immediately. See [relationship evidence and validation](docs/TEAM_WORKSPACE.md).
  152 named schools retain uniform artwork, player texture packs, logos and stadium
  models/textures/configuration. Select **LSU** to see asset family `032` and Alex Box Stadium (`ABOXDAY`),
  then use **Open stadium in 3D**. This opens an assembled archive scene with field, sky, crowd, backgrounds and props; day/dusk/night fallback, component toggles, orbit/free camera and exact texture sources are available. See [stadium scene validation and limitations](docs/STADIUM_SCENES.md). Each link retains its source and assignment evidence. Stadium selection
  is decoded from roster records and the executable's day/dusk/night lookup tables. Registered authentic
  venues are shown separately when the stock roster selects a generic stadium instead.
  **Uniforms → Dressed player** is a customization workspace with original/edited players,
  roster selection, preview surname/number input, native glyph canvases, disc variants and texture replacement.
  Explicit roster Apply and supported texture edits use project history and export; supported GST replacements are saved; preview settings are clearly labeled. Texture sheets and menu artwork remain accessible.
  Native text builders, secondary UV streams, GS alpha composition and per-region dirt textures are implemented.
  Dirt event states are preview-only; WPF supplies scene lighting.
  See [uniform evidence, validation and rendering differences](docs/UNIFORM_PREVIEW.md).

- **Viewers**
  - SSH textures: thumbnails, zoom, RGBA/RGB/Alpha, swizzle and palette overrides, palette strip, PNG export.
  - EA audio (SCHl/BNKl): waveform, playback, WAV export (EA-XA, PS-ADPCM and MicroTalk speech).
  - 3D models (`.ord`/`.orl` EAGL objects — players, heads, stadiums): orbit viewer with Lit/Unlit/Clay/Parts
    shading, per-part toggles, head variations, a Pose tab (63-bone skeleton, per-bone rotation, presets,
    skeleton overlay), a Shapes tab (BS01–BS26 blend shapes), an Animate tab (any of the 4,854 clips in
    ANIMS.BIG: play/pause, scrub, speed, loop, in-place, body+root pairing), textures found automatically across the
    game's SSH packs, OBJ+MTL+PNG export,
    and a Draws & VIF tab that disassembles each draw's PS2 DMA/VIF stream.
    Multi-pass draws honour their GeoPrimState (alpha / additive passes, painter-ordered ground layers; toggle
    **Blending** to compare).
  - APT UI movies: stage reconstruction from paired native `.o` geometry and `.ssh` images, nested timelines,
    transforms, RGB/alpha tint, depth masks, imported characters, button states, selection, bounds and PNG export.
    A GS-style software rasteriser draws native geometry (vertex colours, texel UVs, clamp/repeat, blend state, colour
    transforms, masks); text uses the game's TrueType faces with `10_easo.loc` strings; a bounded script VM runs InitActions,
    constructors, registered classes, frame actions and the gEaso palette. Optional bgPS2 backdrop, scripts on/off, 2× smooth.
    **Limitations** lists what is approximate or unexecuted; see `docs/APT_FIDELITY.md` and `docs/apt-fidelity/`.
  - FEL frontend menus: 640×480 composition with referenced SSH artwork and FEENG text,
    selectable elements with source lines and bounds, normal/highlight/pressed/disabled/selected button
    states, individual group/layer visibility, hidden-element inspection and the original FEL source.
    Includes resolve recursively with cycle detection; matching SFN bitmap fonts provide native glyphs and
    kerning. Sparse keyframe snapshots drive a playable/scrubbable timeline (linear interpolation), with
    group translation and opacity inherited by children. Entrance animations open at their settled pose;
    the PS2 console menu variant is enabled when present. Preview-only X/Y/width/height and text overrides
    let you try changes; Export PNG saves the 640×480 canvas. Game files are unchanged.
    **Resources and fidelity** lists includes and missing assets. Special blend modes, easing and game-driven
    scripts/data are not emulated. Missing artwork is magenta; missing native fonts use a counted fallback.
  - MPC movies: playback with seek/volume and MP4 export (needs `ffmpeg.exe` on `PATH`; transcoded once into
    `%TEMP%\MVP-Workshop\movies`).
  - SFN fonts: live text preview with kerning, glyph atlas with cell overlay, glyph and kerning tables, PNG export.
  - LOCH string tables (`FEENG.LOC` …): searchable id → text grid.
  - AEMS audio: ABKC banks play their embedded BNKl sounds; PROJx `*HDR.BIG` packs are split by their `.OFF` index
    and each speech header shows its fields beside the paired stream; `MVP.CSI` label table.
  - ELF: sections / symbols / segments; `.ord` + `.orl` split objects are joined automatically.
  - CSV/TSV tables, text/config files, TrueType preview, archive listings.
  - Every asset: **Hex** (virtualised, with a data inspector), **Strings**, **Chunks** (generic 4CC walker).
- **Format Survey** — walks all ~38.5k files (including archive contents), identifies each format, and in deep
  mode parses/decodes every asset. Export as Markdown.

## Layout

| Path | |
|---|---|
| `src/MVPWorkshop.Core` | Format parsers/decoders and the virtual file system (no UI dependencies) |
| `src/MVPWorkshop.App` | WPF application |
| `src/MVPWorkshop.Probe` | CLI: `survey <game> --deep --out report.md`, `ls <game> <vpath>`, `export <game> <vpath> <dir>`, `models <game> [--filter x]`, `vif <game> <vpath> [draw]`, `obj <game> <vpath> <dir>`, `skin <game> <vpath> [morph vpath]`, `syms <game> [text]`, `anims <game>` (decode + validate every bank), `speech <game>` (decode + validate every MicroTalk stream), `banks <game> <dir>`, `apt <game> [vpath] [--ctx]` (dump one movie, or tally opcodes across all), `hdrs <game>` (pair speech headers with streams), `models … --states` (per-draw blend state) |
| `docs/FORMATS.md` | What is known about each format |
| `docs/FORMAT_SURVEY.md` | Latest deep survey output |

Developer aid: `MVP-Workshop.exe --snapshot out.png --open DATA/GHEAD.BIG/gaaaaa.ssh` renders the window to
a PNG and exits.
Menu snapshot controls: `--menu-animation ENTER --menu-time 0.5`, `--menu-show GROUPNAME` (repeatable),
and `--categories` (show the categorized browser). CLI validation: `MVPWorkshop.Probe fel-test <game>`,
`MVPWorkshop.Probe fel <game>` (all layouts and timeline samples), `MVPWorkshop.Probe catalog <game>` (coverage/uniqueness).
Team/geometry checks: `MVPWorkshop.Probe team-test <game>`, `MVPWorkshop.Probe teams <game> LSU`,
`MVPWorkshop.Probe apt-native <game>`. Snapshot controls also include `--team LSU` and `--apt-frame 10`.

## Credits

PS2 swizzle and EA GST texture algorithms are ported from
[ReverseBox](https://github.com/bartlomiejduda/ReverseBox) /
[EA-Graphics-Manager](https://github.com/bartlomiejduda/EA-Graphics-Manager) (GPL-3.0).
EA SCHl/BNK/EA-XA layouts follow [vgmstream](https://github.com/vgmstream/vgmstream); the MicroTalk decoder's
tables and structure follow vgmstream's `utkdec.c`, checked instruction-by-instruction against the game's decoder.
The APT structure layout follows [OpenSAGE](https://github.com/OpenSAGE/OpenSAGE)'s reverse engineering of EA's PC
APT format. MPC playback uses [FFmpeg](https://ffmpeg.org) (not bundled).





Interactive menu testing: open an APT or FEL **Menu simulator** tab. See [controls, decoded context, simulated services and validation](docs/MENU_SIMULATOR.md).

APT texture editing: use **Textures** or **Edit textures…** inside an APT movie to
replace images from its paired SSH pack or resolved imported packs without leaving
the APT editor. Supported edits refresh the stage and use normal project history
and mod export. See [details](docs/EDITING.md#texture-editing-inside-apt-movies).


**Disc images:** use **Open ISO** to browse/edit a cooked 2048-byte ISO9660 image directly.
**Export ISO** writes a new verified image; extracted-folder projects select the matching
original ISO as a layout template. Original sources remain unchanged. See
[ISO support and limitations](docs/ISO_EDITING.md).
