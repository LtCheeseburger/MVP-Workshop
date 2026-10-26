namespace MVPWorkshop.Core.Formats;

public static partial class Gst
{
    public static void ValidateLayout(int w, int h, int type, bool swizzled)
    {
        var (bw, bh, bits) = Params(type);
        if (w <= 0 || h <= 0 || w % 8 != 0 || h % bh != 0) throw new NotSupportedException("GST requires complete blocks.");
        if (!swizzled) return;
        _ = Scatter(new byte[BaseSize(w,h,type)], 8, i => BaseOffset(i % (w / bw), i / (w / bw), w / bw, bw, bh));
        _ = Scatter(new byte[DetailSize(w,h,type)], bits, i =>
        {
            var (x2,y2,z2,z1) = Detail(i % w,i / w);
            return bits == 1 ? 16 * (y2 * (w / 4) + x2) + 4 * z2 + z1 : 32 * (y2 * w / 4 + x2) + 8 * z2 + 2 * z1;
        });
    }

    // Fit each block to a consecutive palette window. Palette order matters in GST.
    public static byte[] Encode(byte[] bgra, byte[] palette, int colors, int w, int h, int type, bool swizzled)
    {
        var (bw, bh, bits) = Params(type);
        colors = Math.Min(colors, palette.Length / 4);
        if (colors < 1 || bgra.Length != checked(w * h * 4)) throw new ArgumentException("Invalid GST pixels or palette.");
        if (w % Math.Max(8, bw) != 0 || h % bh != 0)
            throw new NotSupportedException("GST requires complete base blocks and detail rows (width multiple of 8, height multiple of 2).");
        var bases = new byte[BaseSize(w, h, type)];
        var detail = new byte[DetailSize(w, h, type)];
        var mask = (1 << bits) - 1;
        for (var by = 0; by < h; by += bh)
            for (var bx = 0; bx < w; bx += bw)
            {
                long best = long.MaxValue; var selected = 0;
                for (var b = 0; b < colors; b++)
                {
                    long error = 0;
                    for (var y = by; y < by + bh; y++)
                        for (var x = bx; x < bx + bw; x++)
                            error += Closest((y * w + x) * 4, b, out _);
                    if (error < best) { best = error; selected = b; }
                }
                bases[(by / bh) * (w / bw) + bx / bw] = (byte)selected;
                for (var y = by; y < by + bh; y++)
                    for (var x = bx; x < bx + bw; x++)
                    {
                        Closest((y * w + x) * 4, selected, out var d);
                        var bit = (y * w + x) * bits;
                        detail[bit / 8] |= (byte)(d << (bit % 8));
                    }
            }
        if (swizzled)
        {
            bases = Scatter(bases, 8, i => BaseOffset(i % (w / bw), i / (w / bw), w / bw, bw, bh));
            detail = Scatter(detail, bits, i =>
            {
                var (x2, y2, z2, z1) = Detail(i % w, i / w);
                return bits == 1 ? 16 * (y2 * (w / 4) + x2) + 4 * z2 + z1
                    : 32 * (y2 * w / 4 + x2) + 8 * z2 + 2 * z1;
            });
        }
        var output = new byte[TotalSize(w, h, type)];
        bases.CopyTo(output, 0); detail.CopyTo(output, (bases.Length + 15) & ~15);
        return output;

        long Closest(int p, int b, out int offset)
        {
            long error = long.MaxValue; offset = 0;
            for (var d = 0; d <= mask && b + d < colors; d++)
            {
                long e = 0;
                for (var c = 0; c < 4; c++) { var diff = bgra[p + c] - palette[(b + d) * 4 + c]; e += diff * diff; }
                if (e < error) { error = e; offset = d; }
            }
            return error;
        }
    }

    private static byte[] Scatter(byte[] source, int bits, Func<int, int> address)
    {
        var output = new byte[source.Length]; var seen = new bool[source.Length * 8 / bits];
        for (var i = 0; i < seen.Length; i++)
        {
            var bit = address(i);
            if (bit < 0 || bit % bits != 0 || bit / bits >= seen.Length || seen[bit / bits])
                throw new NotSupportedException("GST swizzle is not a bijection at these dimensions; native writing blocked.");
            seen[bit / bits] = true;
            var value = (source[i * bits / 8] >> (i * bits % 8)) & ((1 << bits) - 1);
            output[bit / 8] |= (byte)(value << (bit % 8));
        }
        return output;
    }
}
