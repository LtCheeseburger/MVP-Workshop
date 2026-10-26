# ISO editing

MVP-Workshop accepts an extracted game folder or a cooked **2048-byte-sector ISO9660
image**. **Open ISO** mounts the ISO directory tree read-only without extracting the
whole disc. BIG archives and their nested assets use the same viewers, editing
history, projects, APT texture editor, and live previews as folder-backed assets.
Opening another source closes the previous source's documents after the normal
unsaved-project check. Projects remember the folder or ISO source path.

**Export Mod** still writes changed loose files/rebuilt archives to an output
folder, including for ISO-backed projects. **Export ISO** writes a new image:

- For a directly opened ISO, that image supplies the layout.
- For an extracted folder, choose the matching original ISO as the layout template.
  Each changed source archive must match its original ISO entry before rebuilding.
  This avoids constructing boot/system metadata from an incomplete extraction.
- Outputs must use a new filename. Neither an original ISO nor an existing output
  image is overwritten. Extracted game folders cannot contain the output ISO.

The exporter rebuilds/structurally verifies nested BIG archives first, copies the
source image, retains unchanged sectors and the boot/system area, and patches files
that fit their existing allocated sectors. Growing files are appended on a 2048-byte
boundary; both endian copies of directory extent/length fields and the primary
volume size are updated. The game's decoded **PS2DIR.BIN** runtime index receives
new LBAs/sizes for changed files; manually editing that generated index is rejected.
If the index exists and a changed file cannot be mapped into it, export stops.
The temporary image is reopened and every changed file/index compared before the
new output is published. Partial temporary outputs are removed on failure.

Supported scope is ISO9660 with a single directory tree. Raw BIN/CUE sectors,
Joliet/supplementary trees, UDF bridge export, interleaved/multi-extent files,
extended attribute records, shared/overlapping changed extents, and output beyond
single-layer DVD capacity are rejected. UDF recognition is reported during export;
its metadata is not silently left stale. No new ISO can be created without an
original layout template. Game boot/playback and undocumented hardcoded LBAs
outside PS2DIR.BIN have not been verified on console/emulator.

Native high-resolution texture imports are retained in ISO output under the
[experimental resize restrictions](TEXTURE_IMPORT.md). Retaining pixels does not
rewrite game UV/GS consumers or guarantee runtime texture compatibility.

## Validation

```
dotnet run --project tests/Iso -c Release
dotnet run --project tests/Textures -c Release
MVP-Workshop.exe --game source.iso --iso-upscale-selftest report.txt
```

The ISO regression creates a synthetic ISO9660 fixture containing nested BIG/SSH
assets and PS2DIR.BIN. It checks 2× resolution through project save/load and ISO
export, relocated extents/index values, source hashes, unchanged files/system area,
extracted-folder/template export, folder mod export, overwrite protection, malformed
records, UDF rejection, and mismatched template rejection. The app self-test uses
that fixture to exercise the existing texture viewer and native encoded review,
then exports/reopens an ISO. See [app validation](../research/iso-upscale-test.txt).
No original full-disc ISO was available for real-disc/boot validation in this run.
