using MVPWorkshop.Core.IO;

namespace MVPWorkshop.Core.Formats;

public sealed record ExportedTextureSlot(int Index, string Tag, string File, int Width, int Height);

public static class TextureFolderExport
{
    /// <summary>Index-qualified names retain duplicate, empty and filesystem-unsafe tags without ambiguity.</summary>
    public static string SlotName(SshImage image)
    {
        var tag = new string(image.Tag.Trim().Select(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' ? c : '_').ToArray());
        return $"slot-{image.Index:D3}-{(tag.Length == 0 ? "image" : tag)}";
    }

    /// <summary>Decode all slots before writing. Existing files are never overwritten.</summary>
    public static IReadOnlyList<ExportedTextureSlot> Export(byte[] bytes, string folder)
    {
        var pack = SshFile.Read(bytes);
        if (pack.Images.Count == 0) throw new InvalidDataException("The texture pack has no image slots.");
        var outputs = pack.Images.Select(image =>
        {
            var decoded = pack.Decode(image);
            var path = Path.Combine(folder, SlotName(image) + ".png");
            return (Slot: new ExportedTextureSlot(image.Index, image.Tag, path, decoded.Width, decoded.Height), Bytes: PngWriter.Encode(decoded.Width, decoded.Height, decoded.Bgra));
        }).ToList();
        foreach (var output in outputs) if (File.Exists(output.Slot.File) || Directory.Exists(output.Slot.File)) throw new IOException($"{Path.GetFileName(output.Slot.File)} already exists. Choose an empty export folder.");
        Directory.CreateDirectory(folder);
        foreach (var output in outputs)
        {
            using var stream = new FileStream(output.Slot.File, FileMode.CreateNew, FileAccess.Write);
            stream.Write(output.Bytes);
        }
        return outputs.Select(o => o.Slot).ToList();
    }
}
