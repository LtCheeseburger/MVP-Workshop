# Port integration evidence

The local checkout with the format-1 `.mvpmod` contract is:

`E:\Recomp Source\MVP06-NCAABB\MVP06-Base\MVP06-XBERecomp`

The sibling `E:\Recomp Source\MVP06-NCAABB\MVP06-XBERecomp` is another checkout.
The user has been asked which checkout should receive Phase 4 port changes.
No port files have been modified by this roadmap implementation.

## Installed mods

`docs/MVPMOD.md`, section 3, specifies `mods/` beside `MVP06.exe`, independent of
portable mode. Both ZIP `.mvpmod` files and directories containing `mod.json` are
discoverable. `mods/mods.json` has format 1 and an `order` array of `{id, enabled}`.
Later entries win. Discovered mods absent from the list are disabled. The implementation
is in `sdk/runtime/kernel/mods.cpp`. `--mod <file or folder>` adds the test mod above
the enabled list without changing launcher settings.

## Logs

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
