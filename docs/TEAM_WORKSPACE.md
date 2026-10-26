# School workspace

Teams → school opens a workspace with roster/player details, portraits, equipment,
audio, the assignment editor, dressed uniforms, stadium sources and logos. All source
actions use `MainWindow.OpenAsset`, retaining the existing document tabs, texture/text
editors, model viewers, audio player, hex inspection and exports.

## Relationship evidence

| Relationship | Evidence | Confidence / boundary |
|---|---|---|
| School → roster slots | `roster.bin`: count at team +192; slots start +196, 8-byte stride; variable team record size 487 + count × 8 | Confirmed by all stock roster keys resolving to player DAT rows. Slot order is retained; batting order and the second packed word are not interpreted. |
| Slot → player databases | First slot word is LE32; matches hexadecimal first column in `attrib.dat`, `lhattrib.dat`, `rhattrib.dat`, and (where present) `pitcher.dat` in DATABASE.BIG | Exact key equality, independent of school/name strings. Missing rows are explicitly unresolved, never replaced by a default player. |
| Player → named attributes | DAT header names each indexed field; each detail shows source path and one-based line | Raw values and enums retained. No invented position labels, height/weight conversions or equipment selection semantics. LH/RH/pitcher-specific fields retain their own source headers. |
| Player → appearance/equipment | `attrib.dat` fields 11–17/29 are face/body settings; 18–28 name cap, cap position, eye protection, helmet, elbow guard, wristbands, shin guard, socks and catcher mask | Values confirmed; mesh/texture selection rules unresolved. Shared GEOMETRY.BIG, GHEAD.BIG and non-uniform MODELS.BIG sources are discovery links, not school-owned assets. |
| Player → portrait | PORTRAIT.BIG contains portrait SSH sources; executable contains `frontend\\portrait.big` and `%c%c%c%c` strings | The appearance-to-filename algorithm has not been decoded. No portrait is selected from school ID, player key, jersey or slot. Portrait tab exposes appearance fields and a searchable shared source library. |
| School → uniforms/logo | Packed roster +164, bits 10–18 / 1–9; native `u%03d%c` / `f%03d%c`, menu artwork, LOGOS.BIG `lNNN.ssh` | Existing decoded family resolution and dressed preview retained. Variant suffixes keep disc labels. |
| School → stadium | Packed roster +168, bits 4–9; executable map and native registry described in FORMATS.md and STADIUM_SCENES.md | Assigned and registered authentic venues remain separate. Includes all resolved time-of-day source assets, configuration and menu panoramas. |
| Player → audio fields | `attrib.dat` header fields 30 (`playerattrib_audioid`) and 43 (`playerattrib_ditty`) | Values confirmed; runtime event/bank/stream lookup unresolved. |
| School → audio lookup rows | Exact whole CSV cell matches against school name / nickname in DBMISC.BIG schoolnameaudio.csv / nicknameaudio.csv | Source row confirmed, including line and text. A matching nickname can be shared by multiple schools. No match is explicitly unresolved; no inference of stream ownership. |
| Audio source discovery | AUDIO archive/stream/index membership; executable includes named MUSIC/*.ASF paths | Browse and play sources directly. Filenames alone are not assignment evidence. LSU has no exact school-name row in schoolnameaudio.csv; its nickname matches `92,Tigers`. Neither establishes an event/stream binding. |

For LSU, roster record 32 starts at `0x551C`, first player slot at `0x55E0`
contains key `0B8F1FD6`, and `attrib.dat` line 1228 starts
`00b8f1fd6,0 LSU,1 1,2 1,...`. The stock school has 25 players, uniform/logo 032,
and stadium enum 3 → ABOXDAY / Alex Box Stadium. These are separate identities;
changing a uniform family does not change roster membership or portrait selection.

## Editing and refresh

Teams → school → Roster now provides a structured editor. Search by source name,
jersey, raw position ID or player key, then select a row. Each joined DAT table has
editable raw values with its exact header labels and field IDs. Appearance,
equipment, audio and ratings filters retain the same draft values. Apply commits
all changed fields in that source tab as one undoable project change. **Apply all
player drafts** validates all pending players/sources before committing, with one
undo step per modified DAT. Drafts survive player/section navigation and project
overlay refreshes in the same session. Reset draft discards only the selected
source's pending values; Reset all drafts reloads current project values. Close,
project switching, Save and Export guard against silently losing unapplied drafts.
Drafts themselves are not serialized: apply them before saving. Changes lists the
player key, source and old/new field values. Save Project
and Export Mod include applied changes through the normal session pipeline.

The roster and fields appear side by side, with readable display labels alongside
the exact source labels, original values, edited markers and field-name/ID search.
School info renders the selected school's metadata as labeled rows. Draft apply
merges unrelated concurrent edits but rejects a changed field/definition and keeps
the draft available for correction or reset.

`PlayerAttributeEditor` replaces only the selected value spans in Latin-1 source
bytes, retaining headers, key spellings, field order, whitespace, line endings,
unknown columns and other rows. It rejects ambiguous spans, delimiter/control
injection, unsupported characters, nonnumeric attribute values and fractional edits
to integer fields. Source units, valid gameplay ranges and enum labels remain
unresolved: the editor does not invent them. Unknown fields are read-only. It never
rewrites roster membership, slot words or packed bits. DAT files have a header and
comma-separated indexed rows with `;` terminators; no embedded checksum field has
been identified. The existing archive writer and reopen verifier handle enclosing
BIG/RefPack storage; no speculative checksum bytes are changed.

Selected-player buttons open searchable portrait and ORD source libraries, carrying
the player's key/name as context. Opening a source uses the existing texture or 3D
viewer. These are manual links to shared assets: automatic player portrait,
face-morph and equipment bindings remain unresolved.

School database links now stay scoped to the selected school. Roster/DAT entries
under a school open that school's structured roster, and School info shows only
its decoded team data and exact matching metadata/audio lookup rows. The full
shared database remains accessible through the general file browser. The workspace reads
`AssetNode.ReadAll()` through the project overlay. Apply, undo, redo and project load
rebuild roster joins and links, including changes to DAT records, lookup CSVs,
localization, executable mapping and textures. The selected workspace section and
roster player are retained. Project save without a new history state does not rebuild
the view. Assignment controls are initialized on first use; proposed previews retain
their original behavior. Invalid/duplicate player DAT keys report a workspace problem
instead of inventing associations. Original game files remain untouched.

## Validation and captures

- Editor completion pass: 47 WPF checks, zero failures, covering retained drafts,
  selected-player scope, field search, reset, batch prevalidation, multiple players
  in one DAT, save guards, overlay refresh, rebasing and undo. Report/captures:
  `research/player-finish-ui.txt` and adjacent PNGs. PlayerEditor now passes 30
  checks, including conflicting/unrelated concurrent fields and export reopening:
  `research/player-finish-data.txt`. Release app builds without warnings/errors.
- Structured PlayerEditor: 28 checks against the stock game data. Whole-table
  unchanged round trips retain all 1,609 pitcher and 3,801 records per other DAT
  (sampled selected rows). Edited names, jersey, position, appearance, equipment,
  speed, handedness contact and pitching stamina reopen after project save and
  verified BIG export. Includes unknown payload/format preservation and invalid
  input rejection. Report: `research/player-roundtrip.txt`.
- Structured Apply WPF regression: 27 checks, zero failures, including player Apply,
  exact undo bytes, redo and source navigation. Report and renders:
  `research/player-ui.txt` and adjacent PNGs.
- Release app build: zero warnings/errors.
- TeamAssignments: 319 checks, including all 152 schools' player joins, LSU's exact
  first key, shared-source confidence boundaries, masked assignment edits, undo/redo,
  project reopen, archive export and logical-entry verification.
- `--workspace-selftest report.txt`: LSU, Mississippi State (MSU), Virginia (UVA),
  player source controls, direct portrait/model/audio-source navigation, DAT edit
  refresh, undo/redo and retained selection. MSU/UVA use generic roster stadiums
  while retaining distinct authentic registrations; uniform/logo families also differ.
- `--assignment-selftest report.txt`: existing assignment editor regression checks.
- Reports and representative WPF renders are in [team-workspace](team-workspace/).
  Captures include school overviews, player details, appearance/equipment/audio
  fields and source libraries. WPF rendering captures the app itself without requiring
  a native desktop automation connection.

Run from the repository root:

```powershell
dotnet build src/MVPWorkshop.App -c Release
dotnet run --project tests/TeamAssignments -c Release -- game test-output/workspace-export
dotnet run --project tests/PlayerEditor -c Release -- game test-output/player-export
src/MVPWorkshop.App/bin/Release/net9.0-windows/MVP-Workshop.exe --workspace-selftest workspace-report.txt
```
