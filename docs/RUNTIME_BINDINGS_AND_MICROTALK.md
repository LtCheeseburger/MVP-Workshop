# School/runtime bindings and MicroTalk investigation

Investigation dated 2026-11-02. This is research evidence, not completion of roadmap 5.2 or 6.4.
No school-only audio/model/stadium action or MicroTalk production import is enabled by this work.
Game installations and the native port were read only.

## Datasets and reproducibility

- Xbox loose: `E:/Recomp Source/MVP06-NCAABB/game`.
- PS2: `E:/Recomp Source/MVP07-NCAABB/game`.
- Authorized native port: `E:/Recomp Source/MVP06-NCAABB/MVP06-Base/MVP06-XBERecomp`.
- Xbox `default.xbe` SHA-256: `264E0ECC4E6C33A32670B1F1F4CF1884D7D7FB3DE4A5BCFB6C37F00B12FEF96B`.
  The loose and authorized port `assets/default.xbe` copies match.
- PS2 `SLUS_215.82` SHA-256: `A8F01615DB039A0E7FBA9D5CCFE7A85728D5EC8D6FE368554777E12EF32C8642`.

Native addresses below are Xbox virtual addresses for that exact executable, not portable signatures.
Raw immediate matches are leads until the surrounding instructions are inspected.

```powershell
dotnet build src/MVPWorkshop.Probe -c Release -m:1 -p:UseSharedCompilation=false
# Requires gcc/MinGW with getopt.h and network access for the pinned public sources:
./scripts/codec-research/setup-microtalk.ps1
./scripts/codec-research/run-study.ps1 -GameFolder 'E:/Recomp Source/MVP06-NCAABB/game' -OutputDirectory test-output-research/xbox
./scripts/codec-research/run-study.ps1 -GameFolder 'E:/Recomp Source/MVP07-NCAABB/game' -OutputDirectory test-output-research/ps2
```

The probe inventories audio/database branches rather than rebuilding unrelated textures. It writes
`speech.json` (asset plus stream offset identifies a stream), `samples.json`, `stadium.json`,
`team-audio-candidates.json` on Xbox, `string-xrefs.json`, `native-calls.json`, executable hashes,
and a report. Extracted speech and executable slices stay in ignored local test output, not Git.
The study also writes `native-reference.json`, `encoding-study.json` and `encoding-study-rev3.json`.
The runner checks independent PCM dimensions/rounding and all fitting native repacks. It does not
turn measured signal error into a claim of acceptable listening quality.

`xbe-slice <game> <hex VA> <byte count> <output file>` and `xbe-references <game> <output directory>`
provide bounded read-only native evidence. For example, extract `53E2F0` for 704 bytes and disassemble
with `objdump -D -b binary -m i386 -M intel --adjust-vma=0x53E2F0 <slice>`.

## School audio: selectors found; stream ownership still incomplete

Native loader code at `55F010–55F03B` loads `schoolnameaudio.csv` into an array at object `+8604`
(capacity 160), and `nicknameaudio.csv` at `+4B94` (capacity 220). The loader `51E0D0` parses an
integer followed by a string into 68-byte rows. This establishes actual consumers of these CSVs,
rather than just matching school names to filenames.

The name-matching code in `53E2F0` scans these rows with 68-byte strides. On a nickname match,
`53E53A` reads its ID and calls setter `4D19E0`; on a school-name match, `53E549` calls `4D19B0`.
The setters and getters establish these in-memory team fields:

| Selector | Native team-object field | Getter / setter | Proposed serialized roster field |
| --- | --- | --- | --- |
| School-name audio ID | word `+A4`, bits 11–19 | `4D19A0` / `4D19B0` | record `+160`, bits 11–19 |
| Nickname audio ID | word `+A4`, bits 20–28 | `4D19D0` / `4D19E0` | record `+160`, bits 20–28 |

The serialized offsets account for the native object's extra four bytes. The proposed nickname
field matches the numeric ID and exact nickname text in `nicknameaudio.csv` for **all 128 named
Xbox schools**. LSU reads school-name audio ID **32** and nickname ID **92**, the latter `Tigers`.
This is materially stronger evidence than filename similarity. Explicit serialization tracing,
write/read tests and a runtime reassignment check are still required before authoring these bits.

At `8B4D4` and `8B4DE`, a consumer calls both getters and stores their results into a context at
`+14` and `+18`. Native constructor `41720` also installs explicit speech-header/data pack pairs:
for example `418CA/418D4` populate `AUDIO/proj3/nnamehdr.big` and `AUDIO/proj3/nnamedat.big`.
The final context/event/project/record selection chain has **not** been established. Consequently:

- Do not yet make confirmed school → audio-payload edges in `DependencyIndex`.
- A missing school-name CSV row does not prove missing school speech. The stock roster uses IDs
  outside the CSV's additional-school range; LSU's ID 32 is a concrete example.
- Renaming a sound or copying a DAT/HDR pair does not create an isolated school binding.
- Eight `Events.evt` files remain opaque. Their presence and header words are evidence, not decoded
  event meanings. Their project/script dispatch is the next consumer to trace.

Next implementation gate: trace context `+14/+18` through the speech event selector and its record
loader, validate both school-name and nickname cases in the native runtime, then author a complete
pair/family fork with collision checks and an atomic assignment edit. Reusing a nickname ID must
continue to count every school that actually uses it.

## Shared player bodies

The Xbox image holds a global body-name table at `6AAD40`: `models/hibody`, `models/bgbody`,
`mebody`, `lobody`. The loader at `273EE0` reads that table at `273EFD`; another indexed reference
appears at `277F47`. The texture loader at `273570` formats `models/textures/basebody.%s` and stores
shared resources on its manager. These are real runtime consumers, not school assignments.

No school-specific geometry pathname/ID selector was established. This does **not** prove one
cannot exist elsewhere. Trace the manager's per-player body/LOD selection and serialization before
proposing writable school-only geometry. A copied ORD/ORL pair alone is insufficient.

## Stadiums

The existing school selector is real: roster record `+168`, bits 4–9. In memory, getter `4D1970`
and setter `4D1980` operate on object `+AC`, bits 4–9. The audited Xbox table contains **30 enums**,
with three time-of-day mappings; its map is at file `5F0CC8`, filename-pointer table at `699CB8`.

Of 128 schools, 28 enum values are used. Values **0 and 29** are absent from those base assignments,
but both already have executable registry rows and native time-of-day resolutions; both registry
school-owner fields are `-1`. They are **not proven free fork slots**. Postseason, create-a-ballpark,
menus and save consumers still need auditing before any reuse. The PS2 table has 34 enums and nine
values absent from base school assignments, likewise not evidence that they are free.

Reassigning an existing venue is supported. Adding an arbitrary copied venue requires a runtime
path selector or safely extensible mapping/registry; roster repointing by itself cannot name a new
archive. Do not consume an apparently unused enum to make a school-only button look functional.

## MicroTalk: viable codec path, unresolved allocation/quality integration

The inventory found the following **stream records**, not unique container files. Bank coverage is
not claimed by this stream survey.

| Installation | MicroTalk streams | Codec / revision | Native rate | Loop-tagged streams | Sample-count mismatches |
| --- | ---: | --- | ---: | ---: | ---: |
| Xbox | 12,229 | `04`, revision 3 | 22,050 Hz mono | 0 | 0 |
| PS2 | 13,262 | `04`, revision 3 | 24,000 Hz mono | 0 | 0 |

The inventory reports 4,051 paired / 885 unpaired logical HDR records on Xbox, and 4,141 / 901 on
PS2. Pairing establishes container/record correspondence, not school or named-event ownership.

A genuine [public-domain encoder](https://github.com/daddesio/utkencode/tree/e0b13fac7a75fc5ef16851c165d3f31f0fe220b1)
exists. Its original output is standard Maxis UTM0; it does not directly produce MVP containers.
The research script adds the native revision-3 frame marker and byte alignment, keeping the UTM0
header solely as a temporary experiment envelope. That adapted envelope is **not** a valid Maxis
interchange file. The game codec tags are never changed.

The independent reference uses pinned
[vgmstream `utkdec.c`](https://github.com/vgmstream/vgmstream/blob/d29ffa6e19a41efb54bfd9f68bd65f93c3d2ab25/src/coding/libs/utkdec.c),
compiled separately; it links no Workshop decoder code. Eleven real utterances per installation
decode to matching sample counts and differ from Workshop by at most one signed PCM unit. Six
utterances per installation were then re-encoded at five bitrates in standard and revision-3
framing: **120 encoding experiments**, again with at most one PCM unit of decoder disagreement.

For revision-3 output:

| Target bitrate | Xbox mean SNR / native block fits | PS2 mean SNR / native block fits |
| --- | --- | --- |
| 16 kb/s | 3.31 dB / 2 of 6 | 2.97 dB / 2 of 6 |
| 24 kb/s | 5.76 dB / 2 of 6 | 5.03 dB / 2 of 6 |
| 32 kb/s | 13.17 dB / 1 of 6 | 10.16 dB / 2 of 6 |
| 48 kb/s | 19.02 dB / 0 of 6 | 18.33 dB / 0 of 6 |
| 64 kb/s | 19.93 dB / 0 of 6 | 19.88 dB / 0 of 6 |

At 32 kb/s, all six examples on each installation fit their **total** native allocation; most fail
at individual SCDl blocks. Xbox example `proj0/proj0dat.big` has 35 blocks with different capacities,
usually 1,296 or 1,728 samples and a final partial frame. Treating the sound as one allocation would
overwrite framing or adjacent data. This is an observed failure, not merely a hypothetical risk.

The **11 fitting revision-3 experiments** were actually copied into standalone native SCHl clones:
block sizes, sample counts, channel offsets, leading flags and all non-payload blocks stayed exact.
The independent decoder reproduced the candidate PCM byte-for-byte after repacking. For example,
Xbox `proj4/nnamedat.big/001a.dat` fits at 32 kb/s with 15.16 dB SNR and no clipped samples.
No game source was changed, and no native-game playback acceptance is claimed.

### Per-block budgeted encoder (2026-11-07)

`src/MVPWorkshop.Core/Formats/MicroTalkEncoder.cs` is a C# port of the pinned utkencode. With utkencode's
two uninitialised arrays zeroed (`values`, `zero_counts`; the original reads `zero_counts[107]` undefined
when `a=1`) and its integer-division bit target (138 innovation bits at 32 kb/s, 22,050 Hz), the port is
**byte-identical** to utkencode-rev3 over a 20-frame test signal, so the analysis is a faithful port,
not a reimplementation.

Rate control is per SCDl block. Each block's frames are encoded from a snapshot of the encoder state at a
single innovation bit target, binary-searched for the largest target whose byte-aligned frames fit that
block's fixed capacity. Only then is state committed, so the adaptive codebook and reflection-coefficient
history always equal what the decoder reconstructs from the committed bits. The decoder carries state across
blocks and resets only its bit reader, so rate changes at block boundaries need no state reconciliation.
For starved blocks the encoder has two additions beyond utkencode:

- an all-zero innovation candidate with `a=0, z=1` (one 14-bit run per subframe). This reaches the native
  minimum of 26 bytes per frame, the same size the stock encoder uses in silent stretches (e.g. blocks of
  1,728 samples in 107 bytes). It is offered only below 32 target bits, so default-rate output is unchanged.
- a fallback that clamps the first reflection index into the Huffman range when triangular frames
  (≥48 bytes each) cannot fit. It is reported per block (`ForcedHuffmanBlocks`); the real study needed it
  nowhere.

**Closed-loop stability check.** utkencode is open loop: LPC analysis sees the original signal, not the decoder's
synthesis. At an onset after silence, near-unity reflection coefficients give the synthesis filter enough gain to
amplify innovation error into clipping. In the 2,327-stream trial, `pnamedat.big/600.dat` decoded at -30.7 dB in
its first block (a full-scale burst over a source peaking at 3,015). Each block candidate is now decoded through
a mirror of the real decoder. If its SNR is below 3 dB, the first four reflection indices are clamped progressively
(|rc| ≤ 0.971, 0.952, 0.828) until the block is stable; otherwise the lowest-error candidate is used. That block
now decodes at +2.1 dB or better with no burst, and the other blocks are unchanged. Reports carry
`StabilizedBlocks` and `WorstBlockSnrDb` (near-silent blocks excluded). The synthetic onset test in
`tests/AudioReplacement` checks stability but does not itself trigger the clamp; `600.dat` is the real regression case.

`MVPWorkshop.Probe microtalk-test-mod <game> <out.mvpmod> <folder>...` builds an in-game trial mod that re-encodes
every MicroTalk stream under the given loose folders from its own decode. Packed archives such as `proj4dat.big`
cannot be overridden by mods.

`MVPWorkshop.Probe microtalk-budget-study <dir> <dir> mt-reference.exe` re-encodes each study sample into
its own native SCHl and checks the result with the pinned vgmstream decoder:

| Samples | Fit every block | Framing exact | Independent decoder | SNR vs original decode |
| ---: | ---: | ---: | --- | --- |
| 11 | 11 | 11 | same sample count, max 1 PCM unit | 17.3–21.5 dB (mean 18.98 dB over the 10 non-silent samples) |

Native capacity is the stock encoder's own output size (blocks are tightly packed, about 39 kb/s), so a
replacement can only match the stock rate, never exceed it. The `xbox` and `ps2` study folders hold
identical decoded samples and block layouts. Their results are identical and are not independent evidence.
Clip counts (up to 17 samples) mirror clipping already in the stock decodes. Encoding takes about 0.1–0.6 s
per utterance.

Remaining gates for production replacement:

1. ~~A state-consistent encoder with per-block budgets.~~ Done, see above. Banks remain unhandled because
   a bank sound is one allocation without SCDl blocks.
2. Representative listening tests and a defensible signal-quality policy; SNR alone is insufficient.
3. Loop/substream state, banks/shared allocations, PCM-patch behavior and real MT5 assets. The surveyed
   streams and experimental output do not validate all of these cases.
4. Native game-path verification, bounded/cancellable tool integration or a maintained encoder port,
   then preview, impact review, stale checks, atomic undo and bulk import integration.

The blocker is now **listening and native-game validation**, not the encoder. Keep production rejection in
the editor until gates 2–4 are satisfied; do not mark 6.4 complete. Per-sample `*-budget.wav` (Workshop
decode) and `*-budget.schl` files written by the budget study are the listening-test inputs.
