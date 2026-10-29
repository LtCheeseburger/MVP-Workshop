namespace MVPWorkshop.Core.Formats;

/// <summary>One slot of a pack in a folder import: the picture matched to it and the size it will be saved at.</summary>
public sealed class FolderImportRow
{
    public required SshImage Image { get; init; }
    public string? File { get; init; }
    public int PictureWidth { get; init; }
    public int PictureHeight { get; init; }
    public int Width { get; init; }
    public int Height { get; init; }
    /// <summary>Why this slot won't be replaced; null when it will.</summary>
    public string? Problem { get; init; }
    public bool Replaces => File is not null && Problem is null;
}

/// <summary>
/// Replaces every image of a texture pack from a folder of pictures named after the slots (<c>hat.png</c>, <c>merf.tga</c>…),
/// as one change. Each picture is saved at the largest size the slot allows that fits inside it, so a 4× picture stays 4×.
/// </summary>
public static class TextureFolderImport
{
    public static readonly string[] Extensions = [".png", ".tga", ".dds", ".bmp", ".jpg", ".jpeg", ".tif", ".tiff", ".gif"];

    /// <summary>Pictures in <paramref name="folder"/> matched to slots by tag or name (case-insensitive), and the files no slot uses.</summary>
    public static (List<FolderImportRow> Rows, List<string> Unused) Plan(SshFile file, string folder, Func<string, (byte[] Bgra, int W, int H)> load, Func<SshImage, string?> unsupported)
    {
        var pictures = Directory.EnumerateFiles(folder)
            .Where(f => Extensions.Contains(Path.GetExtension(f).ToLowerInvariant()))
            .GroupBy(f => Path.GetFileNameWithoutExtension(f), StringComparer.OrdinalIgnoreCase)
            // the same name twice (hat.png and hat.tga): the first in the extension list wins
            .ToDictionary(g => g.Key, g => g.OrderBy(f => Array.IndexOf(Extensions, Path.GetExtension(f).ToLowerInvariant())).First(), StringComparer.OrdinalIgnoreCase);
        var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var rows = new List<FolderImportRow>();
        foreach (var img in file.Images)
        {
            // tags are padded to four characters ("hat ")
            var path = pictures.GetValueOrDefault(img.Tag.Trim()) ?? (string.IsNullOrWhiteSpace(img.Name) ? null : pictures.GetValueOrDefault(img.Name.Trim()));
            if (path is null) { rows.Add(new FolderImportRow { Image = img, Problem = "no picture (kept)" }); continue; }
            used.Add(path);
            var h = img.Header;
            if (unsupported(img) is { } why) { rows.Add(new FolderImportRow { Image = img, File = path, Problem = why }); continue; }
            int pw, ph;
            try { (_, pw, ph) = load(path); }
            catch (Exception ex) { rows.Add(new FolderImportRow { Image = img, File = path, Problem = "can't read it: " + ex.Message }); continue; }
            var scale = 1;
            for (var k = SshWriter.MaxUpscaleFor(file, img); k > 1; k--)
                if (h.Width * k <= pw && h.Height * k <= ph && SshWriter.ResizeUnsupported(file, img, h.Width * k, h.Height * k) is null) { scale = k; break; }
            rows.Add(new FolderImportRow { Image = img, File = path, PictureWidth = pw, PictureHeight = ph, Width = h.Width * scale, Height = h.Height * scale });
        }
        var unused = pictures.Values.Where(p => !used.Contains(p)).OrderBy(p => p, StringComparer.OrdinalIgnoreCase).ToList();
        return (rows, unused);
    }

    /// <summary>The pack with every matched slot replaced, resampled to its row's size.</summary>
    public static byte[] Apply(byte[] pack, IReadOnlyList<FolderImportRow> rows, Func<string, (byte[] Bgra, int W, int H)> load, PaletteMode palette, Action<string>? progress = null)
    {
        var data = pack;
        foreach (var row in rows.Where(r => r.Replaces))
        {
            progress?.Invoke($"Importing {Path.GetFileName(row.File)} into {row.Image.Tag}");
            var (px, w, h) = load(row.File!);
            if (w != row.Width || h != row.Height) px = ImageOps.Resize(px, w, h, row.Width, row.Height);
            var file = SshFile.Read(data);
            var img = file.Images.First(i => i.Index == row.Image.Index);
            var resized = row.Width != img.Header.Width || row.Height != img.Header.Height;
            data = SshWriter.ReplaceImage(file, img, px, row.Width, row.Height, palette, allowResize: resized).File;
        }
        return data;
    }
}
