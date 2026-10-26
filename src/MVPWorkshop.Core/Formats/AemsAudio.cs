using System.Text;
using MVPWorkshop.Core.IO;

namespace MVPWorkshop.Core.Formats;

public sealed record LocString(int Id, int Index, string Text);

/// <summary>
/// LOCH binary localisation table (<c>FEENG.LOC</c>, <c>IGENG.LOC</c>, <c>MC_ENG.LOC</c>):
/// <c>LOCH</c> {u32 size, u32 version, u32 languages, u32 →LOCL}, <c>LOCI</c> {u32 size, u32 count, u32 0, count × (u16 id, u16 index)},
/// <c>LOCL</c> {u32 size, u32 language, u32 count, u32 offset[count] (from LOCL start)} → UTF-16LE zero-terminated strings.
/// </summary>
public sealed class LocTable
{
    public int Version { get; private init; }
    public int Languages { get; private init; }
    public int Language { get; private init; }
    public List<LocString> Strings { get; } = [];

    public static LocTable Read(byte[] d)
    {
        if (Bin.FourCC(d) != "LOCH") throw new InvalidDataException("Not a LOCH table.");
        int loci = -1, locl = -1;
        for (var o = 0; o + 8 <= d.Length;)
        {
            var size = (int)Bin.U32LE(d, o + 4);
            switch (Bin.FourCC(d, o)) { case "LOCI": loci = o; break; case "LOCL": locl = o; break; }
            if (size < 8) break;
            o += size;
        }
        if (locl < 0) throw new InvalidDataException("LOCL chunk missing.");
        var t = new LocTable { Version = (int)Bin.U32LE(d, 8), Languages = (int)Bin.U32LE(d, 12), Language = (int)Bin.U32LE(d, locl + 8) };
        var count = (int)Bin.U32LE(d, locl + 12);
        var ids = new Dictionary<int, int>();
        if (loci >= 0)
            for (var i = 0; i < (int)Bin.U32LE(d, loci + 8); i++)
            {
                var o = loci + 16 + i * 4;
                if (o + 4 > d.Length) break;
                ids.TryAdd(Bin.U16LE(d, o + 2), Bin.U16LE(d, o));
            }
        for (var i = 0; i < count; i++)
        {
            var po = locl + 16 + i * 4;
            if (po + 4 > d.Length) break;
            var so = locl + (int)Bin.U32LE(d, po);
            var end = so;
            while (end + 1 < d.Length && (d[end] | d[end + 1]) != 0) end += 2;
            var text = so < d.Length ? Encoding.Unicode.GetString(d, so, end - so) : "";
            t.Strings.Add(new LocString(ids.TryGetValue(i, out var id) ? id : -1, i, text));
        }
        return t;
    }
}

/// <summary>
/// AEMS ABKC module bank. The header points to an embedded BNKl sound bank (when the module has one-shot samples)
/// and to compiled MIPS behaviour code; modules without a bank play streams from the matching <c>.AST</c>.
/// </summary>
public sealed class AbkBank
{
    public int Modules { get; private init; }
    public uint DeclaredSize { get; private init; }
    public int CodeOffset { get; private init; }
    public int BankOffset { get; private init; }
    public int BankSize { get; private init; }
    public int[] Tables { get; private init; } = [];

    // 0x0A u16 module count, 0x14 u32 size, 0x18 u32 end of code, 0x1C u32 →module table (0x5C),
    // 0x20 u32 →BNKl, 0x24 u32 BNKl size, 0x30/0x34/0x38 u32 → trailing tables
    public static AbkBank Read(ReadOnlySpan<byte> d)
    {
        if (Bin.FourCC(d) != "ABKC") throw new InvalidDataException("Not an ABKC bank.");
        return new AbkBank
        {
            Modules = Bin.U16LE(d, 0x0A), DeclaredSize = Bin.U32LE(d, 0x14), CodeOffset = (int)Bin.U32LE(d, 0x1C),
            BankOffset = (int)Bin.U32LE(d, 0x20), BankSize = (int)Bin.U32LE(d, 0x24),
            Tables = [(int)Bin.U32LE(d, 0x30), (int)Bin.U32LE(d, 0x34), (int)Bin.U32LE(d, 0x38)],
        };
    }

    public byte[]? ExtractBank(byte[] d) =>
        BankSize > 0 && BankOffset > 0 && BankOffset + 4 <= d.Length && Bin.FourCC(d, BankOffset) == "BNKl"
            ? d.AsSpan(BankOffset, Math.Min(BankSize, d.Length - BankOffset)).ToArray()
            : null;
}

public sealed record CsiLabel(string Name, int Target);

/// <summary>MOIR (<c>MVP.CSI</c>) crowd/commentary state script: header, then a label table of {→name, u32 script offset, 0}.</summary>
public sealed class MoirScript
{
    public int Version { get; private init; }
    public int[] Counts { get; private init; } = [];
    public List<CsiLabel> Labels { get; } = [];

    public static MoirScript Read(byte[] d)
    {
        if (Bin.FourCC(d) != "MOIR") throw new InvalidDataException("Not a MOIR script.");
        var s = new MoirScript { Version = Bin.U16LE(d, 4), Counts = [Bin.U16LE(d, 8), Bin.U16LE(d, 10), Bin.U16LE(d, 12), Bin.U16LE(d, 14)] };
        for (var o = 0x2C; o + 12 <= d.Length; o += 12)
        {
            var name = (int)Bin.U32LE(d, o);
            if (name <= o || name >= d.Length || Bin.U32LE(d, o + 8) != 0) break;
            var text = Bin.CString(d, name, 128);
            if (text.Length == 0 || text.Any(c => c < ' ' || c > '~')) break;
            s.Labels.Add(new CsiLabel(text, (int)Bin.U32LE(d, o + 4)));
        }
        return s;
    }
}

/// <summary>
/// PROJx speech header record (one per stream in the matching <c>DAT</c> pack). 18-byte head
/// {u16 id, u8 kind, u8 segments, s32 link, u16 ?, u16 ?, 6 bytes 0}, then for kind 0 the (segments − 1)
/// big-endian u16 points that fall between spoken phrases (unit unknown), usually closed by <c>70 70</c>.
/// </summary>
public sealed record SpeechHeader(int Id, int Kind, int Segments, int Link, int Field8, int Length, byte[] Payload)
{
    public const int HeadSize = 0x12;

    public static SpeechHeader? Parse(ReadOnlySpan<byte> d)
    {
        if (d.Length < HeadSize) return null;
        return new SpeechHeader(Bin.U16LE(d, 0), d[2], d[3], (int)Bin.U32LE(d, 4), Bin.U16LE(d, 8), Bin.U16LE(d, 10), d[HeadSize..].ToArray());
    }

    public int[] SplitPoints => Kind == 0 && Segments > 1 && Payload.Length >= (Segments - 1) * 2
        ? Enumerable.Range(0, Segments - 1).Select(i => Payload[i * 2] << 8 | Payload[i * 2 + 1]).ToArray()
        : [];
}

/// <summary><c>NAME.OFF</c> text index ("offset,size," per line) that splits a headerless <c>NAME.BIG</c> pack.</summary>
public static class OffsetIndex
{
    public static List<(long Offset, long Size)> Parse(string text)
    {
        var list = new List<(long, long)>();
        foreach (var line in text.Split('\n'))
        {
            var parts = line.Trim().TrimEnd(',').Split(',');
            if (parts.Length >= 2 && long.TryParse(parts[0], out var o) && long.TryParse(parts[1], out var s)) list.Add((o, s));
        }
        return list;
    }
}
