using System.Text;
using MVPWorkshop.Core.IO;

namespace MVPWorkshop.Core.Formats;

public sealed record Prop(string Group, string Key, string Value);

public sealed record ChunkInfo(long Offset, string Id, uint Size);

/// <summary>Header/metadata extraction for every known format, plus generic research helpers (strings, chunks, CSV).</summary>
public static class FormatInspector
{
    static FormatInspector() => Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

    public static Encoding TextEncoding => Encoding.GetEncoding(1252);

    public static List<Prop> Describe(FormatId fmt, IByteSource src)
    {
        var p = new List<Prop>();
        try
        {
            switch (fmt)
            {
                case FormatId.BigArchive:
                {
                    var big = BigArchive.Read(src);
                    p.Add(new("BIG", "Magic", big.Magic));
                    p.Add(new("BIG", "Declared size", $"{big.DeclaredSize:N0} (0x{big.DeclaredSize:X})"));
                    p.Add(new("BIG", "Entries", big.Entries.Count.ToString("N0")));
                    p.Add(new("BIG", "Header size", $"0x{big.HeaderSize:X}"));
                    var compressed = big.Entries.Count(e => e.Size >= 5 && IsRefPackAt(src, e.Offset));
                    p.Add(new("BIG", "RefPack entries", $"{compressed:N0}"));
                    break;
                }
                case FormatId.SshTexture:
                {
                    var ssh = SshFile.Read(src.ReadAll());
                    p.Add(new("SSH", "Magic", ssh.Magic));
                    p.Add(new("SSH", "Version", ssh.Version));
                    p.Add(new("SSH", "Images", ssh.Images.Count.ToString()));
                    if (ssh.GroupName is { Length: > 0 }) p.Add(new("SSH", "Group", ssh.GroupName));
                    foreach (var g in ssh.Images.GroupBy(i => i.Header.Type))
                        p.Add(new("SSH", $"Type 0x{g.Key:X2}", $"{g.Count()} × {SshFile.TypeName(g.Key)}"));
                    break;
                }
                case FormatId.SchlStream:
                case FormatId.SchlContainer:
                {
                    var streams = SchlStream.ScanAll(src, 20000);
                    p.Add(new("SCHl", "Streams", streams.Count.ToString("N0")));
                    if (streams.FirstOrDefault()?.Header is { } h) AddPatch(p, "First stream", h);
                    break;
                }
                case FormatId.EaBank:
                {
                    var bank = EaBank.Read(src.ReadAll());
                    p.Add(new("BNK", "Version", bank.Version.ToString()));
                    p.Add(new("BNK", "Declared sounds", bank.DeclaredCount.ToString()));
                    p.Add(new("BNK", "Valid sounds", bank.Sounds.Count.ToString()));
                    if (bank.Sounds.FirstOrDefault() is { } s) AddPatch(p, "First sound", s.Header);
                    break;
                }
                case FormatId.Elf:
                {
                    var raw = src.ReadAll();
                    var elf = ElfFile.Read(raw);
                    if (ElfFile.NeedsLinkPartner(raw))
                    {
                        p.Add(new("ELF", "Kind", elf.Kind + " (split — section table is in the paired .orl)"));
                        break;
                    }
                    p.Add(new("ELF", "Kind", elf.Kind));
                    p.Add(new("ELF", "Machine", elf.Machine));
                    p.Add(new("ELF", "Entry", $"0x{elf.Entry:X8}"));
                    p.Add(new("ELF", "Flags", $"0x{elf.Flags:X8}"));
                    p.Add(new("ELF", "Sections", elf.Sections.Count.ToString()));
                    p.Add(new("ELF", "Segments", elf.Segments.Count.ToString()));
                    p.Add(new("ELF", "Symbols", elf.Symbols.Count.ToString("N0")));
                    break;
                }
                case FormatId.MpcMovie:
                {
                    var h = src.ReadBytes(0, 0x100);
                    p.Add(new("MPC", "Header size", $"0x{Bin.U32LE(h, 4):X}"));
                    for (var i = 4; i + 8 < h.Length; i++)
                        if (h[i] == 0 && h[i + 1] == 0 && h[i + 2] == 1 && h[i + 3] == 0xB3)
                        {
                            var w = h[i + 4] << 4 | h[i + 5] >> 4; var ht = (h[i + 5] & 0xF) << 8 | h[i + 6];
                            var fr = (h[i + 7] & 0xF) switch { 1 => "23.976", 2 => "24", 3 => "25", 4 => "29.97", 5 => "30", 6 => "50", 7 => "59.94", 8 => "60", _ => "?" };
                            p.Add(new("MPEG", "Sequence header", $"@0x{i:X}"));
                            p.Add(new("MPEG", "Resolution", $"{w} × {ht}"));
                            p.Add(new("MPEG", "Frame rate", fr));
                            break;
                        }
                    var enc = Encoding.ASCII.GetString(h);
                    var ix = enc.IndexOf("Encoded with", StringComparison.Ordinal);
                    if (ix >= 0) p.Add(new("MPEG", "User data", new string(enc[ix..].TakeWhile(c => c >= ' ' && c < 127).ToArray())));
                    break;
                }
                case FormatId.Vp6Movie:
                {
                    var h = src.ReadBytes(0, 0x40);
                    if (Vp6Header.Dimensions(h) is { } dim) p.Add(new("VP6", "Resolution", $"{dim.Width} × {dim.Height}"));
                    p.Add(new("VP6", "Container", "EA MVhd (On2 VP6 video, EA-XA audio) — decoded through ffmpeg"));
                    break;
                }
                case FormatId.XboxExecutable:
                {
                    var x = XbeFile.Read(src.ReadAll());
                    p.Add(new("XBE", "Title", x.Title));
                    p.Add(new("XBE", "Title ID", $"0x{x.TitleId:X8} ({x.TitleIdText})"));
                    p.Add(new("XBE", "Regions", x.RegionText));
                    p.Add(new("XBE", "Version", x.Version.ToString()));
                    p.Add(new("XBE", "Built", x.TimeDate.ToString("yyyy-MM-dd HH:mm 'UTC'")));
                    p.Add(new("XBE", "Base address", $"0x{x.BaseAddress:X8}"));
                    p.Add(new("XBE", "Image size", $"{x.ImageSize:N0}"));
                    foreach (var s in x.Sections) p.Add(new("XBE sections", s.Name, $"VA 0x{s.VirtualAddress:X8}  size 0x{s.VirtualSize:X}  file 0x{s.RawAddress:X}+0x{s.RawSize:X}"));
                    foreach (var l in x.Libraries) p.Add(new("XBE libraries", l.Name, $"{l.Major}.{l.Minor}.{l.Build}"));
                    break;
                }
                case FormatId.XboxResource:
                {
                    var d = src.ReadAll();
                    var f = Bin.U32LE(d, 0x18);
                    p.Add(new("XPR0", "Texture format", $"0x{f >> 8 & 0xFF:X2}"));
                    p.Add(new("XPR0", "Size", $"{1 << (int)(f >> 20 & 0xF)} × {1 << (int)(f >> 24 & 0xF)}"));
                    break;
                }
                case FormatId.AptConst:
                {
                    var c = AptConstants.Read(src.ReadAll());
                    p.Add(new("APT", "Movie character", $"@0x{c.MovieOffset:X} in the .apt"));
                    p.Add(new("APT", "Constants", c.Items.Count.ToString()));
                    foreach (var g in c.Items.GroupBy(i => i.TypeName)) p.Add(new("APT", $"  {g.Key}", g.Count().ToString()));
                    break;
                }
                case FormatId.AptMovie:
                {
                    var h = src.ReadBytes(0, 64);
                    p.Add(new("APT", "Signature", Bin.CString(h, 0, 32).Split('')[0]));
                    break;
                }
                case FormatId.Ps2IconSys:
                {
                    var h = src.ReadBytes(0, 964);
                    var titleBytes = h.AsSpan(0xC0, 68).ToArray();
                    var title = Encoding.GetEncoding(932).GetString(titleBytes).TrimEnd('\0');
                    p.Add(new("icon.sys", "Title", title));
                    p.Add(new("icon.sys", "Line break", Bin.U16LE(h, 6).ToString()));
                    p.Add(new("icon.sys", "Icon (list)", Bin.CString(h, 0x104, 64)));
                    p.Add(new("icon.sys", "Icon (copy)", Bin.CString(h, 0x144, 64)));
                    p.Add(new("icon.sys", "Icon (delete)", Bin.CString(h, 0x184, 64)));
                    break;
                }
                case FormatId.Ps2Icon:
                {
                    var h = src.ReadBytes(0, 20);
                    p.Add(new("PS2 icon", "Animation shapes", Bin.U32LE(h, 4).ToString()));
                    p.Add(new("PS2 icon", "Texture type", $"0x{Bin.U32LE(h, 8):X}"));
                    p.Add(new("PS2 icon", "Vertices", Bin.U32LE(h, 16).ToString()));
                    break;
                }
                case FormatId.SonyHd:
                {
                    foreach (var c in WalkChunks(src, littleEndianSize: true, reversed: true, sizeAt: 8))
                        p.Add(new("SCEI chunks", c.Id, $"@0x{c.Offset:X} size 0x{c.Size:X}"));
                    break;
                }
                case FormatId.RawRgb:
                {
                    var d = FormatDetector.RawRgbDims(src.Length);
                    p.Add(new("RGB", "Assumed dimensions", d is { } x ? $"{x.W} × {x.H}" : "?"));
                    break;
                }
                case FormatId.EaFontSfn:
                {
                    var f = SfnFont.Read(src.ReadAll());
                    var img = f.Atlas.Images[0].Header;
                    p.Add(new("SFN", "Magic", $"{f.Magic} (version {f.Version})"));
                    p.Add(new("SFN", "Glyphs", $"{f.Glyphs.Count} ({f.RecordSize}-byte records @0x{f.GlyphOffset:X})"));
                    p.Add(new("SFN", "Character range", f.Glyphs.Count == 0 ? "-" : $"U+{f.Glyphs.Min(g => g.Code):X4} – U+{f.Glyphs.Max(g => g.Code):X4}"));
                    p.Add(new("SFN", "Kerning pairs", f.KernOffset > 0 ? $"{f.Kerning.Count} @0x{f.KernOffset:X}" : "none"));
                    p.Add(new("SFN", "Baseline / line height", $"{f.Baseline} / {f.LineHeight}"));
                    p.Add(new("SFN", "Atlas", $"{img.Width} × {img.Height} {SshFile.TypeName(img.Type)} @0x{f.BitmapOffset:X}"));
                    if (f.Comment is { Length: > 0 }) p.Add(new("SFN", "Comment", f.Comment));
                    break;
                }
                case FormatId.LocBinary:
                {
                    var t = LocTable.Read(src.ReadAll());
                    p.Add(new("LOCH", "Version", t.Version.ToString()));
                    p.Add(new("LOCH", "Languages", t.Languages.ToString()));
                    p.Add(new("LOCH", "Strings", t.Strings.Count.ToString("N0")));
                    p.Add(new("LOCH", "String IDs", t.Strings.Count == 0 ? "-" : $"{t.Strings.Min(x => x.Id)} – {t.Strings.Max(x => x.Id)}"));
                    break;
                }
                case FormatId.AudioBankAbk:
                {
                    var all = src.ReadAll();
                    var a = AbkBank.Read(all);
                    p.Add(new("ABKC", "Modules", a.Modules.ToString()));
                    p.Add(new("ABKC", "Declared size", $"{a.DeclaredSize:N0}"));
                    p.Add(new("ABKC", "Module table", $"@0x{a.CodeOffset:X} (contains MIPS behaviour code)"));
                    p.Add(new("ABKC", "Embedded BNKl", a.ExtractBank(all) is { } bnk ? $"@0x{a.BankOffset:X}, {a.BankSize:N0} bytes, {EaBank.Read(bnk).Sounds.Count} sounds" : "none — samples stream from the matching .AST"));
                    break;
                }
                case FormatId.MoirCsi:
                {
                    var m = MoirScript.Read(src.ReadAll());
                    p.Add(new("MOIR", "Version", $"0x{m.Version:X}"));
                    p.Add(new("MOIR", "Header counts", string.Join(", ", m.Counts)));
                    p.Add(new("MOIR", "Labels", m.Labels.Count.ToString()));
                    break;
                }
                case FormatId.SpeechHeader:
                {
                    var h = SpeechHeader.Parse(src.ReadBytes(0, (int)Math.Min(src.Length, 4096)));
                    if (h is null) break;
                    p.Add(new("Speech header", "Record id", $"{h.Id} (0x{h.Id:X})"));
                    p.Add(new("Speech header", "Kind", h.Kind.ToString()));
                    p.Add(new("Speech header", "Segments", h.Segments.ToString()));
                    p.Add(new("Speech header", "Link", h.Link == -1 ? "none (-1)" : h.Link.ToString()));
                    p.Add(new("Speech header", "u16 @0x08", h.Field8.ToString()));
                    p.Add(new("Speech header", "u16 @0x0A", h.Length.ToString()));
                    if (h.SplitPoints.Length > 0) p.Add(new("Speech header", "Split points", string.Join(", ", h.SplitPoints)));
                    p.Add(new("Speech header", "Payload", $"{h.Payload.Length} bytes"));
                    break;
                }
                case FormatId.BilgSkeleton:
                {
                    var all = src.ReadAll();
                    p.Add(new("GLD", "Bone/name strings", string.Join(", ", ExtractStrings(all, 3).Select(s => s.Text).Take(40))));
                    break;
                }
                case FormatId.Csv:
                {
                    var t = ReadText(src);
                    var rows = ParseCsv(t);
                    p.Add(new("Table", "Rows", rows.Count.ToString("N0")));
                    p.Add(new("Table", "Columns", rows.Count == 0 ? "0" : rows.Max(r => r.Length).ToString()));
                    break;
                }
                case FormatId.Text or FormatId.LocText:
                {
                    var t = ReadText(src);
                    p.Add(new("Text", "Lines", t.Split('\n').Length.ToString("N0")));
                    break;
                }
            }
        }
        catch (Exception ex)
        {
            p.Add(new("Errors", "Parse failed", ex.Message));
        }

        var chunks = WalkChunks(src);
        if (chunks.Count > 1 && fmt is not (FormatId.SchlStream or FormatId.SchlContainer or FormatId.SonyHd))
            p.Add(new("Structure", "Chunk walk", $"{chunks.Count} chunks: {string.Join(" ", chunks.Take(12).Select(c => c.Id))}{(chunks.Count > 12 ? " …" : "")}"));
        return p;
    }

    public static void AddPatch(List<Prop> p, string group, EaPatchHeader h)
    {
        p.Add(new(group, "Platform", EaPatchHeader.PlatformName(h.Platform)));
        p.Add(new(group, "Codec", EaPatchHeader.CodecName(h.Codec2) + (h.Get(0xA0) is null ? " (platform default)" : "")));
        p.Add(new(group, "Channels", h.Channels.ToString()));
        p.Add(new(group, "Sample rate", $"{h.SampleRate} Hz" + (h.Get(0x84) is null ? " (default)" : "")));
        p.Add(new(group, "Samples", $"{h.Samples:N0} ({(h.SampleRate > 0 ? h.Samples / (double)h.SampleRate : 0):0.00}s)"));
        p.Add(new(group, "Version", h.Version.ToString()));
        foreach (var t in h.Tags.Where(t => t.Tag is not (0x80 or 0x82 or 0x84 or 0x85 or 0xA0)))
            p.Add(new(group, EaPatchHeader.TagName(t.Tag), $"0x{t.Value:X} ({t.Value})"));
    }

    private static bool IsRefPackAt(IByteSource src, long o) { var b = src.ReadBytes(o, 2); return b.Length == 2 && (b[0] & 0x3E) == 0x10 && b[1] == 0xFB; }

    /// <summary>Generic "4CC + u32 size" chunk walker — useful for unknown EA formats.</summary>
    public static List<ChunkInfo> WalkChunks(IByteSource src, bool littleEndianSize = true, bool reversed = false, int sizeAt = 4, int max = 4096)
    {
        var list = new List<ChunkInfo>();
        long o = 0;
        while (o + sizeAt + 4 <= src.Length && list.Count < max)
        {
            var h = src.ReadBytes(o, sizeAt + 4);
            if (!Bin.IsPrintable4CC(h)) break;
            var id = Bin.FourCC(h);
            if (reversed) id = new string(id.Reverse().ToArray());
            if (sizeAt == 8) id += "/" + new string(Bin.FourCC(h, 4).Reverse().ToArray());
            var size = littleEndianSize ? Bin.U32LE(h, sizeAt) : Bin.U32BE(h, sizeAt);
            if (size < 8 || o + size > src.Length) { list.Add(new ChunkInfo(o, id, size)); break; }
            list.Add(new ChunkInfo(o, id, size));
            o += size;
        }
        return list;
    }

    public sealed record FoundString(long Offset, string Text);

    public static List<FoundString> ExtractStrings(ReadOnlySpan<byte> d, int minLen = 4, int max = 200000)
    {
        var list = new List<FoundString>();
        var start = -1;
        for (var i = 0; i <= d.Length && list.Count < max; i++)
        {
            var ok = i < d.Length && d[i] is >= 0x20 and < 0x7F or (byte)'\t';
            if (ok) { if (start < 0) start = i; continue; }
            if (start >= 0 && i - start >= minLen) list.Add(new FoundString(start, Encoding.ASCII.GetString(d[start..i])));
            start = -1;
        }
        return list;
    }

    public static string ReadText(IByteSource src, int maxBytes = 16 * 1024 * 1024) =>
        TextEncoding.GetString(src.ReadBytes(0, (int)Math.Min(src.Length, maxBytes)));

    public static List<string[]> ParseCsv(string text)
    {
        var rows = new List<string[]>();
        var firstLine = text.Split('\n', 2)[0];
        var sep = firstLine.Count(c => c == '\t') > firstLine.Count(c => c == ',') ? '\t' : ',';
        var cur = new List<string>(); var sb = new StringBuilder(); var inQ = false;
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (inQ)
            {
                if (c == '"') { if (i + 1 < text.Length && text[i + 1] == '"') { sb.Append('"'); i++; } else inQ = false; }
                else sb.Append(c);
                continue;
            }
            if (c == '"') inQ = true;
            else if (c == sep) { cur.Add(sb.ToString()); sb.Clear(); }
            else if (c == '\n') { cur.Add(sb.ToString().TrimEnd('\r')); sb.Clear(); rows.Add([.. cur]); cur.Clear(); }
            else sb.Append(c);
        }
        if (sb.Length > 0 || cur.Count > 0) { cur.Add(sb.ToString().TrimEnd('\r')); rows.Add([.. cur]); }
        return rows;
    }
}
