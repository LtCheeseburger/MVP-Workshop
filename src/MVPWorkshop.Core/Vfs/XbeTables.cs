using System.Text;
using MVPWorkshop.Core.Formats;
using MVPWorkshop.Core.IO;

namespace MVPWorkshop.Core.Vfs;

/// <summary>
/// default.xbe equivalents of the SLUS-215.82 uniform tables (pointer arrays of 4-char names, same layout as PS2):
/// texture slots (PS2 0x85C8B8, Xbox 0x6AA930), dirt texture values (0x85C998 / 0x6AA9D4),
/// dirt material keys (0x85CAB8 / 0x6AAAD8) and material names (0x85CAE8 / 0x6AAB08). Located by their leading names, not addresses.
/// </summary>
public static class XbeTables
{
    public static bool IsXbe(byte[] data) => data.AsSpan().StartsWith("XBEH"u8);

    private sealed class Image(byte[] data)
    {
        private readonly XbeFile _xbe = XbeFile.Read(data);
        public int Offset(uint va)
        {
            var s = _xbe.Sections.FirstOrDefault(s => va >= s.VirtualAddress && va - s.VirtualAddress < s.RawSize);
            return s is null ? -1 : checked((int)(s.RawAddress + va - s.VirtualAddress));
        }
        public uint Address(int offset)
        {
            var s = _xbe.Sections.FirstOrDefault(s => offset >= s.RawAddress && offset - s.RawAddress < s.RawSize);
            return s is null ? 0 : s.VirtualAddress + (uint)offset - s.RawAddress;
        }
        public string? Text(uint va)
        {
            var at = Offset(va);
            return at < 0 || at >= data.Length ? null : Bin.CString(data, at, 16);
        }
        public uint StringAddress(string text)
        {
            var needle = new byte[text.Length + 2];
            Encoding.ASCII.GetBytes(text).CopyTo(needle, 1);
            var at = data.AsSpan().IndexOf(needle);
            return at < 0 ? 0 : Address(at + 1);
        }
        /// <summary>Table offset (file) whose first entries point at the given strings.</summary>
        public int Find(params string[] names)
        {
            var wanted = names.Select(StringAddress).ToArray();
            if (wanted.Any(w => w == 0)) throw new InvalidDataException("Uniform table strings not found in executable.");
            var hits = new List<int>();
            for (int at = 0; at + wanted.Length * 4 <= data.Length; at += 4)
                if (Enumerable.Range(0, wanted.Length).All(i => Bin.U32LE(data, at + i * 4) == wanted[i])) hits.Add(at);
            if (hits.Count != 1) throw new InvalidDataException($"Uniform table '{names[0]}…' is missing or ambiguous.");
            return hits[0];
        }
        public string Entry(int table, int index) => Text(Bin.U32LE(data, table + index * 4)) ?? "";
    }

    public static Dictionary<string, string> UniformBindings(byte[] data)
    {
        var image = new Image(data);
        var textures = image.Find("tors", "hat", "hlm1", "hlm2");
        var materials = image.Find("shoe", "jerk", "merk", "jers");
        var bindings = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (material, slot) in new[] { (1, 4), (3, 5), (6, 17), (7, 18), (9, 6), (10, 9), (33, 1), (34, 2), (38, 15) })
            bindings.Add(image.Entry(materials, material), image.Entry(textures, slot));
        if (bindings.GetValueOrDefault("jers") != "tors" || bindings.GetValueOrDefault("hats") != "hat")
            throw new InvalidDataException("Unsupported executable uniform tables.");
        return bindings;
    }

    public static Dictionary<string, string> DirtBindings(byte[] data)
    {
        var image = new Image(data);
        // Both arrays have an empty entry 0 followed by entries 1–11; anchor on entry 1.. and step back one slot.
        var keys = image.Find("dbtl", "dbtr", "dcht", "dlll") - 4;
        var values = image.Find("dbtl", "dbtl", "dcht", "dcht", "dcht", "dbtl") - 4;
        return Enumerable.Range(1, 11).ToDictionary(i => image.Entry(keys, i), i => image.Entry(values, i));
    }
}
