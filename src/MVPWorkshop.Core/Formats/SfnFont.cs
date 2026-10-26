using MVPWorkshop.Core.IO;

namespace MVPWorkshop.Core.Formats;

public sealed record SfnGlyph(int Code, int Width, int Height, int X, int Y, int Advance, int OffsetX, int OffsetY, int Extra);

public sealed record SfnKern(int Left, int Right, int Amount);

/// <summary>
/// EA bitmap font ("FNTS" / "FntS"; Xbox "FntX" / "FNTX" share the layout with an Xbox texture block). Header 0x80 bytes, glyph table, kerning pairs, then one SSH image block
/// (PAL4/PAL8 atlas) followed by its 0x21 palette — the same block layout as inside an SHPS file.
/// </summary>
public sealed class SfnFont
{
    public string Magic { get; private init; } = "";
    public int Version { get; private init; }
    public int RecordSize { get; private init; }
    /// <summary>Header bytes 0x12/0x13 — baseline and line height in FntS fonts (meaning differs in FNTS).</summary>
    public int Baseline { get; private init; }
    public int LineHeight { get; private init; }
    public int GlyphOffset { get; private init; }
    public int KernOffset { get; private init; }
    public int BitmapOffset { get; private init; }
    public List<SfnGlyph> Glyphs { get; } = [];
    public List<SfnKern> Kerning { get; } = [];
    public SshFile Atlas { get; private init; } = null!;
    public string? Comment { get; private set; }

    public static SfnFont Read(byte[] d)
    {
        var magic = Bin.FourCC(d);
        if (magic is not ("FNTS" or "FntS" or "FNTX" or "FntX")) throw new InvalidDataException("Not an SFN font.");
        var count = Bin.U16LE(d, 0x0A);
        var extra = Bin.U16LE(d, 0x0E);
        var glyphs = (int)Bin.U32LE(d, 0x14);
        var kern = (int)Bin.U32LE(d, 0x18);
        var bitmap = (int)Bin.U32LE(d, 0x1C);
        if (glyphs <= 0 || bitmap <= 0 || bitmap + 16 > d.Length || extra > 16) throw new InvalidDataException("Bad SFN table offsets.");

        var xbox = magic is "FNTX" or "FntX";
        var rs = 12 + extra;
        var wide = extra >= 4;
        var f = new SfnFont
        {
            Magic = magic, Version = Bin.U16LE(d, 8), RecordSize = rs, Baseline = d[0x12], LineHeight = d[0x13],
            GlyphOffset = glyphs, KernOffset = kern, BitmapOffset = bitmap, Atlas = WrapAtlas(d, bitmap, xbox),
        };
        for (var i = 0; i < count; i++)
        {
            var o = glyphs + i * rs;
            if (o + rs > d.Length) break;
            // 12-byte records: +8 u8 advance, +9 s8 x offset, +10 s8 y offset (from the top of the line), +11 u8 ?
            // 16-byte records: +8 u8 ?, +9 s8 x offset, +10 s8 y offset, +11 u8 ?, +12 u16 ?, +14 u16 advance
            f.Glyphs.Add(new SfnGlyph(Bin.U16LE(d, o), d[o + 2], d[o + 3], Bin.U16LE(d, o + 4), Bin.U16LE(d, o + 6),
                wide ? Bin.U16LE(d, o + 14) : d[o + 8], (sbyte)d[o + 9], (sbyte)d[o + 10], wide ? Bin.U16LE(d, o + 12) : d[o + 11]));
        }
        // Xbox fonts keep a big-endian code lookup table at the kerning offset instead of kerning pairs.
        if (!xbox && kern > 0 && kern + 4 <= d.Length)
        {
            // u32 count, then { u16 left, s8 amount, u8 right }
            var n = (int)Bin.U32LE(d, kern);
            for (var i = 0; i < n && kern + 8 + i * 4 <= Math.Min(d.Length, bitmap); i++)
            {
                var o = kern + 4 + i * 4;
                f.Kerning.Add(new SfnKern(Bin.U16LE(d, o), d[o + 3], (sbyte)d[o + 2]));
            }
        }
        var tail = !xbox && kern > 0 ? kern + 4 + f.Kerning.Count * 4 : glyphs + count * rs;
        if (tail < bitmap && Bin.IsPrintable4CC(d, tail)) f.Comment = Bin.CString(d, tail, bitmap - tail).Trim();
        return f;
    }

    /// <summary>Wrap the embedded image block (and its attachments) in a one-image SHPS so the SSH decoder handles it.</summary>
    private static SshFile WrapAtlas(byte[] d, int at, bool xbox = false)
    {
        var body = d.AsSpan(at).ToArray();
        var ssh = new byte[0x18 + body.Length];
        (xbox ? "SHPX"u8 : "SHPS"u8).CopyTo(ssh);
        BitConverter.GetBytes(ssh.Length).CopyTo(ssh, 4);
        BitConverter.GetBytes(1).CopyTo(ssh, 8);
        "G359"u8.CopyTo(ssh.AsSpan(12));
        "font"u8.CopyTo(ssh.AsSpan(16));
        BitConverter.GetBytes(0x18).CopyTo(ssh, 20);
        body.CopyTo(ssh, 0x18);
        return SshFile.Read(ssh);
    }

    public SfnGlyph? Find(int code) => Glyphs.FirstOrDefault(g => g.Code == code);

    public int KernBetween(int left, int right) => Kerning.FirstOrDefault(k => k.Left == left && k.Right == right)?.Amount ?? 0;

    /// <summary>Render text with the atlas (BGRA). Uses the glyph advances, offsets and kerning pairs.</summary>
    public DecodedImage Render(DecodedImage atlas, string text, int scale = 1)
    {
        var lines = text.Replace("\r", "").Split('\n');
        var lineH = Math.Max(1, Glyphs.Count == 0 ? 16 : Glyphs.Max(g => g.OffsetY + g.Height));
        var widths = lines.Select(MeasureLine).ToArray();
        int w = Math.Max(1, widths.DefaultIfEmpty(1).Max() + 4), h = Math.Max(1, lines.Length * (lineH + 2) + 4);
        var px = new byte[w * h * 4];
        for (var li = 0; li < lines.Length; li++)
        {
            var x = 2; var y0 = 2 + li * (lineH + 2);
            var prev = -1;
            foreach (var ch in lines[li])
            {
                var g = Find(ch) ?? Find(char.ToUpperInvariant(ch)) ?? Find('?');
                if (g is null) continue;
                if (prev >= 0) x += KernBetween(prev, ch);
                Blit(atlas, g, px, w, h, x + g.OffsetX, y0 + g.OffsetY);
                x += g.Advance;
                prev = ch;
            }
        }
        if (scale <= 1) return new DecodedImage { Width = w, Height = h, Bgra = px };
        var big = new byte[w * scale * h * scale * 4];
        for (var y = 0; y < h * scale; y++)
            for (var x = 0; x < w * scale; x++)
                Buffer.BlockCopy(px, ((y / scale) * w + x / scale) * 4, big, (y * w * scale + x) * 4, 4);
        return new DecodedImage { Width = w * scale, Height = h * scale, Bgra = big };
    }

    private int MeasureLine(string s)
    {
        var x = 0; var prev = -1;
        foreach (var ch in s)
        {
            var g = Find(ch) ?? Find(char.ToUpperInvariant(ch)) ?? Find('?');
            if (g is null) continue;
            if (prev >= 0) x += KernBetween(prev, ch);
            x += Math.Max(g.Advance, g.OffsetX + g.Width);
            prev = ch;
        }
        return x;
    }

    private static void Blit(DecodedImage atlas, SfnGlyph g, byte[] dst, int dw, int dh, int dx, int dy)
    {
        for (var y = 0; y < g.Height; y++)
            for (var x = 0; x < g.Width; x++)
            {
                int sx = g.X + x, sy = g.Y + y, tx = dx + x, ty = dy + y;
                if (sx >= atlas.Width || sy >= atlas.Height || tx < 0 || ty < 0 || tx >= dw || ty >= dh) continue;
                var s = (sy * atlas.Width + sx) * 4; var t = (ty * dw + tx) * 4;
                var a = atlas.Bgra[s + 3];
                if (a <= dst[t + 3]) continue;
                dst[t] = atlas.Bgra[s]; dst[t + 1] = atlas.Bgra[s + 1]; dst[t + 2] = atlas.Bgra[s + 2]; dst[t + 3] = a;
            }
    }
}
