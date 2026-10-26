using MVPWorkshop.Core.IO;

namespace MVPWorkshop.Core.Formats;

public sealed class SshBlockHeader
{
    public int Offset { get; init; }
    public byte Type { get; init; }
    public int BlockSize { get; init; }
    public int Width { get; init; }
    public int Height { get; init; }
    public short CenterX { get; init; }
    public short CenterY { get; init; }
    public int RawX { get; init; }
    public int RawY { get; init; }
    public int PosX => RawX & 0x0FFF;
    public int PosY => RawY & 0x0FFF;
    public bool FlagReferenced => (RawX & 0x1000) != 0;
    public bool FlagSwizzled => (RawX & 0x2000) != 0;
    public bool FlagTransposed => (RawX & 0x4000) != 0;
    public int Mipmaps => RawY >> 12;

    public static SshBlockHeader Read(ReadOnlySpan<byte> d, int o) => new()
    {
        Offset = o, Type = d[o], BlockSize = (int)Bin.U24LE(d, o + 1),
        Width = Bin.U16LE(d, o + 4), Height = Bin.U16LE(d, o + 6),
        CenterX = Bin.S16LE(d, o + 8), CenterY = Bin.S16LE(d, o + 10),
        RawX = Bin.U16LE(d, o + 12), RawY = Bin.U16LE(d, o + 14),
    };
}

public sealed class SshAttachment
{
    public required SshBlockHeader Header { get; init; }
    public int DataOffset { get; init; }
    public int DataLength { get; init; }
    public string Kind => SshFile.AttachmentName(Header.Type);
}

public sealed class SshImage
{
    public int Index { get; init; }
    public string Tag { get; init; } = "";
    public int DirOffset { get; init; }
    public required SshBlockHeader Header { get; init; }
    public int DataOffset { get; init; }
    public int DataLength { get; init; }
    public List<SshAttachment> Attachments { get; } = [];
    public string? Name { get; set; }
    public string? Comment { get; set; }
    public SshAttachment? Palette => Attachments.FirstOrDefault(a => a.Header.Type is 0x21 or 0x22 or 0x23 or 0x24 or 0x29 or 0x2A or 0x2D);
    public int TypeMasked => Header.Type & 0x7F;
    public string DisplayName => string.IsNullOrEmpty(Name) || Name == Tag ? Tag : $"{Tag} · {Name}";
}

public sealed class DecodeOptions
{
    public bool? Swizzle { get; set; }
    public bool? PaletteSwizzle { get; set; }
    public bool ScaleAlpha { get; set; } = true;
}

public sealed class DecodedImage
{
    public required int Width { get; init; }
    public required int Height { get; init; }
    public required byte[] Bgra { get; init; }
    public byte[]? PaletteBgra { get; init; }
    public string Notes { get; init; } = "";
}

/// <summary>EA SHPS (PS2 shape/FSH) file: "SHPS", u32 LE size, u32 LE count, 4-char version, then directory of (tag[4], u32 LE offset).</summary>
public sealed class SshFile
{
    public string Magic { get; private init; } = "";
    public uint DeclaredSize { get; private init; }
    public string Version { get; private init; } = "";
    public string? GroupName { get; private set; }
    public List<SshImage> Images { get; } = [];
    public byte[] Data { get; private init; } = [];
    /// <summary>True for Xbox SHPX packs (<c>.xsh</c>), whose pixel types are 0x60+ and whose swizzle flag means Xbox Morton order.</summary>
    public bool IsXbox => Magic == "SHPX";

    public static string TypeName(int t) => (t & 0x7F) switch
    {
        0x01 => "PAL4 (4bpp indexed)", 0x02 => "PAL8 (8bpp indexed)", 0x03 => "RGBA5551", 0x04 => "RGB888", 0x05 => "RGBA8888",
        0x60 => "DXT1 (Xbox)", 0x61 => "DXT3 (Xbox)", 0x62 => "DXT5 (Xbox)", 0x78 => "RGB565 (Xbox 0x78)", 0x7B => "PAL8 (Xbox)",
        0x64 => "A8 (Xbox)", 0x7D => "BGRA8888 (Xbox)", 0x7E => "ARGB1555 (Xbox)", 0x7F => "RGB565 (Xbox)",
        0x08 => "GST121", 0x09 => "GST221", 0x0A => "GST421", 0x0B => "GST821",
        0x0C => "GST122", 0x0D => "GST222", 0x0E => "GST422", 0x0F => "GST822",
        var x => $"Unknown 0x{x:X2}"
    } + ((t & 0x80) != 0 ? " +RefPack" : "");

    public static string AttachmentName(int t) => t switch
    {
        0x21 => "Palette RGBA8888", 0x22 => "Palette RGBX6666", 0x23 => "Palette RGBA5551", 0x24 => "Palette RGB888",
        0x29 => "Palette (0x29)", 0x2A => "Palette (0x2A)", 0x2D => "Palette (0x2D)",
        0x69 => "Metal bin", 0x6F => "Comment", 0x70 => "Image name", 0x7C => "Hotspots", _ => $"Block 0x{t:X2}"
    };

    public static int Bpp(int t) => (t & 0x7F) switch
    {
        0x01 => 4, 0x02 => 8, 0x03 => 16, 0x04 => 24, 0x05 => 32, >= 0x08 and <= 0x0F => 8, 0x64 or 0x7B => 8, 0x7D => 32, 0x7E or 0x7F or 0x78 => 16, 0x60 => 4, 0x61 or 0x62 => 8, _ => 0
    };

    public static SshFile Read(byte[] d)
    {
        var magic = Bin.FourCC(d);
        if (magic is not ("SHPS" or "ShpS" or "SHPX")) throw new InvalidDataException("Not an SHPS file.");
        var count = (int)Bin.U32LE(d, 8);
        if (count is < 0 or > 100000) throw new InvalidDataException("Bad SSH image count.");
        var f = new SshFile { Magic = magic, DeclaredSize = Bin.U32LE(d, 4), Version = Bin.FourCC(d, 12), Data = d };

        var dir = new List<(string Tag, int Off, int DirOff)>();
        for (var i = 0; i < count; i++)
        {
            var p = 16 + i * 8;
            dir.Add((Bin.CString(d, p, 4), (int)Bin.U32LE(d, p + 4), p));
        }
        var dirEnd = 16 + count * 8;
        if (Bin.IsPrintable4CC(d, dirEnd))
            f.GroupName = Bin.CString(d, dirEnd, 32).Trim();

        var sorted = dir.Select(x => x.Off).Where(o => o > 0 && o < d.Length).Distinct().OrderBy(x => x).ToList();
        for (var i = 0; i < dir.Count; i++)
        {
            var (tag, off, dOff) = dir[i];
            if (off <= 0 || off + 16 > d.Length) continue;
            var next = sorted.FirstOrDefault(o => o > off);
            var end = next > 0 ? next : d.Length;
            var hdr = SshBlockHeader.Read(d, off);
            var dataLen = (hdr.BlockSize > 16 ? hdr.BlockSize : end - off) - 16;
            var img = new SshImage { Index = i, Tag = tag, DirOffset = dOff, Header = hdr, DataOffset = off + 16, DataLength = Math.Max(0, Math.Min(dataLen, d.Length - off - 16)) };

            // attachment chain
            var pos = off;
            var bs = hdr.BlockSize;
            for (var guard = 0; bs > 0 && guard < 64; guard++)
            {
                pos += bs;
                if (pos + 4 > end) break;
                var ah = SshBlockHeader.Read(d, Math.Min(pos, d.Length - 16));
                var isText = ah.Type is 0x6F or 0x70;
                var hdrLen = isText ? 4 : 16;
                var aEnd = ah.BlockSize > 0 ? pos + ah.BlockSize : end;
                var att = new SshAttachment { Header = ah, DataOffset = pos + hdrLen, DataLength = Math.Max(0, Math.Min(aEnd, d.Length) - pos - hdrLen) };
                img.Attachments.Add(att);
                if (ah.Type == 0x70) img.Name = Bin.CString(d, att.DataOffset, att.DataLength);
                if (ah.Type == 0x6F) img.Comment = Bin.CString(d, att.DataOffset, att.DataLength);
                bs = ah.BlockSize;
            }
            f.Images.Add(img);
        }
        return f;
    }

    public DecodedImage Decode(SshImage img, DecodeOptions? opt = null)
    {
        opt ??= new DecodeOptions();
        var h = img.Header;
        int w = h.Width, ht = h.Height, type = h.Type;
        if (w <= 0 || ht <= 0 || w > 4096 || ht > 4096) throw new InvalidDataException($"Bad dimensions {w}x{ht}.");
        ReadOnlySpan<byte> data = Data.AsSpan(img.DataOffset, img.DataLength);
        var notes = new List<string>();

        byte[]? unpacked = null;
        if ((type & 0x80) != 0 && RefPack.IsCompressed(data)) { unpacked = RefPack.Decompress(data); data = unpacked; notes.Add("RefPack image data"); }

        var t = type & 0x7F;
        var swz = opt.Swizzle ?? h.FlagSwizzled;
        if (IsXbox) return DecodeXbox(img, data, t, swz, opt, notes);
        var outp = new byte[w * ht * 4];
        byte[]? palBgra = null;

        if (t is 0x01 or 0x02 or >= 0x08 and <= 0x0F)
        {
            byte[] idx;
            if (t == 0x01)
            {
                var raw = swz ? Ps2Swizzle.Unswizzle4(data, w, ht) : data.ToArray();
                idx = new byte[w * ht];
                for (var i = 0; i < idx.Length; i++)
                {
                    var b = i / 2 < raw.Length ? raw[i / 2] : (byte)0;
                    idx[i] = (byte)((i & 1) == 0 ? b & 0x0F : b >> 4);
                }
            }
            else if (t == 0x02)
                idx = swz ? Ps2Swizzle.Unswizzle8(data, w, ht) : Pad(data, w * ht);
            else
                idx = Gst.Decode(data, w, ht, t, swz);

            palBgra = ReadPalette(img, opt, notes, t == 0x01 ? 16 : 256);
            for (var i = 0; i < w * ht; i++)
            {
                var c = idx[i] * 4;
                if (c + 3 < palBgra.Length) Buffer.BlockCopy(palBgra, c, outp, i * 4, 4);
            }
        }
        else if (t == 0x03)
        {
            var raw = swz ? Ps2Swizzle.Unswizzle16(data, w, ht) : Pad(data, w * ht * 2);
            for (var i = 0; i < w * ht; i++)
            {
                var v = raw[i * 2] | raw[i * 2 + 1] << 8;
                outp[i * 4 + 2] = Expand5(v & 0x1F); outp[i * 4 + 1] = Expand5(v >> 5 & 0x1F); outp[i * 4 + 0] = Expand5(v >> 10 & 0x1F);
                outp[i * 4 + 3] = (byte)((v & 0x8000) != 0 ? 255 : 0);
            }
        }
        else if (t == 0x04)
        {
            var raw = Pad(data, w * ht * 3);
            for (var i = 0; i < w * ht; i++)
            { outp[i * 4 + 2] = raw[i * 3]; outp[i * 4 + 1] = raw[i * 3 + 1]; outp[i * 4] = raw[i * 3 + 2]; outp[i * 4 + 3] = 255; }
        }
        else if (t == 0x05)
        {
            var raw = Pad(data, w * ht * 4);
            for (var i = 0; i < w * ht; i++)
            {
                outp[i * 4 + 2] = raw[i * 4]; outp[i * 4 + 1] = raw[i * 4 + 1]; outp[i * 4] = raw[i * 4 + 2];
                outp[i * 4 + 3] = opt.ScaleAlpha ? ScaleA(raw[i * 4 + 3]) : raw[i * 4 + 3];
            }
        }
        else throw new NotSupportedException($"SSH image type {TypeName(type)} is not decoded yet.");

        if (swz) notes.Add("PS2 un-swizzled");
        return new DecodedImage { Width = w, Height = ht, Bgra = outp, PaletteBgra = palBgra, Notes = string.Join(", ", notes) };
    }

    private DecodedImage DecodeXbox(SshImage img, ReadOnlySpan<byte> data, int t, bool swz, DecodeOptions opt, List<string> notes)
    {
        int w = img.Header.Width, ht = img.Header.Height;
        var outp = new byte[w * ht * 4];
        byte[]? palBgra = null;
        switch (t)
        {
            case 0x60 or 0x61 or 0x62:
                outp = XboxTexture.DecodeDxt(data, w, ht, t == 0x60 ? 1 : t == 0x61 ? 3 : 5);
                break;
            case 0x7B:
            {
                var idx = swz ? XboxTexture.Unswizzle(data, w, ht, 1) : Pad(data, w * ht);
                palBgra = ReadPalette(img, opt, notes, 256);
                for (var i = 0; i < w * ht; i++)
                {
                    var c = idx[i] * 4;
                    if (c + 3 < palBgra.Length) Buffer.BlockCopy(palBgra, c, outp, i * 4, 4);
                }
                break;
            }
            case 0x64:
            {
                var raw = swz ? XboxTexture.Unswizzle(data, w, ht, 1) : Pad(data, w * ht);
                for (var i = 0; i < w * ht; i++) { outp[i * 4] = outp[i * 4 + 1] = outp[i * 4 + 2] = 255; outp[i * 4 + 3] = raw[i]; }
                break;
            }
            case 0x7D:
            {
                var raw = swz ? XboxTexture.Unswizzle(data, w, ht, 4) : Pad(data, w * ht * 4);
                Buffer.BlockCopy(raw, 0, outp, 0, outp.Length);
                break;
            }
            case 0x7E or 0x7F or 0x78:
            {
                var raw = swz ? XboxTexture.Unswizzle(data, w, ht, 2) : Pad(data, w * ht * 2);
                for (var i = 0; i < w * ht; i++)
                {
                    var v = raw[i * 2] | raw[i * 2 + 1] << 8;
                    if (t == 0x7E)
                    {
                        outp[i * 4 + 2] = Expand5(v >> 10 & 0x1F); outp[i * 4 + 1] = Expand5(v >> 5 & 0x1F); outp[i * 4] = Expand5(v & 0x1F);
                        outp[i * 4 + 3] = (byte)((v & 0x8000) != 0 ? 255 : 0);
                    }
                    else
                    {
                        int r = v >> 11 & 0x1F, g = v >> 5 & 0x3F, b = v & 0x1F;
                        outp[i * 4 + 2] = Expand5(r); outp[i * 4 + 1] = (byte)(g << 2 | g >> 4); outp[i * 4] = Expand5(b); outp[i * 4 + 3] = 255;
                    }
                }
                break;
            }
            default: throw new NotSupportedException($"SSH image type {TypeName(t)} is not decoded yet.");
        }
        if (swz && t is 0x64 or 0x7B or 0x7D or 0x7E or 0x7F or 0x78) notes.Add("Xbox un-swizzled");
        return new DecodedImage { Width = w, Height = ht, Bgra = outp, PaletteBgra = palBgra, Notes = string.Join(", ", notes) };
    }

    private byte[] ReadPalette(SshImage img, DecodeOptions opt, List<string> notes, int fallbackCount)
    {
        var p = img.Palette;
        if (p is null)
        {
            notes.Add("no palette — greyscale ramp");
            var g = new byte[fallbackCount * 4];
            for (var i = 0; i < fallbackCount; i++) { var v = (byte)(i * 255 / (fallbackCount - 1)); g[i * 4] = g[i * 4 + 1] = g[i * 4 + 2] = v; g[i * 4 + 3] = 255; }
            return g;
        }
        var count = Math.Max(1, p.Header.Width * Math.Max(1, p.Header.Height));
        var span = Data.AsSpan(p.DataOffset, Math.Max(0, Math.Min(p.DataLength, Data.Length - p.DataOffset)));
        var bpe = p.Header.Type switch { 0x23 => 2, 0x24 => 3, _ => 4 };
        count = Math.Min(count, span.Length / bpe);
        var raw = span[..(count * bpe)].ToArray();
        var palSwz = opt.PaletteSwizzle ?? p.Header.FlagSwizzled;
        if (palSwz && bpe is 2 or 4 && count >= 32) { raw = Ps2Swizzle.UnswizzlePalette(raw, bpe); notes.Add("palette CSM-unswizzled"); }

        var res = new byte[Math.Max(count, fallbackCount) * 4];
        for (var i = 0; i < count; i++)
        {
            switch (bpe)
            {
                case 4 when IsXbox:
                    Buffer.BlockCopy(raw, i * 4, res, i * 4, 4);
                    break;
                case 4:
                    res[i * 4 + 2] = raw[i * 4]; res[i * 4 + 1] = raw[i * 4 + 1]; res[i * 4] = raw[i * 4 + 2];
                    res[i * 4 + 3] = opt.ScaleAlpha ? ScaleA(raw[i * 4 + 3]) : raw[i * 4 + 3];
                    break;
                case 3:
                    res[i * 4 + 2] = raw[i * 3]; res[i * 4 + 1] = raw[i * 3 + 1]; res[i * 4] = raw[i * 3 + 2]; res[i * 4 + 3] = 255;
                    break;
                default:
                    var v = raw[i * 2] | raw[i * 2 + 1] << 8;
                    res[i * 4 + 2] = Expand5(v & 0x1F); res[i * 4 + 1] = Expand5(v >> 5 & 0x1F); res[i * 4] = Expand5(v >> 10 & 0x1F);
                    res[i * 4 + 3] = (byte)((v & 0x8000) != 0 ? 255 : 0);
                    break;
            }
        }
        return res;
    }

    // PS2 GS alpha is 0..0x80 (0x80 = opaque).
    private static byte ScaleA(byte a) => a >= 0x80 ? (byte)255 : (byte)(a * 2);
    private static byte Expand5(int v) => (byte)(v << 3 | v >> 2);
    private static byte[] Pad(ReadOnlySpan<byte> d, int n) { var r = new byte[n]; d[..Math.Min(n, d.Length)].CopyTo(r); return r; }
}
