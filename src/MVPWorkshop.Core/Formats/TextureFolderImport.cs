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
    /// <summary>For a slot with no picture of its own: what it's made from ("tors, top quadrants mirrored") and how.</summary>
    public string? MadeFrom { get; init; }
    public Func<(byte[] Bgra, int W, int H)>? Make { get; init; }
    public bool Replaces => (File is not null || Make is not null) && Problem is null;
}

/// <summary>
/// Replaces every image of a texture pack from a folder of pictures named after the slots (<c>hat.png</c>, <c>merf.tga</c>…),
/// as one change. Each picture is saved at the largest size the slot allows that fits inside it, so a 4× picture stays 4×.
/// In a uniform pack the pieces the game derives from others are made when they have no picture of their own: <c>torf</c>
/// is <c>tors</c> with its top quadrants mirrored, and <c>llod</c> (the low-detail atlas) gets the new torso, leg and hat.
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
            var scale = Scale(file, img, pw, ph);
            rows.Add(new FolderImportRow { Image = img, File = path, PictureWidth = pw, PictureHeight = ph, Width = h.Width * scale, Height = h.Height * scale });
        }
        MakeUniformPieces(file, rows, load, unsupported);
        var unused = pictures.Values.Where(p => !used.Contains(p)).OrderBy(p => p, StringComparer.OrdinalIgnoreCase).ToList();
        return (rows, unused);
    }

    /// <summary>The largest upscale of <paramref name="img"/> that fits inside a <paramref name="pw"/>×<paramref name="ph"/> picture.</summary>
    static int Scale(SshFile file, SshImage img, int pw, int ph)
    {
        var h = img.Header;
        for (var k = SshWriter.MaxUpscaleFor(file, img); k > 1; k--)
            if (h.Width * k <= pw && h.Height * k <= ph && SshWriter.ResizeUnsupported(file, img, h.Width * k, h.Height * k) is null) return k;
        return 1;
    }

    // ---- uniform packs ----

    /// <summary>
    /// Where each piece sits in a uniform's <c>llod</c>, as fractions of the atlas (measured on every uniform of the game): the
    /// torso's top-left and top-right quadrants down the left, <c>legd</c> and <c>hat</c> along the top right. The rest (catcher
    /// gear, the same on every team) is kept.
    /// </summary>
    static readonly (string Tag, double SX, double SY, double SW, double SH, double X, double Y, double W, double H)[] LlodMap =
    [
        ("tors", 0, 0, .5, .5, 0, 0, .5, .5),
        ("tors", .5, 0, .5, .5, 0, .5, .5, .5),
        ("legd", 0, 0, 1, 1, .5, 0, .25, .5),
        ("hat", 0, 0, 1, 1, .75, 0, .25, .25),
    ];

    static void MakeUniformPieces(SshFile file, List<FolderImportRow> rows, Func<string, (byte[] Bgra, int W, int H)> load, Func<SshImage, string?> unsupported)
    {
        FolderImportRow? Row(string tag) => rows.FirstOrDefault(r => r.Image.Tag.Trim().Equals(tag, StringComparison.OrdinalIgnoreCase));
        if (Row("tors") is not { } tors || Row("torf") is not { } torf || Row("llod") is not { } llod) return;  // not a uniform

        if (torf.File is null && tors.Replaces && unsupported(torf.Image) is null)
        {
            var src = tors.File!;
            var scale = Scale(file, torf.Image, tors.PictureWidth, tors.PictureHeight);
            rows[rows.IndexOf(torf)] = new FolderImportRow
            {
                Image = torf.Image, PictureWidth = tors.PictureWidth, PictureHeight = tors.PictureHeight,
                Width = torf.Image.Header.Width * scale, Height = torf.Image.Header.Height * scale,
                MadeFrom = "tors, top quadrants mirrored",
                Make = () => { var (px, w, h) = load(src); return (MirrorTopQuadrants(px, w, h), w, h); },
            };
        }

        var files = LlodMap.Select(m => m.Tag).Distinct().Select(t => (Tag: t, Row: Row(t)))
            .Where(s => s.Row is { File: not null, Problem: null }).ToDictionary(s => s.Tag, s => s.Row!.File!, StringComparer.OrdinalIgnoreCase);
        // the map holds for the square atlas of 379 of the 381 uniforms; the two 128x256 ones are laid out otherwise and are kept
        if (llod.File is null && files.Count > 0 && llod.Image.Header.Width == llod.Image.Header.Height && unsupported(llod.Image) is null)
        {
            var current = file.Decode(llod.Image);
            rows[rows.IndexOf(llod)] = new FolderImportRow
            {
                Image = llod.Image, PictureWidth = current.Width, PictureHeight = current.Height, Width = current.Width, Height = current.Height,
                MadeFrom = "atlas of " + string.Join(", ", files.Keys),
                Make = () => (BuildLlod(current.Bgra, current.Width, current.Height, files, load), current.Width, current.Height),
            };
        }
    }

    /// <summary><c>torf</c> from <c>tors</c>: the top-left and top-right quadrants each flipped left to right.</summary>
    public static byte[] MirrorTopQuadrants(byte[] bgra, int w, int h)
    {
        var o = (byte[])bgra.Clone();
        var hw = w / 2;
        for (var y = 0; y < h / 2; y++)
            for (var q = 0; q < 2; q++)
                for (var x = 0; x < hw; x++)
                    Array.Copy(bgra, (y * w + q * hw + x) * 4, o, (y * w + q * hw + hw - 1 - x) * 4, 4);
        return o;
    }

    /// <summary>The atlas with each mapped piece that has a new picture pasted over it, scaled down.</summary>
    public static byte[] BuildLlod(byte[] atlas, int aw, int ah, IReadOnlyDictionary<string, string> pictures, Func<string, (byte[] Bgra, int W, int H)> load)
    {
        var o = (byte[])atlas.Clone();
        var loaded = new Dictionary<string, (byte[] Bgra, int W, int H)>(StringComparer.OrdinalIgnoreCase);
        foreach (var (tag, sx, sy, sw, sh, x, y, w, h) in LlodMap)
        {
            if (!pictures.TryGetValue(tag, out var path)) continue;
            if (!loaded.TryGetValue(tag, out var pic)) loaded[tag] = pic = load(path);
            int cx = (int)(sx * pic.W), cy = (int)(sy * pic.H), cw = (int)(sw * pic.W), ch = (int)(sh * pic.H);
            int dx = (int)(x * aw), dy = (int)(y * ah), dw = (int)(w * aw), dh = (int)(h * ah);
            var crop = new byte[cw * ch * 4];
            for (var r = 0; r < ch; r++) Array.Copy(pic.Bgra, ((cy + r) * pic.W + cx) * 4, crop, r * cw * 4, cw * 4);
            var small = ImageOps.Resize(crop, cw, ch, dw, dh);
            for (var r = 0; r < dh; r++) Array.Copy(small, r * dw * 4, o, ((dy + r) * aw + dx) * 4, dw * 4);
        }
        return o;
    }

    /// <summary>The pack with every matched slot replaced, resampled to its row's size.</summary>
    public static byte[] Apply(byte[] pack, IReadOnlyList<FolderImportRow> rows, Func<string, (byte[] Bgra, int W, int H)> load, PaletteMode palette, Action<string>? progress = null)
    {
        var data = pack;
        foreach (var row in rows.Where(r => r.Replaces))
        {
            progress?.Invoke(row.File is null ? $"Making {row.Image.Tag.Trim()} from {row.MadeFrom}" : $"Importing {Path.GetFileName(row.File)} into {row.Image.Tag.Trim()}");
            var (px, w, h) = row.Make?.Invoke() ?? load(row.File!);
            if (w != row.Width || h != row.Height) px = ImageOps.Resize(px, w, h, row.Width, row.Height);
            var file = SshFile.Read(data);
            var img = file.Images.First(i => i.Index == row.Image.Index);
            var resized = row.Width != img.Header.Width || row.Height != img.Header.Height;
            data = SshWriter.ReplaceImage(file, img, px, row.Width, row.Height, palette, allowResize: resized).File;
        }
        return data;
    }
}
