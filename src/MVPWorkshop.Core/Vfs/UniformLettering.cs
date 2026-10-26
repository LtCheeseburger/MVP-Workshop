using MVPWorkshop.Core.Formats;

namespace MVPWorkshop.Core.Vfs;

/// <summary>CPU equivalents of SLUS-215.82 0x4408F0 and 0x440D90 (normal player path).</summary>
public static class UniformLettering
{
    public static string NumberText(string text)
    {
        if (!int.TryParse(text, out int number) || number is < 0 or > 99)
            throw new ArgumentException("Jersey number must be 0–99.");
        return number.ToString(System.Globalization.CultureInfo.InvariantCulture);
    }

    public static DecodedImage Number(SshFile font, string text, bool nativeAlpha = false)
    {
        text = NumberText(text);
        var zero = font.Images.Single(i => i.Tag == "zig0");
        int width = 64, height = 64;
        int cell = zero.Header.Width, glyphHeight = zero.Header.Height;
        var result = Blank(width, height, font.Decode(zero,new DecodeOptions { ScaleAlpha=!nativeAlpha }));
        int start = (width - text.Length * cell) / 2;
        for (int i = 0; i < text.Length; i++)
        {
            var glyph = font.Decode(font.Images.Single(g => g.Tag == "zig" + text[i]), new DecodeOptions { ScaleAlpha = !nativeAlpha });
            // Runtime excludes the top/bottom border, and the outside edge of the first glyph.
            int border = 1;
            Copy(glyph, result, border, 1, cell - border * 2, glyphHeight - 2, start + i * cell + border, 1);
        }
        return result;
    }

    public static DecodedImage Name(SshFile font, string text, bool nativeAlpha = false)
    {
        // strncpy(...,10), fixed cells, odd/even integer centering at 0x440EA8–0x440ECC.
        text = text[..Math.Min(10, text.Length)];
        bool lower = !font.Images.Any(i => i.Tag == "A___");
        var reference=font.Images.FirstOrDefault(i=>i.Tag==(lower?"a___":"A___"))??font.Images.Single(i=>i.Tag=="zig0");
        var result = Blank(256, 32,font.Decode(reference,new DecodeOptions { ScaleAlpha=!nativeAlpha }));
        int start = text.Length % 2 == 1 ? 72 - (text.Length - 1) * 8 : 80 - text.Length * 8;
        for (int i = 0; i < text.Length; i++)
        {
            char c = lower ? char.ToLowerInvariant(text[i]) : char.ToUpperInvariant(text[i]);
            if (c is not (>= 'A' and <= 'Z') && c is not (>= 'a' and <= 'z')) continue;
            var glyph = font.Images.FirstOrDefault(g => g.Tag == c + "___") ?? font.Images.Single(g => g.Tag == "zig0");
            Copy(font.Decode(glyph, new DecodeOptions { ScaleAlpha = !nativeAlpha }), result, 0, 0, 16, 32, start + i * 16, 0);
        }
        return result;
    }

    private static DecodedImage Blank(int w, int h, DecodedImage reference)
    {
        var pixels=new byte[w*h*4];
        for(int at=0;at<pixels.Length;at+=4)reference.Bgra.AsSpan(0,4).CopyTo(pixels.AsSpan(at,4));
        return new DecodedImage { Width=w,Height=h,Bgra=pixels };
    }

    private static void Copy(DecodedImage src, DecodedImage dst, int sx, int sy, int w, int h, int dx, int dy)
    {
        for (int y = 0; y < h; y++) for (int x = 0; x < w; x++)
        {
            if (sx+x < 0 || sx+x >= src.Width || sy+y >= src.Height || dx+x < 0 || dx+x >= dst.Width || dy+y < 0 || dy+y >= dst.Height) continue;
            src.Bgra.AsSpan(((sy+y)*src.Width+sx+x)*4,4).CopyTo(dst.Bgra.AsSpan(((dy+y)*dst.Width+dx+x)*4,4));
        }
    }

}
