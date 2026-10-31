# Port integration evidence

The local checkout with the format-1 `.mvpmod` contract is:

`E:\Recomp Source\MVP06-NCAABB\MVP06-Base\MVP06-XBERecomp`

The user selected the top-level `E:\Recomp Source\MVP06-NCAABB\MVP06-XBERecomp`
for Phase 4 changes on October 31, 2026. This folder contains runtime sources and
existing Debug/Release CMake/Ninja build directories, but no Git metadata. Preserve
original copies and reviewable diffs for port changes. No applicable AGENTS.md was
found in the target/ancestor folders (dependency SDL instructions do not apply).
No port files have been modified by this roadmap implementation.

The selected checkout also has the format-1 mod loader. `main.cpp` calls
`kernel::mods::Init` once before `kernel::Boot`; the game runs on a separate thread
from the Win32 message loop. `mods.cpp` owns mutable global mod/file/hidden/RefPack
maps. `files.cpp` holds shared `modvfs::Virtual` handles, and transfers into guest
memory in `Transfer`. `gpu/texture.h` exposes `MarkDirty(resource)`/`DirtyEpoch`, but
there is no verified file-to-live-guest-texture ownership map yet. Do not report a
reload as applied merely after remounting host files. `src/game/startup.cpp` and
`src/common/startup_ui.cpp` own native front-end routing; diagnostic environment
variables already exercise menu/matchup navigation, but are not production target
flags. Runtime implementation and real-game validation remain outstanding.

## Release compatibility metadata

`release.bat` writes `version.txt` and `port-manifest.json`. The latter currently
contains `{format: 1, version, files}`: its `format` describes release packaging,
not the runtime's mod format. It currently does not advertise runtime mod/layout
capabilities. Workshop displays that absence as unconfirmed compatibility.

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

The proposed Workshop hot-reload transport and exact runtime implementation work
are specified in [HOT_RELOAD_PROTOCOL.md](HOT_RELOAD_PROTOCOL.md). The current port
does not advertise or implement that protocol; the tool gates it explicitly.

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
