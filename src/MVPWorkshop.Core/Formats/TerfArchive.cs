using MVPWorkshop.Core.IO;

namespace MVPWorkshop.Core.Formats;

/// <summary>Tiburon TERF blocks and indexed assets; uncompressed and EA Madden compression are supported.</summary>
public static class TerfArchive
{
    public sealed record Block(string Tag, long Offset, uint Size);
    public sealed record Entry(int Index, long Offset, uint Size, uint Compression, uint UnpackedSize, long DirectoryOffset, long? CompressionOffset);
    public sealed record Directory(int Alignment, Block Data, IReadOnlyList<Entry> Entries);

    public static Directory ReadDirectory(IByteSource source)
    {
        var blocks = Read(source);
        var header = source.ReadBytes(0, 16);
        if (header.Length < 16) throw new InvalidDataException("Truncated TERF header.");
        int count = Bin.U16LE(header, 14), alignment = Bin.U16LE(header, 12);
        if (alignment < 4 || (alignment & (alignment - 1)) != 0) throw new InvalidDataException("Invalid TERF alignment.");
        var directory = blocks.SingleOrDefault(b => b.Tag == "DIR1") ?? throw new InvalidDataException("Missing TERF directory.");
        var data = blocks.SingleOrDefault(b => b.Tag == "DATA") ?? throw new InvalidDataException("Missing TERF data block.");
        var comp = blocks.SingleOrDefault(b => b.Tag == "COMP");
        if (8L + count * 8L > directory.Size || comp is not null && 8L + count * 8L > comp.Size)
            throw new InvalidDataException("TERF entry table exceeds its block.");
        var entries = new List<Entry>(count);
        for (int i = 0; i < count; i++)
        {
            long pos = directory.Offset + 8 + i * 8L;
            uint offset = source.U32LE(pos), size = source.U32LE(pos + 4);
            long? cp = comp is null ? null : comp.Offset + 8 + i * 8L;
            uint algorithm = cp is { } c ? source.U32LE(c) : 0;
            uint unpacked = cp is { } u ? source.U32LE(u + 4) : size;
            if (offset < 8 || offset > data.Size || size > data.Size - offset)
                throw new InvalidDataException($"TERF entry {i} exceeds DATA bounds.");
            entries.Add(new(i, data.Offset + offset, size, algorithm, unpacked, pos, cp));
        }
        return new(alignment, data, entries);
    }

    public static byte[] Decode(IByteSource source, Entry entry)
    {
        var stored = source.ReadBytes(entry.Offset, checked((int)entry.Size));
        return entry.Compression switch
        {
            0 => stored,
            5 => EaMaddenDecoder.Decode(stored, checked((int)entry.UnpackedSize)),
            _ => throw new NotSupportedException($"TERF compression {entry.Compression} is not supported."),
        };
    }

    public static IReadOnlyList<Block> Read(IByteSource source)
    {
        var blocks = new List<Block>();
        long offset = 0;
        while (offset < source.Length)
        {
            var header = source.ReadBytes(offset, 8);
            if (header.Length != 8) throw new InvalidDataException("Truncated TERF block header.");
            var tag = Bin.FourCC(header);
            if (offset == 0 && tag != "TERF") throw new InvalidDataException("Missing TERF signature.");
            if (header.Take(4).Any(b => b < 32 || b > 126)) throw new InvalidDataException("Invalid TERF block tag.");
            var size = Bin.U32LE(header, 4);
            if (size < 8 || size > source.Length - offset) throw new InvalidDataException("TERF block exceeds source bounds.");
            blocks.Add(new(tag, offset, size));
            offset += size;
        }
        if (blocks.Count == 0) throw new InvalidDataException("Empty TERF archive.");
        return blocks;
    }
}
