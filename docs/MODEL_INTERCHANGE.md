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

## Validation

`tests/ModelInterchange` checks mesh and skin export, topology/transform validation, sampled animation
values and unsupported writes. With a game-folder argument it also verifies a real Xbox model's
unchanged byte round trip and reopens an edited mesh through the native decoder.
