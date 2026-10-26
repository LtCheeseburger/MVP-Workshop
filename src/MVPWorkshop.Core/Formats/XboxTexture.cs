namespace MVPWorkshop.Core.Formats;

/// <summary>Xbox (NV2A) texture storage as used by SHPX (<c>.xsh</c>) files: Morton swizzling and DXT1/3/5 block compression.</summary>
public static class XboxTexture
{
    public static bool IsPowerOfTwo(int v) => v > 0 && (v & (v - 1)) == 0;

    /// <summary>Morton (Z-order) index for a pixel. Low bits alternate x,y; the longer axis's remaining bits follow linearly.</summary>
    public static int MortonIndex(int x, int y, int wBits, int hBits)
    {
        int idx = 0, shift = 0;
        for (var i = 0; i < Math.Max(wBits, hBits); i++)
        {
            if (i < wBits) idx |= (x >> i & 1) << shift++;
            if (i < hBits) idx |= (y >> i & 1) << shift++;
        }
        return idx;
    }

    /// <summary>Convert Morton-ordered pixels of <paramref name="bytesPerPixel"/> bytes to row-major order.</summary>
    public static byte[] Unswizzle(ReadOnlySpan<byte> src, int w, int h, int bytesPerPixel)
    {
        var dst = new byte[w * h * bytesPerPixel];
        if (!IsPowerOfTwo(w) || !IsPowerOfTwo(h)) { src[..Math.Min(src.Length, dst.Length)].CopyTo(dst); return dst; }
        int wb = System.Numerics.BitOperations.Log2((uint)w), hb = System.Numerics.BitOperations.Log2((uint)h);
        for (var y = 0; y < h; y++)
            for (var x = 0; x < w; x++)
            {
                var s = MortonIndex(x, y, wb, hb) * bytesPerPixel;
                if (s + bytesPerPixel > src.Length) continue;
                src.Slice(s, bytesPerPixel).CopyTo(dst.AsSpan((y * w + x) * bytesPerPixel, bytesPerPixel));
            }
        return dst;
    }

    /// <summary>Inverse of <see cref="Unswizzle"/>.</summary>
    public static byte[] Swizzle(ReadOnlySpan<byte> src, int w, int h, int bytesPerPixel)
    {
        var dst = new byte[w * h * bytesPerPixel];
        if (!IsPowerOfTwo(w) || !IsPowerOfTwo(h)) { src[..Math.Min(src.Length, dst.Length)].CopyTo(dst); return dst; }
        int wb = System.Numerics.BitOperations.Log2((uint)w), hb = System.Numerics.BitOperations.Log2((uint)h);
        for (var y = 0; y < h; y++)
            for (var x = 0; x < w; x++)
            {
                var d = MortonIndex(x, y, wb, hb) * bytesPerPixel;
                var s = (y * w + x) * bytesPerPixel;
                if (s + bytesPerPixel > src.Length) continue;
                src.Slice(s, bytesPerPixel).CopyTo(dst.AsSpan(d, bytesPerPixel));
            }
        return dst;
    }

    /// <summary>Bytes of one DXT level; <paramref name="dxt"/> is 1, 3 or 5.</summary>
    public static int DxtSize(int w, int h, int dxt) => (w + 3) / 4 * ((h + 3) / 4) * (dxt == 1 ? 8 : 16);

    /// <summary>Decode DXT1/3/5 to BGRA.</summary>
    public static byte[] DecodeDxt(ReadOnlySpan<byte> src, int w, int h, int dxt)
    {
        var dst = new byte[w * h * 4];
        var bw = (w + 3) / 4; var bh = (h + 3) / 4;
        var blockBytes = dxt == 1 ? 8 : 16;
        Span<byte> pal = stackalloc byte[16];
        Span<byte> alpha = stackalloc byte[16];
        Span<byte> ramp = stackalloc byte[8];
        for (var by = 0; by < bh; by++)
            for (var bx = 0; bx < bw; bx++)
            {
                var o = (by * bw + bx) * blockBytes;
                if (o + blockBytes > src.Length) return dst;
                var c = src.Slice(o + (dxt == 1 ? 0 : 8), 8);
                int c0 = c[0] | c[1] << 8, c1 = c[2] | c[3] << 8;
                Rgb565(c0, pal[..4]); Rgb565(c1, pal.Slice(4, 4));
                var punch = dxt == 1 && c0 <= c1;
                for (var k = 0; k < 3; k++)
                {
                    int a = pal[k], b = pal[4 + k];
                    if (punch) { pal[8 + k] = (byte)((a + b) / 2); pal[12 + k] = 0; }
                    else { pal[8 + k] = (byte)((2 * a + b) / 3); pal[12 + k] = (byte)((a + 2 * b) / 3); }
                }
                pal[11] = 255; pal[15] = (byte)(punch ? 0 : 255);
                if (dxt == 3)
                    for (var i = 0; i < 16; i++) alpha[i] = (byte)((src[o + i / 2] >> (i % 2 * 4) & 0xF) * 17);
                else if (dxt == 5)
                {
                    int a0 = src[o], a1 = src[o + 1];
                    ramp[0] = (byte)a0; ramp[1] = (byte)a1;
                    if (a0 > a1) for (var k = 1; k < 7; k++) ramp[k + 1] = (byte)(((7 - k) * a0 + k * a1) / 7);
                    else { for (var k = 1; k < 5; k++) ramp[k + 1] = (byte)(((5 - k) * a0 + k * a1) / 5); ramp[6] = 0; ramp[7] = 255; }
                    ulong bits = 0;
                    for (var k = 0; k < 6; k++) bits |= (ulong)src[o + 2 + k] << (8 * k);
                    for (var i = 0; i < 16; i++) alpha[i] = ramp[(int)(bits >> (3 * i) & 7)];
                }
                var sel = (uint)(c[4] | c[5] << 8 | c[6] << 16 | c[7] << 24);
                for (var i = 0; i < 16; i++)
                {
                    int px = bx * 4 + i % 4, py = by * 4 + i / 4;
                    if (px >= w || py >= h) continue;
                    var s = (int)(sel >> (2 * i) & 3) * 4;
                    var d = (py * w + px) * 4;
                    dst[d] = pal[s]; dst[d + 1] = pal[s + 1]; dst[d + 2] = pal[s + 2];
                    dst[d + 3] = dxt == 1 ? pal[s + 3] : alpha[i];
                }
            }
        return dst;
    }

    private static void Rgb565(int v, Span<byte> bgra)
    {
        int r = v >> 11 & 0x1F, g = v >> 5 & 0x3F, b = v & 0x1F;
        bgra[0] = (byte)(b << 3 | b >> 2); bgra[1] = (byte)(g << 2 | g >> 4); bgra[2] = (byte)(r << 3 | r >> 2); bgra[3] = 255;
    }

    /// <summary>Encode BGRA to DXT1/3/5 (PCA, bounding-box and colour-pair endpoint candidates, least-squares refinement and a ±1 endpoint search; colour error is alpha-weighted) (DXT1 treats alpha &lt; 128 as the transparent index).</summary>
    public static byte[] EncodeDxt(ReadOnlySpan<byte> bgra, int w, int h, int dxt)
    {
        var bw = (w + 3) / 4; var bh = (h + 3) / 4;
        var blockBytes = dxt == 1 ? 8 : 16;
        var dst = new byte[bw * bh * blockBytes];
        Span<byte> px = stackalloc byte[64];
        Span<byte> pal = stackalloc byte[16];
        for (var by = 0; by < bh; by++)
            for (var bx = 0; bx < bw; bx++)
            {
                for (var i = 0; i < 16; i++)
                {
                    int sx = Math.Min(w - 1, bx * 4 + i % 4), sy = Math.Min(h - 1, by * 4 + i / 4);
                    bgra.Slice((sy * w + sx) * 4, 4).CopyTo(px.Slice(i * 4, 4));
                }
                var o = (by * bw + bx) * blockBytes;
                if (dxt == 3)
                    for (var i = 0; i < 16; i += 2) dst[o + i / 2] = (byte)((px[i * 4 + 3] + 8) / 17 | (px[(i + 1) * 4 + 3] + 8) / 17 << 4);
                else if (dxt == 5) EncodeAlphaBlock(px, dst.AsSpan(o, 8));
                EncodeColorBlock(px, dst.AsSpan(o + (dxt == 1 ? 0 : 8), 8), dxt == 1, pal);
            }
        return dst;
    }

    private static void EncodeAlphaBlock(ReadOnlySpan<byte> px, Span<byte> dst)
    {
        int lo = 255, hi = 0;
        for (var i = 0; i < 16; i++) { lo = Math.Min(lo, px[i * 4 + 3]); hi = Math.Max(hi, px[i * 4 + 3]); }
        dst[0] = (byte)hi; dst[1] = (byte)lo;
        Span<int> ramp = stackalloc int[8];
        ramp[0] = hi; ramp[1] = lo;
        if (hi > lo) for (var k = 1; k < 7; k++) ramp[k + 1] = ((7 - k) * hi + k * lo) / 7;
        else { for (var k = 1; k < 5; k++) ramp[k + 1] = ((5 - k) * hi + k * lo) / 5; ramp[6] = 0; ramp[7] = 255; }
        ulong bits = 0;
        for (var i = 0; i < 16; i++)
        {
            int best = 0, bestD = int.MaxValue, a = px[i * 4 + 3];
            for (var k = 0; k < 8; k++) { var d = Math.Abs(ramp[k] - a); if (d < bestD) { bestD = d; best = k; } }
            bits |= (ulong)best << (3 * i);
        }
        for (var k = 0; k < 6; k++) dst[2 + k] = (byte)(bits >> (8 * k));
    }

    private static void EncodeColorBlock(ReadOnlySpan<byte> px, Span<byte> dst, bool dxt1, Span<byte> pal)
    {
        // per-pixel error weight: DXT1 punch-through and alpha-0 pixels are invisible, translucent ones count by their alpha
        Span<int> wt = stackalloc int[16];
        var opaque = 0;
        for (var i = 0; i < 16; i++)
        {
            var a = px[i * 4 + 3];
            wt[i] = dxt1 ? (a < 128 ? 0 : 1) : a == 0 ? 0 : 16 + a;
            if (!dxt1 || a >= 128) opaque++;
        }
        var transparent = dxt1 && opaque < 16;
        Span<int> sel = stackalloc int[16], bestSel = stackalloc int[16];
        var best = (C0: 0, C1: 0, Err: long.MaxValue);

        // distinct colours of the visible pixels (blocks decoded from DXT have at most four)
        Span<int> distinct = stackalloc int[16];
        var nd = 0;
        for (var i = 0; i < 16; i++)
        {
            if (wt[i] == 0) continue;
            var v = px[i * 4] | px[i * 4 + 1] << 8 | px[i * 4 + 2] << 16;
            if (distinct[..nd].IndexOf(v) < 0) distinct[nd++] = v;
        }
        if (nd == 0) { Try(0, 0, px, wt, sel, bestSel, pal, ref best); goto write; }

        // candidate: principal axis through the weighted mean, endpoints at the extreme projections
        double m0 = 0, m1 = 0, m2 = 0, wsum = 0;
        for (var i = 0; i < 16; i++) { if (wt[i] == 0) continue; wsum += wt[i]; m0 += wt[i] * px[i * 4]; m1 += wt[i] * px[i * 4 + 1]; m2 += wt[i] * px[i * 4 + 2]; }
        m0 /= wsum; m1 /= wsum; m2 /= wsum;
        double xx = 0, xy = 0, xz = 0, yy = 0, yz = 0, zz = 0;
        for (var i = 0; i < 16; i++)
        {
            if (wt[i] == 0) continue;
            double x = px[i * 4] - m0, y = px[i * 4 + 1] - m1, z = px[i * 4 + 2] - m2;
            xx += wt[i] * x * x; xy += wt[i] * x * y; xz += wt[i] * x * z; yy += wt[i] * y * y; yz += wt[i] * y * z; zz += wt[i] * z * z;
        }
        double vx = 1, vy = 1, vz = 1;
        for (var it = 0; it < 8; it++)
        {
            double nx = xx * vx + xy * vy + xz * vz, ny = xy * vx + yy * vy + yz * vz, nz = xz * vx + yz * vy + zz * vz;
            var len = Math.Max(Math.Max(Math.Abs(nx), Math.Abs(ny)), Math.Abs(nz));
            if (len < 1e-9) break;
            vx = nx / len; vy = ny / len; vz = nz / len;
        }
        double tMin = double.MaxValue, tMax = double.MinValue, vv = vx * vx + vy * vy + vz * vz;
        for (var i = 0; i < 16; i++)
        {
            if (wt[i] == 0) continue;
            var t = ((px[i * 4] - m0) * vx + (px[i * 4 + 1] - m1) * vy + (px[i * 4 + 2] - m2) * vz) / vv;
            tMin = Math.Min(tMin, t); tMax = Math.Max(tMax, t);
        }
        Try(Pack565(Clamp(m2 + tMax * vz), Clamp(m1 + tMax * vy), Clamp(m0 + tMax * vx)),
            Pack565(Clamp(m2 + tMin * vz), Clamp(m1 + tMin * vy), Clamp(m0 + tMin * vx)), px, wt, sel, bestSel, pal, ref best);

        // candidate: inset bounding box
        int[] lo = [255, 255, 255], hi = [0, 0, 0];
        for (var i = 0; i < 16; i++)
            if (wt[i] != 0) for (var c = 0; c < 3; c++) { lo[c] = Math.Min(lo[c], px[i * 4 + c]); hi[c] = Math.Max(hi[c], px[i * 4 + c]); }
        for (var c = 0; c < 3; c++) { var inset = (hi[c] - lo[c]) / 16; lo[c] += inset; hi[c] -= inset; }
        Try(Pack565(hi[2], hi[1], hi[0]), Pack565(lo[2], lo[1], lo[0]), px, wt, sel, bestSel, pal, ref best);

        // candidate: every pair of distinct colours (recovers the original endpoints when re-encoding decoded DXT data)
        if (nd <= 8)
            for (var i = 0; i < nd && best.Err > 0; i++)
                for (var j = i; j < nd && best.Err > 0; j++)
                {
                    int a = distinct[i], b = distinct[j];
                    Try(Pack565(a >> 16 & 0xFF, a >> 8 & 0xFF, a & 0xFF), Pack565(b >> 16 & 0xFF, b >> 8 & 0xFF, b & 0xFF), px, wt, sel, bestSel, pal, ref best);
                }

        // least-squares refinement of the endpoints from the best selection
        for (var pass = 0; pass < 3 && best.Err > 0; pass++)
        {
            var punch = best.C0 <= best.C1;
            double aa = 0, bb = 0, ab = 0, a0 = 0, a1 = 0, a2 = 0, b0 = 0, b1 = 0, b2 = 0;
            for (var i = 0; i < 16; i++)
            {
                if (wt[i] == 0 || punch && bestSel[i] == 3) continue;
                var t = punch ? bestSel[i] switch { 0 => 1.0, 1 => 0.0, _ => 0.5 } : bestSel[i] switch { 0 => 1.0, 1 => 0.0, 2 => 2.0 / 3, _ => 1.0 / 3 };
                double w = wt[i], wt0 = w * t, wt1 = w * (1 - t);
                aa += wt0 * t; bb += wt1 * (1 - t); ab += wt0 * (1 - t);
                a0 += wt0 * px[i * 4]; a1 += wt0 * px[i * 4 + 1]; a2 += wt0 * px[i * 4 + 2];
                b0 += wt1 * px[i * 4]; b1 += wt1 * px[i * 4 + 1]; b2 += wt1 * px[i * 4 + 2];
            }
            var det = aa * bb - ab * ab;
            if (Math.Abs(det) < 1e-6) break;
            var before = best.Err;
            Try(Pack565(Clamp((a2 * bb - b2 * ab) / det), Clamp((a1 * bb - b1 * ab) / det), Clamp((a0 * bb - b0 * ab) / det)),
                Pack565(Clamp((b2 * aa - a2 * ab) / det), Clamp((b1 * aa - a1 * ab) / det), Clamp((b0 * aa - a0 * ab) / det)), px, wt, sel, bestSel, pal, ref best);
            if (best.Err >= before) break;
        }

        // small endpoint search: step each 565 channel of either endpoint by one while it helps
        for (var improved = best.Err > 0; improved && best.Err > 0;)
        {
            improved = false;
            for (var e = 0; e < 2; e++)
                for (var c = 0; c < 3; c++)
                    for (var d = -1; d <= 1; d += 2)
                    {
                        int shift = c == 0 ? 11 : c == 1 ? 5 : 0, mask = c == 1 ? 0x3F : 0x1F;
                        var ep = e == 0 ? best.C0 : best.C1;
                        var v = (ep >> shift & mask) + d;
                        if (v < 0 || v > mask) continue;
                        ep = ep & ~(mask << shift) | v << shift;
                        var before = best.Err;
                        Try(e == 0 ? ep : best.C0, e == 0 ? best.C1 : ep, px, wt, sel, bestSel, pal, ref best);
                        improved |= best.Err < before;
                    }
        }

        write:
        uint bits = 0;
        for (var i = 0; i < 16; i++) bits |= (uint)bestSel[i] << (2 * i);
        dst[0] = (byte)best.C0; dst[1] = (byte)(best.C0 >> 8); dst[2] = (byte)best.C1; dst[3] = (byte)(best.C1 >> 8);
        dst[4] = (byte)bits; dst[5] = (byte)(bits >> 8); dst[6] = (byte)(bits >> 16); dst[7] = (byte)(bits >> 24);

        void Try(int c0, int c1, ReadOnlySpan<byte> px, ReadOnlySpan<int> wt, Span<int> sel, Span<int> bestSel, Span<byte> pal, ref (int C0, int C1, long Err) best)
        {
            // 4-colour mode needs c0 > c1; punch-through mode (3 colours + transparent) needs c0 <= c1.
            if (transparent ? c0 > c1 : c0 < c1) (c0, c1) = (c1, c0);
            if (!transparent && c0 == c1) { if (c0 < 0xFFFF) c0++; else c1--; }
            var err = Evaluate(px, wt, c0, c1, dxt1, sel, pal);
            if (err < best.Err) { best = (c0, c1, err); sel.CopyTo(bestSel); }
        }
    }

    /// <summary>Weighted squared colour error of a block for an endpoint pair; fills the chosen indices (3 for DXT1 punch-through pixels).</summary>
    private static long Evaluate(ReadOnlySpan<byte> px, ReadOnlySpan<int> wt, int c0, int c1, bool dxt1, Span<int> sel, Span<byte> pal)
    {
        Rgb565(c0, pal[..4]); Rgb565(c1, pal.Slice(4, 4));
        var punch = c0 <= c1;
        for (var k = 0; k < 3; k++)
        {
            int a = pal[k], b = pal[4 + k];
            if (punch) pal[8 + k] = (byte)((a + b) / 2);
            else { pal[8 + k] = (byte)((2 * a + b) / 3); pal[12 + k] = (byte)((a + 2 * b) / 3); }
        }
        long err = 0;
        for (var i = 0; i < 16; i++)
        {
            if (dxt1 && px[i * 4 + 3] < 128) { sel[i] = 3; continue; }
            int best = 0, bestD = int.MaxValue;
            for (var k = 0; k < (punch ? 3 : 4); k++)
            {
                int e0 = px[i * 4] - pal[k * 4], e1 = px[i * 4 + 1] - pal[k * 4 + 1], e2 = px[i * 4 + 2] - pal[k * 4 + 2];
                var d = e0 * e0 + e1 * e1 + e2 * e2;
                if (d < bestD) { bestD = d; best = k; }
            }
            sel[i] = best;
            err += (long)bestD * wt[i];
        }
        return err;
    }

    private static int Clamp(double v) => (int)Math.Clamp(Math.Round(v), 0, 255);

    private static int Pack565(int r, int g, int b) => (r * 31 + 127) / 255 << 11 | (g * 63 + 127) / 255 << 5 | (b * 31 + 127) / 255;
}
