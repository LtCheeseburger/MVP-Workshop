using MVPWorkshop.Core.IO;

namespace MVPWorkshop.Core.Formats;

public sealed record ElfSection(int Index, string Name, string Type, uint Flags, uint Addr, uint Offset, uint Size, uint Link, uint Info, uint Align, uint EntSize)
{
    public string FlagsText => string.Concat((Flags & 1) != 0 ? "W" : "", (Flags & 2) != 0 ? "A" : "", (Flags & 4) != 0 ? "X" : "");
}

public sealed record ElfSymbol(int Index, string Name, uint Value, uint Size, string Type, string Bind, string Section);

public sealed record ElfSegment(int Index, string Type, uint Offset, uint VAddr, uint FileSize, uint MemSize, uint Flags);

/// <summary>32-bit little-endian ELF (PS2 EE/IOP). Handles executables, IRX modules and relocatable .o/.ord objects.</summary>
public sealed class ElfFile
{
    public string Kind { get; private set; } = "";
    public string Machine { get; private set; } = "";
    public uint Entry { get; private set; }
    public uint Flags { get; private set; }
    public List<ElfSection> Sections { get; } = [];
    public List<ElfSymbol> Symbols { get; } = [];
    public List<ElfSegment> Segments { get; } = [];

    /// <summary>
    /// EA split objects: an .ord holds the ELF header and section data; its .orl partner holds everything after
    /// (shstrtab, section header table, relocations, symtab, strtab) and starts exactly at the .ord's end.
    /// Concatenating them reproduces the original relocatable ELF.
    /// </summary>
    public static bool NeedsLinkPartner(byte[] ord) =>
        ord.Length >= 52 && ord[0] == 0x7F && ord[1] == 'E' && Bin.U16LE(ord, 48) > 0 && Bin.U32LE(ord, 32) >= ord.Length;

    public static byte[] JoinSplit(byte[] ord, byte[] orl)
    {
        var r = new byte[ord.Length + orl.Length];
        ord.CopyTo(r, 0); orl.CopyTo(r, ord.Length);
        return r;
    }

    public static ElfFile Read(byte[] d)
    {
        if (d.Length < 52 || d[0] != 0x7F || d[1] != 'E' || d[2] != 'L' || d[3] != 'F') throw new InvalidDataException("Not ELF.");
        if (d[4] != 1 || d[5] != 1) throw new NotSupportedException("Only 32-bit little-endian ELF is supported.");
        var e = new ElfFile
        {
            Kind = Bin.U16LE(d, 16) switch { 1 => "Relocatable", 2 => "Executable", 3 => "Shared object", 0xFF80 => "PS2 IRX (IOP module)", var x => $"0x{x:X4}" },
            Machine = Bin.U16LE(d, 18) == 8 ? "MIPS" : $"0x{Bin.U16LE(d, 18):X}",
            Entry = Bin.U32LE(d, 24),
            Flags = Bin.U32LE(d, 36),
        };
        var phoff = (int)Bin.U32LE(d, 28); var shoff = (int)Bin.U32LE(d, 32);
        int phentsz = Bin.U16LE(d, 42), phnum = Bin.U16LE(d, 44), shentsz = Bin.U16LE(d, 46), shnum = Bin.U16LE(d, 48), shstrndx = Bin.U16LE(d, 50);

        for (var i = 0; i < phnum && phoff + (i + 1) * phentsz <= d.Length; i++)
        {
            var p = phoff + i * phentsz;
            var t = Bin.U32LE(d, p);
            e.Segments.Add(new ElfSegment(i, t switch { 0 => "NULL", 1 => "LOAD", 2 => "DYNAMIC", 3 => "INTERP", 4 => "NOTE", 6 => "PHDR", 0x70000080 => "IOPMOD", _ => $"0x{t:X}" },
                Bin.U32LE(d, p + 4), Bin.U32LE(d, p + 8), Bin.U32LE(d, p + 16), Bin.U32LE(d, p + 20), Bin.U32LE(d, p + 24)));
        }

        var raw = new List<(uint name, uint type, uint flags, uint addr, uint off, uint size, uint link, uint info, uint align, uint ent)>();
        for (var i = 0; i < shnum && shoff + (i + 1) * shentsz <= d.Length; i++)
        {
            var p = shoff + i * shentsz;
            raw.Add((Bin.U32LE(d, p), Bin.U32LE(d, p + 4), Bin.U32LE(d, p + 8), Bin.U32LE(d, p + 12), Bin.U32LE(d, p + 16), Bin.U32LE(d, p + 20),
                Bin.U32LE(d, p + 24), Bin.U32LE(d, p + 28), Bin.U32LE(d, p + 32), Bin.U32LE(d, p + 36)));
        }
        string Str(int sec, uint off)
        {
            if (sec < 0 || sec >= raw.Count || off >= raw[sec].size) return "";
            var o = raw[sec].off + off;
            return o < d.Length ? Bin.CString(d, (int)o, (int)Math.Min(65536, raw[sec].size - off)) : "";
        }
        for (var i = 0; i < raw.Count; i++)
        {
            var s = raw[i];
            e.Sections.Add(new ElfSection(i, Str(shstrndx, s.name), SectionType(s.type), s.flags, s.addr, s.off, s.size, s.link, s.info, s.align, s.ent));
        }
        foreach (var s in e.Sections.Where(s => s.Type == "SYMTAB"))
        {
            var count = s.EntSize == 0 ? 0 : s.Size / s.EntSize;
            for (var i = 0; i < count; i++)
            {
                var p = (int)(s.Offset + i * s.EntSize);
                if (p + 16 > d.Length) break;
                var info = d[p + 12];
                var shndx = Bin.U16LE(d, p + 14);
                var secName = shndx switch { 0 => "UND", 0xFFF1 => "ABS", 0xFFF2 => "COMMON", _ => shndx < e.Sections.Count ? e.Sections[shndx].Name : $"#{shndx}" };
                e.Symbols.Add(new ElfSymbol(i, Str((int)s.Link, Bin.U32LE(d, p)), Bin.U32LE(d, p + 4), Bin.U32LE(d, p + 8),
                    (info & 0xF) switch { 0 => "NOTYPE", 1 => "OBJECT", 2 => "FUNC", 3 => "SECTION", 4 => "FILE", var x => x.ToString() },
                    (info >> 4) switch { 0 => "LOCAL", 1 => "GLOBAL", 2 => "WEAK", var x => x.ToString() }, secName));
            }
        }
        return e;
    }

    private static string SectionType(uint t) => t switch
    {
        0 => "NULL", 1 => "PROGBITS", 2 => "SYMTAB", 3 => "STRTAB", 4 => "RELA", 8 => "NOBITS", 9 => "REL",
        0x70000006 => "MIPS_REGINFO", 0x70000080 => "IOPMOD", 0x7000000D => "MIPS_OPTIONS", _ => $"0x{t:X}"
    };
}


