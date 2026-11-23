using System.Buffers.Binary;
using MVPWorkshop.Core.IO;

namespace MVPWorkshop.Core.Formats;

/// <summary>Retains untouched stored entries and metadata. Modified entries use TERF's uncompressed mode.</summary>
public static class TerfWriter
{
    public static byte[] Write(byte[] original, IReadOnlyDictionary<int, byte[]> replacements)
    {
        var directory = TerfArchive.ReadDirectory(new MemoryByteSource(original));
        if (replacements.Count == 0) return original.ToArray();
        if (replacements.Keys.Any(i => i < 0 || i >= directory.Entries.Count)) throw new InvalidDataException("Invalid TERF replacement index.");
        using var output = new MemoryStream();
        output.Write(original);
        long dataEnd = directory.Data.Offset + directory.Data.Size;
        var tail = original.AsSpan(checked((int)dataEnd)).ToArray();
        output.SetLength(dataEnd);
        long Align(long n) => checked((n + directory.Alignment - 1) & ~(directory.Alignment - 1L));
        void Word(long offset, uint value)
        {
            Span<byte> bytes = stackalloc byte[4]; BinaryPrimitives.WriteUInt32LittleEndian(bytes, value);
            output.Position = offset; output.Write(bytes);
        }
        foreach (var entry in directory.Entries)
        {
            if (!replacements.TryGetValue(entry.Index, out var data)) continue;
            long offset = entry.Offset;
            // Appending larger entries leaves every untouched entry, gap and index in place.
            if (data.Length > entry.Size)
            {
                offset = directory.Data.Offset + Align(dataEnd - directory.Data.Offset);
                dataEnd = checked(offset + data.Length);
                output.SetLength(dataEnd);
            }
            output.Position = offset; output.Write(data);
            Word(entry.DirectoryOffset, checked((uint)(offset - directory.Data.Offset)));
            Word(entry.DirectoryOffset + 4, checked((uint)data.Length));
            if (entry.CompressionOffset is { } cp) { Word(cp, 0); Word(cp + 4, checked((uint)data.Length)); }
        }
        dataEnd = directory.Data.Offset + Align(dataEnd - directory.Data.Offset);
        output.SetLength(dataEnd);
        Word(directory.Data.Offset + 4, checked((uint)(dataEnd - directory.Data.Offset)));
        output.Position = dataEnd; output.Write(tail);
        var result = output.ToArray();
        TerfArchive.ReadDirectory(new MemoryByteSource(result));
        return result;
    }
}
