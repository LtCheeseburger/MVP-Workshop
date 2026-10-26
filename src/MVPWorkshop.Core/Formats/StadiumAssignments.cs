using System.Text;
using MVPWorkshop.Core.IO;

namespace MVPWorkshop.Core.Formats;

public sealed record StadiumDefinition(int Index, int NameId, int FullNameId, int SchoolAssetId, int FileOffset);
public sealed record StadiumResolution(string Code, int NativeIndex, int MapOffset, string TimeOfDay, bool UsedFallback, string ResolvedTimeOfDay);

/// <summary>Disc-derived stadium enum → native archive mapping and authentic venue ownership registry.</summary>
public sealed class StadiumAssignments
{
    public List<string> Codes { get; } = [];
    public List<StadiumDefinition> Definitions { get; } = [];
    public int FilenameTableOffset { get; private set; }
    public int MapTableOffset { get; private set; }
    private int[] _maps = [];

    /// <summary>Stadium enum count per time-of-day set: 34 on PS2, 30 on Xbox.</summary>
    public int Count { get; private set; } = 34;

    public StadiumResolution? Resolve(int stadium, int timeOfDay = 0)
    {
        if (stadium < 0 || stadium >= Count || timeOfDay is < 0 or > 2) return null;
        var selected = timeOfDay * Count + stadium;
        var fallback = false;
        // Mirrors SLUS-215.82 function 0x00197D50: requested time, then dusk, night, day.
        if (_maps[selected] < 0)
        {
            fallback = true;
            selected = Count + stadium;
            if (_maps[selected] < 0) selected = 2 * Count + stadium;
            if (_maps[selected] < 0) selected = stadium;
        }
        var native = _maps[selected];
        return native >= 0 && native < Codes.Count ? new StadiumResolution(Codes[native], native, MapTableOffset + selected * 4,
            new[] { "Day", "Dusk", "Night" }[timeOfDay], fallback, new[] { "Day", "Dusk", "Night" }[selected / Count]) : null;
    }

    public static StadiumAssignments Read(byte[] data) => data.AsSpan().StartsWith("XBEH"u8) ? ReadXbe(data) : ReadElf(data);

    /// <summary>default.xbe: same three structures as the PS2 executable but the enum has 30 entries and the
    /// ownership registry carries no fixed anchor, so it is found as a run of rows whose indices are a permutation.</summary>
    private static StadiumAssignments ReadXbe(byte[] data)
    {
        var xbe = XbeFile.Read(data);
        int Offset(uint address)
        {
            var section = xbe.Sections.FirstOrDefault(s => address >= s.VirtualAddress && address - s.VirtualAddress < s.RawSize);
            return section is null ? -1 : checked((int)(section.RawAddress + address - section.VirtualAddress));
        }
        uint Address(int offset)
        {
            var section = xbe.Sections.First(s => offset >= s.RawAddress && offset - s.RawAddress < s.RawSize);
            return section.VirtualAddress + (uint)offset - section.RawAddress;
        }
        int StringOffset(string value)
        {
            var needle = new byte[value.Length + 2];
            Encoding.ASCII.GetBytes(value).CopyTo(needle, 1);
            var at = data.AsSpan().IndexOf(needle);
            if (at < 0) throw new InvalidDataException($"Stadium string {value} not found in executable.");
            return at + 1;
        }
        var result = new StadiumAssignments();
        var first = Address(StringOffset("venuday")); var second = Address(StringOffset("venudusk")); var third = Address(StringOffset("venunite"));
        var tables = new List<int>();
        for (int at = 0; at + 12 <= data.Length; at += 4)
            if (Bin.U32LE(data, at) == first && Bin.U32LE(data, at + 4) == second && Bin.U32LE(data, at + 8) == third) tables.Add(at);
        if (tables.Count != 1) throw new InvalidDataException("Stadium filename table is missing or ambiguous.");
        result.FilenameTableOffset = tables[0];
        for (int i = 0; i < 128 && result.FilenameTableOffset + i * 4 + 4 <= data.Length; i++)
        {
            var at = Offset(Bin.U32LE(data, result.FilenameTableOffset + i * 4));
            if (at < 0) break;
            var name = Bin.CString(data, at, 32);
            if (!(name.EndsWith("day", StringComparison.Ordinal) || name.EndsWith("dusk", StringComparison.Ordinal) || name.EndsWith("nite", StringComparison.Ordinal))) break;
            result.Codes.Add(name);
        }
        if (result.Codes.Count < 30) throw new InvalidDataException("Incomplete stadium filename table.");
        bool RegistryRow(int at) => at >= 0 && at + 16 <= data.Length && Bin.U32LE(data, at) is >= 1000 and < 65536 &&
            ((int)Bin.U32LE(data, at + 4) == -1 || Bin.U32LE(data, at + 4) is >= 1000 and < 65536) &&
            Bin.U32LE(data, at + 8) < 64 && (int)Bin.U32LE(data, at + 12) is >= -1 and <= 511;
        var registries = new List<(int Start, int Rows)>();
        for (int at = 0; at + 16 <= data.Length; at += 4)
        {
            if (RegistryRow(at - 16) || !RegistryRow(at)) continue;
            var rows = 0;
            while (RegistryRow(at + rows * 16)) rows++;
            if (rows < 20) continue;
            var indices = Enumerable.Range(0, rows).Select(i => (int)Bin.U32LE(data, at + i * 16 + 8)).Order().ToList();
            if (indices.SequenceEqual(Enumerable.Range(0, rows))) registries.Add((at, rows));
        }
        if (registries.Count != 1) throw new InvalidDataException("Authentic stadium ownership registry is missing or ambiguous.");
        var (start, count) = registries[0];
        result.Count = count;
        for (int i = 0; i < count; i++)
        {
            var at = start + i * 16;
            result.Definitions.Add(new StadiumDefinition((int)Bin.U32LE(data, at + 8), (int)Bin.U32LE(data, at), (int)Bin.U32LE(data, at + 4), (int)Bin.U32LE(data, at + 12), at));
        }
        result.MapTableOffset = FindMap(data, result.Codes, count);
        result._maps = Enumerable.Range(0, 3 * count).Select(i => (int)Bin.U32LE(data, result.MapTableOffset + i * 4)).ToArray();
        return result;
    }

    private static int FindMap(byte[] data, List<string> codes, int count)
    {
        var head = new[] { "rbltday", "chanday", "hawkday", "aboxday", "arodday", "dishday" }.Select(name => codes.IndexOf(name)).ToArray();
        var candidates = new List<int>();
        for (int at = 0; at + 3 * count * 4 <= data.Length; at += 4)
        {
            if (!Enumerable.Range(0, head.Length).All(i => (int)Bin.U32LE(data, at + i * 4) == head[i])) continue;
            if (!Enumerable.Range(0, 3 * count).All(i => (int)Bin.U32LE(data, at + i * 4) is var v && v >= -1 && v < codes.Count)) continue;
            if (Bin.U32LE(data, at + (count - 1) * 4) != 0 || Bin.U32LE(data, at + (2 * count - 1) * 4) != 1 || Bin.U32LE(data, at + (3 * count - 1) * 4) != 2) continue;
            candidates.Add(at);
        }
        if (candidates.Count != 1) throw new InvalidDataException("Stadium time-of-day mapping table is missing or ambiguous.");
        return candidates[0];
    }

    private static StadiumAssignments ReadElf(byte[] data)
    {
        var elf = ElfFile.Read(data);
        var result = new StadiumAssignments();
        int Offset(uint address)
        {
            var segment = elf.Segments.FirstOrDefault(s => s.Type == "LOAD" && address >= s.VAddr && (long)address < (long)s.VAddr + s.FileSize);
            return segment is null ? -1 : checked((int)(segment.Offset + address - segment.VAddr));
        }
        uint Address(int offset)
        {
            var segment = elf.Segments.First(s => s.Type == "LOAD" && offset >= s.Offset && (long)offset < (long)s.Offset + s.FileSize);
            return checked(segment.VAddr + (uint)offset - segment.Offset);
        }
        int StringOffset(string value)
        {
            var at = data.AsSpan().IndexOf(Encoding.ASCII.GetBytes(value + "\0"));
            if (at < 0) throw new InvalidDataException($"Stadium string {value} not found in executable.");
            return at;
        }
        var first = Address(StringOffset("venuday"));
        var second = Address(StringOffset("venudusk"));
        var third = Address(StringOffset("venunite"));
        var filenameCandidates = new List<int>();
        for (int at = 0; at + 12 <= data.Length; at += 4)
            if (Bin.U32LE(data, at) == first && Bin.U32LE(data, at + 4) == second && Bin.U32LE(data, at + 8) == third) filenameCandidates.Add(at);
        if (filenameCandidates.Count != 1) throw new InvalidDataException("Stadium filename table is missing or ambiguous.");
        result.FilenameTableOffset = filenameCandidates[0];
        for (int i = 0; i < 128 && result.FilenameTableOffset + i * 4 + 4 <= data.Length; i++)
        {
            var at = Offset(Bin.U32LE(data, result.FilenameTableOffset + i * 4));
            if (at < 0) break;
            var name = Bin.CString(data, at, 32);
            if (!(name.EndsWith("day", StringComparison.Ordinal) || name.EndsWith("dusk", StringComparison.Ordinal) || name.EndsWith("nite", StringComparison.Ordinal))) break;
            result.Codes.Add(name);
        }
        if (result.Codes.Count < 38) throw new InvalidDataException("Incomplete stadium filename table.");
        var head = new[] { "rbltday", "chanday", "hawkday", "aboxday", "arodday", "dishday" }.Select(name => result.Codes.IndexOf(name)).ToArray();
        var mapCandidates = new List<int>();
        for (int at = 0; at + 102 * 4 <= data.Length; at += 4)
        {
            if (Bin.U32LE(data, at) != head[0]) continue;
            if (!Enumerable.Range(0, head.Length).All(i => (int)Bin.U32LE(data, at + i * 4) == head[i])) continue;
            if (!Enumerable.Range(0, 102).All(i => (int)Bin.U32LE(data, at + i * 4) is var v && v >= -1 && v < result.Codes.Count)) continue;
            if (Bin.U32LE(data, at + 33 * 4) != 0 || Bin.U32LE(data, at + 67 * 4) != 1 || Bin.U32LE(data, at + 101 * 4) != 2) continue;
            mapCandidates.Add(at);
        }
        if (mapCandidates.Count != 1) throw new InvalidDataException("Stadium time-of-day mapping table is missing or ambiguous.");
        result.MapTableOffset = mapCandidates[0];
        result._maps = Enumerable.Range(0, 102).Select(i => (int)Bin.U32LE(data, result.MapTableOffset + i * 4)).ToArray();
        bool RegistryRow(int at) => at >= 0 && at + 16 <= data.Length && Bin.U32LE(data, at) is >= 1000 and < 65536 &&
            ((int)Bin.U32LE(data, at + 4) == -1 || Bin.U32LE(data, at + 4) is >= 1000 and < 65536) &&
            Bin.U32LE(data, at + 8) < 34 && (int)Bin.U32LE(data, at + 12) is >= -1 and <= 511;
        var registries = new List<int>();
        for (int at = 4; at + 12 <= data.Length; at += 4)
        {
            if (Bin.U32LE(data, at) != 17204 || Bin.U32LE(data, at + 4) != 3 || Bin.U32LE(data, at + 8) != 32) continue;
            var start = at - 4;
            while (RegistryRow(start - 16)) start -= 16;
            if (Enumerable.Range(0, 34).All(i => RegistryRow(start + i * 16)) &&
                Enumerable.Range(0, 34).Select(i => Bin.U32LE(data, start + i * 16 + 8)).Distinct().Count() == 34) registries.Add(start);
        }
        if (registries.Distinct().Count() != 1) throw new InvalidDataException("Authentic stadium ownership registry is missing or ambiguous.");
        for (int i = 0; i < 34; i++)
        {
            var at = registries[0] + i * 16;
            result.Definitions.Add(new StadiumDefinition((int)Bin.U32LE(data, at + 8), (int)Bin.U32LE(data, at), (int)Bin.U32LE(data, at + 4), (int)Bin.U32LE(data, at + 12), at));
        }
        return result;
    }
}

