# MVP-Workshop — MVP 07 NCAA Baseball (PS2, SLUS-215.82) and MVP 06 NCAA Baseball (Xbox, default.xbe) — file format notes

Living notes from the read-only research phase (write-side behaviour is in [EDITING.md](EDITING.md)). Every claim here is either implemented in `MVPWorkshop.Core` and
exercised by the deep survey (`docs/FORMAT_SURVEY.md`, 38,547 files, 0 parse failures), or explicitly marked
**unknown / TODO**. All multi-byte values are little-endian unless stated.

## Status at a glance

| Format | Where | Status |
|---|---|---|
| BIGF archive | `*.BIG`, nested `*.big` | ✅ full (directory, nested, RefPack) |
| RefPack (`10 FB`) | most BIG entries | ✅ decompress |
| SSH / SHPS textures | `*.ssh`, `*.SSH` | ✅ types 0x01, 0x02, 0x05, 0x0E decode (all 43,621 images in the game) |
| SCHl audio streams | `*.ASF`, `*.AST`, PROJx `*DAT.BIG`, `*.dat` | ✅ EA-XA and MicroTalk (speech) decode/playback |
| BNKl sound banks | `*.BNK`, `*.GEN`, `EasoSounds.asf` | ✅ PS-ADPCM + EA-XA decode/playback |
| ELF (EE/IOP) | `SLUS_215.82`, `*.ELF`, `*.irx`, `*.o` | ✅ sections, segments, symbols |
| Split ELF `.ord` + `.orl` | models, anims, geometry | ✅ joined into one relocatable ELF (see below) |
| EAGL models | `GEOMETRY.BIG`, `STADIUM/*.BIG` | ✅ 3D geometry, UVs, parts, textures, variations |
| Skeleton / skin / morphs | `__Skeleton`, weight tables, `*_morph.ord` | ✅ bind pose, skinning, 26 blend shapes |
| Animation banks | `ANIMS/ANIMS.BIG` (`__AnimationBank`) | ✅ all 4 channel types, 4,854 clips, playback |
| CSV / TSV / text | databases, stadium data, configs | ✅ |
| LOC text (`KEY=Value`) | `EASOAPT/loc`, `LOC_PS2.BIG` | ✅ text |
| MPC movie | `FRONTEND/MOVIES/*.MPC` | ✅ playback via ffmpeg (MPEG-2 video + EA-XA R3 audio) |
| APT UI | `*.apt` + `*.const` | ✅ characters, frames, display list, constants, full bytecode disassembly (968 movies, 1,235 actions) |
| LOCH binary strings | `FEENG.LOC`, `IGENG.LOC`, `MC_ENG.LOC` | ✅ id → UTF-16 string table |
| ABKC banks | `AUDIO/AEMS/*.ABK` | ✅ header + embedded BNKl playback; module code not disassembled |
| MOIR script (`MVP.CSI`) | AEMS | 🟡 label table; script body TODO |
| PROJx HDR | `PROJxHDR.BIG` + `.OFF`, `*NAMEHDR.BIG` | 🟡 split into records, paired with their streams; payload partly known |
| EVENTS.EVT | `AUDIO/PROJx` | 🔴 identified only |
| SFN fonts | `FONTS.BIG` | ✅ glyph table, kerning, atlas, text rendering |
| Raw RGB | `NETGUI/BG.RGB` | ✅ 640×448 RGB888 |

## Containers

### BIGF
```
0x00  char[4]  "BIGF"
0x04  u32 LE   archive size
0x08  u32 BE   entry count
0x0C  u32 BE   header (directory) size
0x10  entries: u32 BE offset, u32 BE size, char[] name\0
```
Names may contain `\` sub-paths (e.g. `cafe\artAssets\PS2\icons\c.big`) — the tool shows them as folders.
Entries are frequently RefPack-compressed; nested `.big` archives occur (e.g. `EASOAPT.BIG`, `BUNDLE.BIG`, `INITPRGZ.BIG`).

### RefPack
Header byte 0 flags: `0x80` = 4-byte sizes, `0x01` = compressed size precedes the decompressed size; byte 1 = `0xFB`.
Standard EA LZ77 command set (2/3/4-byte copies, literal runs `0xE0–0xFB`, stop `0xFC–0xFF`).

### PROJx DAT/HDR pairs (speech)
`DATA/AUDIO/PROJn/PROJnDAT.BIG` is **not** a BIGF — it is SCHl streams concatenated back to back;
`PROJnDAT.OFF` is a text list of `offset,size` for each stream. `PROJnHDR.BIG` (also not BIGF) is split the same
way by `PROJnHDR.OFF`; record *N* describes DAT stream *N* (the `PNAME/NNAME/TNAME` packs are BIGF and pair by
entry name instead, `201.hdr` ↔ `201.dat`). The tool exposes both as browsable records ("OFF-indexed Pack").

**Speech header record** (`.hdr`, 1,302 in the game) — 18-byte head, then a kind-specific payload:
```
0x00 u16 record id     0x02 u8 kind (0–6, some with bit 7 set)     0x03 u8 segment count
0x04 s32 link (-1 = none)                        0x08 u16 ?     0x0A u16 ?     0x0C 6 bytes 0
0x12 kind 0: (segments − 1) × BE u16 points, usually followed by 70 70
```
For kind 0 the points fall exactly between the spoken phrases (PROJ0 record 0: 5 phrases, points 53/87/125/177
of 218), but neither 0x0A nor the point unit maps to a fixed sample count across all records — **TODO**.

## Textures — SSH (`SHPS`)
```
0x00 "SHPS"  0x04 u32 file size  0x08 u32 image count  0x0C char[4] version ("G359")
0x10 directory: count × { char[4] tag, u32 offset }
     then (optionally) a group name such as "Buy ERTS"
```
Each image block starts with a 16-byte header:
```
+0 u8  type            (bit 7 = RefPack-compressed pixels)
+1 u24 block size      (offset to next attachment; 0 = runs to next image)
+4 u16 width   +6 u16 height
+8 s16 center x  +10 s16 center y
+12 u16 x pos : bits 0-11 pos, bit 12 referenced, bit 13 SWIZZLED, bit 14 transposed
+14 u16 y pos : bits 0-11 pos, bits 12-15 mip count
```
Attachments chain after the pixels (same header): `0x21` palette RGBA8888 (width = colour count; bit 13 of
x pos = CSM1 palette swizzle), `0x70` image name (4-byte header + string), `0x6F` comment, `0x7C` hotspots.

Types seen in this game (counts from the survey):

| Type | Count | Encoding |
|---|---:|---|
| 0x02 PAL8 | 27,820 | 8bpp indices, PS2 PSMT8 swizzle when flagged |
| 0x0E GST422 | 14,406 | EA "GST" compression (player heads, faces, uniforms) |
| 0x05 RGBA8888 | 969 | 32bpp, alpha 0–0x80 |
| 0x01 PAL4 | 426 | 4bpp indices, PSMT4 swizzle when flagged |

**GST422** (`Gst.cs`): pixels are palette indices rebuilt from a *base* plane (one byte per 4×2 block) plus a
*detail* plane (2 bits per pixel added to the base); base starts at 0, detail at `align16(baseSize)`.
Both planes are GS-swizzled when bit 13 is set. Palette is 256 × RGBA8888, CSM1-swizzled.
Alpha is PS2-style (0x80 = opaque) and is doubled for display.

Algorithms for PS2 un-swizzle and GST are ported from ReverseBox / EA-Graphics-Manager (GPL-3.0).

## Audio

### SCHl streams
Blocks: `char[4] id, u32 LE size` (size includes the 8-byte header) — `SCHl` header, `SCCl` count,
`SCDl` data …, `SCEl` end. The `SCHl` body is a "PT" patch header: `"PT" u16 platform(5 = PS2)` then tags
`tag, len, value(BE)`; `0xFC/0xFD` markers, `0xFF` end. Key tags: `0x80` version, `0x82` channels,
`0x84` sample rate, `0x85` samples, `0xA0` codec, `0x88/0x89` data offsets (banks).

`SCDl`: `u32 size, u32 samples, u32 channel offsets[ch]` relative to the end of the offset table.

Codecs found:
- **EA-XA (0x0A)**, version 3 → "EA-XA v2" frames: 0x0F bytes / 28 samples, or `0xEE` PCM frames (0x3D bytes).
  Music (`MUSIC/*.ASF`, 44.1 kHz stereo) and most SFX/commentary (24 kHz).
- **MicroTalk 10:1 (0x04)** — 17,275 speech streams (PA announcer, player names; ≈11.3 hours). Decoded in
  `Formats/MicroTalk.cs`; all streams decode with output length = header sample count.

#### MicroTalk (EA-MT / "UTK")
Verified against the game's own EE decoder (recompiled MIPS): bit reader `0x681FF0` (LSB-first), header
`0x6824F0`, frame `0x682678`, excitation `0x6820A8`, reflection-coefficient table at `0x880EB0`.
- Stream header (first bits of the first block, once per stream): 1 bit reduced-bandwidth, 4 bits threshold
  (multipulse if first RC index < 32 − t), 4 bits gain (`g0 = 8·(g+1)`), 6 bits multiplier
  (`g[i] = g[i−1]·(1.04 + 0.001·m)`, 64 fixed gains).
- Frame = 432 samples: 12 RC indices (four 6-bit, eight 5-bit + 16) interpolated over 4 steps (×0.25);
  4 subframes × {8-bit pitch lag (source `108·(i+2) − lag`), 4-bit pitch gain /15, 6-bit gain index,
  [reduced bandwidth: align bit, zero bit], excitation (multipulse Huffman pulses or 3-level RELP)};
  12th-order LPC synthesis (RC→LPC each quarter).
- In SCHl: each `SCDl` channel's data starts 1 byte after its offset; the bit reader restarts at every block
  (decoder state persists) and frames are decoded until the block's sample count is reached.
  Version-3 streams prefix every frame with a byte; `0xEE` = a raw PCM patch `{s16 offset, s16 count, s16…}` follows.

### ABKC banks (`AEMS/*.ABK`)
```
0x00 "ABKC"  0x04 u8×4 version (01 01 01 0x)  0x0A u16 module count  0x14 u32 file size
0x18 u32 end of module code  0x1C u32 → module table (always 0x5C)
0x20 u32 → embedded BNKl (0 = none)  0x24 u32 BNKl size  0x30/0x34/0x38 u32 → trailing tables
```
The module table holds compiled **MIPS** behaviour code (event logic). 15 of 23 banks embed a BNKl whose data
offsets are relative to the BNKl itself (all 221 sounds decode); the other 8 (`BATDIT`, `HECKLEx`, `VENDOR`,
`VNDRCBP`) have no samples and drive streams from the matching `.AST` pack.

### MOIR (`MVP.CSI`)
`"MOIR", u16 0x300, u16 0, u16 counts[4] (2, 206, 162, 185), u32 @0x10`, then at 0x2C a table of 368 labels
`{u32 → name, u32 script offset, u32 0}` — crowd/commentary states such as `home_team_jeering_END`,
`wild_pitch_home_BEGIN`. The script body is **TODO**.

### BNKl banks
`"BNKl", u8 version (5), u8 ?, u16 sound count`, table of u32 offsets at 0x14 (relative to each table slot)
pointing to PT headers. Data offsets (tag 0x88) are absolute. PS2 banks default to PS-ADPCM at 32 kHz.

## Code & models

### ELF
Standard ELF32 LE MIPS. Kinds: executable (`SLUS_215.82`, `EACN/BIN/*.ELF`), IRX (type 0xFF80),
relocatable `.o` (APT action script objects, 897 of them).

### Split objects `.ord` + `.orl`  ← found during this survey
EA stores each model/animation object as a relocatable ELF **cut in two**:
- `.ord` = ELF header + `.data` section, ends right before the section header table (`e_shoff >= .ord length`).
- `.orl` = everything after: `.shstrtab`, section header table, `.rel.data`, `.symtab`, `.strtab`.

`ord ++ orl` is a valid ELF (1,418 / 1,421 pairs verified). Symbols name the data, e.g.
`__Bone:::FBXdummyNode.Head`, `__Model:::hihead_model2.variation1`, `__const MATRIX4:::EAGL::ViewPort::gpViewProjectionMatrix`.
The `.data` is a block of C structs fixed up by `.rel.data` at load time.
Three `.orl`s in `INITPRGZ.BIG/initpurge.big` pair with `.ord`s in `INITSTAZ.BIG/initstay.big`.

## EAGL models (`__Model` objects)  ← decoded, see `Formats/Eagl/`

All relocations are `R_MIPS_32`. A slot either points into `.data` (section symbol; pointer = stored addend)
or names a runtime symbol (textures `__EAGL::TAR:::…SHAPENAME=face,6;…`, `__EAGL::GeoPrimState:::…`,
VU1 microcode `MVPIrradTexture_Skin0__EAGLMicroCode`, matrices `__const MATRIX4:::EAGL::ViewPort::gp…Matrix`).
821 objects decode (787,558 triangles); 326 more are ~100-byte empty placeholders (jumbotron/track layers).
`ANIMS.BIG` `.ord`s are animations, not models.

**`__Model` struct** (offsets from the symbol; `0x754` bytes for heads, `0xB00` for bodies):

| Offset | Meaning |
|---|---|
| +0x00 | → state table (GeoPrimState / IrradLight blocks) |
| +0x4C | float3 quantisation **scale S** |
| +0x5C | float3 quantisation **half H** (= S/2) |
| +0x6C / +0x7C | float3 bounds min / max (w = 1.0) |
| +0x8C | bounding sphere (x, y, z, r) |
| +0xA0 | → name pointer table (e.g. `G_HEADShape`, `BS01Shape…` blend shapes) |
| +0xB4 | → model name string |
| +0xB8 | → vertex-channel table (`Coordinates`, `Normals`, `UV`: per-batch vertex counts/offsets) |
| +0xBC | → texture binding table (this is what `.variationN` models change — texture swaps only) |
| +0xCC | → **geometry list**: `u32 0`, then one pointer per *part*; part → slot → array of render programs |
| +0xE0… | texture table: `{→name, 1, 0, flags}` + TAR symbol per texture |

Variations (`__Model:::x.variationN`) share the geometry list and differ only in texture bindings.
Heads have 1 part; `mebody` has 38 parts (helmets, masks, gloves, sleeves, socks…) that the game toggles.
`__Bone:::FBXdummyNode.<name>` symbols are `{u32 index, 12 bytes 0}`; `__Skeleton:::FBXdummyNode` has the
bone count (63) at +8. Bind-pose matrices are not in these objects.

**Render program** — a word stream; top byte = opcode:

| Word(s) | Meaning |
|---|---|
| `01000000 →microcode` | start of program, select VU1 microcode |
| `030000nn →TAR…` | textures used by this program |
| `0b0000nn …` | animation buffer / constant upload |
| `05000002 qwc →data` + `10000000 vifcode` | upload (matrices, lighting block) |
| `06010002 qwc →TAR` | upload/bind texture — **current texture for following draws** |
| `07000002 qwc →GeoPrimState` | primitive state (alpha test/blend, prim type) |
| `04000002 →chain` | **draw**: run a PS2 DMA chain stored elsewhere |
| `14000004 <16-byte DMA tag>` | **draw**: inline single REF tag (bodies, stadiums) |
| `00000000 00000000` | end |

**DMA chains**: standard PS2 source-chain tags — `0x3…` REF (send qwc×16 bytes from addr), `0x5…` CALL,
`0x6…` RET, `0x1…` CNT, `0x2…` NEXT, `0x0…` REFE, `0x7…` END. Each draw usually REFs one vertex batch,
then REFs a shared per-draw block (batch header + kick).

**VIF batches** (one per `MSCNT`/`MSCAL` kick, all UNPACKs TOPS-relative):

*Skinned layout* (heads, bodies, most stadium geometry):
1. `STROW 1.0,1.0,1.0,1.0` + `STMOD offset` + `UNPACK V4-16 usn` — positions: `xyz = u16/65536 · S − H`;
   `w` = per-vertex skin/morph reference (multiple of 7, or 0xFFFF).
2. `UNPACK V4-16` signed — `u/4096, v/4096, slotA·3, slotB·3`: the vertex's **output slots** in the strip.
3. `UNPACK V3-32` (masked) — normals (tiny magnitudes; normalise).

*Rigid layout* (skies, seats, cameramen…): `V3-32` float positions, `V2-32` float UVs, `V4-8` colours (0x80 = 1.0),
vertices already in strip order.

Shared block: `UNPACK V4-32` header at addr 0 = `(slot count, vertex count, …)`, then three `V4-8` words =
**zero-terminated list of strip-restart slots ×3** (addr 1–3 for skinned, 20–22 for rigid), then `MSCNT`.

**Triangles**: order vertices by slot (a vertex may occupy two slots), walk as a triangle strip, and skip the
two triangles that would cross each restart slot. Winding alternates and is not reliable; the viewer orients
faces by welded smooth normals.

**Variation jump tables**: a draw chain that *starts* with a run of empty CALL tags and no RET is not a call
sequence but a table indexed by geometry variation (heads: 6 entries = `hihead_model2`, `.variation1…5`, each a
different head shape). Following only one entry gives the real mesh (head: 1,053 vertices = channel-table total).

### Skeleton (`__Skeleton:::FBXdummyNode`)

Header `{u32 hash, 0, u32 boneCount (63), 0}`, then `boneCount × 0x70`:

| Offset | Field |
|---|---|
| +0x00 | float3 scale, s32 parent (−1 = root; parents precede children) |
| +0x10 | quaternion x, y, z, w (local rotation) |
| +0x20 | float3 translation, u32 |
| +0x30 | 4×4 **inverse bind matrix**, row-major, row-vector convention (translation in row 3) |

`World = Scale·Rotate·Translate (local) × World(parent)`; `World × InverseBind = I` to 1e-4 for every bone.
`__Bone:::FBXdummyNode.<name>` gives each index a name. Bat / Ball / Trajectory hang off the root/hips and
R/LConstraint off the hands (IK/prop helpers). Body-type scaling profiles: `INITPRGZ.BIG/…/profiles/bones.csv`.

### Skinning

* **Palette** — program opcode `0b0000nn →EAGLAnimationBuffer count →list`: list of skeleton bone indices,
  one per VU matrix slot (e.g. face draw: Head, REye, RUpCheek, RLoCheek, RCornerMouth, UpLip, Jaw, LUpCheek, …).
* **Weight table** — each draw uploads an absolute (non-TOPS) `UNPACK V4-32` to VU address 4: up to 3 float weights
  per entry; the **low mantissa byte of each weight is the palette slot × 4** (weights are read with that byte masked).
* **Per vertex** — vertices are sorted by weight set: position `w = entry × 7` starts a new set, `0xFFFF` repeats
  the previous vertex's set.
* Check: copies of the same vertex in different batches/draws carry identical weights (0% mismatch on `lobody`,
  3% on `mebody`, 1.7% on heads — the remainder are lip/mouth-corner pairs that are genuinely different).

Deformation: `v' = Σ wᵢ · v · (InverseBind_bᵢ × World_bᵢ)`.

#### Xbox (MVP 06) skinning — `EaglXboxReader`

Xbox bodies (`mebody`, `lobody`) have no VU palette. The skin lives in two places per render method (`__RM`):

* **Per-vertex stream** — the 2-byte `__VB` stream (listed in the RM's stream table with count `0`, so it is easy to miss) is an
  `s16` register offset: `entry = (value + 96) / 4`. It selects a *weight set*; rigid draws use `-96` (entry 0) for every vertex.
* **Weight table** — 16-byte records `{ f32 w0, f32 w1, f32 w2, f32 packed }`; the weights sum to 1. `packed` is a float whose
  integer value is `bone0·2²⁴ + bone1·2¹⁶ + bone2·2⁸` (**skeleton bone indices directly, no palette**; bone *n* is unused when `wₙ = 0`).
  The table is the `{pointer, 38}` pair just before the RM's `EAGLAnimationBuffer` constant slot (38 is the table capacity, not the
  used length; the tail is zero). Tables are shared between RMs.
* **Record index** — `entry` counts shader constant slots and slot 14 holds another constant, so `record = entry` for
  `entry < 14` and `record = entry − 1` for `entry > 14` (entry 14 never occurs). Getting this wrong shifts every set after the 14th by
  one and looks like a "sentinel" entry at the end of the table.
* **RMs without an `EAGLAnimationBuffer` slot** (rigid hand, lens, name-plate, sole draws and one jersey patch) have no table pointer;
  they are drawn with **the preceding RM's table** (nb33 after hats → Hat, lens after frame → Head, sole after shoe → feet). The
  `0x00030000`-marked block embedded after their constants is an unused template (it carries wrong bones for several of them), so the
  reader ignores it unless there is no earlier RM.

Verified against the PS2 `mebody` by nearest-vertex matching (same art): 99.7 % of the 5,011 Xbox vertices get the same primary bone
and weights (`xskin` probe). Pose/animation tear test (edges stretched > 50 % over 40 `DEF_BANK` clips at mid-frame): 2.5 % on Xbox vs
2.3 % on PS2. The 4-byte streams on bodies are colour (`0xFFFFFFFE`, constant) and a packed normal (not decoded).

`dotnet run --project src/MVPWorkshop.Probe -c Release -- xskin <game> <path.ord> [<bank.ord>]` prints skin coverage and the tear tests.

### Blend shapes (`<head>_morph.ord`, `__Morph:::morph.modelname`)

Header `{u32 shapeCount (26), →names "BS01Shape"…, →runtime slots, 0, u32 channelCount (2), →"Coordinates","Normals", …}`.
Then per channel, per shape: `{u32 shapeIndex, u32 recordCount}` + records
`{u32 0xE0000000|bytes (V3-16) / 0xC0000000|bytes (V3-32 normals), u32 vertexCount, 0, u32 batchIndex, data}`.
`batchIndex` indexes the head's vertex-channel table (`__Model+0xB8`, whose entries give each batch's position
data offset). Coordinate deltas are in quantised units: `Δpos = d / 65536 · S`. Shapes are sparse and symmetric
(e.g. BS01 pulls the cheeks in, BS17 is part of a smile).

**GeoPrimState** names spell out the GS state, e.g.
`RUNTIME_ALLOC::SetTextureEnable=true;SetAlphaBlendMode=EAGL::ABM_ADD;SetDepthTestMethod=EAGL::DTM_ALWAYS;…` or
`PS2EXTOBJ_SetAlphaBlend=EAGL::ALPHA_CS,EAGL::ALPHA_CD,EAGL::ALPHA_AS,EAGL::ALPHA_CD,128` (the raw
`(A − B)·C >> 7 + D` equation); a few are named (`clip_texture_blend_state`, `glower_state`, `crowd_state`, …).
`EaglPrimState` maps them to Opaque / Alpha / Additive / Subtractive. Field ground (`mfieldN`) is ~36 painter-ordered
passes with `DTM_ALWAYS` (no depth test), alpha shadows and an additive detail layer. The viewer draws opaque
passes first, then blended passes in program order, lifting each no-depth layer slightly along its normals because
WPF always depth-tests. Additive passes use `EmissiveMaterial` (which WPF blends additively); vertex alpha is
approximated per draw because WPF has no per-vertex alpha.

Known gaps: morph normals are not applied (normals are recomputed after deformation).

## Animations (`ANIMS.BIG`, `__AnimationBank:::NAME`)  ← decoded from the game's own evaluators

270 banks, 4,854 clips, 0 decode failures; 2.15 M rotation keys, 99.998 % unit length. Clips come in pairs:
`name_e` (body) and `name_r` (root: Hips + Trajectory) — play both. Each bank also has a `.axt` text manifest
(source file names and dates). Code references below are addresses in `SLUS_215.82`.

Bank: `{u32 0x1C, u32 count, →?, 0, →anim pointer[count], →name pointer[count]}`.
Every node starts with `u32 (skeletonHash & 0xFFFF) << 16 | type` (0x1558 = the player skeleton `0x02761558`;
0x06C6 = a second skeleton). The factory at `0x570628` switches on `type`:

| Type | Class / evaluator | Content |
|---|---|---|
| 0x0F | compound | `{hdr, →shared, u16 childCount, u16 frames, →child[]}` |
| 0x12 | `0x5725C0`, setup `0x572470` | quaternion tracks |
| 0x13 | `0x578D70` | single-axis rotations (twist bones) |
| 0x14 | `0x573DE8`, params `0x5755F0` | float3 tracks (translations) |
| 0x15 | `0x577700`, params `0x5789B0` | scalar tracks |

Outputs are absolute **local** transforms per bone: a 12-float record where floats 4–7 are the rotation and
8–10 the translation. Key times: `→u16[keys−1]` (key 0 is frame 0); no table = one key per frame; 30 fps.
Keys are grouped in blocks of `2^shift`; each block starts with a full key and continues with accumulated deltas.
Values between keys are lerped (quaternions nlerped).

**0x12 rotations** — `+4 u16 keys, +6 u8 tracks, +7 u8 constants, +8 →times, +0xC →u8 bone list, +0x10 u8 shift,
+0x12 tracks × {u16 min[4], u16 range[4]}` (`min = u/32767 − 1`, `range = u/32767`). Block =
tracks × 6-byte key + (2^shift−1) × tracks × 3-byte delta; stride rounded up to even.
6-byte key (`0x5856F0`): u16 a,b,c → `x,y,z = (u>>4)/2047 − 1`, `w = (a&15)<<8 | (b&15)<<4 | (c&15)` /2047 − 1.
3-byte delta: `x,y,z = byte>>2`, `w = the three low 2-bit fields`; `q += min + range·v/63`.
Constants after the last block: u8 bone list, then 6-byte keys (2-aligned).

**0x13 axis rotations** — `+4 keys, +6 tracks, +7 shift, +8 times, +0xC bone list, +0x10 tracks × 14 bytes
{u16 A, u16 B (u·2π/65536 − π), u16 min0, min1 (u/32767 − 1), u16 range0, range1 (u/32767), u8 mode, pad}`.
Block = 2 bytes per track (axis component, w; `b·2/255 − 1`) + 1 byte per track per key (high nibble →
`min0 + range0·n/15`, low → `min1 + range1·n/15`, accumulated). The single-axis quaternion (X/Y/Z by mode 0/1/2)
is normalised and composed `post ⊗ q ⊗ pre`: mode 0 post = E(0,A,B); mode 1 pre = E(A,0,0), post = E(0,0,B);
mode 2 pre = E(A,B,0). E is the game's half-angle cos/sin Euler build (`func_517458` = cos, `func_517648` = sin).

**0x14 / 0x15 vectors and scalars** — `+4 →u16 target list, +8 →times, +0xC u16 keys, +0xE u16 tracks,
+0x10 u8 shift, +0x11 u8 constants, +0x14 tracks × {float min[c], float range[c], u16 dmin[c], u16 dscale[c]}`
(c = 3 or 1). Targets are float slots `bone·12 + 8..10` (translation x..z). Full key: `min + u16·range/65535`;
delta byte: `(dmin/65535·2 − 1)·range + b·(dscale/65535·2·range)/255`, accumulated. Constants: u16 target list
after the last block (2-aligned), then c floats each (4-aligned).

## Fonts — SFN (`FONTS.BIG`; `FNTS` v316/366, `FntS` v408/414)
```
0x00 "FNTS"/"FntS"  0x04 u32 size  0x08 u16 version  0x0A u16 glyph count  0x0C u16 ?  0x0E u16 extra record bytes (0/4)
0x12 u8 baseline  0x13 u8 line height (FntS)  0x14 u32 → glyphs (0x80)  0x18 u32 → kerning (0 = none)  0x1C u32 → atlas
```
Glyph record (12 bytes, 16 when 0x0E = 4):
`u16 code (UCS-2), u8 w, u8 h, u16 atlas x, u16 atlas y, u8 advance, s8 x offset, s8 y offset (from the line top), u8 ?`;
the 16-byte form keeps the offsets at +9/+10 and moves the advance to a u16 at +14.
Kerning: `u32 count`, then `{u16 left, s8 adjust, u8 right}` sorted by the right character; an optional comment
string ("Buy ERTS") follows. The atlas is a standard SSH image block (0x01 PAL4 or 0x02 PAL8, white with an alpha-ramp
palette in a trailing 0x21 block), decoded by wrapping it in a one-image SHPS. 12 fonts, 1,147 glyphs, 4,726 pairs.
Fonts with few glyphs (e.g. `Tw24_en`, 34) are uppercase/digit display faces.

## Localisation — LOCH (`FEENG.LOC`, `IGENG.LOC`, `MC_ENG.LOC`)
```
LOCH  u32 0x14, u32 version (1), u32 languages (1), u32 → LOCL
LOCI  u32 size, u32 count, u32 0, count × { u16 string id, u16 index }
LOCL  u32 size, u32 language, u32 count, u32 offset[count] (from the LOCL tag) → UTF-16LE zero-terminated strings
```
5,964 strings in all (`FEENG.LOC`: 4,236, ids 8–29104); U+00A0 is used as a non-breaking space.

## APT UI movies (`*.apt` + `*.const` + `*.o` + `*.ssh`)

Same layout as EA's PC APT (as reversed by OpenSAGE): little-endian, every pointer is a file offset.
`"Apt Data:6\x1A"` / `"Apt Data:7\x1A"` in the game's movies; the EA online module (`EACN/BUNDLE.BIG`) writes
`"Apt Data\x1A"` with no version. 968 movies, 7,155 characters, 0 failures.

**.const**: `"Apt constant file\x1A\0\0"`, `0x14 u32 → movie character in the .apt`, `0x18 u32 count`,
`0x1C u32 → items`; item = `{u32 type, u32 value}` (1 string → offset, 4 register, 5 bool, 6 float, 7 int).

**Characters** start `{u32 type, u32 0x09876543}`: 1 Shape `{rect, u32 geometry}`, 2 EditText, 3 Font, 4 Button,
5 Sprite `{u32 frames, → frames, → ?}`, 7 Image `{u32 texture}`, 9 Movie:
```
+0x08 u32 frame count  +0x0C → frames  +0x10 → ?  +0x14 u32 character count  +0x18 → character pointers
+0x1C u32 stage width  +0x20 height  +0x24 u32 frame time (ms)  +0x28 imports {→movie, →name, u32 char, ?}
+0x30 exports {→name, u32 char}
```
Frame = `{u32 item count, → item pointers}`. Items: 1 Action `{→ bytecode}`, 2 FrameLabel `{→ name, u32, u32 frame}`,
3 PlaceObject `{flags, depth, character, f32 rotscale[4], f32 translate[2], u32 colour, ?, f32 ratio, → name,
s32 clip depth, → clip actions}` (flags: 1 move, 2 has character, 4 matrix, 8 colour), 4 RemoveObject `{depth}`,
5 BackgroundColor `{rgba}`, 8 InitAction `{sprite, → bytecode}`.

**Bytecode** — SWF ActionScript 1 plus EA's compact opcodes. Operands of opcodes ≥ 0x80 are 4-byte aligned
*except* EA's: `A2 u8` PushConstant, `A3 u16` PushWordConstant, `AE u8` PushValueOfVar, `AF u8` GetNamedMember,
`B0/B1 u8` CallNamedFunction(Pop), `B2/B3 u8` CallNamedMethod(Pop), `B4 f32` PushFloat, `B5 s8` PushByte,
`B6 s16` PushShort, `B7 s32` PushLong, `B9 u8` PushRegister, `9F u8` GotoFrame2 — all unaligned. Aligned:
`88` ConstantPool `{count, → u32 const indices}`, `96` PushData `{count, → indices}`, `99/9D/B8` branches `{s32}`,
`A1/A4–A7` string ops `{→ string}`, `9B` DefineFunction `{→ name, argc, → arg names, size, 8-byte signature}` +
inline body, `8E` DefineFunction2 `{→ name, argc, u8 registers, u8 0, u16 flags, → {reg, → name}[], size, signature}`.
No operand: `59` PushZero, `5A` PushOne, `5B–5E` Call{Function,Method}{Pop}, `70` PushThis, `71` PushGlobal,
`72` (unknown), `73` PushTrue, `74` PushFalse, `75` PushNull, `76` PushUndefined; `9A` GetURL2 has no operand here.
All 1,235 actions (396,734 instructions) disassemble to their end with this table (`MVPWorkshop.Probe apt <game>`).
Names for 0x70–0x76 follow OpenSAGE and fit every use seen (e.g. `iconHL._visible = PushTrue`).

## Other
- **MPC**: chunked — `"MPCh"` blocks carry the MPEG-2 video ES (576×448, 30 fps, "Encoded with MPEG Gimex module")
  interleaved with an EA `SCHl`/`SCCl`/`SCDl` audio stream (EA-XA R3, 48 kHz stereo). ffmpeg reads the container
  directly; the viewer transcodes once to H.264/AAC in `%TEMP%\MVP-Workshop\movies` and plays that.
- **icon.sys** (`PS2D`), **.ico** (PS2 3D icons), **HD/BD** (Sony `IECSsreV` banks in NETGUI), **PFR/PF/TTF** fonts — identified.
- **.ssh / .ord stubs** of 1–2 bytes (`"0"`) appear in `AWARDS.BIG` / `CSASSETS.BIG` — placeholders.
# Team and stadium assignments

`DATABASE.BIG/roster.bin` begins with a little-endian team count (153 including the empty team 0).
Records are `487 + playerCount * 8` bytes; playerCount is u32 at +192. Names occupy +0 (8), +8 (48),
+56 (16), +72 (40), +112 (32). Packed u32 at +164 contains three 9-bit asset slots at bits 1, 10 and 19.
The first slots identify logo/uniform families. Stadium enum is bits 4–9 of u32 at +168.
LSU record starts 0x551C: uniform/logo family 032 and stadium enum 3.

`StadiumAssignments` discovers three tables in SLUS_215.82 using validated signatures and ELF LOAD mappings:
filename pointers at file 0x75B4D8, three 34-entry time-of-day maps at 0x7F4F90, and the 34-entry venue registry
at 0x84B5C8. Registry rows contain short/full localized name IDs, stadium enum and owning school asset ID.
Function VA 0x00197D50 resolves requested time; negative entries fall back through dusk, night, then day.
Enum 3 resolves to `aboxday`, so LSU links to `STADIUM/ABOXDAY.BIG`, matching DATAFILE configuration,
and menu panoramas using the observed one-based hexadecimal `s04` family convention.

Stock roster assignments and the authentic venue ownership registry are distinct: Mississippi State and Virginia
select generic enum 25 in the supplied roster, while owning authentic enums 22 and 21. The browser exposes both
without replacing roster data. Native player uniform filenames follow the executable's `u%03d%c.%s` family,
with matching `f` number fonts; frontend uniform/logo packs use the same numeric asset ID. Legacy `stadium.csv`
is not used as the NCAA assignment table. Stadium models resolve texture tags within their own archive first,
including sibling SSH packs, to avoid binding another venue's identically named textures.

Validation on the supplied disc: 152 unique named schools resolve uniform packs and native stadium models;
all 34 stadium enums resolve for all three times. All 873 APT movies with paired native geometry decode
1,917 nonempty shapes and 5,870 triangles without a decoding failure. This validates coverage, not pixel parity.


## Xbox (MVP 06) summary

Full notes: [XBOX_MVP06.md](XBOX_MVP06.md).

* **SHPX (`.xsh`)** — SHPS layout with Xbox pixel types: `0x60/0x61/0x62` DXT1/3/5, `0x7B` PAL8, `0x7D` BGRA8888, `0x64/0x78/0x7E/0x7F` A8/16-bit; swizzle flag `0x2000` = NV2A Morton order; mip count in header `RawY >> 12`.
* **XPR0 (`.xbx`)** — D3D resource, data offset at +8, format dword at +0x18; converted in memory to SHPX for viewing.
* **FntX (`.xfn`)** — `FntS` layout plus an Xbox texture block.
* **MVhd (`.vp6`)** — EA movie container, VP6 video + EA-XA audio; ffmpeg required.
* **XBE** — `XBEH` header, certificate, sections and SDK library table.
* **EAGL models** — `__VB` vertex streams, `__RM` render methods, u16 triangle-strip index buffers instead of VIF packets.
