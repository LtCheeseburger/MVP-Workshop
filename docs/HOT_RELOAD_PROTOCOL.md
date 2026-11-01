# Workshop reload protocol 1

Workshop and the selected MVP06-Base port implement atomic publication, process reuse,
revision acknowledgements and resident texture replacement. Supported loose XSH slots
(DXT1, DXT3 and Morton BGRA) reload at renderer frame boundaries. Unopened data remounts;
loaded databases/rosters, unsupported texture layouts, changed slot topology and ambiguous
shared texture identities return `restart-required`, preserving the entire previous layer.
The earlier top-level port retains its older unopened-assets implementation.

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

## Runtime implementation

`kernel/workshop_reload.inc` polls every 250 ms. It validates format/revision, requires
exactly `generations/<revision>` beneath the request, validates the complete mod, and
uses the mount transaction lock for VFS reconstruction. Existing reads finish before
publication. Failed validation or unsupported loaded resources preserve the old mount.
Changed directories already enumerated by the game also require restart unless covered
by a supported texture mapping. Reindex exceptions roll back the old mount.

Every generation includes `live-textures.json` and auxiliary DDS images:

```json
{"format":1,"problems":[],"mappings":[{"assetKey":"example","slot":0,"packPath":"art/example.xsh","file":"live-textures/000000.dds","keys":["6175d92eed39ad7d"]}]}
```

The tool computes the port's exact texture content key from format, dimensions and
native level-zero bytes. Keys include source, selected base, current edit and prior
generations. Revert publishes the original image for keys still resident in guest memory.
It scans source/base packs for shared identities and refuses ambiguous mappings rather
than changing another asset. New Added packs and unsupported raw slots fall back to the
unopened-data path; editing those after load requires restart. Sidecars are test-only and
never enter exported `.mvpmod` payloads.

`workshop_live.inc` validates paths, nonempty keys, uniqueness, problem reports and size
limits. `gpu/workshop_gpu.inc` decodes all DDS files before mount publication; no decode
occurs on the renderer thread. Commit queues an immutable replacement map. On the next
renderer update it swaps maps, increments the texture generation and releases texture
caches through the existing frame-fenced garbage queue. Original guest texture memory
stays intact. The Workshop map works independently of user texture-pack settings.
Acknowledgement waits for this renderer boundary. An empty map clears prior overrides.
Loaded nontexture data never reports applied merely because the VFS map changed.

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

## Verification

`./scripts/verify.ps1` covers immutable publication, process reuse, revision/status
handling, revert-all, native content-key parity, history and shared identity refusal.
`./scripts/verify-port.ps1 -BuildPort` builds both Release runtime and game DLL, runs
19 checks against the actual mod loader, 9 tool texture-plan checks, and 7 native renderer
checks for DDS staging, frame-boundary acknowledgement, replacement and clearing.
These renderer checks exercise actual CPU renderer code without creating a GPU device.
Real-game launch validation and its limits are recorded in PORT_INTEGRATION.md.

For optional real GPU audits only, `--workshop-texture-audit` enables configured texture
dumping and requests a screenshot after each acknowledged generation. It is never passed
by normal Test in Game launches. Dumps remain beside the validation executable; captures
are beneath the isolated user directory. Stop the validation process before clearing them.