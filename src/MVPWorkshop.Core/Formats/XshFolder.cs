using System.Buffers.Binary;
using System.Text;

namespace MVPWorkshop.Core.Formats;

/// <summary>
/// An Xbox SHPX pack (<c>.xsh</c>) as a folder of editable images (docs/LOOSE_ASSETS.md in the port): one <c>.dds</c> per
/// DXT1/DXT3/BGRA8888 image, one raw <c>.bin</c> for any other type, and <c>xsh.meta</c> holding every other byte, so
/// <see cref="Build"/> gives back the original file exactly while the images are unchanged.
/// </summary>
/// <remarks>
/// xsh.meta, little endian: "XSHM", u32 version (1), u32 head length + head (file start up to the first block: header,
/// directory, group name), u32 image count, then per image in file order: u32 directory index, u8 kind (0 DDS, 1 raw),
/// u16 name length + name, the 16-byte block header, u32 pad length (zeros inside the block after the pixels),
/// u32 rest length + rest (attachments up to the next block). The runtime's C++ reader (sdk/runtime/kernel/xsh.cpp) must match.
/// </remarks>
public static class XshFolder
{
    public const string MetaName = "xsh.meta";
    const int Version = 1;

    public sealed record Image(int DirIndex, string File, bool Dds, byte[] Header, int Pad, byte[] Rest);

    /// <summary>Splits an .xsh; null when it isn't an SHPX this layout can hold exactly (it then stays a plain file).</summary>
    public static Dictionary<string, byte[]>? Split(byte[] d)
    {
        if (d.Length < 16 || Encoding.ASCII.GetString(d, 0, 4) != "SHPX") return null;
        var count = BinaryPrimitives.ReadInt32LittleEndian(d.AsSpan(8));
        if (count <= 0 || count > 10000 || 16 + count * 8 > d.Length) return null;
        var dir = new (int Index, string Tag, int Off)[count];
        for (var i = 0; i < count; i++)
            dir[i] = (i, Encoding.ASCII.GetString(d, 16 + i * 8, 4).TrimEnd('\0'), BinaryPrimitives.ReadInt32LittleEndian(d.AsSpan(16 + i * 8 + 4)));
        var order = dir.OrderBy(x => x.Off).ToArray();
        if (order[0].Off < 16 + count * 8 || order[^1].Off + 16 > d.Length) return null;
        for (var i = 1; i < count; i++) if (order[i].Off <= order[i - 1].Off) return null;  // shared blocks

        var files = new Dictionary<string, byte[]>();
        var images = new List<Image>();
        for (var i = 0; i < count; i++)
        {
            var (index, tag, off) = order[i];
            var end = i + 1 < count ? order[i + 1].Off : d.Length;
            var h = SshBlockHeader.Read(d, off);
            var blockEnd = h.BlockSize > 0 ? off + h.BlockSize : end;
            if (blockEnd > end || blockEnd < off + 16) return null;
            var type = h.Type;
            var pixels = PixelBytes(type, h.Width, h.Height, h.Mipmaps);
            // BGRA must be swizzled and DXT linear, as on the disc; anything else stays raw
            var dds = pixels > 0 && off + 16 + pixels <= blockEnd && h.FlagSwizzled == (type == 0x7D) && AllZero(d, off + 16 + pixels, blockEnd);
            if (type == 0x7D && !(XboxTexture.IsPowerOfTwo(h.Width) && XboxTexture.IsPowerOfTwo(h.Height))) dds = false;
            var safe = new string(tag.Select(c => char.IsLetterOrDigit(c) ? c : '_').ToArray());
            var name = $"{i:D2}_{safe}" + (dds ? ".dds" : ".bin");
            var pixelLen = dds ? pixels : blockEnd - off - 16;
            var px = d.AsSpan(off + 16, pixelLen);
            files[name] = dds ? WriteDds(type, h.Width, h.Height, h.Mipmaps, px) : px.ToArray();
            images.Add(new Image(index, name, dds, d.AsSpan(off, 16).ToArray(), blockEnd - off - 16 - pixelLen, d.AsSpan(blockEnd, end - blockEnd).ToArray()));
        }
        var meta = WriteMeta(d.AsSpan(0, order[0].Off).ToArray(), images);
        files[MetaName] = meta;
        // only a layout that rebuilds exactly is used
        var back = Build(meta, n => files[n], out _);
        return back != null && back.AsSpan().SequenceEqual(d) ? files : null;
    }

    /// <summary>
    /// Rebuilds the .xsh from xsh.meta and the image files. An image saved at another size or format is converted to the
    /// slot's type; one that can't be is left as the original bytes are unknown here, so the build fails with a reason.
    /// </summary>
    public static byte[]? Build(byte[] meta, Func<string, byte[]?> open, out string? error)
    {
        error = null;
        if (!ReadMeta(meta, out var head, out var images)) { error = "xsh.meta is damaged"; return null; }
        var outp = new MemoryStream();
        outp.Write(head);
        var dirOffsets = new int[images.Count];
        var k = 0;
        foreach (var img in images)
        {
            var file = open(img.File);
            if (file == null) { error = $"{img.File} is missing"; return null; }
            var header = (byte[])img.Header.Clone();
            byte[] pixels;
            if (img.Dds)
            {
                pixels = FromDds(file, header, out error)!;
                if (pixels == null) { error = $"{img.File}: {error}"; return null; }
            }
            else
            {
                pixels = file;
            }
            var size = BinaryPrimitives.ReadUInt32LittleEndian(header) >> 8;
            if (size != 0)
            {
                var bs = 16 + pixels.Length + img.Pad;
                header[1] = (byte)bs; header[2] = (byte)(bs >> 8); header[3] = (byte)(bs >> 16);
            }
            dirOffsets[k++] = (int)outp.Position;
            outp.Write(header);
            outp.Write(pixels);
            outp.Write(new byte[img.Pad]);
            outp.Write(img.Rest);
        }
        var result = outp.ToArray();
        if (result.Length < head.Length || head.Length < 16) { error = "xsh.meta is damaged"; return null; }
        BinaryPrimitives.WriteInt32LittleEndian(result.AsSpan(4), result.Length);
        for (var i = 0; i < images.Count; i++)
        {
            var at = 16 + images[i].DirIndex * 8 + 4;
            if (at + 4 > head.Length) { error = "xsh.meta is damaged"; return null; }
            BinaryPrimitives.WriteInt32LittleEndian(result.AsSpan(at), dirOffsets[i]);
        }
        return result;
    }

    /// <summary>Bytes of an image's pixels with its mip chain, 0 for a type kept raw.</summary>
    public static int PixelBytes(int type, int w, int h, int mips)
    {
        var total = 0;
        for (var m = 0; m <= mips; m++)
        {
            int lw = Math.Max(1, w >> m), lh = Math.Max(1, h >> m);
            total += type switch { 0x60 => XboxTexture.DxtSize(lw, lh, 1), 0x61 => XboxTexture.DxtSize(lw, lh, 3), 0x7D => lw * lh * 4, _ => 0 };
            if (total == 0) return 0;
        }
        return total;
    }

    static bool AllZero(byte[] d, int from, int to)
    {
        for (var i = from; i < to; i++) if (d[i] != 0) return false;
        return true;
    }

    // ---- DDS ----

    const uint DdsdCaps = 0x1, DdsdHeight = 0x2, DdsdWidth = 0x4, DdsdPitch = 0x8, DdsdPixelFormat = 0x1000, DdsdMipMapCount = 0x20000, DdsdLinearSize = 0x80000;
    const uint DdpfAlphaPixels = 0x1, DdpfFourCC = 0x4, DdpfRgb = 0x40;

    static byte[] WriteDds(int type, int w, int h, int mips, ReadOnlySpan<byte> px)
    {
        var data = px.ToArray();
        if (type == 0x7D)
        {
            // each level is swizzled on its own
            var pos = 0;
            for (var m = 0; m <= mips; m++)
            {
                int lw = Math.Max(1, w >> m), lh = Math.Max(1, h >> m);
                XboxTexture.Unswizzle(px.Slice(pos, lw * lh * 4), lw, lh, 4).CopyTo(data, pos);
                pos += lw * lh * 4;
            }
        }
        var hdr = new byte[128];
        var s = hdr.AsSpan();
        Encoding.ASCII.GetBytes("DDS ").CopyTo(s);
        var flags = DdsdCaps | DdsdHeight | DdsdWidth | DdsdPixelFormat | (type == 0x7D ? DdsdPitch : DdsdLinearSize) | (mips > 0 ? DdsdMipMapCount : 0);
        BinaryPrimitives.WriteUInt32LittleEndian(s[4..], 124);
        BinaryPrimitives.WriteUInt32LittleEndian(s[8..], flags);
        BinaryPrimitives.WriteInt32LittleEndian(s[12..], h);
        BinaryPrimitives.WriteInt32LittleEndian(s[16..], w);
        BinaryPrimitives.WriteInt32LittleEndian(s[20..], type == 0x7D ? w * 4 : XboxTexture.DxtSize(w, h, type == 0x60 ? 1 : 3));
        BinaryPrimitives.WriteInt32LittleEndian(s[28..], mips + 1);
        BinaryPrimitives.WriteUInt32LittleEndian(s[76..], 32);
        if (type == 0x7D)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(s[80..], DdpfRgb | DdpfAlphaPixels);
            BinaryPrimitives.WriteUInt32LittleEndian(s[88..], 32);
            BinaryPrimitives.WriteUInt32LittleEndian(s[92..], 0x00FF0000);
            BinaryPrimitives.WriteUInt32LittleEndian(s[96..], 0x0000FF00);
            BinaryPrimitives.WriteUInt32LittleEndian(s[100..], 0x000000FF);
            BinaryPrimitives.WriteUInt32LittleEndian(s[104..], 0xFF000000);
        }
        else
        {
            BinaryPrimitives.WriteUInt32LittleEndian(s[80..], DdpfFourCC);
            Encoding.ASCII.GetBytes(type == 0x60 ? "DXT1" : "DXT3").CopyTo(s[84..]);
        }
        BinaryPrimitives.WriteUInt32LittleEndian(s[108..], 0x1000 | (mips > 0 ? 0x400008u : 0));  // TEXTURE, COMPLEX|MIPMAP
        return [.. hdr, .. data];
    }

    /// <summary>A DDS's format: 0x60/0x61/0x62 for DXT1/3/5, 0x7D for 32-bit BGRA, 0 for anything else.</summary>
    public static int DdsType(ReadOnlySpan<byte> f, out int w, out int h, out int levels, out int dataOffset)
    {
        w = h = levels = dataOffset = 0;
        if (f.Length < 128 || !f[..4].SequenceEqual("DDS "u8)) return 0;
        h = BinaryPrimitives.ReadInt32LittleEndian(f[12..]);
        w = BinaryPrimitives.ReadInt32LittleEndian(f[16..]);
        var flags = BinaryPrimitives.ReadUInt32LittleEndian(f[8..]);
        levels = (flags & DdsdMipMapCount) != 0 ? Math.Max(1, BinaryPrimitives.ReadInt32LittleEndian(f[28..])) : 1;
        dataOffset = 128;
        var pf = BinaryPrimitives.ReadUInt32LittleEndian(f[80..]);
        var fourcc = Encoding.ASCII.GetString(f.Slice(84, 4));
        if ((pf & DdpfFourCC) != 0)
        {
            if (fourcc == "DX10")
            {
                if (f.Length < 148) return 0;
                dataOffset = 148;
                return BinaryPrimitives.ReadUInt32LittleEndian(f[128..]) switch { 71 or 72 => 0x60, 74 or 75 => 0x61, 77 or 78 => 0x62, 87 or 91 => 0x7D, _ => 0 };
            }
            return fourcc switch { "DXT1" => 0x60, "DXT2" or "DXT3" => 0x61, "DXT4" or "DXT5" => 0x62, _ => 0 };
        }
        var bits = BinaryPrimitives.ReadUInt32LittleEndian(f[88..]);
        var r = BinaryPrimitives.ReadUInt32LittleEndian(f[92..]);
        var b = BinaryPrimitives.ReadUInt32LittleEndian(f[100..]);
        return (pf & DdpfRgb) != 0 && bits == 32 && r == 0x00FF0000 && b == 0x000000FF ? 0x7D : 0;
    }

    /// <summary>A DDS as the slot's pixels: copied when it matches, otherwise converted; updates size and mips in the header.</summary>
    static byte[]? FromDds(byte[] file, byte[] header, out string? error)
    {
        error = null;
        var slot = header[0];
        var oldMips = BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(14)) >> 12;
        var type = DdsType(file, out var w, out var h, out var levels, out var at);
        if (type == 0) { error = "not a DXT1/DXT3/DXT5 or 32-bit BGRA DDS"; return null; }
        if (w <= 0 || h <= 0 || w > 4096 || h > 4096) { error = $"bad size {w}x{h}"; return null; }
        if (slot == 0x7D && !(XboxTexture.IsPowerOfTwo(w) && XboxTexture.IsPowerOfTwo(h))) { error = $"{w}x{h}: this texture needs power-of-two sides"; return null; }
        levels = Math.Min(levels, 13);
        // the chain the DDS holds, or level 0 only if it's short
        var have = 0;
        var pos = at;
        for (var m = 0; m < levels; m++)
        {
            var n = PixelBytes(type == 0x62 ? 0x61 : type, Math.Max(1, w >> m), Math.Max(1, h >> m), 0);
            if (pos + n > file.Length) break;
            pos += n;
            have++;
        }
        if (have == 0) { error = "the file is cut short"; return null; }

        byte[] pixels;
        int mips;
        if (type == slot)
        {
            mips = have - 1;
            pixels = file.AsSpan(at, pos - at).ToArray();
            if (slot == 0x7D)
            {
                var p = 0;
                for (var m = 0; m <= mips; m++)
                {
                    int lw = Math.Max(1, w >> m), lh = Math.Max(1, h >> m);
                    XboxTexture.Swizzle(pixels.AsSpan(p, lw * lh * 4), lw, lh, 4).CopyTo(pixels, p);
                    p += lw * lh * 4;
                }
            }
        }
        else
        {
            // another format: decode level 0, rebuild the slot's mip count by halving
            var bgra = type == 0x7D ? file.AsSpan(at, w * h * 4).ToArray() : XboxTexture.DecodeDxt(file.AsSpan(at), w, h, type == 0x60 ? 1 : type == 0x61 ? 3 : 5);
            mips = Math.Min(oldMips, (int)Math.Log2(Math.Max(w, h)));
            var outp = new MemoryStream();
            int lw = w, lh = h;
            for (var m = 0; m <= mips; m++)
            {
                outp.Write(slot == 0x7D ? XboxTexture.Swizzle(bgra, lw, lh, 4) : XboxTexture.EncodeDxt(bgra, lw, lh, slot == 0x60 ? 1 : 3));
                if (m == mips) break;
                (bgra, lw, lh) = Half(bgra, lw, lh);
            }
            pixels = outp.ToArray();
        }
        BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(4), (ushort)w);
        BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(6), (ushort)h);
        var ry = BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(14));
        BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(14), (ushort)((ry & 0x0FFF) | mips << 12));
        return pixels;
    }

    static (byte[], int, int) Half(byte[] src, int w, int h)
    {
        int nw = Math.Max(1, w / 2), nh = Math.Max(1, h / 2);
        var dst = new byte[nw * nh * 4];
        for (var y = 0; y < nh; y++)
        for (var x = 0; x < nw; x++)
        for (var c = 0; c < 4; c++)
        {
            int x0 = Math.Min(w - 1, x * 2), x1 = Math.Min(w - 1, x * 2 + 1), y0 = Math.Min(h - 1, y * 2), y1 = Math.Min(h - 1, y * 2 + 1);
            dst[(y * nw + x) * 4 + c] = (byte)((src[(y0 * w + x0) * 4 + c] + src[(y0 * w + x1) * 4 + c] + src[(y1 * w + x0) * 4 + c] + src[(y1 * w + x1) * 4 + c] + 2) / 4);
        }
        return (dst, nw, nh);
    }

    // ---- xsh.meta ----

    static byte[] WriteMeta(byte[] head, List<Image> images)
    {
        var ms = new MemoryStream();
        var bw = new BinaryWriter(ms);
        bw.Write("XSHM"u8);
        bw.Write(Version);
        bw.Write(head.Length); bw.Write(head);
        bw.Write(images.Count);
        foreach (var img in images)
        {
            var name = Encoding.ASCII.GetBytes(img.File);
            bw.Write(img.DirIndex);
            bw.Write((byte)(img.Dds ? 0 : 1));
            bw.Write((ushort)name.Length); bw.Write(name);
            bw.Write(img.Header);
            bw.Write(img.Pad);
            bw.Write(img.Rest.Length); bw.Write(img.Rest);
        }
        return ms.ToArray();
    }

    public static bool ReadMeta(byte[] meta, out byte[] head, out List<Image> images)
    {
        head = [];
        images = [];
        try
        {
            var br = new BinaryReader(new MemoryStream(meta));
            if (Encoding.ASCII.GetString(br.ReadBytes(4)) != "XSHM" || br.ReadInt32() != Version) return false;
            head = br.ReadBytes(br.ReadInt32());
            var n = br.ReadInt32();
            for (var i = 0; i < n; i++)
            {
                var index = br.ReadInt32();
                var dds = br.ReadByte() == 0;
                var name = Encoding.ASCII.GetString(br.ReadBytes(br.ReadUInt16()));
                var header = br.ReadBytes(16);
                var pad = br.ReadInt32();
                var rest = br.ReadBytes(br.ReadInt32());
                images.Add(new Image(index, name, dds, header, pad, rest));
            }
            return br.BaseStream.Position == meta.Length;
        }
        catch (EndOfStreamException)
        {
            return false;
        }
    }
}
