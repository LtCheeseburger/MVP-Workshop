# Workshop reload protocol 1

Roadmap 4.1 uses the prompt's tool-side implementation plus precise port-side design
option. Workshop implements publication, process reuse and acknowledgement handling.
The selected top-level port now implements a conservative receiver: it atomically
remounts changes only to assets the game has never opened. Previously opened assets,
containing archives, or additions/removals in previously enumerated directories
return `restart-required`, leaving the entire old layer active. This avoids touching
live guest/GPU resources. The MVP06-Base reference checkout has not been changed.
Live-resource replacement and real gameplay validation remain outstanding.

## Discovery and launch

The port release's `port-manifest.json` advertises `"workshopReload": 1` only after
implementing this contract. Workshop does not infer support from version numbers.
Older/newer/absent values use the existing launch-only behavior; a second Test while
that process runs asks the user to close it, rather than launching another instance.

For a capable port Workshop starts:

```
MVP06.exe --mod <absolute generation folder> --workshop-reload <absolute reload.json>
```

Each test run has its own directory under the Workshop temporary test folder. The
first `--mod` works with the existing loader. The new flag identifies the publication
pointer. Keep it associated only with this process and this extra mod, never with
the launcher's installed-mod settings. Project/executable changes require closing
the previous game; losing the log watcher does not lose the process identity.

## Atomic publication

The tool exports a complete ordinary format-1/layout-1 mod into
`generations/<revision>/`, where revision is a fresh 32-character lowercase hex GUID.
It closes all files, writes a temporary request and atomically replaces `reload.json`:

```json
{"format":1,"revision":"0123456789abcdef0123456789abcdef","mod":"generations/0123456789abcdef0123456789abcdef"}
```

Failed exports never change the request. Generations are immutable and retained for
the process lifetime: the runtime can finish outstanding reads against the old one.
An empty files directory is valid and means remove all Workshop overrides. It must
fall back to enabled installed mods and then the game, including invalidating files
removed from the previous generation. Ordinary user exports still reject no-op mods.
Generation retention currently has no automatic pruning; stop the game before
manually clearing old Workshop test directories under the system temporary folder.

## Runtime implementation points

Verified source locations in the discovered MVP06-Base checkout:

- `sdk/runtime/app/main.cpp`: the command-line loop around `--mod` and the call to
  `kernel::mods::Init`. Parse the new flag and register the watcher after initial
  loading. Never pass an unrecognized Workshop flag through as a game argument.
- `sdk/runtime/kernel/mods.cpp`: the current `Init` fills global mod/file maps.
  Refactor those maps into an immutable mount-state object. Build the next state
  from enabled launcher mods plus the requested generation using existing format,
  layout, XBE, dependencies, removals, RefPack and DDS validations. Do not call `Init`
  in place on a background thread while `Stat`, `Load`, `Exists` or packing reads
  access its globals. Readers hold a shared generation reference for the entire
  operation, including generated BIG/XSH buffers.
- Texture reconstruction in `sdk/runtime/kernel/xsh.cpp` and the renderer's GPU
  resource cache need generation-aware invalidation. The affected set is the union
  of old/new paths and removals, plus containing BIG/XSH assets and their derived
  resources. Paths that disappear must invalidate too. Defer GPU disposal until
  in-flight frames complete; do not replace texture bytes beneath active readers.

Poll the pointer every 100–250 ms, or watch its parent directory and reread on rename.
Read at most 1 MiB; require format 1, a valid revision and exactly
`generations/<revision>` as the relative path. Resolve against the request's parent,
reject traversal/absolute paths and reparse-point escapes. The launch flag itself
is local-user supplied. Do not treat the JSON as commands. Validate the entire new
layer before modifying active state. A revision is processed at most once unless
the previous attempt explicitly failed transiently before any state change.

Perform state swaps at a game-thread safe point. Initially allow reload only at a
known front-end/menu state with no active match and no outstanding guest file
handles tied to changed assets. Drain work or retain the old generation for handles
that cannot be drained. Clear/rebuild dependent native and guest caches as one
transaction. Database/roster reload requires reconstructing guest database state;
until that exists, return `restart-required` for those changes. Never report an
unsupported data change as applied merely because the host file map was replaced.
If any affected resource cannot be invalidated safely, leave the **entire** active
generation unchanged and request restart. Reject invalid mods without partial swaps.

The implemented unopened-asset receiver uses a recursive mount lock around complete
file open/read transactions and VFS reconstruction, and records asset/archive opens
and directory enumerations. It validates the candidate before swapping its mount
maps, with rollback if reindexing throws. Since it refuses all changed assets with
existing consumers, there are no affected guest/GPU resources to invalidate in an
accepted transaction. A Win32 timer polls the pointer every 250 ms. Extending this
mode to already-loaded assets requires the game-thread/cache work described above.

## Acknowledgement

After the safe-point transaction, write `reload.ack.json` beside the pointer through
a same-directory temporary file and atomic rename:

```json
{"format":1,"revision":"0123456789abcdef0123456789abcdef","status":"applied","messages":["Reloaded 2 textures at the main menu"]}
```

Statuses: `applied` means all changed resources are active; `restart-required` means
the old generation remains active because a resource/state cannot reload safely;
`rejected` means validation failed and the old generation remains active. Include
relative asset paths and concrete reasons in messages and the ordinary runtime log.
Process initial pointer revision consistently with the already mounted `--mod`
generation; acknowledge it without double-applying it.

Workshop ignores stale revisions, waits up to eight seconds for the requested
revision, and reports missing/rejected/restart-required acknowledgements as warnings
in Diagnostics. It never claims success from publication alone and never kills or
restarts the game automatically. A slow runtime may still finish after the timeout;
its ordinary log remains visible. The next Test publishes another full generation.

## Validation before advertising support

The self-contained console suite checks pointer preservation on export failure,
immutable old generations, exact revision acknowledgements, all statuses, capability
gating and revert-all. The live WPF test launches a disposable protocol peer and
checks repeated Test reuses that process, acknowledgement reporting and empty-layer
publication. These are transport checks, not evidence of real runtime cache reload.

Port acceptance must additionally test an edited texture and its containing archive,
deleted/reverted overrides, roster restart-required behavior, an active match,
outstanding reads, GPU frames, malformed requests, invalid mods, rapid successive
revisions, project switching and process exit. Hash the installed game and launcher
settings before/after; they must remain unchanged.

Run `./scripts/verify-port.ps1 -BuildPort` for native, no-game verification in the
selected checkout. It builds MVP06 Release and compiles the actual `mods.cpp` and
`inflate.cpp` with a standalone harness. Checks cover new bytes, preserved old bytes
on restart-required/rejection, unopened additions/reverts, containing archives,
directory enumeration, traversal and mutual exclusion. The reviewable native patch
is [port-phase4.patch](port-phase4.patch). These checks do not claim live-texture or
gameplay coverage.
