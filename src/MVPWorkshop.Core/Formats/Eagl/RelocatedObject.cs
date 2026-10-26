using MVPWorkshop.Core.IO;

namespace MVPWorkshop.Core.Formats.Eagl;

/// <summary>A pointer slot in .data after applying R_MIPS_32: either into .data itself, or to an external (runtime) symbol.</summary>
public readonly record struct Reloc(int Target, string? External)
{
    public bool IsInternal => External is null;
}

/// <summary>
/// A relocatable EAGL object (.ord+.orl joined) with its .data section and relocations resolved.
/// All offsets are relative to the start of .data.
/// </summary>
public sealed class RelocatedObject
{
    public byte[] Data { get; private init; } = [];
    public Dictionary<int, Reloc> Relocs { get; } = [];
    public List<(string Name, int Value, int Size)> Symbols { get; } = [];

    public static RelocatedObject Load(byte[] elf)
    {
        var f = ElfFile.Read(elf);
        var data = f.Sections.FirstOrDefault(s => s.Name == ".data") ?? throw new InvalidDataException("No .data section.");
        var dataIndex = data.Index;
        var obj = new RelocatedObject { Data = elf.AsSpan((int)data.Offset, (int)Math.Min(data.Size, elf.Length - data.Offset)).ToArray() };

        var symtab = f.Sections.First(s => s.Type == "SYMTAB");
        var syms = new List<(string Name, uint Value, ushort Shndx)>();
        foreach (var s in f.Symbols)
        {
            var p = (int)(symtab.Offset + s.Index * 16);
            var shndx = Bin.U16LE(elf, p + 14);
            syms.Add((s.Name, s.Value, shndx));
            if (shndx == dataIndex && s.Name.Length > 0) obj.Symbols.Add((s.Name, (int)s.Value, (int)s.Size));
        }

        foreach (var rel in f.Sections.Where(s => s.Type == "REL" && s.Info == dataIndex))
        {
            for (var i = 0; i < rel.Size / 8; i++)
            {
                var p = (int)rel.Offset + i * 8;
                var off = (int)Bin.U32LE(elf, p);
                var info = Bin.U32LE(elf, p + 4);
                var type = info & 0xFF;
                var sym = syms[(int)(info >> 8)];
                if (type != 2 || off + 4 > obj.Data.Length) continue; // R_MIPS_32 only
                var addend = (int)Bin.U32LE(obj.Data, off);
                obj.Relocs[off] = sym.Shndx == dataIndex
                    ? new Reloc(addend + (int)sym.Value, null)
                    : new Reloc(addend, sym.Name);
            }
        }
        return obj;
    }

    public uint U32(int o) => o >= 0 && o + 4 <= Data.Length ? Bin.U32LE(Data, o) : 0;
    public float F32(int o) => BitConverter.ToSingle(Data, o);

    /// <summary>Internal pointer stored at <paramref name="o"/>, or null.</summary>
    public int? Ptr(int o) => Relocs.TryGetValue(o, out var r) && r.IsInternal ? r.Target : null;

    public string? External(int o) => Relocs.TryGetValue(o, out var r) ? r.External : null;
}
