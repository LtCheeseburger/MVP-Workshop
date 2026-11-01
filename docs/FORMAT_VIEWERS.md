# Structure and viewer coverage

Every asset document exposes Preview, Hex, Strings and Structure. Structure shows decoded fields,
bounded source-byte regions and an explicitly uninterpreted header-word table (first 4 KiB, both
byte orders and printable bytes). Formats without decoded fields open the raw table immediately.
Selecting a region navigates to and highlights that byte range in Hex. Hex also has a region picker.

Known regions cover BIG directories/members, SHPS/SHPX image directories/headers/pixels/attachments,
ELF sections, XBE sections, DDS headers/payloads, SCHl blocks, BNK patch headers and Sony HD chunks.
Split ORD/ORL structure offsets refer to the selected source file; the model/ELF preview can join
partners separately. Truncated or invalid structures retain safe raw access and report parse errors.

The format survey includes viewer coverage in its grid and Markdown export. **Needs specialized
viewer** filters raw and header-only formats, sorted by file count, to prioritize further decoding.
A raw table does not mean the format's semantics have been decoded.

## Current research gaps

The Xbox source survey on 2026-11-01 scanned 36,847 assets. The largest gaps by file count were
3,649 binary `.hdr` records, 8 binary `.evt` files, 3 binary `.bin` files and single binary history
data, ASF lookup and GLD skeleton files. XBE files already expose decoded headers/sections, while
GLD exposes recovered names. All now have table/header views and Hex regions where known.
Speech headers recognized by the existing parser retain their specialized viewer; unrecognized
variants remain raw. School roster editors supply additional context for supported roster data.

PS2 icon headers and icon.sys fields are decoded. PFR/PF font payloads, raw code overlays and unknown
binary record layouts remain research formats with raw word tables. No guessed record boundaries,
audio event names or editable fields are presented as confirmed structure.

`tests/Structure` checks bounded overlays, truncated input for every format and survey coverage.
The desktop viewers test exercises the actual Structure and Hex tabs on a native audio fixture.
