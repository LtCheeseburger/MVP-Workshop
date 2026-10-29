namespace MVPWorkshop.Core.Formats;

/// <summary>
/// Pictures Windows' imaging doesn't read, for replacing textures: TGA (uncompressed and RLE; true colour, grey and
/// colour-mapped; alpha kept) and DDS (DXT1/3/5 and 32/24-bit RGB, top level). Both come back as BGRA, top row first.
/// </summary>
public static class PictureFile
{
    public static bool Handles(string path) =>
        Path.GetExtension(path).ToLowerInvariant() is ".tga" or ".dds";

    public static (byte[] Bgra, int Width, int Height) Decode(string path)
    {
        var d = File.ReadAllBytes(path);
        return Path.GetExtension(path).ToLowerInvariant() == ".dds" ? DecodeDds(d) : DecodeTga(d);
    }

    public static (byte[] Bgra, int Width, int Height) DecodeTga(byte[] d)
    {
        if (d.Length < 18) throw new InvalidDataException("The TGA file is too short.");
        int idLength = d[0], mapType = d[1], type = d[2];
        int mapFirst = d[3] | d[4] << 8, mapLength = d[5] | d[6] << 8, mapBits = d[7];
        int w = d[12] | d[13] << 8, h = d[14] | d[15] << 8, bpp = d[16], desc = d[17];
        if (w == 0 || h == 0 || (long)w * h > 16777216) throw new InvalidDataException($"Unsupported TGA size {w}×{h}.");
        var rle = type >= 9;
        var kind = type & 7;  // 1 colour-mapped, 2 true colour, 3 grey
        if (kind is not (1 or 2 or 3)) throw new InvalidDataException($"Unsupported TGA image type {type}.");
        var at = 18 + idLength;

        // a colour map as BGRA
        byte[]? map = null;
        if (mapType == 1)
        {
            var bytes = (mapBits + 7) / 8;
            map = new byte[mapLength * 4];
            for (var i = 0; i < mapLength; i++) Pixel(d, at + i * bytes, mapBits, map, i * 4);
            at += mapLength * bytes;
        }
        if (kind == 1 && map is null) throw new InvalidDataException("The colour-mapped TGA has no colour map.");

        var stride = (bpp + 7) / 8;
        var px = new byte[w * h * 4];
        var count = w * h;
        void Put(int i, int src)
        {
            if (kind == 1)
            {
                var index = (stride == 1 ? d[src] : d[src] | d[src + 1] << 8) - mapFirst;
                if (index >= 0 && index < mapLength) Array.Copy(map!, index * 4, px, i * 4, 4);
            }
            else if (kind == 3)
            {
                px[i * 4] = px[i * 4 + 1] = px[i * 4 + 2] = d[src];
                px[i * 4 + 3] = stride == 2 ? d[src + 1] : (byte)255;
            }
            else Pixel(d, src, bpp, px, i * 4);
        }
        if (!rle)
        {
            if (at + count * stride > d.Length) throw new InvalidDataException("The TGA file is cut short.");
            for (var i = 0; i < count; i++) Put(i, at + i * stride);
        }
        else
        {
            for (var i = 0; i < count;)
            {
                if (at >= d.Length) throw new InvalidDataException("The TGA file is cut short.");
                var head = d[at++];
                var n = (head & 0x7F) + 1;
                if (head >= 0x80)
                {
                    if (at + stride > d.Length) throw new InvalidDataException("The TGA file is cut short.");
                    for (var k = 0; k < n && i < count; k++) Put(i++, at);
                    at += stride;
                }
                else
                {
                    if (at + n * stride > d.Length) throw new InvalidDataException("The TGA file is cut short.");
                    for (var k = 0; k < n && i < count; k++, at += stride) Put(i++, at);
                }
            }
        }
        // no alpha bits declared (24-bit, or 32-bit with 0 attribute bits and an all-zero channel): opaque
        if (kind == 2 && bpp == 32 && (desc & 15) == 0 && AllZeroAlpha(px)) for (var i = 3; i < px.Length; i += 4) px[i] = 255;

        // stored bottom row first unless bit 5 says top; right to left when bit 4 is set
        var flipY = (desc & 0x20) == 0;
        var flipX = (desc & 0x10) != 0;
        if (flipY || flipX)
        {
            var o = new byte[px.Length];
            for (var y = 0; y < h; y++)
            for (var x = 0; x < w; x++)
                Array.Copy(px, ((flipY ? h - 1 - y : y) * w + (flipX ? w - 1 - x : x)) * 4, o, (y * w + x) * 4, 4);
            px = o;
        }
        return (px, w, h);
    }

    static bool AllZeroAlpha(byte[] px)
    {
        for (var i = 3; i < px.Length; i += 4) if (px[i] != 0) return false;
        return true;
    }

    // one 15/16/24/32-bit TGA pixel (stored B, G, R, A) into BGRA
    static void Pixel(byte[] d, int src, int bits, byte[] dst, int o)
    {
        switch (bits)
        {
            case 32: dst[o] = d[src]; dst[o + 1] = d[src + 1]; dst[o + 2] = d[src + 2]; dst[o + 3] = d[src + 3]; break;
            case 24: dst[o] = d[src]; dst[o + 1] = d[src + 1]; dst[o + 2] = d[src + 2]; dst[o + 3] = 255; break;
            case 15 or 16:
                var v = d[src] | d[src + 1] << 8;
                dst[o] = (byte)((v & 31) * 255 / 31);
                dst[o + 1] = (byte)((v >> 5 & 31) * 255 / 31);
                dst[o + 2] = (byte)((v >> 10 & 31) * 255 / 31);
                dst[o + 3] = bits == 16 && (v & 0x8000) == 0 ? (byte)0 : (byte)255;
                break;
            default: throw new InvalidDataException($"Unsupported TGA pixel size {bits} bits.");
        }
    }

    public static (byte[] Bgra, int Width, int Height) DecodeDds(byte[] d)
    {
        if (d.Length < 128 || d[0] != 'D' || d[1] != 'D' || d[2] != 'S' || d[3] != ' ') throw new InvalidDataException("Not a DDS file.");
        int U(int o) => d[o] | d[o + 1] << 8 | d[o + 2] << 16 | d[o + 3] << 24;
        int h = U(12), w = U(16);
        int pfFlags = U(80), bits = U(88);
        var fourcc = System.Text.Encoding.ASCII.GetString(d, 84, 4);
        if (w <= 0 || h <= 0 || (long)w * h > 16777216) throw new InvalidDataException($"Unsupported DDS size {w}×{h}.");
        if ((pfFlags & 4) != 0)
        {
            var dxt = fourcc switch { "DXT1" => 1, "DXT2" or "DXT3" => 3, "DXT4" or "DXT5" => 5, _ => 0 };
            if (dxt == 0) throw new InvalidDataException($"Unsupported DDS compression {fourcc.TrimEnd('\0')} (DXT1, DXT3 and DXT5 are read).");
            var size = XboxTexture.DxtSize(w, h, dxt);
            if (128 + size > d.Length) throw new InvalidDataException("The DDS file is cut short.");
            return (XboxTexture.DecodeDxt(d.AsSpan(128, size), w, h, dxt), w, h);
        }
        if ((pfFlags & 0x40) == 0 || bits is not (24 or 32)) throw new InvalidDataException("Unsupported DDS pixel format (DXT1/3/5 or 24/32-bit RGB).");
        uint rMask = (uint)U(92), gMask = (uint)U(96), bMask = (uint)U(100), aMask = (pfFlags & 1) != 0 ? (uint)U(104) : 0;
        var stride = bits / 8;
        if (128 + (long)w * h * stride > d.Length) throw new InvalidDataException("The DDS file is cut short.");
        static byte Channel(uint v, uint mask)
        {
            if (mask == 0) return 255;
            var shift = System.Numerics.BitOperations.TrailingZeroCount(mask);
            var max = mask >> shift;
            return (byte)(((v & mask) >> shift) * 255 / max);
        }
        var px = new byte[w * h * 4];
        for (var i = 0; i < w * h; i++)
        {
            var o = 128 + i * stride;
            var v = (uint)(d[o] | d[o + 1] << 8 | d[o + 2] << 16 | (stride == 4 ? d[o + 3] << 24 : 0));
            px[i * 4] = Channel(v, bMask);
            px[i * 4 + 1] = Channel(v, gMask);
            px[i * 4 + 2] = Channel(v, rMask);
            px[i * 4 + 3] = aMask == 0 ? (byte)255 : Channel(v, aMask);
        }
        return (px, w, h);
    }
}
