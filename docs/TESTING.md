# Verification

From PowerShell 7 on Windows, with the .NET 9 SDK:

```powershell
./scripts/verify.ps1
```

This is the same entry point used by `.github/workflows/verify.yml` for pushes,
pull requests and manual runs. It builds the solution and every test project in
Release mode, runs the self-contained console suites, generates new synthetic
fixtures, and runs sixteen WPF checks. A nonzero command exit, missing desktop report,
`FAIL` report line or desktop timeout fails the run. Desktop tests run sequentially
because they use modal dialogs and the clipboard.

Each invocation creates a new `test-output-ci/run-<guid>` directory. Reports and
screenshots stay there; the workflow does not upload artifacts. Test fixtures are
generated from source and contain no game assets. Desktop checks use an isolated
settings file through the self-test-only `MVPWORKSHOP_TEST_SETTINGS` override, keeping
normal user preferences and recent-project lists separate.

## Coverage without game files

| Project / check | Coverage |
|---|---|
| BaseMods | Read-only ZIP/folder composition, added paths, removals, merged textures, undo and project-only export isolation |
| Dependencies | Explicit references, ambiguity, reverse impact, overlay refresh, cancellation |
| Diagnostics | Texture/metadata/icon errors, previews, missing references, shared schools, snapshots |
| GameLog | Release/debug logs, incremental reads, rotation, source filtering and asset links |
| HotReload | Atomic publication, immutable generations, failed-export isolation, acknowledgements, empty layers and capability gating |
| InstalledMods | ZIP/folder discovery, order, disabled mods, conflicts, removals, compatibility |
| Iso | Synthetic disc parsing, edits, rebuilds, export verification and rejected layouts |
| MenuSimulation | Navigation, modal state, authored APT script dispatch and timeline behavior |
| MvpMod | Tiny fake loose install; export, reopen and re-export with byte-identical file maps; overrides, Added and DDS folders |
| PortCompatibility | Release/schema distinction, BOM manifests, version mismatch, newer formats/layouts and missing metadata |
| RosterCsv | CSV quoting, one/all-school round trips, scope, identity/range validation, unresolved/shared players and stale reviews |
| Tasks | School/player record attribution and undo |
| Textures | Synthetic format/dimension/swizzle round trips, editing/export and resize behavior |
| PlayerEditor | Synthetic player records, value spans, drafts, undo/redo, project and export round trips |
| LooseTextures | Synthetic XSH split/rebuild and PNG-folder round trips, duplicate tags, native-byte retention |
| Install | Install synthetic files, then verify the resulting manifest |
| WPF shell | Menus, tabs, bookmarks and shortcut behavior |
| WPF explorer | Real context-menu dispatch, dialogs, filters, Added files, export/revert and unchanged fixture hashes |
| WPF project | Dirty state, autosave rotation, save prompts, recent projects and crash recovery |
| WPF options | Validation, persistence, timer changes, log level, layout reset and crash reports |
| WPF open-mod | Import, Added files, metadata/version retention and dirty state |
| WPF diagnostics | Debounce, badge, navigation and export/Test in Game gates |
| WPF tasks | School selection, all seven task routes, workspace reuse and recent edits |
| WPF game-log | Live polling into Diagnostics, source labels, navigation and session clearing |
| WPF conflicts | Nonblocking warnings, asset navigation and successful conflicting exports |
| WPF texture-folder | Export edited packs and school sets, actual PNG decoding, unchanged re-import, output guards |
| WPF port-compatibility | Start warnings/update links, export diagnostics and rereading changed metadata |
| WPF roster-csv | Menu/workspace commands, changed-field review, Apply, range errors, undo and unchanged source files |
| WPF base-mods | Actual installed-mod selector, source labels, merged viewer bytes, cache reset, project persistence and deselection |
| WPF hot-reload | Disposable runtime peer; same-process reuse, acknowledgement, immutable old files, revert-all and retained process identity |

`tests/Fixtures` is the fixture generator. `tests/DataEdits` currently has no tracked
source project. `tests/TeamAssignments` is built, but its exact LSU/player-key and
authentic-venue assertions require the supported real validation installation;
they are not represented as synthetic evidence.

## Checks requiring a game installation

Native port reload checks require the selected port's sources and Visual Studio
toolchain, but no game assets:

```powershell
./scripts/verify-port.ps1 -BuildPort
```

This builds the port's Release target and tests its actual mod-loader sources with
synthetic generations. Unopened assets can reload; already-open assets/archives and
cached directory topology changes require restart. This separate check is excluded
from standalone Workshop CI because the port sources are external.

```powershell
./scripts/verify.ps1 -GameFolder 'D:/validation-install'
```

This additionally runs TeamAssignments, PlayerEditor and LooseTextures against the
provided files, with output under the verification run directory. TeamAssignments
contains assertions specific to its original validation dataset; an arbitrary
installation is not a substitute for that dataset.

The older full-editor `--selftest`, `--iso-upscale-selftest`, `--apt-texture-selftest`,
`--menu-selftest`, `--uniform-selftest`, `--workspace-selftest` and
`--assignment-selftest` modes need their documented game assets and platform-specific
fixtures. They are not part of the no-game CI run. Run them individually with the
appropriate `--game` source and an output report path. Do not interpret a successful
synthetic run as proof of those real-game checks.

Use `-SkipDesktop` for console-only verification, `-Configuration Debug` for a Debug
build, or `-OutputDirectory <folder>` to choose where new run directories are created.

Phase 4 adds console suites `LiveTextures` (9 checks) and `GameLaunch` (11 checks),
plus the fifteenth desktop mode, `--launch-targets-selftest`. It checks actual target
dialogs, automatic defaults, capability gating and process arguments through a disposable
peer. `verify-port.ps1 -BuildPort` builds the selected MVP06-Base runtime and matching DLL,
runs 19 native loader checks and 7 real renderer CPU checks without proprietary assets.
See PORT_INTEGRATION.md for separate real-game target and visible GPU validation.

`SchoolFork` covers complete family isolation, free-slot allocation, other-school
preservation, Added VFS resolution, grouped undo/redo, stale review protection, preview
role mapping and decoded fabric colours. `--replacement-selftest` is the sixteenth WPF
mode and exercises both scope choices, project reload and export. The real-data
`--uniform-selftest` additionally checks role switching, swatches and actual dressed
surface changes/restoration; it requires the PS2 validation assets and is not part of CI.
