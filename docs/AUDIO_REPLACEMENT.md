# Audio playback and replacement

The audio viewer opens SCHl streams and concatenated stream packs, BNKl banks, embedded ABKC banks,
and paired Sony HD/BD banks. Sony sample offsets and rates come from the HD Vagi table; a missing
partner is reported rather than assuming a rate or sample boundary. Stream packs list their streams
directly, while the Explorer retains their individual entries.

Playback/WAV export uses the existing EA-XA, MicroTalk 10:1/5:1, VAG and PCM decoders. Metadata-only
ASF lookups, speech headers and CSI scripts link or describe sounds; they are not audio payloads.
The Xbox source survey found EA-XA and MicroTalk streams; real PS2 validation also covers Sony VAG.

## Replace audio

Select a sound and **Replace audio**. Import 16-bit PCM WAV directly. MP3, OGG and other WAV encodings
are converted through `ffmpeg.exe` on PATH; the viewer explains this dependency if it is missing.
Conversion runs off the UI thread with bounded input, output and runtime. No shell command is used.

The replacement is resampled to the native rate, mixed/routed to the native channel count, and
trimmed or silence-padded to the exact original sample count. A preview dialog plays the actual
re-encoded and decoded bytes before the replacement impact review. Cancel leaves the project
unchanged. Commit uses the normal project overlay and undo; reopen the viewer after committing
to replace another sound. Source data and Sony header partners are rechecked before commit.

Supported replacement codecs are VAG/PS-ADPCM (0x05), EA-XA (0x0A, including version-3 streams),
PCM16BE (0x07), PCM16LE (0x08) and PCM8 (0x09). ADPCM encoding uses predictor zero with a fitted
shift per 28 samples, so quality may differ from the original game's encoder. The preview reflects
that quantization. Stream block sizes, bank pointers, native sample counts and loop metadata remain
fixed; replacements that cannot fit are rejected. ABKC replacements retain the complete outer bank.
VAG flags are preserved. Shared bank/sample payloads are rejected for isolated replacement.

**MicroTalk replacement remains unavailable:** no verified MicroTalk encoder is implemented.
Playback and export remain available. The tool does not silently substitute another codec.
Growing/repacking banks, changing durations or authoring new loop points are also unsupported.

## Folder import

**Import folder…** replaces many sounds of the open container (SCHl stream file or pack, BNKl bank
including ABKC-wrapped banks, or a Sony HD/BD pair) at once. Inputs are matched by exact,
case-insensitive file name, the same names **Export All** writes:

| Target | File name |
| --- | --- |
| Stream file / stream pack | `<asset stem>_Stream_<index>.wav` |
| Bank sound | `<asset stem>_Sound_<index>.wav` |
| Sony HD/BD sample | `<BD stem>_Sony_sample_<index>.wav` |

`.wav`, `.mp3` and `.ogg` are accepted; each goes through the same conversion as a single import.
The review lists every input with its native target (codec, rate, channels, duration), conversion
result and an encoded preview. Files with no matching sound, other extensions, two inputs naming one
sound (all copies are rejected; none is chosen), conversion failures and MicroTalk targets are shown
with a reason and not applied. Proposed bytes are built before review by applying each accepted sound
in index order to one copy of the container, so several sounds in one bank form one edit and untouched
sounds keep their bytes. Cancel applies nothing. After the impact review the asset bytes and partner
sources are rechecked; any newer edit aborts the import. The result is committed as a single undoable
change of the container asset (stream-pack virtual children are views of that same asset).

## Usage and impact

The viewer shows confirmed dependency consumers and opens **Used by / moments** for the complete
relationship view. Undecoded school/event bindings are labelled unresolved. Filename similarity is
not presented as a confirmed event assignment. Audio school-only forks remain unavailable until
their writable assignments are decoded; global replacement uses the existing impact review.

## Validation

`tests/AudioReplacement` verifies native encode/decode dimensions and signal error, unchanged
headers/frames, shared payload rejection, WAV input, resampling/channel conversion and unsupported
codecs. A PS2 game-folder argument additionally checks real Sony HD/BD and EA-XA stream replacement.
The desktop viewers check covers WAV/MP3/OGG input (compressed inputs when ffmpeg is installed),
encoded-preview cancel, impact review, commit and byte-exact undo, plus folder import matching, unmatched/duplicate/corrupt rows, cancel, stale-review rejection, single-edit commit, undo/redo and project save/reload. `tests/AudioReplacement` covers name matching and composing several sounds of one bank. `scripts/verify.ps1` runs it with
synthetic fixtures and isolated app settings; it does not require game files.
