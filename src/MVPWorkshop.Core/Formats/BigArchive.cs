using MVPWorkshop.Core.IO;

namespace MVPWorkshop.Core.Formats;

/// <summary><see cref="DirPos"/> is where this entry's offset/size fields sit in the directory, so a rebuild can patch them in place.</summary>
public sealed record BigEntry(int Index, string Name, uint Offset, uint Size, int DirPos = 0);

/// <summary>
/// EA BIGF archive. Header: "BIGF", u32 LE archive size, u32 BE entry count, u32 BE header size.
/// Directory entries: u32 BE offset, u32 BE size, NUL-terminated name (may contain '\' sub-paths).
/// </summary>
public sealed class BigArchive
{
    public string Magic { get; private init; } = "";
    public uint DeclaredSize { get; private init; }
    public uint HeaderSize { get; private init; }
    public List<BigEntry> Entries { get; } = [];

    public static BigArchive Read(IByteSource src)
    {
        var hdr = src.ReadBytes(0, 16);
        if (hdr.Length < 16) throw new InvalidDataException("Truncated BIG header.");
        var count = Bin.U32BE(hdr, 8);
        var headerSize = Bin.U32BE(hdr, 12);
        if (count > 200000) throw new InvalidDataException($"Implausible BIG entry count {count}.");
        var dirLen = (int)Math.Min(src.Length, Math.Max(headerSize, 16 + count * 64));
        var dir = src.ReadBytes(0, dirLen);

        var big = new BigArchive { Magic = Bin.FourCC(hdr), DeclaredSize = Bin.U32LE(hdr, 4), HeaderSize = headerSize };
        var p = 16;
        for (var i = 0; i < count; i++)
        {
            if (p + 8 > dir.Length) throw new InvalidDataException("BIG directory runs past header.");
            var dirPos = p; var off = Bin.U32BE(dir, p); var size = Bin.U32BE(dir, p + 4); p += 8;
            var name = Bin.CString(dir, p, 1024); p += name.Length + 1;
            if (off + (long)size > src.Length) throw new InvalidDataException($"Entry '{name}' exceeds archive bounds.");
            big.Entries.Add(new BigEntry(i, name, off, size, dirPos));
        }
        return big;
    }
}
