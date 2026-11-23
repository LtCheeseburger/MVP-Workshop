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
    /// <summary>Preview role defaults follow available suffix order; users can map roles explicitly.</summary>
    public static Dictionary<string, char> PreviewRoles(IReadOnlyList<UniformVariant> variants)
    {
        var roles = new Dictionary<string, char>();
        var names = new[] { "Home", "Away", "Alternate" };
        for (int i = 0; i < Math.Min(names.Length, variants.Count); i++) roles[names[i]] = variants[i].Suffix;
        return roles;
    }

    /// <summary>Dominant opaque fabric colours; no invented official school palette.</summary>
    public static IReadOnlyList<uint> FabricColours(UniformVariant variant)
    {
        var ssh = SshFile.Read(variant.Texture.ReadAll());
        var image = ssh.Images.FirstOrDefault(i => i.Tag.TrimEnd() == "tors") ?? ssh.Images.First();
        var decoded = ssh.Decode(image);
        var bins = new Dictionary<int, (long R, long G, long B, int Count)>();
        for (int i = 0; i < decoded.Bgra.Length; i += 4)
        {
            if (decoded.Bgra[i + 3] < 128) continue;
            int b = decoded.Bgra[i], g = decoded.Bgra[i + 1], r = decoded.Bgra[i + 2];
            int key = (r >> 4) << 8 | (g >> 4) << 4 | b >> 4;
            var bin = bins.GetValueOrDefault(key); bins[key] = (bin.R + r, bin.G + g, bin.B + b, bin.Count + 1);
        }
        return bins.Values.OrderByDescending(b => b.Count).Take(5).Select(b => 0xff000000u | (uint)(b.R / b.Count) << 16 | (uint)(b.G / b.Count) << 8 | (uint)(b.B / b.Count)).ToList();
    }
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
    public static List<AssetNode> Sources(AssetNode root) => root.EnumerateDeep(n =>
        !n.IsLooseTexturePack && n.Name.ToUpperInvariant() is not ("AUDIO" or "STADIUM" or "ANIMS" or "APTANIMS" or "FRONTEND")).ToList();

    public static bool IsPlayerTexture(AssetNode node)
    {
        var stem = System.IO.Path.GetFileNameWithoutExtension(node.Name);
        return node.IsTexturePack && stem.Length == 5 && char.ToLowerInvariant(stem[0]) is 'u' or 'f'
            && stem.Skip(1).Take(3).All(char.IsAsciiDigit);
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

