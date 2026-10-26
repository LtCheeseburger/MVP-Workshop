# Assembled stadium scenes

Opening `mstadium.ord` opens the stadium scene rather than an isolated structure. The school button explicitly selects **Assigned models**; registered authentic venues remain separate links. Opening an archive that has no stadium enum mapping retains that archive and reports the missing mapping instead of substituting enum zero.

## Controls and resources

Day/Dusk/Night uses the executable map and its requested-time → dusk → night → day fallback. The status shows both the requested time and the map row used. Field 0–2, Sky 0–4 and Crowd 0–2 select exclusive resource variants. These numbered variants retain their disc labels; they are not assigned invented weather names.

Drag to orbit/look, right-drag to pan, wheel to zoom or move forward in free mode. Free camera uses W/A/S/D and Q/E, with Shift for larger movement steps. Fit resets the view. Each component has a visibility checkbox. Configuration buttons open the original assets. **Texture sources** lists every binding's exact archive path and unresolved resources.

Geometry retains native world coordinates. Camera-operator meshes are already placed in those coordinates. Flags use decoded V3-16 positions plus `flags.csv` scale/translation; wind phase and runtime flag material selection remain unavailable. Unbound flag materials are magenta. The mound-shadow component has a runtime material binding and defaults off.

Textures are decoded exclusively from the selected resolved stadium archive. Matching `fieldN.ssh` and `vfieldN.ssh` take precedence for field N. Crowd `v000` uses local `crowdN.ssh/x000`; the executable contains these runtime tag names at VA 0x92E040/0x92E050 and pointer entries at 0x85E610–0x85E620. Texture provenance reports the alias explicitly. Missing textures/materials are listed and drawn magenta.

## Rendering and decoding fixes

The stadium renderer uses perspective-correct UV/colour interpolation, near-plane clipping, a depth buffer, native clamp/repeat settings, bilinear sampling, alpha testing and GS blend equations. It shares the existing tested GS sampler/blender with the APT renderer. Authored vertex colours supply the static lighting; this does not simulate dynamic stadium lights or weather.

Sky and field painter passes precede the structure; crowd and alpha components follow. Native field-line state is rendered as lines rather than triangle strips. The `lod1` distant-field alternative is excluded from the detailed field pass. This removes the previous large white triangles and duplicate field layers.

ELF symbol strings now read through their bounded string-table entries rather than truncating at 256 characters. This preserves render/TAR settings. V3-16 flag position uploads now decode as positions instead of allowing the normal stream to be mistaken for geometry.

## Validation and captures

Release App/Probe builds pass. `stadium-test game` assembles all 36 resolved archives across all 102 stadium/time resolutions, checks archive-local texture provenance, finite geometry, index validity, and line/triangle separation. Synthetic raster checks cover depth occlusion, visibility, vertex alpha and near-plane clipping. `team-test game` checks 152 schools and all 34 authentic registry entries. Existing APT raster checks pass after the shared sampler and ELF changes.

Captures in `stadium-scenes/`:

- `lsu-day.png`: Alex Box, assembled field/structure/crowd/background/props.
- `lsu-dusk-fallback.png`: requested dusk and the native fallback result.
- `authentic-arod.png`: another authentic venue.
- `generic-g001.png`: numbered generic park with authored field geometry.
- `generic-dusk.png`, `generic-night.png`: explicit custom-venue limitations.

## Remaining fidelity limits

The customizable `VENU*` archives contain tiny placeholder field triangles and require runtime field/wall construction and shared CSASSETS selections. Their missing field sections are reported explicitly; geometry or textures are not silently borrowed from another venue. Native sky textures are displayed as stored, including the blue sky in the custom night archive's Sky 0. Choosing the night archive does not apply an invented dark tint.

`render.ifo`, `remap.ifo`, `tanim.csv`, `ganim.csv` and other native configuration remain inspectable, but dynamic visibility, scoreboard events, flag materials/wind, procedural crowds/seats, full custom-venue construction and dynamic lighting are not fully reproduced. GS alpha comparison variants and dynamic microcode effects are approximated. The scene preview is substantially more complete than a single model, but has not reached in-game visual parity.
