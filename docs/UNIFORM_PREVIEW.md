# School uniform preview

Open **Teams → LSU → Uniforms → Dressed player**. The same view is available for
every decoded school. Select a disc suffix (`a` or `b`), drag to orbit, right/middle
drag to pan, or scroll to zoom. Fit, Front, Back, Side and Three-quarter presets
and Lit/Unlit shading reuse the EAGL model viewer. Resizing fits the player again.
Switching uniforms preserves the camera.

The workspace shows original disc and edited project players side by side. Select a
roster player, or enter a surname and number (0–99). The same selected disc variant
is used on both sides so texture edits can be compared. The original side uses
original texture bytes and the player's original DAT surname/number; the edited
side uses project bytes and preview text. **Match original camera** copies the
edited camera to the original side. Each side retains its camera presets.

**Apply name / number to roster** explicitly commits fields 1 and 2 of the selected
`attrib.dat` record through project history. Preview typing does not edit the DAT.
The complete surname is saved; the native rendering path displays ten characters.

Choose a texture tag and **Replace selected texture…** with an exactly sized image.
PAL4/PAL8/RGBA8888 replacements use the existing SSH writer, undo/redo, Save Project
and Export Mod. GST images (including `tors` and most pants sheets) remain
**PREVIEW ONLY**, using an undoable viewer replacement. They are excluded from
projects and exported archives. The capability appears when selecting a tag and
after replacement. **Open texture editor** retains sheet inspection and editing.

Selected variant, player, preview text, camera, and sample appearance are preview
settings, not saved game settings. Temporary texture replacements and preview text
survive school-view rebuilds after an edit or undo; a new project session discards
them. The original **Texture sheets & menu artwork** subtab is retained.

**Texture sources** opens the exact SSH assets and shows material-to-tag bindings.
Native name and number canvases are displayed beside the model. The evidence
expander distinguishes decoded rules from remaining 3D rendering differences.

## Evidence and scope

- The roster's decoded 9-bit uniform slot selects `u%03d%c` and `f%03d%c`.
  LSU's record at `0x551C`, field at `0x55C0`, selects family `032`.
- `GEOMETRY.BIG/mebody.ord` + `.orl` is the medium-detail body; its native model is
  `mebody_t`. `hihead.ord` + `.orl` supplies the head. Both are decoded by EaglReader.
  The preview combines their meshes, retaining positions, UVs, skin weights and
  primitive states. It uses representative cap, torso, hands, arms, legs and shoes
  parts, not every mutually exclusive equipment piece at once.
- SLUS-215.82 VA `0x85C8B8` is a 39-entry uniform texture-name table. The loader
  at `0x43C588–0x43C600` resolves these entries into uniform object slots.
- VA `0x85CAE8` is the material table used by binder `0x4449B8`. The jersey
  path `0x43F058–0x43F304` reads uniform slots 4/5 and 17/18 and binds the standard
  `jerk/jers/slvl/slvr` materials to `tors`. The preview reads the actual table
  strings from the ELF's load segments rather than matching similarly named tags
  across unrelated schools. `hats` binds `hat`; the corresponding native leg and
  helmet tags also come from these tables. Padded four-character SSH tags such as
  `hat ` are normalized at the preview boundary.
- Shared basebody/basehead/flesh resources are in
  `INITSTAZ.BIG/initstay.big/models/textures/`. Sample shoe `bshoe0.ssh` and head
  `GHEAD.BIG/gaaaaa.ssh` are explicitly listed in the source inspector. Skin index
  0, the head and equipment are sample choices, not decoded roster appearance.
- No home/away interpretation is assigned to suffixes. Frontend artwork remains
  separately accessible and is not used as a player texture.

## Native text builders and remaining rendering differences

Decoded from the supplied SLUS-215.82 executable and its generated instruction
listings in `config/output` (addresses are virtual addresses):

- `0x43E7B8` allocates a 256×32 name texture, a 64×64 number texture and a
  32×32 secondary number texture at object offsets `+0x0C/+0x10/+0x14`.
- `0x4408F0`: jersey numbers are converted to one digit below 10, otherwise
  quotient/remainder decimal digits. Thus `00` renders as `0`. The `zig0`
  dimensions provide the fixed cell size. `0x9223C8` contains output widths
  64 and 32. Digits center with integer division; the border is excluded and
  the source glyph interior is copied without a system-font substitution.
- `0x440D90`: copy at most ten bytes of surname; prefer `A___`, else `a___`;
  normalize ASCII case. Nonletters retain their cell advance but draw nothing.
  Each letter uses a 16×32 cell. Odd lengths start at
  `72 - (length-1)*8`, even at `80 - length*8`. Letters advance 16 pixels.
  The output allocation is 256×32; the builder centers in its first 160 pixels.
  Missing alphabet glyphs fall back to `zig0` as the runtime does.
- `0x4418E0` clears an indexed canvas with the source's first pixel index.
  The CPU builder clears to that pixel's decoded colour/alpha, then copies native
  glyph interiors. Composition uses unscaled GS alpha (0–128 for ordinary opacity),
  rather than round-tripping through display alpha.

## Native secondary coordinates and GS composition

`MVPIrradTexture2_Skin0` programs have a two-entry TAR list: fabric first,
number/name/dirt second. Their skinned VIF batches upload **two** coordinate streams:
V4-16 fabric UVs plus output-slot references at TOPS+52, and V2-32 secondary ST
coordinates at TOPS+148. The old decoder discarded the latter when V4-16 UVs were
present. The decoder now retains secondary coordinates per index-buffer corner,
using each triangle strip's output-slot order, including strip restarts and winding.
It does not infer a decal rectangle, mirror, scale, or offset from mesh bounds.

`UniformCompositor` interpolates these native coordinates across the fabric-UV
triangles, samples the second TAR using its repeat/clamp modes and bilinear texel
footprint, then applies GS `ABM_BLEND`: `Cd + ((Cs - Cd) * As >> 7)`, with signed
integer shift and channel saturation. Negative/out-of-range ST repeats correctly.
This includes the name canvas's native 256-pixel backing width; no special 160-pixel
stretch is applied. The composed opaque 1024-pixel atlas is a WPF presentation cache,
not a replacement game asset. Native fabric and source glyph textures remain
editable through the existing project/export paths.

## Dirt composition

`0x43ED48` loads `models/textures/basebody.ssh` into object `+0x2E8`.
It contains the game's 128×128 `dcht` and `dbtl` stain textures.
`0x43EE28` loops region indices 1–11, uses executable table `0x85C998` for source
names and `0x85CAB8` for material names. `0x43EEC8` conditionally selects the stain
or the clean/default resource. The workspace reads these tables and uses the
region's first TAR for fabric and its native secondary UVs for the selected stain.

**Dirt regions · preview only** exposes the visible body's region states. Selecting
`dcht` enables the chest stain; the leg regions select their mapped `dbtl` source.
These are discrete native event states, not an invented universal opacity slider.
The original comparison remains clean. Dirt states survive edit/undo refreshes,
but are neither player database edits nor exported textures.

## Remaining scope

The three former rendering gaps are implemented for the representative body:
secondary UV placement, local two-texture GS alpha composition, and native dirt
region composition. WPF still supplies scene lighting, depth and antialiasing;
this is not a complete GS framebuffer/VU microcode emulator. The 1024-pixel atlas
cache adds a resampling step. Gameplay dirt-event timing/accumulation, automatic
roster equipment/appearance selection, alternate garment selection and per-school
front/sleeve number enable flags are not simulated by this static workspace.
Sample head, skin and equipment remain representative rather than roster likenesses.

## Validation

```powershell
dotnet build MVPWorkshop.slnx -c Release --no-restore -m:1
dotnet src/MVPWorkshop.Probe/bin/Release/net9.0/MVPWorkshop.Probe.dll uniform-test game
dotnet src/MVPWorkshop.Probe/bin/Release/net9.0/MVPWorkshop.Probe.dll uniform-workspace-test game research/uniform-workspace/validation
dotnet src/MVPWorkshop.App/bin/Release/net9.0-windows/MVP-Workshop.dll --uniform-selftest research/uniform-workspace/ui.txt
```

`uniform-test` checks all 152 schools / 304 variants, executable standard material
bindings, every uniform image and the ten number glyphs in every paired font.
`uniform-workspace-test` covers LSU, Alabama and Stanford, both suffixes, numbers
0/7/10/33/99/00 and names of lengths 0/1/3/8/10/12 including punctuation/spaces
(84 cases). It checks native crop/centering, case normalization and truncation,
invalid numbers, supported font replacement, roster changes, texture and preview
undo/redo, project reload and verified archive export with preview exclusions.
The compositor checks raw VIF output-slot coordinates, complete triangle-corner UVs,
GS alpha endpoints/signed rounding/saturation, negative-ST repeat, TAR clamp, native
dirt bindings and clean/decorated fabric outputs across both variants of all three
schools. The UI test exercises real roster Apply, undo, retained preview settings and both
comparison models across the same three schools, and writes front/back screenshots with the native dirt states enabled.

For manual captures select Uniforms (outer tab 6):

```powershell
dotnet src/MVPWorkshop.App/bin/Release/net9.0-windows/MVP-Workshop.dll --snapshot lsu.png --categories --team LSU --tab 6 --uniform-variant b --uniform-back --uniform-number 33 --uniform-name WILLIAMS --size 2100x1200 --wait 10000
```

`uniform-sources` and `uniform-uv` in Probe retain source-tag and mesh UV diagnostics.
