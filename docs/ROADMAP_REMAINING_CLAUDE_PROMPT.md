# Claude handoff: finish the MVP Workshop roadmap

Copy the prompt below into Claude Code, opened in `E:/Tool Source/MVP-Tool`.

---

Continue implementation of the remaining MVP Workshop roadmap. Work from the current checkout;
do not restart completed phases or treat checked boxes as proof that every original requirement
is satisfied. Read `docs/ROADMAP_PROMPTS.md` as the original specification, then `docs/ROADMAP.md`,
`CHANGELOG.md`, and the implementation documents listed below.

The application is WPF/.NET 9: `src/MVPWorkshop.App` and `src/MVPWorkshop.Core`. All edits go through
`EditSession`, with source files unchanged, one undo history, project Added files and an explicit
distinction between saved edits and preview-only changes. Keep the existing theme and UI style.

## Repositories and data

- Workshop: `E:/Tool Source/MVP-Tool`.
- Authorized native port: `E:/Recomp Source/MVP06-NCAABB/MVP06-Base/MVP06-XBERecomp`.
- Xbox loose validation data: `E:/Recomp Source/MVP06-NCAABB/game`.
- Xbox packed/source validation data: the port's `assets` directory.
- PS2 validation data: `E:/Recomp Source/MVP07-NCAABB/game`.

Verify these paths and applicable repository instructions before using them. Use the authorized
port checkout if native changes are necessary. Do not silently target another similarly named
checkout. If Git on PATH invokes broken devkitPro/MSYS Git, use
`C:/Program Files/Git/cmd/git.exe`.

`docs/ROADMAP_PROMPTS.md` was an existing user-owned untracked file at handoff. Preserve it; do not
stage or overwrite it accidentally. Inspect Git status first and preserve other user changes.

## Current baseline — verify, then retain

Phases 1–5 and 7.1, 7.2, 8.1 and 8.2 have substantial implementations. Phase 6 recently landed:

- `a82a7b3`: model inspection, sibling LODs, materials, wireframe/normals/UV, glTF/OBJ export and
  constrained Xbox FLOAT3 position replacement.
- `ff26553`: animation clip export and playback improvements.
- `bc913b2`: universal Structure tables, Hex byte-region overlays and survey viewer coverage.
- `7054e56`: native audio replacement, encoded preview, impact review and undo; final UI fixes.

Relevant documents:

- `docs/REPLACEMENT_SCOPE.md`
- `docs/UNIFORM_PREVIEW.md`
- `docs/MODEL_INTERCHANGE.md`
- `docs/FORMAT_VIEWERS.md`
- `docs/AUDIO_REPLACEMENT.md`
- `docs/TESTING.md`
- `docs/HOT_RELOAD_PROTOCOL.md`

Latest recorded full verification passed the console suites and all 17 synthetic WPF checks.
Real Xbox mesh/animation and PS2 audio checks also passed. These are historical evidence, not a
substitute for testing new changes. Original 1.1–1.3 have live-app regression coverage in the
explorer/project desktop tests; audit the actual requirements and fix uncovered gaps rather than
rewriting working functionality. The old Edited-only action was superseded by Modified/Added
tree filters; do not reintroduce a redundant control just to match old wording.

## Remaining implementation, in practical order

### 1. Complete 7.3: bulk audio replacement

This is the clearly unimplemented numbered prompt. Add a reachable folder-import action using
the existing audio conversion/encoding pipeline, with a review screen comparable to
`FolderImportReview`. Inspect `AudioView`, `AudioReplacement`, `SonySoundBank`, `AemsViews`,
`EditSession` and existing review patterns before designing it.

Define and document deterministic filenames for stream, bank and multi-sound targets. Support
WAV/MP3/OGG through the same conversion path as individual imports. Display each matched input,
native target, rate/channels/codec/duration, conversion result and confirmed impact. Show
unmatched, duplicate/ambiguous, corrupt and unsupported inputs explicitly; never silently choose
between collisions. Do not turn a per-sound import into an overwrite of its entire container.

Build proposed bytes before approval. Allow encoded previews. Cancellation must apply nothing.
Recheck session/source/partner baselines after review; a stale plan must not overwrite newer edits.
Preserve unrelated sounds, bank/ABKC wrappers, offsets and loop metadata. Publish accepted edits
as an atomic undoable batch, including multiple edits to one bank. Handle parent stream packs and
their virtual children without overlapping conflicting writes. Include meaningful automated and
running-app checks for matching, conversion, ambiguity, cancel, stale review, commit, undo/redo,
project save/reload and exported bytes. Keep unsupported MicroTalk rows visible with a reason until
the encoder below is implemented.

### 2. Finish the unmet portion of 6.4: MicroTalk replacement

Playback/export already exists for MicroTalk 10:1/5:1. Replacement currently supports VAG, EA-XA
and PCM only. The Xbox survey found thousands of MicroTalk speech assets, so this is a material
gap in the original requirement, not an obscure optional enhancement.

Investigate the actual native codec and container constraints and implement a verified native
encoding path. Inspect `MicroTalk.cs`, EA patch headers, SCHl/bank handling and available native
decoder evidence. Do not silently change the codec tag or relabel PCM as MicroTalk. Do not claim
an encoder exists merely because Workshop's own decoder accepts its output: verify against an
independent decoder or the native game path and representative real speech. Preserve framing,
loop state and unrelated data; demonstrate usable signal quality. If a genuine external blocker
remains, record its concrete evidence and leave this requirement incomplete while advancing
other work. Explicit rejection is necessary safety behavior, but does not fulfill replacement.

### 3. Finish 5.2 scope isolation and 6.4 usage relationships

The current school-only fork is implemented for uniform/logo families. Audio, shared player-model
and stadium bindings still lack writable per-school isolation; their button is disabled with an
explanation. Audio school/event usage also remains unresolved where references are undecoded.

Trace real assignment and runtime-consumer relationships, then extend `DependencyIndex`,
`TeamAssignmentEditor`/appropriate editors and fork planning where runtime references support it.
Copying an asset without repointing the actual consumer is not school-only replacement. Preserve
complete required families/partners and all unrelated schools, use collision-free Added paths,
and publish copies plus assignment edits together. Verify both scopes, source preservation,
cancel, stale plans, undo/redo, project persistence, export and native runtime resolution.

Audit every texture/audio/model replacement entry point, including raw and bulk imports, for
impact-review consistency. Usage counts must reflect confirmed relationships; filename guesses
must never be presented as confirmed ownership or named game moments. If a category truly has
no school-specific runtime selector, prove and document the structural limitation instead of
inventing one or declaring the original requirement complete.

### 4. Finish the semantic gaps in 6.3

Every file now has Hex/Strings/Structure and raw header-word tables. These are useful inspection
tools, but do not constitute decoded format-specific structure for unknown layouts.

Run current Xbox and PS2 surveys, prioritize missing structured viewers by actual file frequency,
and implement verified tables/headers/records with bounded offsets and Hex navigation. The last
Xbox survey scanned 36,847 assets; major gaps included 3,649 binary `.hdr` records, 8 `.evt` files,
binary roster/history variants, ASF lookup and GLD skeleton data. PFR/PF font payloads and raw code
overlays also retain generic/raw views. Some recognized speech-header variants and supported
roster structures already have specialized views: reuse those parsers rather than duplicating them.

Separate confirmed fields from raw/unknown values. Check truncated/malformed data and representative
real records; show format/parser variants accurately in coverage reports. Update the roadmap to
distinguish universal inspection from remaining semantic decoding. Do not call arbitrary u32 words
decoded fields simply to make coverage green.

### 5. Finish the remaining explorer actions

The roadmap still lists Open original and format-specific Import/Export as incomplete. Bookmark
already exists in the normal context menu, so verify it and avoid duplicating it.

Add read-only original/source inspection that cannot mutate the active overlay; clearly distinguish
vanilla bytes, selected base-mod bytes and current project bytes when applicable. Route format
commands to existing model/audio/texture/table operations. Check tree and search-result targeting,
Added assets, base-mod sources, disabled/unsupported states, source-folder output protection and
interaction with modified documents. Reuse working command implementations.

### 6. Complete release and end-state verification

Run the dataset-specific TeamAssignments, PlayerEditor and LooseTextures checks on their supported
real installations. `TeamAssignments` contains exact LSU/roster expectations: determine the intended
dataset before treating failures on another release as product defects. Preserve meaningful
assertions; do not weaken tests to make a mismatched dataset pass. Repair genuine implementation
bugs and keep an explicit record of any unavailable validation dataset.

Verify real Home/Away/Alternate uniform behavior where the native suffix mapping is still labelled
unverified. Preserve the working editable preview mappings and do not assert runtime roles based
only on names. Audit hot reload and launch-target evidence against the authorized port as needed.

The existing constrained mesh importer is explicitly allowed by 6.1's "where the format allows,
with clear limits": PS2/quantized streams, topology changes and normal/bounds rebuilding are not
automatically new requirements. Likewise 6.2 explicitly defers animation editing. Do not spend
this task building an animation editor or expanding mesh formats before the required gaps above.

## Working and completion requirements

Make concrete implementation progress, not just another plan. Work sequentially where shared
files are involved. Keep each roadmap item in a separate commit. Update `docs/ROADMAP.md`, the
Unreleased section of `CHANGELOG.md` only, relevant capability documents and CI coverage with each
item. Do not mark a partially implemented item complete by moving its original requirements into
an untracked future wish list.

Useful commands, with output directories outside game sources:

```powershell
dotnet build src/MVPWorkshop.App -c Release
./scripts/verify.ps1
# Use the appropriate supported validation installation:
./scripts/verify.ps1 -GameFolder 'E:/Recomp Source/MVP06-NCAABB/game'
dotnet run --project tests/ModelInterchange -c Release -- 'E:/Recomp Source/MVP06-NCAABB/MVP06-Base/MVP06-XBERecomp/assets'
dotnet run --project tests/AudioReplacement -c Release -- 'E:/Recomp Source/MVP07-NCAABB/game'
```

For model UI verification, the app's `--model-viewers-selftest <report.txt>` with the Xbox loose
game checks `data/geometry.big/mebody.ord`, materials/LODs, animation playback and model/clip export.
The synthetic `--viewers-selftest` is wired into the shared verification script and covers audio
preview/cancel/commit/undo. Extend it appropriately for bulk imports. Native WPF layout should be
checked in the running app and via rendered screenshots; inspect the images, not just file existence.

Before reporting completion, audit every original numbered prompt against code and current runtime
evidence. Report changes, commit hashes, validation results and all genuinely incomplete work.
Passing synthetic checks alone does not prove native codec correctness, writable runtime bindings,
or support for every surveyed format. Keep progressing through independent work when one research
item is difficult; do not redefine the requested end state around the easy cases.
