# Editing, saving and rebuilding

The tool edits assets in memory, keeps them in a **project**, and **exports** rebuilt copies of only the changed
game files into a separate folder. The game folder is opened read-only and is never written to.

## Concepts

| Term | Meaning |
|---|---|
| **Edit** | New *logical* (decompressed) bytes for one asset, keyed by its path below the game folder (`DATA/FRONTEND/TITLE.BIG/fes_title.fel`; BIG entries carry `#index` so duplicate names stay distinct). |
| **SAVED change** | The asset can be written back. It is stored in the project and rebuilt into its archive(s) on export. |
| **PREVIEW ONLY change** | Viewers show the new contents, but they cannot be written back (see below). Kept in the session's undo history and listed in **Changes**; never saved in a project, never exported. |
| **Project** | `name.mvpwproj` (legacy `.mvp07proj` files from the pre-1.0 tool still open; JSON manifest with checksums, notes and the SHA-256 of each asset's original) plus `name.mvpwproj.data/NNNN.bin` replacement bytes. |
| **Output folder** | Default `<game folder>/../mod_output`. Mirrors the game's layout and contains only files that changed, plus `mod_manifest.json`. The tool refuses an output folder that is, contains or sits inside the game folder. |

Every viewer reads assets through `AssetNode.Open()`, which consults the active session, so an edit shows up in every open
viewer at once (for example editing `x.ssh` re-renders the stage of `x.apt`). Rebuild and verification code reads untouched
data through `OpenOriginal()`.

### When is an edit preview-only?

* The format is editable but the *container* cannot be rebuilt: assets inside SCHl stream packs or PROJx `.OFF` packs
  (only loose files and BIG archives, nested to any depth, are rebuildable).
* The file was opened with **Open File** from outside the project's game folder.
* The specific image cannot be re-encoded: RGBA5551, unsupported palette layouts, referenced/transposed images,
  mip chains without verified regeneration, or invalid swizzle layouts. GST now has a lossy native encoder.
  See [texture import constraints and validation](TEXTURE_IMPORT.md).

The document header shows **EDITABLE** or **PREVIEW ONLY**; a banner in each editor says why; the Changes tab marks every row.

## Unsaved-change tracking and undo

* One linear undo/redo history across all assets (`Ctrl+Z`, `Ctrl+Y` / `Ctrl+Shift+Z`, toolbar). *Revert selected asset* in the Changes tab is itself undoable.
* The project is **dirty** when the newest saved-kind change differs from the last project save; undoing back to the saved state makes it clean again. Preview-only changes never make it dirty (there is nothing to save).
* Closing the window, opening another game folder or another project asks to save when dirty.
* Loading a project restores the edits but not the undo history.

## BIG rebuilding (`BigWriter`, `ModExporter`)

* Bottom-up: edited leaf -> (re-compress) -> its BIG -> (re-compress) -> the BIG that contains that -> ... -> the file on disk.
* Compression is decided per entry from the **original stored bytes**: an entry that was RefPack stays RefPack with the same header
  style (all 22,765 compressed entries in this game use flag byte `0x10`: 3-byte size, no compressed-size field); an entry that was
  stored raw stays raw. `RefPack.Compress` is a greedy hash-chain encoder emitting the standard 2/3/4-byte copy, `0xE0` literal-run
  and `0xFC` stop opcodes; its output is about 1% larger than EA's own.
* Everything not edited is copied verbatim: the whole header (including bytes after the directory), entry order and names, padding
  between entries, trailing bytes, and every untouched entry's stored bytes (even if compressed). Only entry offset/size fields and the
  declared archive size (`u32 LE @4`) are patched. With no edited entries the output is byte-identical to the input.
* When an entry changes size, later entries shift; padding is recomputed to the archive's inferred alignment (largest power of two
  dividing all offsets - 128 for most archives), keeping the original filler bytes and any extra slack.
* Special cases found in the game data and handled: zero-size placeholder entries at offset 0 (`EACN/BUNDLE.BIG`), entries sharing
  identical ranges, partially overlapping entries (left alone while untouched; editing one is refused), directory tails (117 archives),
  one unsorted directory, duplicate names (8 archives), nested compressed archives.

### Verification

Each exported file is re-opened as a fresh tree and compared with the original:

* every BIG, at every nesting level: same entry count, order and names; same header size; every header byte except the offset/size
  fields and declared size is identical (so unknown header data survives); declared size equals the real size; no entry lost its alignment;
* every non-container entry: logical bytes equal the original or, for edited assets, equal the replacement.

The export dialog reports how many entries were compared per file. Any difference blocks the "verified" result and is listed.

## Format writers

### SSH (`SshWriter`)

Replaces one image's pixels (and the palette block for indexed types) in a copy of the file; the directory, other images, names, comments,
hotspots and unrecognised bytes stay. Dimensions must match (the UI offers to rescale). PAL8/PAL4 can **build a new palette** (exact when
the picture has <= 256/16 colours, weighted median-cut otherwise; fully transparent pixels collapse to one entry) or **keep the existing
palette** (nearest colour). Alpha uses the PS2 range 0-0x80. If a RefPack image's new data is larger than the old region, later blocks
shift and the directory offsets, the image's block size and the file size are fixed up. PS2 swizzles are produced by inverting the
verified un-swizzlers (probe images reveal the permutation; a non-bijective layout is rejected). No non-GST image in this game is
swizzled, so those paths are exercised on synthetic data only.

### APT / const (`AptEditor`)

Numeric fields are patched in place. A new string overwrites the old one only if it fits, is referenced exactly once and starts its own
string; otherwise it is **appended at the end of the file** and just that pointer is redirected (the old string stays, so shared strings are
never disturbed). Placement edits set the matrix/colour/character/clip flags as needed (a placement without a matrix starts from identity).

> Appending to the end of an `.apt` follows from the format (all pointers are file offsets) but has **not been tested inside the game**.

### LOCH (`LocEditor`)

Header and id table are kept byte for byte; the string area is rebuilt in index order with new offsets (untouched strings keep their exact
UTF-16 bytes) and trailing chunks are appended unchanged.

### Text (`TextCodec`)

Detects UTF-8 / UTF-16 BOM / UTF-8 / Latin-1 and re-encodes identically (all 1,785 text assets re-encode to the same bytes). Line endings
are preserved (mixed endings are normalised to CRLF on edit).

## Validation (`MVPWorkshop.Probe validate <game>`)

Runs against the real game files:

| Check | Result |
|---|---|
| `layout` - every BIG (1,085 incl. nested) rewritten with one entry replaced by itself -> byte-identical; with one entry grown -> reopens with same names/header/padding/alignment, all other entries identical | 1,085 / 1,085 |
| `reencode` - every top-level archive rebuilt with every RefPack entry decompressed and recompressed and every BIG re-laid-out, then re-opened and compared entry by entry | 28,358 entries, 0 differences |
| `ssh-roundtrip` - decode -> re-encode (existing and rebuilt palette) -> decode for all PAL4/PAL8/RGBA8888 images; nothing outside the image changes; noisy growth path | 29,215 images: 29,213 pixel-exact; 2 (`IGCRSR1.SSH` APMH/LPMH) index past their 27-colour palette in the original data; 14,406 GST images unsupported (preview-only) |
| `swizzle-test` - forward swizzle then un-swizzle on synthetic data | OK (one 16x16 4bpp layout rejected as non-bijective) |
| `edit-formats` - APT text/placement/const, LOCH, text codec | 968 movies, 446 text fields, 1,370 placements, 536 constants, 3 LOC tables, 1,785 text files |
| `edit-test` - real edits three levels deep (BUNDLE.BIG -> stdArt.big -> .const) and in TITLE.BIG, exported, re-opened, game files hashed before and after | OK |

The GUI path is covered by `MVP-Workshop.exe --selftest report.txt`: it applies edits through the real viewers and checks that the
viewers refresh, the tab/tree markers, undo/redo, dirty tracking, project save/load, export verification, the preview-only paths and the
output-folder guard.

## Not done / limits

* RGBA5551 textures, models, animations, audio, `.fel` layout (only its text), APT bytecode and shape geometry are not editable. Linear RGB888 and supported GST layouts now have writers; see [texture import](TEXTURE_IMPORT.md). The older survey above predates these writers.
* Assets inside SCHl packs / PROJx OFF packs can only be previewed.
* New files cannot be added to an archive and entries cannot be deleted or renamed (the directory is preserved as is).
* Edits have not been run in the game or an emulator.

## Team assignments

Open **Teams → school → Edit assignments**. Select a native uniform family, logo family, or decoded stadium enum. The draft shows current/proposed IDs, packed field offsets, executable stadium mapping evidence, resolved asset links, texture sheets, dressed-player and stadium previews before **Apply previewed assignments to project**. Select the preview's Uniforms, Logos or Stadium tab to inspect that category; source assets open from the preview.

Apply creates one undoable roster edit. Save Project persists it; Export Mod rebuilds the database archive into the separate mod output folder and reopens it for verification. Undo/redo and project loading refresh open school views, the team browser's IDs, and its asset links. No executable ownership records are written: a roster stadium assignment is separate from the authentic venue registry. Only logo bits 1–9 and uniform bits 10–18 at record +164, and stadium bits 4–9 at +168 are changed. The third asset slot and all other packed bits/unknown bytes are retained. Stale drafts must be reopened if the roster changed after review.

Regression validation against a game folder:

```powershell
dotnet run --project tests/TeamAssignments -c Release -- ../../game research/assignment-roundtrip
# UI selection/apply/browser/undo/redo regression and screenshot:
src/MVPWorkshop.App/bin/Release/net9.0-windows/MVP-Workshop.exe --assignment-selftest research/assignment-ui.txt
```

The core check exercises all named variable-length records at minimum/maximum IDs, verifies unrelated bits byte by byte, checks browser asset resolution, undo/redo, project reopen, and a real verified archive export. Its output contains only a project, rebuilt database archive and export manifest; originals remain untouched.

Validated on the supplied disc data: **12 core/export checks passed**, including all 152 named records and all 11 logical entries in the rebuilt database archive; **9 UI checks passed** for draft isolation, Apply, live team/browser refresh, and undo/redo. Reports: `research/assignment-roundtrip.txt`, `research/assignment-ui.txt`; UI capture: `research/assignment-ui.txt.png`. The app was built to `research/assignment-app` for validation because an existing process held the normal Release executable open.

## Texture editing inside APT movies

Open an APT movie and choose **Textures**, or use **Edit textures…** in the stage's
edit panel. The tab embeds the existing SSH texture editor: select an image,
export PNG for external painting, then use **Replace image…** to review/import it.
Palette rebuilding/retention, channels, swizzle inspection and native dimension
checks are the same as in the standalone texture editor. There is no built-in
pixel-painting canvas.

The source picker lists the movie's paired `.ssh` and packs belonging to recursively
resolved APT imports. Movies without a local SSH pack can edit their resolved
imported packs. Hover the picker for full movie/texture source paths. Import
resolution follows the stage renderer's same-directory preference; missing and
ambiguous imports are diagnosed instead of choosing an unrelated pack. Cycles
and import depth are bounded. This list identifies source packs; it does not
claim every image in a pack is used by the currently displayed frame.

Replacing a shared imported image changes every consumer of that source.
Supported replacements enter the normal project history and can be saved and
exported. The APT stage rebuilds after local or discovered imported pack edits,
undo/redo and project changes. Source/image selection survives those refreshes.
The simulator also refreshes through its existing live-project integration.
Unsupported formats/container layouts retain the existing **PREVIEW ONLY** policy:
those replacements appear only in the embedded texture viewer, not in the APT
stage or exported mod. Eligible uniform native upscales can retain higher resolution (experimental); see
[texture import limits](TEXTURE_IMPORT.md).

Validation: `MVP-Workshop.exe --apt-texture-selftest report.txt` drives the actual
embedded editor on the game's `stdPopup.apt`, edits a resolved shared texture,
checks stage refresh, selection retention, untouched images, undo/redo and a
paired local movie. Results: [APT texture integration report](../research/apt-texture-test.txt).
The existing `tests/Textures` suite covers writer round trips, project save/load
and mod export/reopen.
