# MVP 06 NCAA Baseball (Xbox) — formats and limitations

What MVP-Workshop reads and writes for the Xbox build. Code: `Formats/XboxFiles.cs`, `Formats/XboxTexture.cs`, `Formats/Ssh.cs`,
`Formats/SshWriter.cs`, `Formats/SfnFont.cs`, `Formats/Eagl/EaglXboxReader.cs`. PS2 notes are in [FORMATS.md](FORMATS.md).

The game folder holds `default.xbe` (plus `dashupdate.xbe`, `update.xbe`, `saveimage.xbx`, `titleimage.xbx`) and a `data` folder of BIGF archives
(same container and RefPack compression as PS2).

## SHPX textures (`.xsh`)
EA's Xbox texture pack: same layout as PS2 SHPS (magic `SHPX`, entry table, one block per image) but the pixel type byte uses an Xbox range:

| Type | Format | Notes |
|---|---|---|
| `0x60` / `0x61` / `0x62` | DXT1 / DXT3 / DXT5 | block compressed |
| `0x7B` | PAL8 | 256-entry palette block follows the pixels; Morton-swizzled when the swizzle flag (`0x2000` at block +12) is set |
| `0x7D` | BGRA8888 | Morton-swizzled when flagged |
| `0x64`, `0x78`, `0x7E`, `0x7F` | A8, RGB565 (0x78/0x7F), ARGB1555 | decoded for viewing |

* **Swizzle** is NV2A Morton (Z-order) order: low bits alternate x/y, the longer axis' remaining bits follow linearly (`XboxTexture.Swizzle/Unswizzle`).
* **Mip levels**: header `RawY >> 12` is the mip count; levels follow the base level back to back, each half-size (minimum 1).
* **Replacement**: the new image is resampled, then *every* mip level is regenerated (box downsample) and encoded: DXT with a PCA/bounding-box
  endpoint search plus least-squares refinement (alpha-weighted; DXT1 treats alpha < 128 as transparent), PAL8 via palette quantisation, BGRA8888 as swizzled raw.
  Writable types: `0x60 0x61 0x62 0x7B 0x7D`. DXT sizes must be multiples of 4.

## XPR0 images (`.xbx`)
`saveimage.xbx` / `titleimage.xbx` are Xbox packed resources: magic `XPR0`, data offset at +8, D3D format dword at +0x18
(format `>> 8 & 0xFF`, log2 width `>> 20 & 0xF`, log2 height `>> 24 & 0xF`). Decoded formats: `0x0C` DXT1, `0x0E` DXT3, `0x0F` DXT5, `0x06/0x07` ARGB8888 (swizzled).
Other formats report "not decoded yet". They are converted in memory to a one-image SHPX so the normal texture viewer works; they are view/export only.

## FntX fonts (`.xfn`)
Header `FntX`/`FNTX` shares the PS2 `FntS` layout (0x80-byte header, glyph table of 12+ byte records, kerning pairs) followed by an Xbox texture block
(PAL4/PAL8 atlas plus palette) instead of the PS2 swizzled block. Glyph metrics are read the same way as PS2.

## MVhd movies (`.vp6`)
EA `MVhd` container: width/height as u16 at +0x0C/+0x0E; On2 VP6 video with EA-XA audio. Playback and MP4 export go through `ffmpeg` (not bundled).

## XBE header (`default.xbe`)
Magic `XBEH`. Read: base address (+0x104), image size (+0x10C), timestamp (+0x114), certificate pointer (+0x118; title UTF-16 at cert+0x0C,
title ID cert+8, media cert+0x9C, region cert+0xA0, version cert+0xAC), section headers (count +0x11C, pointer +0x120, 56 bytes each) and linked SDK
library table (+0x148/+0x14C, 16 bytes each). Display only; the executable is not edited.

## Xbox EAGL models
Same relocated-object container and symbol names (`__Model`, `__Skeleton`, `__Bone`, shape names) as PS2, but geometry is not VIF:

* `__VB:::…` — vertex buffer: u32 next, u32 byteSize, u32 stride, u32 1, data pointer at +0x10 (data at +0x14 when inline).
* `__RM:::…` — render method (one draw). Its stream table is a run of {pointer, vertexCount}: position (3×f32), UV (2×f32), optional second UV, colour (4 bytes),
  normal (3×f32) or packed normal (4 bytes), and a 2-byte skin stream (listed with count 0).
* Index buffer — after the `EAGLAnimationBuffer` constant slot: {pointer, indexCount} to a header and a u16 triangle-strip index array (header + 0x20).
* Skin: 16-byte weight records `{w0, w1, w2, packed bones}`; bone indices are skeleton indices (no palette). See "Xbox (MVP 06) skinning" in [FORMATS.md](FORMATS.md).

## Limitations
* **No skin-weight animation on Xbox models** — geometry, textures and skeleton are shown; posed/animated deformation is not supported for the Xbox layout.
* **Teams workspace**: stadium and uniform tables are not yet resolved for Xbox; the Teams/uniform/stadium workspaces are PS2-only.
* **DXT replacement is lossy** (block fit); logos with fine detail may show artefacts. Dimensions must be multiples of 4.
* XPR0 images are view-only; unusual XPR0 pixel formats are not decoded.
* Movies need ffmpeg; VP6 files are read-only.
* Modded Xbox output has not been verified by running it on hardware or an emulator.
