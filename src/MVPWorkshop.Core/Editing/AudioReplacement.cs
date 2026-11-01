using System.Buffers.Binary;
using MVPWorkshop.Core.Formats;
using MVPWorkshop.Core.IO;

namespace MVPWorkshop.Core.Editing;

public static class AudioReplacement
{
    public static string? Unsupported(EaPatchHeader header) => header.Codec2 is 0x05 or 0x0A or 0x07 or 0x08 or 0x09 ? null
        : "No verified encoder for " + EaPatchHeader.CodecName(header.Codec2) + ". Playback/export remain available; codec substitution is not performed.";

    public static DecodedAudio ReadPcmWav(byte[] bytes)
    {
        if (bytes.Length < 12 || Bin.FourCC(bytes) != "RIFF" || Bin.FourCC(bytes, 8) != "WAVE") throw new InvalidDataException("Expected a PCM WAV file.");
        int rate = 0, channels = 0, bits = 0, encoding = 0; byte[]? pcm = null;
        for (int at = 12; at <= bytes.Length - 8;)
        {
            int size = checked((int)Bin.U32LE(bytes, at + 4)); if (size < 0 || size > bytes.Length - at - 8) throw new InvalidDataException("Truncated WAV chunk.");
            if (Bin.FourCC(bytes, at) == "fmt " && size >= 16) { encoding = Bin.U16LE(bytes, at + 8); channels = Bin.U16LE(bytes, at + 10); rate = checked((int)Bin.U32LE(bytes, at + 12)); bits = Bin.U16LE(bytes, at + 22); }
            if (Bin.FourCC(bytes, at) == "data") pcm = bytes.AsSpan(at + 8, size).ToArray(); at = checked(at + 8 + size + (size & 1));
        }
        if (encoding != 1 || bits != 16 || channels is < 1 or > 8 || rate is < 1000 or > 384000 || pcm is null || pcm.Length % (2 * channels) != 0) throw new NotSupportedException("Use 16-bit PCM WAV, or convert this input through ffmpeg.");
        var samples = new short[pcm.Length / 2]; for (int i = 0; i < samples.Length; i++) samples[i] = Bin.S16LE(pcm, i * 2);
        return new(samples, channels, rate, "PCM WAV");
    }
    public static DecodedAudio Normalize(DecodedAudio input, int rate, int channels, int frames)
    {
        if (input.Channels < 1 || input.SampleRate < 1 || channels is < 1 or > 4 || rate < 1000 || frames is < 1 or > 30_000_000) throw new InvalidDataException("Invalid audio dimensions or replacement exceeds sample limit.");
        var result = new short[checked(frames * channels)]; int available = input.Pcm.Length / input.Channels;
        for (int frame = 0; frame < frames; frame++)
        {
            double source = frame * (double)input.SampleRate / rate; int lo = (int)source; if (lo >= available) break; int hi = Math.Min(available - 1, lo + 1); double mix = source - lo;
            for (int c = 0; c < channels; c++)
            {
                double At(int i) => channels == 1 ? Enumerable.Range(0, input.Channels).Average(k => input.Pcm[i * input.Channels + k]) : input.Pcm[i * input.Channels + Math.Min(c, input.Channels - 1)];
                result[frame * channels + c] = (short)Math.Clamp(Math.Round(At(lo) * (1 - mix) + At(hi) * mix), short.MinValue, short.MaxValue);
            }
        }
        return new(result, channels, rate, "Converted to native rate/channels; trimmed or silence-padded to the original sample count");
    }
    private static byte[] Encode(short[] mono, int codec, ReadOnlySpan<byte> original)
    {
        if (codec is 0x07 or 0x08) { var pcm = new byte[mono.Length * 2]; for (int i = 0; i < mono.Length; i++) { if (codec == 7) BinaryPrimitives.WriteInt16BigEndian(pcm.AsSpan(i * 2), mono[i]); else BinaryPrimitives.WriteInt16LittleEndian(pcm.AsSpan(i * 2), mono[i]); } return pcm; }
        if (codec == 9) return mono.Select(s => (byte)(s >> 8)).ToArray();
        if (codec is not (5 or 10)) throw new NotSupportedException("No verified native encoder.");
        int blockSize = codec == 5 ? 16 : 15; var result = new byte[((mono.Length + 27) / 28) * blockSize];
        for (int frame = 0; frame * 28 < mono.Length; frame++)
        {
            int start = frame * 28, count = Math.Min(28, mono.Length - start), bestShift = 0; long bestError = long.MaxValue;
            for (int shift = 0; shift <= 12; shift++)
            { long error = 0; int scale = 1 << (12 - shift); for (int i = 0; i < count; i++) { long delta = mono[start + i] - Math.Clamp((int)Math.Round(mono[start + i] / (double)scale), -8, 7) * scale; error += delta * delta; } if (error < bestError) { bestError = error; bestShift = shift; } }
            int at = frame * blockSize; result[at] = (byte)bestShift;
            if (codec == 5 && at + 1 < original.Length) result[at + 1] = original[at + 1];
            int step = 1 << (12 - bestShift);
            for (int i = 0; i < 28; i++) { int sample = i < count ? mono[start + i] : 0; int nibble = Math.Clamp((int)Math.Round(sample / (double)step), -8, 7) & 15; int offset = at + (codec == 5 ? 2 : 1) + i / 2; int shift = codec == 5 ? (i & 1) * 4 : (1 - (i & 1)) * 4; result[offset] |= (byte)(nibble << shift); }
        }
        return result;
    }
    public static byte[] Stream(byte[] source, int streamIndex, DecodedAudio audio)
    {
        var streams = SchlStream.ScanAll(new MemoryByteSource(source)); var stream = streams[streamIndex]; var header = stream.Header ?? throw new InvalidDataException("Missing PT header.");
        if (Unsupported(header) is { } why) throw new NotSupportedException(why);
        int total = stream.Blocks.Where(b => b.Id == "SCDl").Sum(b => checked((int)(stream.LittleEndianBlocks ? Bin.U32LE(source, (int)(stream.Offset + b.Offset + 8)) : Bin.U32BE(source, (int)(stream.Offset + b.Offset + 8)))));
        var normalized = Normalize(audio, header.SampleRate, header.Channels, total); var result = source.ToArray(); int sampleAt = 0;
        foreach (var block in stream.Blocks.Where(b => b.Id == "SCDl"))
        {
            int at = checked((int)(stream.Offset + block.Offset)); uint Read(int offset) => stream.LittleEndianBlocks ? Bin.U32LE(source, offset) : Bin.U32BE(source, offset);
            int samples = checked((int)Read(at + 8)); int payload = at + 12 + header.Channels * 4;
            var starts = Enumerable.Range(0, header.Channels).Select(c => checked(payload + (int)Read(at + 12 + c * 4))).ToArray();
            for (int c = 0; c < header.Channels; c++)
            {
                int start = starts[c] + (header.Codec2 == 10 && !header.UsePcmBlocks ? 4 : 0); int end = c + 1 < starts.Length ? starts[c + 1] : at + checked((int)block.Size);
                if (start < payload || end < start || end > source.Length) throw new InvalidDataException("Invalid native channel bounds.");
                var mono = Enumerable.Range(0, samples).Select(i => normalized.Pcm[(sampleAt + i) * header.Channels + c]).ToArray(); var encoded = Encode(mono, header.Codec2, source.AsSpan(start, end - start));
                if (encoded.Length > end - start) throw new InvalidOperationException("Encoded replacement exceeds the original block capacity.");
                encoded.CopyTo(result, start);
            }
            sampleAt += samples;
        }
        return result;
    }
    public static byte[] Bank(byte[] source, int soundIndex, DecodedAudio audio)
    {
        var bank = EaBank.Read(source); var sound = bank.Sounds.Single(s => s.Index == soundIndex); var h = sound.Header;
        if (Unsupported(h) is { } why) throw new NotSupportedException(why);
        var normalized = Normalize(audio, h.SampleRate, h.Channels, checked((int)h.Samples)); var result = source.ToArray();
        var tags = new byte[] { 0x88, 0x89, 0x94, 0x95 }; int extra = h.Platform == 5 && ((h.Get(0x8C) ?? 0) & 0x100) != 0 ? 16 : 0;
        var boundaries = bank.Sounds.SelectMany(s => tags.Select(t => (int)(s.Header.Get(t) ?? 0))).Concat(bank.Sounds.Select(s => s.HeaderOffset)).Append(source.Length).Where(i => i > 0).Distinct().Order().ToArray();
        for (int c = 0; c < h.Channels; c++)
        {
            int raw = checked((int)(h.Get(tags[c]) ?? throw new InvalidDataException("Missing channel pointer."))); int at = raw + extra;
            if (bank.Sounds.Sum(s => tags.Take(s.Header.Channels).Count(t => s.Header.Get(t) == (uint)raw)) != 1)
                throw new InvalidOperationException("This payload is shared by bank channels or sounds; isolated replacement is unavailable.");
            int end = boundaries.FirstOrDefault(i => i > raw, source.Length);
            if (at < 0 || end < at || end > source.Length) throw new InvalidDataException("Invalid bank channel allocation.");
            var mono = Enumerable.Range(0, checked((int)h.Samples)).Select(i => normalized.Pcm[i * h.Channels + c]).ToArray(); var encoded = Encode(mono, h.Codec2, source.AsSpan(at, end - at));
            if (encoded.Length > end - at) throw new InvalidOperationException("Encoded sound exceeds its original bank allocation."); encoded.CopyTo(result, at);
        }
        return result;
    }
    public static byte[] Sony(byte[] source, SonySound sound, DecodedAudio audio)
    {
        var normalized = Normalize(audio, sound.SampleRate, 1, sound.Length / 16 * 28);
        var encoded = Encode(normalized.Pcm, 5, source.AsSpan(sound.Offset, sound.Length));
        var result = source.ToArray(); encoded.CopyTo(result, sound.Offset); return result;
    }
}
