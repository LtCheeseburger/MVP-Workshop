using MVPWorkshop.Core.IO;

namespace MVPWorkshop.Core.Formats;

/// <summary>EA "PT" variable patch header, shared by SCHl streams and BNKl banks.</summary>
public sealed class EaPatchHeader
{
    public int Platform { get; set; } = -1;
    public List<(byte Tag, uint Value, int Offset)> Tags { get; } = [];
    public int EndOffset { get; set; }

    public uint? Get(byte tag) { foreach (var t in Tags) if (t.Tag == tag) return t.Value; return null; }

    public int Version => (int)(Get(0x80) ?? (uint)(Platform == 0x05 ? 1 : 0));
    public int Channels => (int)Math.Max(1, Get(0x82) ?? 1);
    public int SampleRate => (int)(Get(0x84) ?? (uint)(Platform == 0x05 ? 22050 : 22050));
    public long Samples => Get(0x85) ?? 0;
    public int Codec2 => (int)(Get(0xA0) ?? (uint)(Platform switch { 0x05 => 0x05, 0x01 => 0x05, _ => 0x0A }));
    public bool UsePcmBlocks => Version == 3;

    public static string PlatformName(int p) => p switch
    {
        0x00 => "PC", 0x01 => "PSX", 0x02 => "N64", 0x03 => "Mac", 0x04 => "Saturn", 0x05 => "PS2", 0x06 => "GameCube",
        0x07 => "Xbox", 0x08 => "Generic", 0x09 => "Xbox 360", 0x0A => "PSP", 0x0E => "PS3", 0x0F => "Wii", _ => $"0x{p:X2}"
    };

    public static string CodecName(int c) => c switch
    {
        0x00 => "PCM16LE (interleaved)", 0x01 => "PCM16BE (interleaved)", 0x02 => "PCM8 (interleaved)", 0x03 => "EA-XA (interleaved)",
        0x04 => "MicroTalk 10:1", 0x05 => "PS-ADPCM (VAG)", 0x06 => "N64 VADPCM", 0x07 => "PCM16BE", 0x08 => "PCM16LE", 0x09 => "PCM8",
        0x0A => "EA-XA", 0x0D => "DVI IMA", 0x0F => "MPEG Layer II", 0x10 => "MPEG Layer III", 0x12 => "GC DSP", 0x14 => "Xbox IMA",
        0x16 => "MicroTalk 5:1", 0x17 => "EALayer3", _ => $"0x{c:X2}"
    };

    public static string TagName(byte t) => t switch
    {
        0x06 => "Priority", 0x0B => "Bank channels", 0x13 => "Effect bus", 0x80 => "Version", 0x81 => "Bits per sample", 0x82 => "Channels",
        0x83 => "Codec1", 0x84 => "Sample rate", 0x85 => "Sample count", 0x86 => "Loop start", 0x87 => "Loop end",
        0x88 => "Data offset ch1", 0x89 => "Data offset ch2", 0x8C => "Flags", 0xA0 => "Codec2", 0x1A => "Loop offset ch1", 0x26 => "Loop offset ch2",
        _ => $"Tag 0x{t:X2}"
    };

    /// <summary>Parse at <paramref name="o"/> which must point at "PT\0\0" (or GSTR).</summary>
    public static EaPatchHeader? Parse(ReadOnlySpan<byte> d, int o, int maxLen = 0x2000)
    {
        if (o + 4 > d.Length) return null;
        var h = new EaPatchHeader();
        if (d[o] == 'P' && d[o + 1] == 'T') { h.Platform = Bin.U16LE(d, o + 2); o += 4; }
        else if (Bin.FourCC(d, o) == "GSTR") { h.Platform = 0x08; o += 8; }
        else return null;

        var start = o;
        while (o < d.Length && o - start < maxLen)
        {
            var tagOff = o;
            var tag = d[o++];
            if (tag == 0xFF || tag == 0xFE) break;
            if (tag is 0xFC or 0xFD) continue;
            if (o >= d.Length) break;
            var count = d[o++];
            uint value = 0;
            if (count == 0xFF) { o += 4 + (int)Bin.U32BE(d, o); }
            else if (count > 4) { o += count; }
            else { for (var i = 0; i < count && o < d.Length; i++) value = value << 8 | d[o++]; }
            h.Tags.Add((tag, value, tagOff));
        }
        h.EndOffset = o;
        return h;
    }
}

public sealed record SchlBlock(string Id, long Offset, uint Size);

public sealed record DecodedAudio(short[] Pcm, int Channels, int SampleRate, string Notes)
{
    public double Duration => Channels == 0 || SampleRate == 0 ? 0 : Pcm.Length / (double)Channels / SampleRate;
}

/// <summary>One EA blocked stream: SCHl header, optional SCCl/SCLl, SCDl data blocks, SCEl end.</summary>
public sealed class SchlStream
{
    public long Offset { get; init; }
    public long Length { get; set; }
    public List<SchlBlock> Blocks { get; } = [];
    public EaPatchHeader? Header { get; set; }
    public bool LittleEndianBlocks { get; set; } = true;
    public int DataBlockCount => Blocks.Count(b => b.Id == "SCDl");

    /// <summary>Scan streams sequentially (blocks are self-sized). Works for single .ASF files and PROJx DAT packs.</summary>
    public static List<SchlStream> ScanAll(IByteSource src, int max = 100000)
    {
        var list = new List<SchlStream>();
        long o = 0;
        while (o + 8 <= src.Length && list.Count < max)
        {
            var id = Bin.FourCC(src.ReadBytes(o, 4));
            if (id != "SCHl")
            {
                // skip padding / junk to the next 4-aligned SCHl
                var next = FindNext(src, o + 4);
                if (next < 0) break;
                o = next;
                continue;
            }
            var s = ReadOne(src, o);
            list.Add(s);
            o = s.Offset + Math.Max(s.Length, 8);
        }
        return list;
    }

    private static long FindNext(IByteSource src, long from)
    {
        from = (from + 3) & ~3L;
        var buf = new byte[0x10000];
        while (from < src.Length)
        {
            var n = src.Read(from, buf);
            if (n < 4) return -1;
            for (var i = 0; i + 4 <= n; i += 4)
                if (buf[i] == 'S' && buf[i + 1] == 'C' && buf[i + 2] == 'H' && buf[i + 3] == 'l') return from + i;
            from += n - (n % 4 == 0 ? 0 : n % 4);
            if (n < buf.Length) return -1;
        }
        return -1;
    }

    public static SchlStream ReadOne(IByteSource src, long start)
    {
        var s = new SchlStream { Offset = start };
        var le = src.U32LE(start + 4);
        s.LittleEndianBlocks = le is >= 8 and < 0x1000000;
        long o = start;
        while (o + 8 <= src.Length)
        {
            var id = Bin.FourCC(src.ReadBytes(o, 4));
            var size = s.LittleEndianBlocks ? src.U32LE(o + 4) : src.U32BE(o + 4);
            if (size < 8 || o + size > src.Length + 8) break;
            if (id == "SCHl" && s.Blocks.Count > 0) break; // next stream without an SCEl terminator
            s.Blocks.Add(new SchlBlock(id, o - start, size));
            if (id == "SCHl" && s.Header is null)
            {
                var hb = src.ReadBytes(o, (int)Math.Min(size, 0x4000));
                s.Header = EaPatchHeader.Parse(hb, 8);
            }
            o += size;
            if (id == "SCEl") break;
        }
        s.Length = o - start;
        return s;
    }

    public DecodedAudio Decode(IByteSource src)
    {
        var h = Header ?? throw new InvalidDataException("Stream has no PT header.");
        var ch = h.Channels; var codec = h.Codec2;
        var outCh = Enumerable.Range(0, ch).Select(_ => new List<short>()).ToArray();
        var states = new AdpcmState[ch];
        var microTalk = codec is 0x04 or 0x16 ? Enumerable.Range(0, ch).Select(_ => new MicroTalkDecoder(h.UsePcmBlocks)).ToArray() : null;
        foreach (var b in Blocks)
        {
            if (b.Id != "SCDl") continue;
            var blk = src.ReadBytes(Offset + b.Offset, (int)b.Size);
            var samples = (int)(LittleEndianBlocks ? Bin.U32LE(blk, 8) : Bin.U32BE(blk, 8));
            for (var c = 0; c < ch; c++)
            {
                var rel = (int)(LittleEndianBlocks ? Bin.U32LE(blk, 0x0C + 4 * c) : Bin.U32BE(blk, 0x0C + 4 * c));
                var start = 0x0C + 4 * ch + rel;
                if (microTalk is not null)
                {
                    // channel data starts one byte in (flag byte); VBR frames are read bit by bit until the
                    // block's sample count is reached; leftovers of the last frame are dropped (vgmstream ea_mt)
                    var next = c + 1 < ch ? 0x0C + 4 * ch + (int)(LittleEndianBlocks ? Bin.U32LE(blk, 0x0C + 4 * (c + 1)) : Bin.U32BE(blk, 0x0C + 4 * (c + 1))) : blk.Length;
                    var mt = microTalk[c];
                    mt.SetBuffer(blk, start + 1, Math.Max(start + 1, next));
                    for (var done = 0; done < samples;)
                    {
                        mt.DecodeFrame();
                        var take = Math.Min(MicroTalkDecoder.FrameSamples, samples - done);
                        var frame = mt.Output;
                        for (var i = 0; i < take; i++) outCh[c].Add(MicroTalkDecoder.ToPcm(frame[i]));
                        done += take;
                    }
                    continue;
                }
                if (codec == 0x0A && !h.UsePcmBlocks) start += 4;
                if (start < 0 || start >= blk.Length) continue;
                AudioCodecs.DecodeMono(codec, h.UsePcmBlocks, blk.AsSpan(start), samples, ref states[c], outCh[c]);
            }
        }
        return new DecodedAudio(AudioCodecs.Interleave(outCh), ch, h.SampleRate, $"{EaPatchHeader.CodecName(codec)}, {DataBlockCount} data blocks");
    }
}

public sealed record BankSound(int Index, int HeaderOffset, EaPatchHeader Header);

/// <summary>EA BNKl sound bank: version u8 @4, sound count u16 @6, table of relative PT header offsets @0x14 (v4/v5).</summary>
public sealed class EaBank
{
    public int Version { get; private init; }
    public int DeclaredCount { get; private init; }
    public List<BankSound> Sounds { get; } = [];

    public static EaBank Read(byte[] d)
    {
        var bigEndian = d[3] == 'b';
        int U16(int o) => bigEndian ? d[o] << 8 | d[o + 1] : d[o] | d[o + 1] << 8;
        uint U32(int o) => bigEndian ? Bin.U32BE(d, o) : Bin.U32LE(d, o);

        var bank = new EaBank { Version = d[4], DeclaredCount = U16(6) };
        var table = bank.Version == 2 ? 0x0C : 0x14;
        for (var i = 0; i < bank.DeclaredCount; i++)
        {
            var e = table + 4 * i;
            if (e + 4 > d.Length) break;
            var rel = U32(e);
            if (rel == 0) continue;
            var ho = (int)(e + rel);
            var h = EaPatchHeader.Parse(d, ho);
            if (h is not null) bank.Sounds.Add(new BankSound(i, ho, h));
        }
        return bank;
    }

    public static DecodedAudio Decode(byte[] d, BankSound s)
    {
        var h = s.Header;
        var ch = h.Channels;
        var samples = (int)h.Samples;
        var codec = h.Codec2;
        var outCh = Enumerable.Range(0, ch).Select(_ => new List<short>()).ToArray();
        var extra = h.Platform == 0x05 && ((h.Get(0x8C) ?? 0) & 0x100) != 0 ? 0x10 : 0;
        uint?[] offs = [h.Get(0x88), h.Get(0x89), h.Get(0x94), h.Get(0x95)];
        for (var c = 0; c < ch; c++)
        {
            var off = (int)(offs[Math.Min(c, 3)] ?? offs[0] ?? 0) + extra;
            if (off <= 0 || off >= d.Length) continue;
            var state = new AdpcmState();
            AudioCodecs.DecodeMono(codec, h.UsePcmBlocks, d.AsSpan(off), samples, ref state, outCh[c]);
        }
        return new DecodedAudio(AudioCodecs.Interleave(outCh), ch, h.SampleRate, EaPatchHeader.CodecName(codec));
    }
}

public struct AdpcmState { public int Hist1, Hist2; }

public static class AudioCodecs
{
    private static readonly int[,] VagCoefs = { { 0, 0 }, { 60, 0 }, { 115, -52 }, { 98, -55 }, { 122, -60 } };
    private static readonly int[] EaXaTable = [0, 240, 460, 392, 0, 0, -208, -220, 0, 1, 3, 4, 7, 8, 10, 11, 0, -1, -3, -4];

    public static bool IsSupported(int codec2) => codec2 is 0x04 or 0x05 or 0x0A or 0x07 or 0x08 or 0x09 or 0x16;

    public static void DecodeMono(int codec2, bool pcmBlocks, ReadOnlySpan<byte> d, int samples, ref AdpcmState st, List<short> output)
    {
        switch (codec2)
        {
            case 0x05: DecodeVag(d, samples, ref st, output); break;
            case 0x0A: DecodeEaXa(d, samples, pcmBlocks, ref st, output); break;
            case 0x07: for (var i = 0; i < samples && i * 2 + 1 < d.Length; i++) output.Add(Bin.S16BE(d, i * 2)); break;
            case 0x08: for (var i = 0; i < samples && i * 2 + 1 < d.Length; i++) output.Add(Bin.S16LE(d, i * 2)); break;
            case 0x09: for (var i = 0; i < samples && i < d.Length; i++) output.Add((short)((sbyte)d[i] << 8)); break;
            default: throw new NotSupportedException($"Codec {EaPatchHeader.CodecName(codec2)} is not decoded yet.");
        }
    }

    public static void DecodeVag(ReadOnlySpan<byte> d, int samples, ref AdpcmState st, List<short> output)
    {
        var done = 0;
        for (var f = 0; done < samples && f + 16 <= d.Length; f += 16)
        {
            var pred = Math.Min(4, d[f] >> 4); var shift = d[f] & 0x0F;
            for (var i = 0; i < 28 && done < samples; i++, done++)
            {
                var b = d[f + 2 + i / 2];
                var nib = (i & 1) == 0 ? b & 0x0F : b >> 4;
                var s = (short)(nib << 12) >> shift;
                s += (st.Hist1 * VagCoefs[pred, 0] + st.Hist2 * VagCoefs[pred, 1] + 32) >> 6;
                s = Math.Clamp(s, short.MinValue, short.MaxValue);
                st.Hist2 = st.Hist1; st.Hist1 = s;
                output.Add((short)s);
            }
        }
    }

    public static void DecodeEaXa(ReadOnlySpan<byte> d, int samples, bool v2, ref AdpcmState st, List<short> output)
    {
        var done = 0; var p = 0;
        while (done < samples && p < d.Length)
        {
            var info = d[p];
            if (v2 && info == 0xEE)
            {
                if (p + 0x3D > d.Length) break;
                st.Hist1 = Bin.S16BE(d, p + 1); st.Hist2 = Bin.S16BE(d, p + 3);
                for (var i = 0; i < 28 && done < samples; i++, done++) output.Add(Bin.S16BE(d, p + 5 + i * 2));
                p += 0x3D;
                continue;
            }
            if (p + 0x0F > d.Length) break;
            var c1 = EaXaTable[(info >> 4) + 0]; var c2 = EaXaTable[(info >> 4) + 4];
            var shift = (info & 0x0F) + 8;
            for (var i = 0; i < 28 && done < samples; i++, done++)
            {
                var b = d[p + 1 + i / 2];
                var nib = (i & 1) == 0 ? b >> 4 : b & 0x0F;
                var s = (nib << 28) >> shift;
                s = (s + c1 * st.Hist1 + c2 * st.Hist2 + (v2 ? 0 : 128)) >> 8;
                s = Math.Clamp(s, short.MinValue, short.MaxValue);
                st.Hist2 = st.Hist1; st.Hist1 = s;
                output.Add((short)s);
            }
            p += 0x0F;
        }
    }

    public static short[] Interleave(List<short>[] channels)
    {
        var len = channels.Length == 0 ? 0 : channels.Min(c => c.Count);
        var r = new short[len * channels.Length];
        for (var i = 0; i < len; i++)
            for (var c = 0; c < channels.Length; c++) r[i * channels.Length + c] = channels[c][i];
        return r;
    }

    public static byte[] ToWav(DecodedAudio a)
    {
        var dataLen = a.Pcm.Length * 2;
        using var ms = new MemoryStream(44 + dataLen);
        using var w = new BinaryWriter(ms);
        w.Write("RIFF"u8); w.Write(36 + dataLen); w.Write("WAVE"u8);
        w.Write("fmt "u8); w.Write(16); w.Write((short)1); w.Write((short)a.Channels); w.Write(a.SampleRate);
        w.Write(a.SampleRate * a.Channels * 2); w.Write((short)(a.Channels * 2)); w.Write((short)16);
        w.Write("data"u8); w.Write(dataLen);
        var bytes = new byte[dataLen];
        Buffer.BlockCopy(a.Pcm, 0, bytes, 0, dataLen);
        w.Write(bytes);
        return ms.ToArray();
    }
}
