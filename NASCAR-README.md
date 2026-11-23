# NASCAR-Workshop

Separate Windows build for **NASCAR 06: Total Team Control (Xbox)**. Shared workbench
code and parsers stay in the MVP-Workshop repository; NASCAR has its own executable,
output folder, start page, settings, autosaves and crash reports. It does not install
updates from the MVP-Workshop release feed.

## Run

Open `release/NASCAR-Workshop/NASCAR-Workshop.exe`, then **Open game / DATA folder**.
Choose the extracted game's root or its `DATA` directory. Command line:

```powershell
.\NASCAR-Workshop.exe --game 'E:\Recomp Source\nascar06-ttc\game\DATA'
```

Keep the executable together with its DLLs and `NASCAR-Decoder.exe`. This build uses
the .NET 9 Windows Desktop runtime. Settings live in `%APPDATA%\NASCAR-Workshop`.

## Current support

### Reliability and scene export (v0.1.7)

**Export scene** saves the visible preview to OBJ, with MTL materials and PNG
textures in a generated companion folder. It includes placed track objects,
assembled wheels and selected model parts. Keep the companion folder beside the
OBJ. Advanced game shaders remain approximated. A failed export preserves an
existing OBJ. Model and texture refresh failures display inside the viewer; undo
can restore a preview after an invalid texture replacement. The texture footer
identifies names that share pixels and will change together on replacement.
Track and other embedded-texture models now have 3D/2D tabs. Texture edits refresh
the 3D scene; refreshes retain the camera, chosen model parts and selected texture.

Release builds now stage and test the package before replacing the stable folder,
retain the previous package for rollback, and include a SHA-256 manifest. Run
`Verify-Package.ps1` in the package to check its files. This checks integrity,
not publisher authenticity. See `docs/NASCAR_PRODUCTION_READINESS.md` in the source
for verification steps and the remaining production release gates.

### 3D and 2D views (v0.1.3)

Car and character MMAP packs and models now have **3D** and **2D** tabs. The 3D
tab binds runtime material slots to separate MMAP packs (car DECAL paint,
UNIVERSAL trim, DRIVER detail, and character face/body/helmet slots). Character
resources prefer their neighbouring authored model and texture group. The model
selector lets you choose a compatible body/LOD when a paint atlas fits multiple
models; a shared DECAL slot does not identify a particular manufacturer by itself.
The texture selector previews other matching packs without changing game bytes.

The 2D tab retains PNG export and native-size replacement, including embedded
model textures. Project changes and undo/redo refresh the 3D textures. The preview
keeps the camera and selected flat texture when edits refresh. **Model parts**
selects authored alternatives rather than drawing all body/helmet/manufacturer
variants over each other. Skinned models use their stored skeleton/cluster pose;
animation playback remains unsupported. Draw groups bind their own skin palettes. Shader effects and the complete vehicle assembly
(including runtime suspension and damage transforms) are not simulated.

The v0.1.4 stock-car preview corrects roof/window material slots to use DECAL,
with chassis/tires using UNIVERSAL. It places four copies of the authored tire
mesh using deterministic circle fits to the visible fender arches. These are
preview-derived wheel positions, not recovered suspension settings. Placement
requires both arches to provide sufficient evidence; otherwise unplaced tires
are omitted. Unplaced roof flaps and damage fragments are hidden, and the car is
framed more closely. Source geometry and game files are unchanged.

The v0.1.5 vehicle preview joins `CRSK.CTID` through `CRSK.CRID` to `CARS.CMDL`
in NASCAR.DB. Stock, truck, modified, pace-car and linked Viper schemes select
their appropriate model family. Stock Dodge/Ford panels use the first two packed
manufacturer morphs and their separate UV streams; damaged IDP alternatives are
not substituted for an intact body. Shared texture atlases are selected per model
family. Opaque paint surfaces do not use the atlas's shader-mask alpha as holes
through the body; glass retains source alpha. The modified's exposed wheels are
placed using suspension/frame endpoints rather than nonexistent fender arches.

Paints without a database vehicle link open in 2D and ask for a body selection in
the Model control instead of silently using a Chevrolet. Manual body choices are
remembered for that asset in the current session. The #187 Which-Way modified,
yellow checkered pace car and #110 Outtake Intake Dodge were used as regression
examples. Wheel placement remains a static preview approximation.

The v0.1.6 character preview converts skeleton offsets from centimetres to the
model's foot units. Each draw group uses its own bone palette; the palette number
is no longer mistaken for a different index encoding. Projected shadow hulls are
hidden rather than drawn as grey body surfaces.

Track render models now assemble static scenes using their authored `Inst` mesh
ranges and world transforms. Embedded texture aliases share their bitmap data
while retaining each material name. Open a model in **Tracks / Track geometry and artwork**
(or `TK_REND.DAT`) to see the circuit and placed scenery. Drag to orbit,
right-drag to pan, use the wheel to zoom, or choose Top view, Angled view and Fit
view. This is a static scene preview: advanced multi-layer shaders, lighting,
animated objects, traffic and race-session entities are not simulated.

Validation decoded 178,750 placements in 321 instance-bearing assets, including
all 65 track render models. Atlanta was visually checked with embedded textures;
this does not mean every track has been visually checked or matches game shaders. The scene texture audit decoded 33,076 named base images across 193 embedded packs, including shared-pixel aliases with alternate palettes.

| Format | Support |
|---|---|
| TERF DAT | Indexed and nested archive browsing, decompression (algorithms 0 and 5), extraction, project edits and verified rebuilding. Untouched entries retain stored bytes and offsets. Changed entries use uncompressed storage; larger entries append with source alignment. |
| MMAP v2 | Named textures and mipmaps; Xbox PAL8, BGRA32, linear L8; Morton decoding where required; palette and pixel bounds validation; PNG export; image replacement at native dimensions; mip regeneration. Shared palettes remain intact and replacement pixels map to their existing colours. |
| EA DB v8 | Table browser, original field IDs, packed signed/unsigned integers, floats, fixed text and plain/Huffman variable text; edit fields, export CSV; table, directory and file checksum updates. Variable string edits append payloads and update table offsets. New characters must exist in a compressed table's Huffman alphabet. No record addition/deletion. |
| QL01 QKL | Source/cache reference inspection; exports synchronize matching cached TERF directories and stored entry payloads, including relocation of growing cache entries. A cache that does not match its source stops verified export with an error. |
| TMdl | Section inspection, orbit preview and OBJ export for float XYZ, XYZ+normal and interleaved XYZ/normal/colour/UV declarations (0x152). Separate runtime texture slots and embedded MMAP materials are previewed. Stored skin clusters and part alternatives are decoded; unsupported geometry is listed and omitted. Vertex colours and advanced material effects are not rendered. |
| SKL1 / 1LKS | Bone names, parent tree, local XYZ translations and binding offsets. Read-only; rotation and skin binding payloads remain undecoded. |
| MAD movies | Signature detection, video playback and MP4 export using FFmpeg. Place ffmpeg.exe beside the app or install it on PATH. The workshop does not bundle FFmpeg. Movie caches use SHA-256 of the current asset bytes, including project edits. |
| SFN fonts | Font atlas and glyph preview, including the NASCAR linear packed A4 atlas (type 0x7A). |
| APT | Nested TERF movies resolve their unique constants companion by signature, so numeric archive filenames no longer prevent constant-pool loading. Native shape resources and scripting still need further decoding. |
| EA audio | Existing SCHl/BNKl/ABKC/MOIR inspection and supported codec playback/export. Codec or bank layouts outside existing decoders remain unsupported. |
| Every file | Raw extraction, hexadecimal view, strings and bounded structure inspection; supported raw replacements use project history/export. |

**This is not complete game-format support.** Full material rendering and model import,
animation, BFME and frontend native shapes/script execution, headerless
physics/camera/AI records, particle and damage resources, and some audio layouts remain
in research. Raw access does not imply semantic editing. Other NASCAR titles and
PS2 data sets have not been validated.

Projects retain the workbench's `.mvpwproj` format. Use **Save**, then **Export Mod
Folder**. Exports go outside the source folder and include matching QKL changes.
These are replacement game files, not MVP port `.mvpmod` packages. Runtime game
testing is still required before describing edited archives as game-validated.

## Build and validation

Requires .NET 9 SDK, Visual C++ x86 compiler and Windows SDK:

```powershell
.\scripts\build-nascar.ps1
dotnet run --project tests/Nascar -c Release
dotnet run --project tests/Nascar -c Release -- 'E:\Recomp Source\nascar06-ttc\game\DATA'
dotnet run --project tests/Nascar -c Release -- 'E:\Recomp Source\nascar06-ttc\game\DATA' --export-test
```

The local data scan covered 37,719 files/entries, 614 TERF archives (including
nested archives), 9,691 MMAP packs, 12,460 bitmaps/mipmaps, 712 TMdl section
directories and five DB files. Archive, texture and DB decoding/checksum checks
passed. The v0.1.2 model scan decoded 90,993 meshes and recorded 169 limitations:
84 files without a Geom section, 82 meshes with index encoding 1 and three meshes
whose indices fail vertex bounds checks. The skeleton scan decoded 81 files and
2,480 bones. An actual compressed CARRESFE texture export was rebuilt, reopened and
verified, with dependent FE.QKL and GAME.QKL cache updates. Source data remained
unchanged. These checks do not substitute for running the exported files in-game.

The v0.1.2 media check decoded the first video frame of all 89 MAD assets with
FFmpeg, decoded all eight SFN font atlases, and parsed all 497 nested APT movies
with their matching constant pools. This validates first-frame decoding, not
complete movie playback or native APT shape rendering. Regression fixtures also
cover the interleaved model stream, A4 nibble order, signature detection and
unchanged-source verified nested archive export.

## Format references and decoder source

EA Madden decompression uses Luigi Auriemma's decoder from
[QuickBMS](https://github.com/LittleBigBug/QuickBMS/blob/master/src/included/ea_madden.c).
It runs in a separate 32-bit process with bounded requests, output length checks
and a timeout. `src/NASCARWorkshop.Native/third_party` contains the upstream source
and GPL-2.0 license; the only upstream source adjustment is the function pointer
declaration syntax needed by MSVC. The packaged `decoder-source` folder includes
that source, the host and build script. Inside `decoder-source`, rebuild with
`./build-nascar-native.ps1 -SourceDirectory .`; in the repository use
`scripts/build-nascar-native.ps1` (Visual C++ x86 tools required).

EA DB layout, string and checksum research was cross-checked against
[madden-file-tools](https://github.com/bep713/madden-file-tools/tree/master/filetypes/TDB)
and its [parser](https://github.com/bep713/madden-file-tools/blob/master/streams/TDBParser.js).
The workbench's managed DB parser/writer is implemented separately. Archive, MMAP,
QKL and TMdl layout validation uses the user's local source data.

## Category browser (v0.1.1)

Categories is the default view. Assets are grouped into Cars, Drivers, Tracks,
Menus and HUD, Audio, Video, Databases, Effects and scenery, System and a visible
fallback for unclassified resources. Purpose groups retain the source archive and
split mixed packs by asset kind. Lists longer than 100 entries are divided into
numbered groups. Texture/model labels include embedded names when present;
texture labels also show dimensions. Numeric entry identifiers and original paths
remain unchanged for editing, projects and exports. Search accepts category words,
asset kinds, embedded names and source paths. Switch to **Files and archives** for
the original disk/archive tree.

The supplied DATA folder has 35,297 browsable assets across 63 purpose groups;
coverage checks confirm every leaf is present exactly once with its original
source node/key. The packaged UI checks default category browsing, readable labels,
100-item group limits, opening editable assets, category-term search and switching
between category and archive views. Re-run coverage with:

```powershell
dotnet run --project tests/Nascar -c Release -- 'E:\Recomp Source\nascar06-ttc\game\DATA' --catalog-test
```

