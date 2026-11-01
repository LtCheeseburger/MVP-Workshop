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
encoded-preview cancel, impact review, commit and byte-exact undo. `scripts/verify.ps1` runs it with
synthetic fixtures and isolated app settings; it does not require game files.
