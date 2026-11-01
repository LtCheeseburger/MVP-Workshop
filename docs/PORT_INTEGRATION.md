# Port integration evidence

The local checkout with the format-1 `.mvpmod` contract is:

`E:\Recomp Source\MVP06-NCAABB\MVP06-Base\MVP06-XBERecomp`

The current Phase 4 implementation is in the MVP06-Base checkout above. Release runtime
and game DLL build successfully; the native loader has 19 checks and the renderer has
7 checks. `scripts/verify-port.ps1` defaults to this checkout. Existing untracked
`tools/` files remain untouched. Capability metadata advertises `workshopReload: 1`,
`workshopLiveTextures: 1`, `workshopReloadMode: "live-textures"`.
Release packaging preserves version and hashed-file metadata.
Earlier, the user selected the top-level `E:\Recomp Source\MVP06-NCAABB\MVP06-XBERecomp`.
That folder contains runtime sources and
existing Debug/Release CMake/Ninja build directories, but no Git metadata. Preserve
original copies and reviewable diffs for port changes. No applicable AGENTS.md was
found in the target/ancestor folders (dependency SDL instructions do not apply).
The port now has a native `--workshop-reload` receiver, VFS transaction locking,
opened-asset/directory-listing tracking, atomic mount replacement for unopened assets,
and revision acknowledgements. The exact source changes are in
[port-phase4.patch](port-phase4.patch), checked against preserved originals under
`test-output-port-staging/original` with `git apply --check --ignore-space-change`
(the original checkout mixes CRLF/LF files). The port Release target builds successfully.
`scripts/verify-port.ps1 -BuildPort` compiles the actual mod-loader sources into a
standalone synthetic harness; all 14 assertions pass. No game assets are required.

The selected checkout also has the format-1 mod loader. `main.cpp` calls
`kernel::mods::Init` once before `kernel::Boot`; the game runs on a separate thread
from the Win32 message loop. `mods.cpp` owns mutable global mod/file/hidden/RefPack
maps. `files.cpp` holds shared `modvfs::Virtual` handles, and transfers into guest
memory in `Transfer`. `gpu/texture.h` exposes `MarkDirty(resource)`/`DirtyEpoch`, but
there is no verified file-to-live-guest-texture ownership map yet. Do not report a
reload as applied merely after remounting host files. `src/game/startup.cpp` and
`src/common/startup_ui.cpp` own native front-end routing; diagnostic environment
variables already exercise menu/matchup navigation, but are not production target
flags. Already-open guest/GPU resources are deliberately refused with
`restart-required`; live replacement of those resources, launch targets and actual
gameplay validation remain outstanding. No automatic restart or guest-memory
overwrite is performed by the reload receiver.

## Release compatibility metadata

In the MVP06-Base reference checkout, `release.bat` writes `version.txt` and
`port-manifest.json`. The latter currently
contains `{format: 1, version, files}`: its `format` describes release packaging,
not the runtime's mod format. It currently does not advertise runtime mod/layout
capabilities. Workshop displays that absence as unconfirmed compatibility. The
user-selected top-level checkout has a different release script: it previously
wrote neither metadata file. It now copies `config/port-manifest.json` to the build
folder through CMake and includes it in the release ZIP. It advertises mod format 1,
layout 1, reload protocol 1 and `workshopReloadMode: "unopened-assets"`. It does not
invent a release version; version stays unknown when no version.txt exists.

Workshop recognizes optional positive-integer `modFormat` and `layout` fields in
`port-manifest.json` as explicit runtime capabilities. A future port release can
add these alongside the existing fields without changing the packaging schema.
The version display also detects disagreement between the two release files.
The game's `manifest.json` format is read separately as the installed loose layout
(the format written by `LooseInstaller`). Newer capabilities or mixed layouts
produce nonblocking Start/export warnings and trusted release-page links. No
versions or layouts are inferred from the release version number alone.

## Installed mods

`docs/MVPMOD.md`, section 3, specifies `mods/` beside `MVP06.exe`, independent of
portable mode. Both ZIP `.mvpmod` files and directories containing `mod.json` are
discoverable. `mods/mods.json` has format 1 and an `order` array of `{id, enabled}`.
Later entries win. Discovered mods absent from the list are disabled. The implementation
is in `sdk/runtime/kernel/mods.cpp`. `--mod <file or folder>` adds the test mod above
the enabled list without changing launcher settings.

## Logs

The implemented transport and live-texture contract are specified in
[HOT_RELOAD_PROTOCOL.md](HOT_RELOAD_PROTOCOL.md). Both checkouts now have the receiver;
future Phase 4 changes target MVP06-Base.

Verified against `src/common/game_info.cpp`, `sdk/runtime/app/main.cpp` (`UserDir`
and `wWinMain`) and `sdk/runtime/core/log.cpp`:

- Game name: `MVP06`; log prefix: `mvp06`.
- With `portable.txt` beside the executable: `<exe directory>/logs`.
- Otherwise: the Windows Documents known folder plus `MVP06 Recomp/logs`.
  The runtime falls back beside the executable if it cannot create its user directory.
- Release: `mvp06.log`; debug: `mvp06_NNN.log`, retaining the newest 30 runs.
  Additional instances add `-<instance>` to the prefix. Relaunches append to the session log.
- Debug severity is `[warn]` / `[error]`; release errors have `error:`.
  Release builds suppress ordinary debug/info/warn messages; Workshop can only report
  messages the runtime actually writes. Load-order conflict messages use the runtime's
  always-visible reporting level and contain `(over <mod>)`.
- `mods.cpp` emits `mods:` messages; texture loading in `kernel/xsh.cpp` emits
  `textures:` messages, including unreadable metadata/images and textures shown blank.

Workshop's log-reader tests use these exact formats with disposable synthetic logs.
Its live WPF check verifies polling, badge updates, asset navigation, retention across
validation refreshes, and clearing on project switches without launching a game.

The visible D3D12 audit also passed in one process (PID 47120): synthetic DDS sidecars
changed already-resident field textures to magenta during the attract-mode match;
the next empty replacement map restored the field. Revisions 2 and 3 both received
`applied` after renderer boundaries. Inspected captures are `MVP06_20261031_201700.png`
and `MVP06_20261031_201715.png` under session `reload-34005377880346c7b0d86879c51d2d3d`.
This validates the native GPU transport; the separate LiveTextures suite validates
Workshop's source-key ownership, history and exact DDS generation.
