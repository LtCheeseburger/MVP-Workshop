# Read-only base layer implementation status

Roadmap 2.2 is not complete yet. The core layer and export isolation are implemented;
the selector, origin presentation and project persistence still need to be connected.

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

## Remaining integration work

- Select compatible installed mods using the launcher catalog/order, independently
  of the launcher's saved enable flags; never write launcher settings.
- Invalidate explorer/search/team/dependency caches and refresh documents on a base
  change. Mark source files as `from <mod>` in the tree and viewers.
- Persist exact base references with project/autosave files and restore the layer
  before resolving project edits. Warn on missing/changed mod sources.
- Preserve project-owned edits to base-added files if the base selection changes.
  They must remain resolvable/exportable after their source layer is removed. Do not
  expose selection changes that strand overrides on missing virtual nodes.
- Add live WPF checks for selection, origin labels, viewer data and project reloads.

`tests/BaseMods` currently covers ZIP/folder order, clean mounts, base additions,
removals, partial DDS composition, new texture packs, vanilla restoration, undo,
snapshot namespace isolation, project-only export and unchanged source hashes.
