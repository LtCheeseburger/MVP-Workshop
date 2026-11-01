using MVPWorkshop.Core.IO;

namespace MVPWorkshop.Core.Formats;

public sealed record SonySound(int Index, int Offset, int Length, int SampleRate);
public static class SonySoundBank
{
    public static IReadOnlyList<SonySound> Read(byte[] header, int bodyLength)
    {
        var source = new MemoryByteSource(header);
        var chunk = FormatInspector.WalkChunks(source, reversed: true, sizeAt: 8).SingleOrDefault(c => c.Id.EndsWith("/Vagi", StringComparison.Ordinal)) ?? throw new InvalidDataException("Sony HD Vagi table is missing.");
        int at = (int)chunk.Offset; int count = checked((int)Bin.U32LE(header, at + 12) + 1);
        if (count is < 1 or > 10000 || at + 16L + count * 4 > at + chunk.Size) throw new InvalidDataException("Invalid Vagi table size.");
        var entries = new List<(int Offset, int Rate)>();
        for (int i = 0; i < count; i++)
        {
            int record = checked(at + (int)Bin.U32LE(header, at + 16 + i * 4));
            if (record < at + 16 + count * 4 || record > at + chunk.Size - 8) throw new InvalidDataException("Vagi record outside its chunk.");
            int offset = checked((int)Bin.U32LE(header, record)), rate = Bin.U16LE(header, record + 4);
            if (offset < 0 || offset >= bodyLength || offset % 16 != 0 || rate is < 1000 or > 192000) throw new InvalidDataException("Invalid Sony sample offset or rate."); entries.Add((offset, rate));
        }
        return entries.Select((entry, index) => { int end = entries.Where(e => e.Offset > entry.Offset).Select(e => e.Offset).DefaultIfEmpty(bodyLength).Min(); if ((end - entry.Offset) % 16 != 0) throw new InvalidDataException("Sony sample allocation is not VAG-frame aligned."); return new SonySound(index, entry.Offset, end - entry.Offset, entry.Rate); }).ToArray();
    }
    public static DecodedAudio Decode(byte[] body, SonySound sound)
    {
        var samples = new List<short>(); var state = new AdpcmState(); AudioCodecs.DecodeVag(body.AsSpan(sound.Offset, sound.Length), sound.Length / 16 * 28, ref state, samples);
        return new(samples.ToArray(), 1, sound.SampleRate, "Sony VAG · rate/offset from paired HD Vagi record");
    }
}
