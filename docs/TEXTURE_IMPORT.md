# Texture import and native limits

Native imports use the existing byte-edit history, project persistence and mod/ISO
export. The import review shows the imported picture beside an actual native
encode/decode preview, including palette/GST losses. Preview updates when changing
resolution policy or the fitting filter. Alpha is retained subject to native format
limits. Images larger than 16 million pixels are rejected before allocating BGRA.

**Keep imported resolution** is selected by default for eligible native upscales.
It retains the higher-resolution dimensions and pixels in SSH, nested BIG, project,
mod and ISO output. This is an experimental storage feature: authored APT texel
matrices, GS texture state, UV consumers, upload stride and runtime VRAM allocation
are not rewritten. The APT limitations panel identifies resized packs. Console and
emulator gameplay compatibility is unverified; encoding success does not establish
that every consumer renders correctly.

Eligible changes are uniform 2×/4×/8×/16× upscales, power-of-two dimensions, at most
1024×1024, and a writable source layout. Positioned/centered/hotspot images,
referenced/transposed storage, mipmaps and unsupported palettes remain blocked.
The pack directory, attachments and unaffected images are retained; dimensions,
base payload, palette and shifted block offsets are rebuilt and reparsed. RefPack
compression and trailing decoded bytes survive resizing.

**Fit to source dimensions** uses premultiplied-alpha area coverage by default when
reducing an upscale, so all source pixels contribute. Bilinear and nearest are also
selectable. This avoids the old bilinear-only reduction losing fine detail or
aliasing. The importer uses the supplied upscale; it does not generate AI detail.


## Evidence and restrictions

* `Ssh.cs`: 16-byte SHPS header, 8-byte directory entries, 16-byte image headers; 16-bit width/height, 24-bit chained block size. RawX contains referenced, swizzled and transposed flags; RawY's high nibble records mipmaps. Keeping dimensions avoids changing centers, positions and attachment interpretation. References/transposition and declared mip chains are blocked pending verified writing support.
* PAL4/PAL8 retain 4/8-bit storage. CSM palette permutation is reversible. Existing supported 4-byte palettes remain 4-byte palettes; 16/24-bit palettes remain blocked. RGB888 writing is linear only and discards alpha. RGBA8888 keeps the reader's linear layout. RGBA5551 writing is still unsupported. PS2 8-bit alpha writes 0–128, with 128 opaque; conversion loses odd display-alpha precision.
* GST types 08–0F use one base byte per 1/2/4/8 by 2 block, plus 1/2-bit detail pixels. Base/detail boundary is aligned to 16 bytes. The encoder searches consecutive palette windows per block; arbitrary imported images are lossy, including some images with fewer than 256 colours. Rebuilding a palette does not imply exact GST representation. Existing palette mode retains original entries. Width must be divisible by 8 and height by 2. Swizzle inversion rejects non-bijective/out-of-range layouts rather than silently dropping pixels.
* RefPack is transport compression, separate from GST. The writer preserves the decompressed tail and compression header style. Uncompressed tails remain intact. Growth uses the existing 16-byte block alignment and updates directory offsets, chained image block size and file length. Every result is reparsed and decoded before committing.
* `Formats/Eagl/EaglReader.cs` around line 128 explicitly normalizes texel UVs by image dimensions. `Formats/AptRaster.cs` samples texel-unit UVs. Enlarging only the SSH image would therefore change sampling in these consumers. Normalized ST consumers may not need UV changes, but this has not been established for every referencing model/material. Dependency impact review still runs before commit. No automatic model/material rewrite is implemented.
* GS TEX0 carries texture base pointer, buffer width, pixel format, power-of-two width/height exponents and CLUT configuration. TEX1 and MIPTBP configure mip sampling/addresses; CLAMP can carry dimension-related bounds. BITBLTBUF/TRXREG upload stride/rectangle and runtime allocation must also agree. A 16-bit SSH dimension field is not proof of a valid GS upload. The GS has 4 MiB shared VRAM, also used for frame/depth buffers. File payload estimates exclude page/block allocation, staging, mipmaps and simultaneous residency; GST's effective runtime upload representation is not established. Passing a size estimate is not a native memory-budget proof.

Hardware reference: [ps2tek GS registers and memory](https://israpps.github.io/ps2tek/). These findings explain why native resizing remains experimental; they do not establish an engine-specific maximum safe texture size. Native same-size encoding success is also not an in-game compatibility test.

## PCSX2 replacement export

The separate **PCSX2 replacement…** action selects an actual matching runtime dump, then an external image. It preserves the imported resolution and display alpha, requires the dump's aspect ratio, and proposes the dump's exact filename. It does not enter native project history or Export Mod: it is a standalone PNG export. The user must visually verify that the dump is the desired runtime texture, retain the filename, and place it in the target PCSX2 texture directory under `<serial>/replacements/` (this disc is SLUS-21582; use the emulator's actual serial directory).

Verified against [PCSX2 GSTextureReplacements.cpp](https://github.com/PCSX2/pcsx2/blob/master/pcsx2/GS/Renderers/HW/GSTextureReplacements.cpp), inspected 2026-10-25: base-level names contain a texture hash, optional CLUT hash, optional `rWIDTHxHEIGHT` region, and eight hexadecimal digits encoding GS format/size/alpha state. Hashes are hexadecimal values up to 16 digits, not SSH tags, PNG hashes or disc CRCs. Runtime content/state is needed to identify replacements; MVP-Workshop does not compute these hashes from SSH. Only current base-level PNG naming is accepted; mip-suffix and legacy region formats are rejected. Dump with the target emulator version because naming/hash behavior can change. Export does not prove the chosen dump matches the asset or that replacement loading succeeds.

## Validation

Run from repository root:

```powershell
dotnet run --project tests/Textures -c Release -- game/DATA/GHEAD.BIG game/DATA/MISC/TEXTURES/LAWN.SSH game/DATA/IGSHAPES/IGCRSR.SSH
```

Validated 88 synthetic source-size format/dimension/swizzle cases (32×32, 64×32, 128×128, 256×128), 16 patterned GST plane inversions, RefPack encoding/reopening with preserved trailing bytes for all 12 writable types, GST history undo/redo, project save/load and mod export/reopen. Stock samples include GHEAD face GST422 at 128×256, LAWN PAL8 at 128×128 and IGCRSR PAL8 at 512×256. Other stock images in sampled packs are compared after replacement. These are format and workflow tests, not console/emulator gameplay validation. Additional tests cover native upscales for each writable type with swizzled/linear variants and RefPack tails, plus area/nearest alpha-edge behavior. A WPF self-test exercises the native encoded review, import, undo, and ISO export. These validate storage and UI plumbing; real-world palette variants and runtime memory/upload behavior remain unverified.

APT movies also expose this editor directly through **Textures** / **Edit textures…**,
including paired packs and resolved shared imports. See [APT texture editing](EDITING.md#texture-editing-inside-apt-movies).
