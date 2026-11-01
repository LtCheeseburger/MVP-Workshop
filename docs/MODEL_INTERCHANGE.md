# Model interchange

Open an EAGL ORD model with its ORL partner. The model toolbar offers sibling hi/me/lo body/head LODs,
wireframe, smooth normals, UV layout, OBJ and glTF export. Materials lists every draw material and its
resolved texture; unresolved textures stay labelled. Pose offers bone display and manual posing.
UVs retain native repeat coordinates. Inspection geometry is capped for responsiveness.

glTF exports the complete native vertex pool, indexed material primitives, smooth normals, UVs,
resolved textures as embedded PNGs, and available skeleton/skin. Coordinates remain in native game
units; choose the desired scale in the destination application. OBJ remains a static mesh export.

## Position replacement

Use **Import glTF positions** on the base ORD model. Export from Workshop first and preserve the
complete vertex pool, order, material primitive order and exact triangle indices. Apply mesh-node
transforms before import. JSON `.gltf` with embedded buffers or local buffers inside the import
folder is supported; GLB, sparse accessors and normalized position storage are not.

Replacement supports decoded Xbox FLOAT3 streams only. PS2 VIF and quantized Xbox streams remain
export-only. This is a position edit: UVs, native normals, materials, weights, bones, topology,
relocations and bounds remain byte-exact. Keep positions within the original bounds. Shared stream
vertices must retain identical positions. Reopen the asset after committing to refresh the preview.
Large shape changes may require native normal/bounds rebuilding, which is not implemented.

Imports use the replacement impact review and project undo; they never write to source game files.
The importer checks both ORD and ORL again after review to reject stale source data.

## Animation playback and export

Choose the model in its model picker, then Animate. Choose a bank, category and clip; readable names
retain the native identifiers. Playback supports pause, scrub, quarter/half/normal/double speed,
loop, frame count, optional root motion and in-place preview. Pose supplies the bone overlay.
The control area scrolls on short windows so the clip list remains visible. Loose-install bank
names are matched without case sensitivity.

**Export clip glTF** writes the selected clip on the current model, with available skin and resolved
textures. Body `_e` and enabled root `_r` tracks are sampled at the native 30 fps into local
translation/quaternion channels. Quaternion signs remain continuous. The clip skeleton must match
the model. Missing channels retain bind transforms. In-place preview and manual pose adjustments
are preview controls and are not baked into the exported native clip.

Native clip editing, retargeting, changing animation duration and writing compressed animation
banks are not supported. Decoded scalar slots outside bone translation/rotation are not exported
as glTF animation channels. Changing a mesh does not modify its animation banks.

## Validation

`tests/ModelInterchange` checks mesh and skin export, topology/transform validation, sampled animation
values and unsupported writes. With a game-folder argument it also verifies a real Xbox model's
unchanged byte round trip and reopens an edited mesh through the native decoder.
The desktop `--model-viewers-selftest report.txt` check expects a real loose installation containing
`data/geometry.big/mebody.ord`; it checks LOD/material/overlay behavior, compatible clip selection,
timeline scrub/pause and native model/clip exports. It writes a screenshot beside the report.
