using System.Buffers.Binary;
using MVPWorkshop.Core.IO;

namespace MVPWorkshop.Core.Formats;

public enum PaletteMode
{
    /// <summary>Build a fresh palette that fits the new picture (exact when it has few enough colours, median-cut otherwise).</summary>
    Rebuild,
    /// <summary>Keep the image's palette and map every pixel to the nearest existing entry.</summary>
    KeepExisting,
}

public sealed record SshReplaceResult(byte[] File, string Notes);

/// <summary>
/// Writes pixels back into an SHPS (.ssh) texture pack. Only the replaced image's pixel data (and, for indexed images, its palette block)
/// change; the directory, every other image, names, comments, hotspots and any unrecognised bytes are carried over untouched. If the new
/// data is smaller than the old region it is padded in place; if it grows (RefPack only) the later blocks are shifted and the directory
/// offsets, block size and file size are fixed up.
/// Supported: PAL4, PAL8, linear RGB888, RGBA8888 and GST, including RefPack. Native dimensions are preserved by default; validated storage layouts can opt into experimental uniform upscales.
/// </summary>
public static class SshWriter
{
    /// <summary>Why <paramref name="img"/> cannot be replaced (null when it can).</summary>
    public static string? Unsupported(SshFile file, SshImage img)
    {
        var t = img.Header.Type & 0x7F;
        if (img.Header.Width <= 0 || img.Header.Height <= 0 || img.Header.Width > 4096 || img.Header.Height > 4096) return "Invalid or excessive texture dimensions.";
        if (file.IsXbox) return UnsupportedXbox(file, img, t);
        if (t >= 8 && t <= 15 && (img.Header.Width % 8 != 0 || img.Header.Height % 2 != 0)) return "GST needs complete blocks: width multiple of 8 and height multiple of 2.";
        if (img.Header.FlagReferenced || img.Header.FlagTransposed) return "Referenced/transposed storage has not been verified for writing.";
        if (img.Header.Mipmaps != 0) return "Native mipmap regeneration is not verified; replacement would leave stale lower levels.";
        if (t is not (0x01 or 0x02 or 0x04 or 0x05 or >= 0x08 and <= 0x0F)) return $"{SshFile.TypeName(img.Header.Type)} is not supported for writing.";
        if (t == 0x04 && img.Header.FlagSwizzled) return "Swizzled RGB888 storage is not verified.";
        if (t is 0x01 or 0x02 or >= 0x08 and <= 0x0F)
        {
            if (img.Palette is not { } pal) return "The image has no palette block to write.";
            if (pal.Header.Type is not (0x21 or 0x22 or 0x29 or 0x2A or 0x2D)) return $"Palette type 0x{pal.Header.Type:X2} ({pal.Kind}) is not supported for writing.";
        }
        try
        {
            if (t is >= 8 and <= 15) Gst.ValidateLayout(img.Header.Width, img.Header.Height, t, img.Header.FlagSwizzled);
            var expected = ExpectedBytes(img);
            var region = file.Data.AsSpan(img.DataOffset, img.DataLength);
            var length = (img.Header.Type & 0x80) != 0 && RefPack.IsCompressed(region) ? RefPack.Decompress(region).Length : region.Length;
            if (length < expected) return "Truncated native texture payload.";
        }
        catch (Exception ex) { return ex.Message; }
        return null;
    }

    private static string? UnsupportedXbox(SshFile file, SshImage img, int t)
    {
        var h = img.Header;
        if (t is not (0x60 or 0x61 or 0x62 or 0x7B or 0x7D)) return $"{SshFile.TypeName(h.Type)} is not supported for writing.";
        if (h.FlagReferenced || h.FlagTransposed) return "Referenced/transposed storage has not been verified for writing.";
        if (h.FlagSwizzled && (!XboxTexture.IsPowerOfTwo(h.Width) || !XboxTexture.IsPowerOfTwo(h.Height))) return "Swizzled Xbox textures must have power-of-two dimensions.";
        if (t is 0x60 or 0x61 or 0x62 && (h.Width % 4 != 0 || h.Height % 4 != 0)) return "DXT textures need dimensions that are multiples of 4.";
        if (t == 0x7B)
        {
            if (img.Palette is not { } pal) return "The image has no palette block to write.";
            if (pal.Header.Type is not (0x2A or 0x2D or 0x21)) return $"Palette type 0x{pal.Header.Type:X2} ({pal.Kind}) is not supported for writing.";
        }
        try
        {
            var expected = ExpectedBytes(img);
            var region = file.Data.AsSpan(img.DataOffset, img.DataLength);
            var length = (h.Type & 0x80) != 0 && RefPack.IsCompressed(region) ? RefPack.Decompress(region).Length : region.Length;
            if (length < expected) return "Truncated native texture payload.";
        }
        catch (Exception ex) { return ex.Message; }
        return null;
    }

    /// <summary>Bytes of one Xbox mip level.</summary>
    private static int XboxLevelBytes(int t, int w, int h) => t switch
    {
        0x60 => XboxTexture.DxtSize(w, h, 1),
        0x61 => XboxTexture.DxtSize(w, h, 3),
        0x62 => XboxTexture.DxtSize(w, h, 5),
        0x7B => w * h,
        _ => w * h * 4,
    };

    /// <summary>Bytes of an Xbox base level plus <paramref name="mips"/> successively halved levels stored back to back.</summary>
    private static int XboxChainBytes(int t, int w, int h, int mips)
    {
        var total = 0;
        for (var m = 0; m <= mips; m++) total += XboxLevelBytes(t, Math.Max(1, w >> m), Math.Max(1, h >> m));
        return total;
    }

    /// <summary>Half-size box filter on straight-alpha BGRA (colour weighted by alpha so transparent texels do not bleed).</summary>
    private static (byte[] Bgra, int W, int H) Downsample(byte[] bgra, int w, int h)
    {
        int nw = Math.Max(1, w / 2), nh = Math.Max(1, h / 2);
        var o = new byte[nw * nh * 4];
        for (var y = 0; y < nh; y++)
            for (var x = 0; x < nw; x++)
            {
                double a = 0, b = 0, g = 0, r = 0; var n = 0;
                for (var dy = 0; dy < 2; dy++)
                    for (var dx = 0; dx < 2; dx++)
                    {
                        int sx = Math.Min(w - 1, x * 2 + dx), sy = Math.Min(h - 1, y * 2 + dy);
                        var i = (sy * w + sx) * 4; var pa = bgra[i + 3];
                        b += bgra[i] * pa; g += bgra[i + 1] * pa; r += bgra[i + 2] * pa; a += pa; n++;
                    }
                var d = (y * nw + x) * 4;
                if (a > 0) { o[d] = (byte)Math.Round(b / a); o[d + 1] = (byte)Math.Round(g / a); o[d + 2] = (byte)Math.Round(r / a); }
                o[d + 3] = (byte)Math.Round(a / n);
            }
        return (o, nw, nh);
    }

    /// <summary>Largest allowed texture upscale factor.</summary>
    public const int MaxUpscale = 4;

    /// <summary>Storage restrictions for experimental native upscales. Consumer/VRAM compatibility is a separate runtime concern.</summary>
    public static string? ResizeUnsupported(SshFile file, SshImage img, int width, int height)
    {
        if (Unsupported(file, img) is { } why) return why;
        var h = img.Header;
        if (width == h.Width && height == h.Height) return null;
        if (file.IsXbox) return "Native resizing of Xbox textures is not verified; use the original dimensions.";
        if (width > 2048 || height > 2048 || width < h.Width || height < h.Height || width <= 0 || height <= 0) return "Native upscales must stay between source dimensions and 2048×2048.";
        int scale = width / h.Width;
        if (scale is < 2 or > MaxUpscale || width != h.Width * scale || height != h.Height * scale)
            return $"Use a uniform upscale of 2× to {MaxUpscale}× the original size.";
        // swizzled layouts are only verified for power-of-two sizes; linear storage has no such constraint
        if (h.FlagSwizzled && ((width & (width - 1)) != 0 || (height & (height - 1)) != 0))
            return "Swizzled textures need power-of-two upscaled dimensions.";
        if (img.TypeMasked == 5 && h.FlagSwizzled) return "Swizzled RGBA8888 upscale storage is unverified.";
        if (h.CenterX != 0 || h.CenterY != 0 || h.PosX != 0 || h.PosY != 0 || img.Attachments.Any(a => a.Header.Type == 0x7C))
            return "Positioned, centered or hotspot textures require consumer-coordinate updates before native resizing.";
        if (img.TypeMasked is >= 8 and <= 15 && (width % 8 != 0 || height % 2 != 0)) return "Incomplete GST blocks.";
        return null;
    }

    public static int ExpectedBytes(SshImage img)
    {
        var h = img.Header;
        return (h.Type & 0x7F) switch
        {
            0x01 => (h.Width * h.Height + 1) / 2,
            0x02 => h.Width * h.Height,
            0x04 => h.Width * h.Height * 3,
            0x05 => h.Width * h.Height * 4,
            0x60 or 0x61 or 0x62 or 0x7B or 0x7D => XboxChainBytes(h.Type & 0x7F, h.Width, h.Height, h.Mipmaps),
            >= 0x08 and <= 0x0F => Gst.TotalSize(h.Width, h.Height, h.Type),
            _ => throw new NotSupportedException("Unsupported image type."),
        };
    }

    /// <summary>Encode BGRA at the requested dimensions. Native upscales require explicit opt-in; splice into a copy and verify decoding.</summary>
    public static SshReplaceResult ReplaceImage(SshFile file, SshImage img, byte[] bgra, int width, int height, PaletteMode mode = PaletteMode.Rebuild, bool allowResize = false)
    {
        if (Unsupported(file, img) is { } why) throw new NotSupportedException(why);
        var h = img.Header;
        if (width <= 0 || height <= 0 || bgra.Length != checked(width * height * 4)) throw new ArgumentException("Invalid BGRA buffer or dimensions.");
        var resizing = width != h.Width || height != h.Height;
        if (resizing)
        {
            if (!allowResize) throw new ArgumentException($"The picture is {width}×{height} but this image is {h.Width}×{h.Height}.");
            if (ResizeUnsupported(file, img, width, height) is { } resizeWhy) throw new NotSupportedException(resizeWhy);
        }
        var t = h.Type & 0x7F;
        var notes = new List<string>();
        var data = (byte[])file.Data.Clone();
        var swz = h.FlagSwizzled;
        if (resizing)
        {
            BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(h.Offset + 4), (ushort)width);
            BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(h.Offset + 6), (ushort)height);
            notes.Add($"native dimensions retained: {h.Width}×{h.Height} → {width}×{height}; runtime consumer compatibility unverified");
        }

        byte[] pixels;
        byte[]? newPalette = null;
        if (file.IsXbox)
            (pixels, newPalette) = EncodeXbox(file, img, t, bgra, width, height, mode, notes);
        else if (t == 0x04)
        {
            pixels = new byte[width * height * 3];
            for (var i = 0; i < width * height; i++)
            { pixels[i * 3] = bgra[i * 4 + 2]; pixels[i * 3 + 1] = bgra[i * 4 + 1]; pixels[i * 3 + 2] = bgra[i * 4]; }
            notes.Add("RGB888 discards alpha");
        }
        else if (t == 0x05)
        {
            pixels = new byte[width * height * 4];
            for (var i = 0; i < width * height; i++)
            {
                pixels[i * 4] = bgra[i * 4 + 2]; pixels[i * 4 + 1] = bgra[i * 4 + 1]; pixels[i * 4 + 2] = bgra[i * 4];
                pixels[i * 4 + 3] = ToPs2Alpha(bgra[i * 4 + 3]);
            }
        }
        else
        {
            var pal = img.Palette!;
            var colors = Math.Max(1, pal.Header.Width * Math.Max(1, pal.Header.Height));
            var limit = Math.Min(colors, t == 0x01 ? 16 : 256);
            byte[] indices;
            byte[] palBgra;
            if (mode == PaletteMode.KeepExisting)
            {
                palBgra = ReadPalette(file, img);
                indices = Quantizer.MapToPalette(bgra, palBgra, limit);
                notes.Add("mapped to the existing palette");
            }
            else
            {
                (palBgra, indices) = Quantizer.Build(bgra, limit);
                notes.Add($"new {limit}-entry palette ({Quantizer.LastUniqueColors:N0} distinct colours{(Quantizer.LastUniqueColors > limit ? ", reduced" : ", exact")})");
                newPalette = EncodePalette(file, img, palBgra, colors);
            }
            if (t >= 0x08)
            {
                pixels = Gst.Encode(bgra, palBgra, limit, width, height, t, swz);
                notes.Add("lossy GST palette-window fitting");
            }
            else if (t == 0x02) pixels = swz ? Ps2Swizzle.Swizzle8(indices, width, height) : indices;
            else
            {
                var packed = new byte[(width * height + 1) / 2];
                for (var i = 0; i < indices.Length; i++) packed[i / 2] |= (byte)((i & 1) == 0 ? indices[i] & 0x0F : indices[i] << 4);
                pixels = swz ? Ps2Swizzle.Swizzle4(packed, width, height) : packed;
            }
        }

        // keep whatever followed the base pixels (mip levels, padding) exactly as it was
        var oldRegion = data.AsSpan(img.DataOffset, img.DataLength);
        byte[] region;
        var compressedBefore = (h.Type & 0x80) != 0 && RefPack.IsCompressed(oldRegion);
        if (compressedBefore)
        {
            var original = RefPack.Decompress(oldRegion);
            var expected = ExpectedBytes(img);
            if (original.Length < expected) throw new InvalidDataException("Truncated compressed pixel data.");
            if (resizing) { var grown = new byte[checked(pixels.Length + original.Length - expected)]; original.AsSpan(expected).CopyTo(grown.AsSpan(pixels.Length)); original = grown; }
            else PreserveGstPadding(original);
            pixels.CopyTo(original, 0);
            region = RefPack.Compress(original, RefPack.StyleOf(oldRegion));
            notes.Add("RefPack re-compressed");
        }
        else
        {
            var expected = ExpectedBytes(img);
            if (oldRegion.Length < expected) throw new InvalidDataException("The original pixel data is shorter than its dimensions imply.");
            if (!resizing) PreserveGstPadding(oldRegion);
            region = new byte[resizing ? checked(pixels.Length + oldRegion.Length - expected) : oldRegion.Length];
            if (resizing) oldRegion[expected..].CopyTo(region.AsSpan(pixels.Length)); else oldRegion.CopyTo(region);
            pixels.CopyTo(region, 0);
        }

        var result = Splice(file, data, img, region, newPalette, out var spliceNote);
        var reopened = SshFile.Read(result);
        _ = reopened.Decode(reopened.Images.Single(i => i.Index == img.Index));
        if (spliceNote.Length > 0) notes.Add(spliceNote);
        return new SshReplaceResult(result, string.Join(", ", notes));

        void PreserveGstPadding(ReadOnlySpan<byte> original)
        {
            if (t is < 8 or > 15) return;
            var start = Gst.BaseSize(width, height, t);
            var end = (start + 15) & ~15;
            original.Slice(start, end - start).CopyTo(pixels.AsSpan(start));
        }
    }

    private static (byte[] Pixels, byte[]? Palette) EncodeXbox(SshFile file, SshImage img, int t, byte[] bgra, int w, int h, PaletteMode mode, List<string> notes)
    {
        var swz = img.Header.FlagSwizzled;
        var mips = img.Header.Mipmaps;
        byte[]? newPalette = null;
        byte[]? palBgra = null, baseIndices = null;
        var limit = 256;
        if (t == 0x7B)
        {
            var pal = img.Palette!;
            var colors = Math.Max(1, pal.Header.Width * Math.Max(1, pal.Header.Height));
            limit = Math.Min(colors, 256);
            if (mode == PaletteMode.KeepExisting)
            {
                palBgra = ReadPalette(file, img);
                notes.Add("mapped to the existing palette");
            }
            else
            {
                (palBgra, baseIndices) = Quantizer.Build(bgra, limit);
                notes.Add($"new {limit}-entry palette ({Quantizer.LastUniqueColors:N0} distinct colours{(Quantizer.LastUniqueColors > limit ? ", reduced" : ", exact")})");
                var region = new byte[pal.DataLength];
                file.Data.AsSpan(pal.DataOffset, pal.DataLength).CopyTo(region);
                if (palBgra.Length > region.Length) throw new InvalidDataException("Palette block is smaller than its colour count.");
                palBgra.CopyTo(region, 0);
                newPalette = region;
            }
        }
        else if (t is 0x60 or 0x61 or 0x62) notes.Add("lossy DXT block fit");
        if (mips > 0) notes.Add($"{mips} mip level{(mips == 1 ? "" : "s")} regenerated");

        using var chain = new MemoryStream();
        var (lw, lh, level) = (w, h, bgra);
        for (var m = 0; m <= mips; m++)
        {
            byte[] bytes = t switch
            {
                0x7D => swz ? XboxTexture.Swizzle(level, lw, lh, 4) : level,
                0x7B => PaletteLevel(level, lw, lh),
                _ => XboxTexture.EncodeDxt(level, lw, lh, t == 0x60 ? 1 : t == 0x61 ? 3 : 5),
            };
            chain.Write(bytes);
            if (m < mips) (level, lw, lh) = Downsample(level, lw, lh);
        }
        return (chain.ToArray(), newPalette);

        byte[] PaletteLevel(byte[] src, int lw, int lh)
        {
            var idx = ReferenceEquals(src, bgra) && baseIndices is not null ? baseIndices : Quantizer.MapToPalette(src, palBgra!, limit);
            return swz ? XboxTexture.Swizzle(idx, lw, lh, 1) : idx;
        }
    }

    /// <summary>Palette of the image as BGRA with display alpha, exactly as <see cref="SshFile.Decode"/> reads it.</summary>
    private static byte[] ReadPalette(SshFile file, SshImage img)
    {
        var decoded = file.Decode(img);
        return decoded.PaletteBgra ?? throw new InvalidDataException("No palette.");
    }

    private static byte[] EncodePalette(SshFile file, SshImage img, byte[] palBgra, int colors)
    {
        var pal = img.Palette!;
        var raw = new byte[colors * 4];
        for (var i = 0; i < Math.Min(colors, palBgra.Length / 4); i++)
        {
            raw[i * 4] = palBgra[i * 4 + 2]; raw[i * 4 + 1] = palBgra[i * 4 + 1]; raw[i * 4 + 2] = palBgra[i * 4]; raw[i * 4 + 3] = ToPs2Alpha(palBgra[i * 4 + 3]);
        }
        if (pal.Header.FlagSwizzled && colors >= 32) raw = Ps2Swizzle.UnswizzlePalette(raw);   // the CSM1 shuffle is its own inverse
        if (raw.Length > pal.DataLength) throw new InvalidDataException("Palette block is smaller than its colour count.");
        var region = new byte[pal.DataLength];
        file.Data.AsSpan(pal.DataOffset, pal.DataLength).CopyTo(region);
        raw.CopyTo(region, 0);
        return region;
    }

    // PS2 alpha is 0..0x80 with 0x80 opaque; SshFile.Decode shows it doubled (0x80+ → 255).
    public static byte ToPs2Alpha(byte display) => display >= 255 ? (byte)0x80 : (byte)(display / 2);

    private static byte[] Splice(SshFile file, byte[] data, SshImage img, byte[] region, byte[]? paletteRegion, out string note)
    {
        note = "";
        var old = img.DataLength;
        if (region.Length <= old)
        {
            var padded = new byte[old];
            region.CopyTo(padded, 0);
            // leave the unused tail of the old region as it was rather than zeroing it
            data.AsSpan(img.DataOffset + region.Length, old - region.Length).CopyTo(padded.AsSpan(region.Length));
            padded.CopyTo(data, img.DataOffset);
            if (paletteRegion is not null) paletteRegion.CopyTo(data, img.Palette!.DataOffset);
            return data;
        }

        // grow: keep the 16-byte alignment the format uses between blocks
        var newLen = (region.Length + 15) & ~15;
        var delta = newLen - old;
        var cut = img.DataOffset + old;
        var output = new byte[data.Length + delta];
        data.AsSpan(0, img.DataOffset).CopyTo(output);
        region.CopyTo(output, img.DataOffset);
        data.AsSpan(cut).CopyTo(output.AsSpan(cut + delta));
        if (paletteRegion is not null) paletteRegion.CopyTo(output, img.Palette!.DataOffset >= cut ? img.Palette.DataOffset + delta : img.Palette.DataOffset);

        // directory offsets of every block that moved
        var count = (int)Bin.U32LE(output, 8);
        for (var i = 0; i < count; i++)
        {
            var p = 16 + i * 8 + 4;
            var off = BinaryPrimitives.ReadUInt32LittleEndian(output.AsSpan(p));
            if (off > img.Header.Offset) BinaryPrimitives.WriteUInt32LittleEndian(output.AsSpan(p), (uint)(off + delta));
        }
        // the image header's block size spans header + pixels when attachments follow
        var bs = img.Header.BlockSize;
        if (bs > 0 && bs == 16 + old)
        {
            output[img.Header.Offset + 1] = (byte)(bs + delta); output[img.Header.Offset + 2] = (byte)((bs + delta) >> 8); output[img.Header.Offset + 3] = (byte)((bs + delta) >> 16);
        }
        BinaryPrimitives.WriteUInt32LittleEndian(output.AsSpan(4), (uint)output.Length);
        note = $"file grew by {delta:N0} bytes; later blocks shifted";
        return output;
    }
}
