using MVPWorkshop.Core.Formats;
using MVPWorkshop.Core.Formats.Eagl;

namespace MVPWorkshop.Core.Vfs;

public sealed record UniformVariant(char Suffix, AssetNode Texture, AssetNode? Font)
{
    public override string ToString() => $"Variant {Suffix} · {Texture.Name}";
}

/// <summary>Disc-backed uniform sources. Suffixes deliberately carry no home/away interpretation.</summary>
public static class UniformPreview
{
    public static Dictionary<string,string> ReadDirtBindings(byte[] executable)
    {
        if (XbeTables.IsXbe(executable)) return XbeTables.DirtBindings(executable);
        var elf=ElfFile.Read(executable);
        int Offset(uint address)
        {
            var segment=elf.Segments.First(s=>address>=s.VAddr&&address<s.VAddr+s.FileSize);
            return checked((int)(segment.Offset+address-segment.VAddr));
        }
        string Entry(uint table,int index)
        {
            int start=Offset(BitConverter.ToUInt32(executable,Offset(table)+index*4));
            return System.Text.Encoding.ASCII.GetString(executable,start,Array.IndexOf(executable,(byte)0,start)-start);
        }
        return Enumerable.Range(1,11).ToDictionary(i=>Entry(0x85CAB8,i),i=>Entry(0x85C998,i));
    }
    public static List<UniformVariant> Variants(TeamProfile profile) => profile.Assets
        .Where(a => a.Category == "Uniforms/Player textures" && a.Asset.Name.StartsWith('u'))
        .OrderBy(a => a.Asset.Name).Select(a => new UniformVariant(a.Asset.Name[4], a.Asset,
            profile.Assets.FirstOrDefault(f => f.Asset.Name.Equals("f" + a.Asset.Name[1..], StringComparison.OrdinalIgnoreCase))?.Asset)).ToList();

    // SLUS-215.82: uniform texture lookup VA 0x85C8B8, material lookup VA 0x85CAE8.
    // 0x43F058 reads uniform slots 4/5 and 17/18; 0x43F274–0x43F304 binds jersey/sleeves.
    public static Dictionary<string, string> ReadBindings(byte[] executable)
    {
        if (XbeTables.IsXbe(executable)) return XbeTables.UniformBindings(executable);
        var elf = ElfFile.Read(executable);
        string At(uint address)
        {
            var seg = elf.Segments.First(s => address >= s.VAddr && address < s.VAddr + s.FileSize);
            int offset = checked((int)(seg.Offset + address - seg.VAddr));
            int end = Array.IndexOf(executable, (byte)0, offset);
            return System.Text.Encoding.ASCII.GetString(executable, offset, end - offset);
        }
        string Entry(uint table, int slot)
        {
            var seg = elf.Segments.First(s => table >= s.VAddr && table < s.VAddr + s.FileSize);
            return At(BitConverter.ToUInt32(executable, checked((int)(seg.Offset + table - seg.VAddr + slot * 4))));
        }
        var bindings = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (material, slot) in new[] { (1, 4), (3, 5), (6, 17), (7, 18), (9, 6), (10, 9), (33, 1), (34, 2), (38, 15) })
            bindings.Add(Entry(0x85CAE8, material), Entry(0x85C8B8, slot));
        if (bindings.GetValueOrDefault("jers") != "tors" || bindings.GetValueOrDefault("hats") != "hat")
            throw new InvalidDataException("Unsupported executable uniform tables.");
        return bindings;
    }
}

