using System.Buffers.Binary;
using MVPWorkshop.Core.IO;

namespace MVPWorkshop.Core.Formats;

/// <summary>QL01 quick-load cache: FILS names, DTLS records, then DATA-relative cached archive headers and stored entries.</summary>
public sealed class QklFile
{
    public sealed record Record(int Index, ushort Kind, string FileName, uint EntryIndex, long Offset, long PointerOffset);
    public long DataOffset { get; private init; }
    public List<Record> Records { get; } = [];
    public IReadOnlyList<string> Files { get; private init; } = [];

    public static QklFile Read(IByteSource source)
    {
        var header = source.ReadBytes(0, 12);
        if (header.Length != 12 || Bin.FourCC(header) != "QL01" || Bin.U32LE(header, 4) != 12) throw new InvalidDataException("Invalid QKL header.");
        long dataOffset = Bin.U32LE(header, 8), pos = 12, namesAt = -1, detailsAt = -1;
        uint namesSize = 0, detailsSize = 0;
        while (pos < dataOffset)
        {
            var block = source.ReadBytes(pos, 12);
            if (block.Length != 12) throw new InvalidDataException("Truncated QKL block.");
            uint size = Bin.U32LE(block, 4);
            if (size < 12 || size > dataOffset - pos) throw new InvalidDataException("Invalid QKL block size.");
            switch (Bin.FourCC(block)) { case "FILS": namesAt = pos; namesSize = size; break; case "DTLS": detailsAt = pos; detailsSize = size; break; }
            pos += size;
        }
        if (pos != dataOffset || namesAt < 0 || detailsAt < 0 || Bin.FourCC(source.ReadBytes(dataOffset, 4)) != "DATA") throw new InvalidDataException("Missing QKL tables/DATA.");
        uint fileCount = source.U32LE(namesAt + 8), recordCount = source.U32LE(detailsAt + 8);
        if (fileCount > 4096 || recordCount > 1000000 || 12L + fileCount * 48L > namesSize || 12L + recordCount * 12L > detailsSize) throw new InvalidDataException("QKL table exceeds block bounds.");
        var files = Enumerable.Range(0, (int)fileCount).Select(i => Bin.CString(source.ReadBytes(namesAt + 12 + i * 48L, 48), 0, 48)).ToArray();
        var result = new QklFile { DataOffset = dataOffset, Files = files };
        for (int i = 0; i < recordCount; i++)
        {
            long at = detailsAt + 12 + i * 12L; var bytes = source.ReadBytes(at, 12);
            int file = Bin.U16LE(bytes, 2); long offset = dataOffset + Bin.U32LE(bytes, 8);
            if (file >= files.Length || offset < dataOffset + 8 || offset > source.Length) throw new InvalidDataException("Invalid QKL cache reference.");
            result.Records.Add(new(i, Bin.U16LE(bytes, 0), files[file], Bin.U32LE(bytes, 4), offset, at + 8));
        }
        return result;
    }

    public static byte[] Synchronize(byte[] original, IReadOnlyDictionary<string, (byte[] Original, byte[] Rebuilt)> changedArchives, out int updated)
    {
        var cache = Read(new MemoryByteSource(original)); updated = 0;
        using var output = new MemoryStream(); output.Write(original);
        var archives = new Dictionary<string, (TerfArchive.Directory Before, TerfArchive.Directory After)>(StringComparer.OrdinalIgnoreCase);
        Span<byte> pointer = stackalloc byte[4];
        foreach (var (name, pair) in changedArchives) archives[name] = (TerfArchive.ReadDirectory(new MemoryByteSource(pair.Original)), TerfArchive.ReadDirectory(new MemoryByteSource(pair.Rebuilt)));
        foreach (var record in cache.Records)
        {
            if (!changedArchives.TryGetValue(record.FileName, out var bytes)) continue;
            var directory = archives[record.FileName];
            byte[] before, after;
            if (record.Kind == 0)
            {
                // The header cache contains the complete prefix before the first stored entry.
                int prefix = checked((int)(directory.Before.Entries.Count == 0 ? directory.Before.Data.Offset + 8 : directory.Before.Entries.Min(e => e.Offset)));
                if (record.EntryIndex != 0 || prefix > bytes.Rebuilt.Length) throw new NotSupportedException("Unsupported QKL archive-header reference.");
                before = bytes.Original.AsSpan(0, prefix).ToArray(); after = bytes.Rebuilt.AsSpan(0, prefix).ToArray();
            }
            else if (record.Kind == 1)
            {
                if (record.EntryIndex >= directory.Before.Entries.Count || record.EntryIndex >= directory.After.Entries.Count) throw new InvalidDataException("QKL references an invalid TERF entry.");
                var a = directory.Before.Entries[(int)record.EntryIndex]; var b = directory.After.Entries[(int)record.EntryIndex];
                before = bytes.Original.AsSpan(checked((int)a.Offset), checked((int)a.Size)).ToArray();
                after = bytes.Rebuilt.AsSpan(checked((int)b.Offset), checked((int)b.Size)).ToArray();
            }
            else throw new NotSupportedException($"Unsupported QKL record type {record.Kind}.");
            if (before.AsSpan().SequenceEqual(after)) continue;
            if (record.Offset + before.Length > original.Length || !original.AsSpan((int)record.Offset, before.Length).SequenceEqual(before))
                throw new InvalidDataException($"QKL cached data differs from {record.FileName}, record {record.Index}. Export stopped to retain the cache.");
            long offset = record.Offset;
            if (after.Length > before.Length) { offset = (output.Length + 2047) & ~2047L; output.SetLength(checked(offset + after.Length)); }
            output.Position = offset; output.Write(after);
            output.Position = record.PointerOffset; BinaryPrimitives.WriteUInt32LittleEndian(pointer, checked((uint)(offset - cache.DataOffset))); output.Write(pointer);
            updated++;
        }
        var result = output.ToArray();
        Read(new MemoryByteSource(result));
        return result;
    }
}
