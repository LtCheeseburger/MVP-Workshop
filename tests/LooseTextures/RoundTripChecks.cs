using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;
using MVPWorkshop.Core.Formats;
using MVPWorkshop.Core.IO;

internal static class RoundTripChecks
{
    public static int Run()
    {
        int failures = 0;
        void Check(string label, bool value) { Console.WriteLine($"{(value ? "PASS" : "FAIL")} {label}"); if (!value) failures++; }
        var folder = Path.Combine(Path.GetTempPath(), "mvp-texture-pngs-" + Guid.NewGuid().ToString("N"));
        try
        {
            var tags = new[] { "dupe", "dupe", "a/b?", "    " };
            var bytes = new byte[16 + tags.Length * 8 + tags.Length * 24];
            "SHPX"u8.CopyTo(bytes); BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(4), bytes.Length); BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(8), tags.Length);
            for (int i = 0; i < tags.Length; i++)
            {
                int offset = 16 + tags.Length * 8 + i * 24;
                Encoding.ASCII.GetBytes(tags[i]).CopyTo(bytes, 16 + i * 8); BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(20 + i * 8), offset);
                BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(offset), (24 << 8) | 0x60);
                BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(offset + 4), 4); BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(offset + 6), 4);
                BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(offset + 16), (ushort)(0xf800 + i)); BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(offset + 18), (ushort)(0x001f + i));
                BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(offset + 20), 0x12345678u + (uint)i);
            }
            var pack = SshFile.Read(bytes);
            var slots = TextureFolderExport.Export(bytes, folder);
            Check("one PNG per native slot", slots.Count == tags.Length && Directory.GetFiles(folder, "*.png").Length == tags.Length);
            Check("duplicate and unsafe tags produce distinct safe filenames", slots.Select(s => Path.GetFileName(s.File)).Distinct(StringComparer.OrdinalIgnoreCase).Count() == tags.Length && slots.All(s => Path.GetDirectoryName(s.File) == folder));
            foreach (var slot in slots)
            {
                var (pixels, width, height) = LoadPng(slot.File);
                Check($"slot {slot.Index} exports native decoded pixels", width == 4 && height == 4 && pixels.SequenceEqual(pack.Decode(pack.Images[slot.Index]).Bgra));
            }
            var plan = TextureFolderImport.Plan(pack, folder, LoadPng, image => SshWriter.Unsupported(pack, image));
            Check("every exported filename matches its own slot", plan.Rows.All(r => r.Replaces && r.File == slots.Single(s => s.Index == r.Image.Index).File) && plan.Unused.Count == 0);
            var unchanged = TextureFolderImport.Apply(bytes, plan.Rows, LoadPng, PaletteMode.Rebuild);
            Check("unchanged PNG folder re-import is byte-identical including DXT blocks", unchanged.SequenceEqual(bytes));
            File.WriteAllBytes(Path.Combine(folder, "dupe.png"), PngWriter.Encode(4, 4, new byte[64]));
            var prioritized = TextureFolderImport.Plan(pack, folder, LoadPng, image => SshWriter.Unsupported(pack, image));
            Check("explicit slot names take priority over ambiguous legacy tags", prioritized.Rows[0].File == slots[0].File && prioritized.Unused.Any(p => Path.GetFileName(p) == "dupe.png"));
            var first = LoadPng(slots[0].File); Array.Fill<byte>(first.Bgra, 255);
            File.WriteAllBytes(slots[0].File, PngWriter.Encode(first.W, first.H, first.Bgra));
            var changedPlan = TextureFolderImport.Plan(pack, folder, LoadPng, image => SshWriter.Unsupported(pack, image));
            var changed = TextureFolderImport.Apply(bytes, changedPlan.Rows, LoadPng, PaletteMode.Rebuild);
            Check("edited PNG changes its matched slot", !changed.SequenceEqual(bytes));
            var reopened = SshFile.Read(changed);
            Check("other duplicate-tag slots retain exact native bytes", pack.Images.Skip(1).All(img => bytes.AsSpan(img.Header.Offset, 24).SequenceEqual(changed.AsSpan(reopened.Images.Single(i => i.Index == img.Index).Header.Offset, 24))));
            bool rejected = false; var before = File.ReadAllBytes(slots[0].File);
            try { TextureFolderExport.Export(bytes, folder); } catch (IOException) { rejected = true; }
            Check("existing PNGs are protected from overwrite", rejected && File.ReadAllBytes(slots[0].File).SequenceEqual(before));
        }
        catch (Exception ex) { Console.WriteLine("FAIL " + ex); failures++; }
        finally { if (Directory.Exists(folder)) Directory.Delete(folder, true); }
        return failures == 0 ? 0 : 1;
    }

    // Read the generated standard RGBA PNGs independently of the source decoded-pixel arrays.
    private static (byte[] Bgra, int W, int H) LoadPng(string path)
    {
        var data = File.ReadAllBytes(path);
        int width = BinaryPrimitives.ReadInt32BigEndian(data.AsSpan(16)), height = BinaryPrimitives.ReadInt32BigEndian(data.AsSpan(20));
        using var payload = new MemoryStream();
        for (int at = 8; at + 12 <= data.Length;)
        {
            int length = BinaryPrimitives.ReadInt32BigEndian(data.AsSpan(at));
            if (data.AsSpan(at + 4, 4).SequenceEqual("IDAT"u8)) payload.Write(data, at + 8, length);
            at += length + 12;
        }
        payload.Position = 0;
        using var zlib = new ZLibStream(payload, CompressionMode.Decompress);
        var pixels = new byte[width * height * 4];
        var row = new byte[width * 4];
        for (int y = 0; y < height; y++)
        {
            if (zlib.ReadByte() != 0) throw new InvalidDataException("Expected unfiltered PNG row.");
            zlib.ReadExactly(row);
            for (int x = 0; x < width; x++) { int s = x * 4, d = (y * width + x) * 4; pixels[d] = row[s + 2]; pixels[d + 1] = row[s + 1]; pixels[d + 2] = row[s]; pixels[d + 3] = row[s + 3]; }
        }
        return (pixels, width, height);
    }
}
