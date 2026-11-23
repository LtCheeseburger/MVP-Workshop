using MVPWorkshop.Core.IO;

namespace MVPWorkshop.Core.Formats;

/// <summary>Xbox MMAP v2 bitmap, palette and file tables. Each file's base bitmap and palette have 128-byte resource headers.</summary>
public sealed class MmapTexture
{
    public sealed record Image(int Index, int FileIndex, int Mip, string Name, int Width, int Height, uint Format, int Offset, int Length, int PaletteIndex)
    {
        public string Label => $"{Name} · mip {Mip} · {Width}×{Height} · {FormatName(Format)}";
    }
    public sealed record Palette(int Offset, int Length);
    public byte[] Data { get; private init; } = [];
    public List<Image> Images { get; } = [];
    public List<Palette> Palettes { get; } = [];
    public IReadOnlyList<string> SharedPixelFiles(Image image)
    {
        var owned = Images.Where(i => i.FileIndex == image.FileIndex).Select(i => (i.Offset, i.Length)).ToHashSet();
        return Images.Where(i => i.FileIndex != image.FileIndex && owned.Contains((i.Offset, i.Length)))
            .Select(i => i.Name).Distinct().ToArray();
    }
    public static string FormatName(uint id) => id switch { 6 => "BGRA32", 11 => "PAL8", 19 => "Linear L8", _ => $"Unknown {id}" };

    public static MmapTexture Read(byte[] data)
    {
        void Bounds(long offset, long length)
        {
            if (offset < 0 || length < 0 || offset > data.Length || length > data.Length - offset)
                throw new InvalidDataException("MMAP table or payload exceeds file bounds.");
        }
        Bounds(0, 40);
        if (Bin.FourCC(data) != "MMAP" || Bin.U32LE(data, 4) != 2) throw new NotSupportedException("Only Xbox MMAP v2 is supported.");
        int files = Bin.U16LE(data, 12), bitmaps = Bin.U16LE(data, 14);
        var palettes = checked((int)Bin.U32LE(data, 16));
        int fileInfo = checked((int)Bin.U32LE(data, 20)), bitmapTable = checked((int)Bin.U32LE(data, 24)), paletteTable = checked((int)Bin.U32LE(data, 28)), names = checked((int)Bin.U32LE(data, 32));
        Bounds(fileInfo, files * 12L); Bounds(names, files * 16L); Bounds(bitmapTable, bitmaps * 16L); Bounds(paletteTable, palettes * 12L);
        var result = new MmapTexture { Data = data };
        for (int i = 0; i < palettes; i++)
        {
            int p = paletteTable + i * 12, size = checked((int)Bin.U32LE(data, p + 4)), offset = checked((int)Bin.U32LE(data, p + 8) + 128);
            if (Bin.U32LE(data, p) != 1 || size != 1024) throw new NotSupportedException("Unsupported MMAP palette layout.");
            Bounds(offset, size); result.Palettes.Add(new(offset, size));
        }
        var owners = new (int File, int Mip, int Palette, string Name)?[bitmaps];
        var aliases = new List<(int Bitmap, int File, int Mip, int Palette, string Name)>();
        for (int file = 0; file < files; file++)
        {
            int p = fileInfo + file * 12, pals = Bin.U16LE(data, p), mips = Bin.U16LE(data, p + 2);
            int first = checked((int)Bin.U32LE(data, p + 4)), firstPal = checked((int)Bin.U32LE(data, p + 8));
            if (first > bitmaps || mips > bitmaps - first || firstPal > palettes || pals > palettes - firstPal) throw new InvalidDataException("MMAP file references invalid bitmap/palette indices.");
            string name = Bin.CString(data, names + file * 16, 16);
            if (name.Length == 0) name = $"Texture {file}";
            for (int mip = 0; mip < mips; mip++)
            {
                // A file with no new palette can reuse the preceding file's palette.
                int palette = pals == 0 ? (palettes == 0 ? -1 : Math.Max(0, firstPal - 1)) : firstPal + Math.Min(mip, pals - 1);
                if (owners[first + mip] is { } owner)
                {
                    // Shared PAL8 pixels may deliberately use a different palette
                    // (for example, several colours of the same track-side object).
                    if (owner.Mip != mip) throw new InvalidDataException("Conflicting MMAP bitmap mip aliases.");
                    aliases.Add((first + mip, file, mip, palette, name));
                }
                else owners[first + mip] = (file, mip, palette, name);
            }
        }
        for (int i = 0; i < bitmaps; i++)
        {
            int p = bitmapTable + i * 16, w = Bin.U16LE(data, p), h = Bin.U16LE(data, p + 2);
            uint format = Bin.U32LE(data, p + 4);
            var owner = owners[i] ?? throw new InvalidDataException("Unassigned MMAP bitmap.");
            // Only a file's base level has an Xbox resource header; following mips point directly to pixels.
            int size = checked((int)Bin.U32LE(data, p + 8)), offset = checked((int)Bin.U32LE(data, p + 12) + (owner.Mip == 0 ? 128 : 0));
            if (w == 0 || h == 0 || w > 8192 || h > 8192 || !XboxTexture.IsPowerOfTwo(w) || !XboxTexture.IsPowerOfTwo(h)) throw new NotSupportedException("Unsupported MMAP dimensions.");
            Bounds(offset, size);
            if (size < checked(w * h * (format == 6 ? 4 : 1))) throw new NotSupportedException("Truncated MMAP pixel storage.");
            if (format == 11 && owner.Palette < 0) throw new InvalidDataException("PAL8 bitmap has no palette.");
            result.Images.Add(new(i, owner.File, owner.Mip, owner.Name, w, h, format, offset, size, owner.Palette));
        }
        foreach (var alias in aliases)
        {
            var image = result.Images[alias.Bitmap];
            result.Images.Add(image with { Index = result.Images.Count, FileIndex = alias.File, Mip = alias.Mip, Name = alias.Name, PaletteIndex = alias.Palette });
        }
        return result;
    }

    public DecodedImage Decode(Image image)
    {
        int bpp = image.Format == 6 ? 4 : 1;
        var pixels = image.Format == 19 ? Data.AsSpan(image.Offset, image.Width * image.Height).ToArray() : XboxTexture.Unswizzle(Data.AsSpan(image.Offset, image.Length), image.Width, image.Height, bpp);
        if (image.Format == 6) return new() { Width = image.Width, Height = image.Height, Bgra = pixels };
        var bgra = new byte[image.Width * image.Height * 4];
        for (int i = 0; i < pixels.Length; i++)
        {
            if (image.Format == 11) Data.AsSpan(Palettes[image.PaletteIndex].Offset + pixels[i] * 4, 4).CopyTo(bgra.AsSpan(i * 4));
            else if (image.Format == 19) { bgra[i * 4] = bgra[i * 4 + 1] = bgra[i * 4 + 2] = pixels[i]; bgra[i * 4 + 3] = 255; }
            else throw new NotSupportedException($"MMAP format {image.Format} is not supported.");
        }
        return new() { Width = image.Width, Height = image.Height, Bgra = bgra };
    }

    /// <summary>Replace a named texture and regenerate its mips without changing dimensions, tables or other files.</summary>
    public byte[] ReplaceFile(int imageIndex, byte[] bgra, int width, int height)
    {
        var selected = Images[imageIndex];
        var group = Images.Where(i => i.FileIndex == selected.FileIndex).OrderBy(i => i.Mip).ToArray();
        if (width != group[0].Width || height != group[0].Height || bgra.Length != checked(width * height * 4)) throw new InvalidDataException($"Replacement must be {group[0].Width}×{group[0].Height}.");
        var ownedPalettes = group.Where(i => i.PaletteIndex >= 0).Select(i => i.PaletteIndex).Distinct().ToArray();
        bool sharedPalette = Images.Any(i => i.FileIndex != selected.FileIndex && ownedPalettes.Contains(i.PaletteIndex));
        var result = Data.ToArray();
        byte[]? palette = null;
        if (group.Any(i => i.Format == 11) && !sharedPalette)
        {
            palette = Quantizer.Build(bgra, 256).Palette;
            foreach (int index in ownedPalettes)
            {
                result.AsSpan(Palettes[index].Offset, 1024).Clear();
                palette.CopyTo(result, Palettes[index].Offset);
            }
        }
        foreach (var image in group)
        {
            var resized = ImageOps.Resize(bgra, width, height, image.Width, image.Height);
            byte[] pixels;
            if (image.Format == 6) pixels = resized;
            else if (image.Format == 11)
            {
                var colors = sharedPalette ? Data.AsSpan(Palettes[image.PaletteIndex].Offset, 1024).ToArray() : palette!;
                pixels = Quantizer.MapToPalette(resized, colors, colors.Length / 4);
            }
            else if (image.Format == 19) { pixels = new byte[image.Width * image.Height]; for (int i = 0; i < pixels.Length; i++) pixels[i] = (byte)((resized[i * 4] * 29 + resized[i * 4 + 1] * 150 + resized[i * 4 + 2] * 77 + 128) >> 8); }
            else throw new NotSupportedException($"Unsupported MMAP format {image.Format}.");
            (image.Format == 19 ? pixels : XboxTexture.Swizzle(pixels, image.Width, image.Height, image.Format == 6 ? 4 : 1)).CopyTo(result, image.Offset);
        }
        return result;
    }
}
