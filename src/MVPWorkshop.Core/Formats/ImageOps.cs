namespace MVPWorkshop.Core.Formats;

public enum ResizeFilter { Area, Bilinear, Nearest }

public static class ImageOps
{
    /// <summary>Bilinear resize of straight-alpha BGRA; colour is weighted by alpha so transparent edges do not bleed dark fringes.</summary>
    public static byte[] Resize(byte[] bgra, int w, int h, int nw, int nh, ResizeFilter filter = ResizeFilter.Area)
    {
        if (w <= 0 || h <= 0 || nw <= 0 || nh <= 0 || (long)w * h * 4 != bgra.LongLength || (long)nw * nh > 16777216) throw new ArgumentException("Invalid or excessive resize dimensions.");
        if (filter == ResizeFilter.Nearest)
        {
            var result = new byte[checked(nw * nh * 4)];
            for (int y = 0; y < nh; y++) for (int x = 0; x < nw; x++) bgra.AsSpan(((int)((long)y * h / nh) * w + (int)((long)x * w / nw)) * 4, 4).CopyTo(result.AsSpan((y * nw + x) * 4));
            return result;
        }
        if (filter == ResizeFilter.Area && nw <= w && nh <= h && (nw != w || nh != h)) return Area(bgra, w, h, nw, nh);
        if (w == nw && h == nh) return (byte[])bgra.Clone();
        var o = new byte[nw * nh * 4];
        double sx = (double)w / nw, sy = (double)h / nh;
        for (var y = 0; y < nh; y++)
        {
            var fy = Math.Max(0, (y + 0.5) * sy - 0.5); int y0 = Math.Min(h - 1, (int)fy), y1 = Math.Min(h - 1, y0 + 1); var ty = fy - y0;
            for (var x = 0; x < nw; x++)
            {
                var fx = Math.Max(0, (x + 0.5) * sx - 0.5); int x0 = Math.Min(w - 1, (int)fx), x1 = Math.Min(w - 1, x0 + 1); var tx = fx - x0;
                double a = 0, r = 0, g = 0, b = 0;
                void Add(int px, int py, double wt)
                {
                    var i = (py * w + px) * 4; var al = bgra[i + 3] * wt;
                    a += al; b += bgra[i] * al; g += bgra[i + 1] * al; r += bgra[i + 2] * al;
                }
                Add(x0, y0, (1 - tx) * (1 - ty)); Add(x1, y0, tx * (1 - ty)); Add(x0, y1, (1 - tx) * ty); Add(x1, y1, tx * ty);
                var d = (y * nw + x) * 4;
                if (a > 0) { o[d] = (byte)Math.Round(b / a); o[d + 1] = (byte)Math.Round(g / a); o[d + 2] = (byte)Math.Round(r / a); }
                o[d + 3] = (byte)Math.Round(a);
            }
        }
        return o;
    }
    // Exact box coverage in premultiplied alpha; all source pixels contribute when reducing an upscale.
    private static byte[] Area(byte[] pixels, int w, int h, int nw, int nh)
    {
        var output = new byte[checked(nw * nh * 4)]; double sx = (double)w / nw, sy = (double)h / nh;
        for (int y = 0; y < nh; y++) for (int x = 0; x < nw; x++)
        {
            double x0 = x * sx, x1 = (x + 1) * sx, y0 = y * sy, y1 = (y + 1) * sy;
            double a = 0, b = 0, g = 0, r = 0;
            for (int py = (int)y0; py < Math.Min(h, (int)Math.Ceiling(y1)); py++)
            for (int px = (int)x0; px < Math.Min(w, (int)Math.Ceiling(x1)); px++)
            {
                double weight = (Math.Min(x1, px + 1) - Math.Max(x0, px)) * (Math.Min(y1, py + 1) - Math.Max(y0, py));
                int at = (py * w + px) * 4; double alpha = pixels[at + 3] * weight;
                a += alpha; b += pixels[at] * alpha; g += pixels[at + 1] * alpha; r += pixels[at + 2] * alpha;
            }
            int target = (y * nw + x) * 4;
            if (a > 0) { output[target] = (byte)Math.Clamp(Math.Round(b / a), 0, 255); output[target + 1] = (byte)Math.Clamp(Math.Round(g / a), 0, 255); output[target + 2] = (byte)Math.Clamp(Math.Round(r / a), 0, 255); }
            output[target + 3] = (byte)Math.Clamp(Math.Round(a / (sx * sy)), 0, 255);
        }
        return output;
    }

}
