# Read-only base mods

Tools > Installed mods as base reads the port's `mods/mods.json` order and discovers
ZIP/folder packages beside it. Select compatible mods independently of launcher
enable flags. Later selections in launcher order win. The selector never writes
launcher settings. Tree rows and asset viewers show `from <mod>`.

## Core contract

`BaseModLayer.Build(root, sources)` reads selected ZIP or development-folder mods in
the supplied load order. It checks format/layout/XBE compatibility, rejects duplicate
IDs, merges loose files, respects archive-member removals, reconstructs partial/full
DDS folders and creates virtual nodes for new files and texture packs. It writes
neither the installation nor installed mods. Build itself does not change the active
VFS. Mod references retain source path, id, version and a content fingerprint.

`EditSession.SetBaseLayer` supplies the layer through the asset byte/child hooks.
Project overrides take priority. Mounting alone contributes no overrides or Added
files. `BaseBytes` is the comparison baseline for editing; `ReadOriginal` still reads
the unmodified installation for existing nodes. Synthetic base nodes retain their
source bytes in memory.

`EditSession.Resolve` and snapshots retain their own namespace, independent of later
active VFS switches. Export only collects explicit project changes, including an
intentional restoration of vanilla bytes over a changed base. Parent texture edits
are compared with the base pack, so unrelated base DDS slots are not packed into the
new mod. RefPack flags on edited base-added members are retained.

## Project behavior

- Changing the base marks the project dirty, closes stale asset/workspace documents
  and invalidates explorer/search/team/dependency caches.
- Project/autosave files store ordered source paths, ids, versions and fingerprints.
  Loading restores the layer before resolving edits. Changed contents warn; missing
  or incompatible sources stop loading with an error and preserve the active overlay.
- Editing a base-added asset promotes it to a project-owned Added file. Undo removes
  the copy; redo restores it. The copy survives deselection, saving and reloading.
- Explicit edits to original files take precedence over a base-mod removal. Revert
  exposes the removal again. Opening a mod retains the current base selection.

`tests/BaseMods` currently covers ZIP/folder order, clean mounts, base additions,
removals, partial DDS composition, new texture packs, vanilla restoration, undo,
snapshot namespace isolation, project/autosave persistence, changed/missing sources,
project-owned copies, project-only export and unchanged source hashes. The live
`--base-mods-selftest` exercises the actual selector, source labels, viewer bytes,
cache/document reset, project reload and deselection.
