namespace MVPWorkshop.Core.Formats;

/// <summary>
/// PS2 GS memory un-swizzling for EA textures. Algorithms ported from ReverseBox
/// (https://github.com/bartlomiejduda/ReverseBox, GPL-3.0) as used by EA-Graphics-Manager.
/// </summary>
public static partial class Ps2Swizzle
{
    public static byte[] Unswizzle8(ReadOnlySpan<byte> src, int w, int h)
    {
        var dst = new byte[w * h];
        for (var y = 0; y < h; y++)
            for (var x = 0; x < w; x++)
            {
                var blockLocation = (y & ~0xF) * w + (x & ~0xF) * 2;
                var swapSelector = (((y + 2) >> 2) & 1) * 4;
                var posY = (((y & ~3) >> 1) + (y & 1)) & 7;
                var columnLocation = posY * w * 2 + ((x + swapSelector) & 7) * 4;
                var byteNum = ((y >> 1) & 1) + ((x >> 2) & 2);
                var id = blockLocation + columnLocation + byteNum;
                if (id < src.Length) dst[y * w + x] = src[id];
            }
        return dst;
    }

    public static byte[] Unswizzle16(ReadOnlySpan<byte> src, int w, int h)
    {
        var dst = new byte[w * h * 2];
        for (var y = 0; y < h; y++)
            for (var x = 0; x < w; x++)
            {
                int px = x, py = y, bit = 0;
                if (w >= 16)
                {
                    int pageX = px >> 6, pageY = py >> 6;
                    var nx = (py & 0x38) + (px & 7);
                    var ny = ((px & 0x30) >> 1) + (py & 7);
                    bit = (px & 8) << 1;
                    px = (pageX << 6) + nx; py = (pageY << 5) + ny;
                }
                var loc = (32 * (py * w + px) + bit) >> 3;
                var di = (y * w + x) * 2;
                if (loc + 1 < src.Length) { dst[di] = src[loc]; dst[di + 1] = src[loc + 1]; }
            }
        return dst;
    }

    // ---- 4bpp (PSMT4 written as PSMCT32) ----
    private static readonly byte[] Lut4 =
    [
        0, 8, 16, 24, 32, 40, 48, 56, 2, 10, 18, 26, 34, 42, 50, 58,
        4, 12, 20, 28, 36, 44, 52, 60, 6, 14, 22, 30, 38, 46, 54, 62,
        64, 72, 80, 88, 96, 104, 112, 120, 66, 74, 82, 90, 98, 106, 114, 122,
        68, 76, 84, 92, 100, 108, 116, 124, 70, 78, 86, 94, 102, 110, 118, 126,
        33, 41, 49, 57, 1, 9, 17, 25, 35, 43, 51, 59, 3, 11, 19, 27,
        37, 45, 53, 61, 5, 13, 21, 29, 39, 47, 55, 63, 7, 15, 23, 31,
        97, 105, 113, 121, 65, 73, 81, 89, 99, 107, 115, 123, 67, 75, 83, 91,
        101, 109, 117, 125, 69, 77, 85, 93, 103, 111, 119, 127, 71, 79, 87, 95,
        32, 40, 48, 56, 0, 8, 16, 24, 34, 42, 50, 58, 2, 10, 18, 26,
        36, 44, 52, 60, 4, 12, 20, 28, 38, 46, 54, 62, 6, 14, 22, 30,
        96, 104, 112, 120, 64, 72, 80, 88, 98, 106, 114, 122, 66, 74, 82, 90,
        100, 108, 116, 124, 68, 76, 84, 92, 102, 110, 118, 126, 70, 78, 86, 94,
        1, 9, 17, 25, 33, 41, 49, 57, 3, 11, 19, 27, 35, 43, 51, 59,
        5, 13, 21, 29, 37, 45, 53, 61, 7, 15, 23, 31, 39, 47, 55, 63,
        65, 73, 81, 89, 97, 105, 113, 121, 67, 75, 83, 91, 99, 107, 115, 123,
        69, 77, 85, 93, 101, 109, 117, 125, 71, 79, 87, 95, 103, 111, 119, 127,
    ];
    private static readonly int[] BlockTable4 = [0, 2, 8, 10, 1, 3, 9, 11, 4, 6, 12, 14, 5, 7, 13, 15, 16, 18, 24, 26, 17, 19, 25, 27, 20, 22, 28, 30, 21, 23, 29, 31];
    private static readonly int[] BlockTable32 = [0, 1, 4, 5, 16, 17, 20, 21, 2, 3, 6, 7, 18, 19, 22, 23, 8, 9, 12, 13, 24, 25, 28, 29, 10, 11, 14, 15, 26, 27, 30, 31];

    private static byte[] Convert4Block(byte[] input)
    {
        var output = new byte[256];
        int index1 = 0, pIn = 0;
        for (var k = 0; k < 4; k++)
        {
            var index0 = (k % 2) * 128;
            for (var i = 0; i < 16; i++)
                for (var j = 0; j < 4; j++)
                {
                    int i0 = Lut4[index0++], i1 = i0 / 2, i2 = (i0 & 1) * 4;
                    var cOut = (input[pIn + i1] & (0x0F << i2)) >> i2;
                    i0 = Lut4[index0++]; i1 = i0 / 2; i2 = (i0 & 1) * 4;
                    var cIn = (input[pIn + i1] & (0x0F << i2)) >> i2;
                    cOut |= (cIn << 4) & 0xF0;
                    output[index1++] = (byte)cOut;
                }
            pIn += 64;
        }
        return output;
    }

    private static byte[] Convert4Page(byte[] inputPage)
    {
        var output = new byte[64 * 4 * 32];
        var h32 = new int[32]; var v32 = new int[32];
        var idx = 0;
        for (var i = 0; i < 4; i++)
            for (var j = 0; j < 8; j++) { var b = BlockTable32[idx++]; h32[b] = j; v32[b] = i; }

        const int nWidth = 128 / 32, nHeight = 128 / 16, inLine = 256, outLine = 64;
        var po0 = new byte[256];
        for (var i = 0; i < nHeight; i++)
            for (var j = 0; j < nWidth; j++)
            {
                var blk = BlockTable4[i * nWidth + j];
                var po1 = 8 * v32[blk] * inLine + h32[blk] * 32;
                for (var k = 0; k < 8; k++) Array.Copy(inputPage, po1 + k * inLine, po0, k * 32, 32);
                var ob = Convert4Block(po0);
                for (var k = 0; k < 16; k++)
                    Array.Copy(ob, k * 16, output, 16 * i * outLine + j * 16 + k * outLine, 16);
            }
        return output;
    }

    public static byte[] Unswizzle4(ReadOnlySpan<byte> input, int w, int h)
    {
        var size = (w * h + 1) / 2;
        var output = new byte[size];
        var nPageW = (w - 1) / 128 + 1;
        var nPageH = (h - 1) / 128 + 1;
        const int page4WidthByte = 64, page32WidthByte = 256;
        int inWidthByte, outHeight, inHeight, outWidthByte;
        if (nPageH == 1) { inWidthByte = h * 2; outHeight = h; } else { inWidthByte = page32WidthByte; outHeight = 128; }
        if (nPageW == 1) { inHeight = w / 4; outWidthByte = w / 2; } else { inHeight = 32; outWidthByte = page4WidthByte; }

        for (var i = 0; i < nPageH; i++)
            for (var j = 0; j < nPageW; j++)
            {
                var po0 = inWidthByte * inHeight * nPageW * i + inWidthByte * j;
                var page = new byte[64 * 128];
                for (var k = 0; k < inHeight; k++)
                {
                    var s = po0 + k * inWidthByte * nPageW;
                    var n = Math.Min(inWidthByte, Math.Max(0, input.Length - s));
                    if (n > 0) input.Slice(s, n).CopyTo(page.AsSpan(k * page32WidthByte));
                }
                var op = Convert4Page(page);
                var pi0 = outWidthByte * outHeight * nPageW * i + outWidthByte * j;
                for (var k = 0; k < outHeight; k++)
                {
                    var d = pi0 + k * outWidthByte * nPageW;
                    var n = Math.Min(outWidthByte, size - d);
                    if (n > 0) Array.Copy(op, k * page4WidthByte, output, d, n);
                }
            }
        return output;
    }

    /// <summary>Swap CSM1/CSM2 palette ordering (works both directions); 32-bit entries.</summary>
    public static byte[] UnswizzlePalette(ReadOnlySpan<byte> pal, int bytesPerEntry = 4)
    {
        var res = new byte[pal.Length];
        var entries = pal.Length / bytesPerEntry;
        var o = 0;
        for (var part = 0; part < entries / 32; part++)
            for (var block = 0; block < 2; block++)
                for (var stripe = 0; stripe < 2; stripe++)
                    for (var color = 0; color < 8; color++)
                    {
                        var idx = part * 32 + block * 8 + stripe * 16 + color;
                        pal.Slice(idx * bytesPerEntry, bytesPerEntry).CopyTo(res.AsSpan(o));
                        o += bytesPerEntry;
                    }
        if (o < res.Length) pal[o..].CopyTo(res.AsSpan(o));
        return res;
    }
}

/// <summary>EA PS2 "GST" compressed palettised textures (SSH types 0x08–0x0F): base + detail planes.</summary>
public static partial class Gst
{
    public static (int BlockW, int BlockH, int DetailBpp) Params(int type) => (type & 0x7F) switch
    {
        0x08 => (1, 2, 1), 0x09 => (2, 2, 1), 0x0A => (4, 2, 1), 0x0B => (8, 2, 1),
        0x0C => (1, 2, 2), 0x0D => (2, 2, 2), 0x0E => (4, 2, 2), 0x0F => (8, 2, 2),
        _ => throw new ArgumentOutOfRangeException(nameof(type))
    };

    public static int BaseSize(int w, int h, int type) { var (bw, bh, _) = Params(type); return w / bw * (h / bh); }
    public static int DetailSize(int w, int h, int type) { var (_, _, db) = Params(type); return w * h * db / 8; }
    public static int TotalSize(int w, int h, int type) => ((BaseSize(w, h, type) + 15) & ~15) + DetailSize(w, h, type);

    public static byte[] Decode(ReadOnlySpan<byte> data, int w, int h, int type, bool swizzled)
    {
        var (bw, bh, dbpp) = Params(type);
        var baseSize = BaseSize(w, h, type);
        var detailSize = DetailSize(w, h, type);
        var baseData = Take(data, 0, baseSize);
        var detailData = Take(data, (baseSize + 15) & ~15, detailSize);

        if (swizzled)
        {
            baseData = UnswizzleBase(baseData, w, h, bw, bh);
            detailData = dbpp == 2 ? UnswizzleDetail2(detailData, w, h) : UnswizzleDetail1(detailData, w, h);
        }

        var outp = new byte[w * h];
        var o = 0; var dOff = 0;
        for (var y = 0; y < h; y++)
        {
            if (dbpp == 2)
            {
                for (var bx = 0; bx < w; bx += 4, dOff++)
                    for (var x = 0; x < 4; x++)
                    {
                        var bo = w / bw * (y / bh) + (bx + x) / bw;
                        var dv = dOff < detailData.Length ? detailData[dOff] : 0;
                        var bv = bo < baseData.Length ? baseData[bo] : 0;
                        if (o < outp.Length) outp[o++] = (byte)(bv + ((dv >> (x * 2)) & 3));
                    }
            }
            else
            {
                for (var bx = 0; bx < w; bx += 8, dOff++)
                    for (var x = 0; x < 8; x++)
                    {
                        var bo = w / bw * (y / bh) + (bx + x) / bw;
                        var dv = dOff < detailData.Length ? detailData[dOff] : 0;
                        var bv = bo < baseData.Length ? baseData[bo] : 0;
                        if (o < outp.Length) outp[o++] = (byte)(bv + ((dv >> x) & 1));
                    }
            }
        }
        return outp;
    }

    private static byte[] Take(ReadOnlySpan<byte> d, int o, int n)
    {
        var r = new byte[n];
        if (o < d.Length) d.Slice(o, Math.Min(n, d.Length - o)).CopyTo(r);
        return r;
    }

    private static int BaseOffset(int x, int y, int width, int bw, int bh)
    {
        x *= bw; y *= bh;
        var x1 = ((x & ~0x0F) >> 1) + ((x & 7) ^ ((y & 2) << 1) ^ (y & 4));
        var y1 = ((y & ~3) >> 1) + (y & 1);
        var z1 = ((x & 8) >> 2) + ((y & 2) >> 1);
        x1 /= bw; y1 /= bh;
        return 32 * (y1 * width / 2 + x1) + 8 * z1;
    }

    private static (int x2, int y2, int z2, int z1) Detail(int x, int y)
    {
        var x1 = ((x & ~0x0F) >> 1) + ((x & 7) ^ ((y & 2) << 1) ^ (y & 4));
        var y1 = ((y & ~3) >> 1) + (y & 1);
        var z1 = ((x & 8) >> 2) + ((y & 2) >> 1);
        var x2 = ((x1 & ~0x0F) >> 1) + ((x1 & 7) ^ ((y1 & 2) << 1) ^ (y1 & 4));
        var y2 = ((y1 & ~3) >> 1) + (y1 & 1);
        var z2 = ((x1 & 8) >> 2) + ((y1 & 2) >> 1);
        return (x2, y2, z2, z1);
    }

    private static byte[] UnswizzleBase(byte[] d, int w, int h, int bw, int bh)
    {
        int baseW = w / bw, baseH = h / bh;
        var r = new byte[d.Length];
        var c = 0;
        for (var y = 0; y < baseH; y++)
            for (var x = 0; x < baseW; x++)
            {
                var o = BaseOffset(x, y, baseW, bw, bh) >> 3;
                if (c < r.Length && o < d.Length) r[c] = d[o];
                c++;
            }
        return r;
    }

    private static int Bits(byte[] d, int off, int mask) { var i = off >> 3; return i < d.Length ? (d[i] >> (off & 7)) & mask : 0; }

    private static byte[] UnswizzleDetail1(byte[] d, int w, int h)
    {
        var r = new byte[d.Length]; var di = 0;
        for (var y = 0; y < h; y++)
            for (var x = 0; x < w; x += 8)
            {
                var v = 0;
                for (var xf = 7; xf >= 0; xf--)
                {
                    var (x2, y2, z2, z1) = Detail(x + xf, y);
                    v = (v << 1) + Bits(d, 16 * (y2 * (w / 4) + x2) + 4 * z2 + z1, 1);
                }
                if (di < r.Length) r[di] = (byte)v;
                di++;
            }
        return r;
    }

    private static byte[] UnswizzleDetail2(byte[] d, int w, int h)
    {
        var r = new byte[d.Length]; var di = 0;
        for (var y = 0; y < h; y++)
            for (var x = 0; x < w; x += 4)
            {
                var v = 0;
                for (var xf = 3; xf >= 0; xf--)
                {
                    var (x2, y2, z2, z1) = Detail(x + xf, y);
                    v = (v << 2) + Bits(d, 32 * (y2 * w / 4 + x2) + 8 * z2 + 2 * z1, 3);
                }
                if (di < r.Length) r[di] = (byte)v;
                di++;
            }
        return r;
    }
}
