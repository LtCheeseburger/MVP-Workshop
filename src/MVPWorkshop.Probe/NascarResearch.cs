using System.Text.Json;
using MVPWorkshop.Core.Formats;
using MVPWorkshop.Core.IO;
using MVPWorkshop.Core.Vfs;

internal static class NascarResearch
{
    internal static void TextureAudit(AssetNode root, string output)
    {
        var entries = new List<object>();
        foreach (string pack in new[] { "CARDATA.DAT", "CARRESFE.DAT", "CARPARTS.DAT", "CHARDAT.DAT", "CHARTEX.DAT" })
        {
            var source = root.Children.FirstOrDefault(n => n.Name.Equals(pack, StringComparison.OrdinalIgnoreCase));
            if (source is null) continue;
            foreach (var node in source.EnumerateDeep().Where(n => n.Format == FormatId.MmapTexture))
            {
                var textures = MmapTexture.Read(node.ReadAll());
                entries.Add(new { node.Key, Images = textures.Images.Where(i => i.Mip == 0).Select(i => new { i.Name, i.FileIndex, i.Width, i.Height }) });
                node.DropCache();
            }
        }
        File.WriteAllText(output, JsonSerializer.Serialize(entries, new JsonSerializerOptions { WriteIndented = true }));
    }
    internal static void Audit(AssetNode root, string output)
    {
        Directory.CreateDirectory(output); Directory.CreateDirectory(Path.Combine(output, "samples")); Directory.CreateDirectory(Path.Combine(output, "models"));
        var groups = new Dictionary<string, AuditGroup>(); var models = new List<object>(); int total = 0;
        foreach (var node in root.EnumerateDeep().Where(n => !n.IsFolder))
        {
            total++; var source = node.Open(); var head = source.ReadBytes(0, 256);
            string magic = Convert.ToHexString(head.AsSpan(0, Math.Min(4, head.Length)));
            string family = NascarAssetCatalog.SourceArchive(node).Name;
            string key = node.Format + ":" + magic + ":" + family;
            if (!groups.TryGetValue(key, out var group))
            {
                group = new AuditGroup { Format = node.Format.ToString(), Magic = magic, Pack = family, Sample = node.Key, Header = Convert.ToHexString(head) }; groups[key] = group;
                if (!node.IsContainer && source.Length <= 16 * 1024 * 1024)
                {
                    group.SampleFile = $"sample-{groups.Count:D4}-{magic}.bin";
                    File.WriteAllBytes(Path.Combine(output, "samples", group.SampleFile), source.ReadAll());
                }
            }
            group.Count++; group.Bytes += source.Length;
            try
            {
                if (node.Format == FormatId.TiburonModel)
                {
                    var data = source.ReadAll(); var sections = TiburonModel.Read(source); var geometry = sections.FirstOrDefault(s => s.Tag == "Geom");
                    string filename = $"model-{models.Count:D4}.bin"; File.WriteAllBytes(Path.Combine(output, "models", filename), data);
                    models.Add(new { node.Key, File = filename, Sections = sections, GeometryHeader = geometry is null ? "" : Convert.ToHexString(source.ReadBytes(geometry.Offset, 64)) });
                }
                if (node.Format == FormatId.EaFontSfn) { var font = SfnFont.Read(source.ReadAll()); font.Atlas.Decode(font.Atlas.Images[0]); }
                if (node.Format is FormatId.SchlStream or FormatId.SchlContainer)
                    foreach (var stream in SchlStream.ScanAll(source, 50000))
                    { string codec = stream.Header is { } h ? $"{h.Codec2:X2}/{h.Version}/{h.Channels}/{h.SampleRate}" : "missing"; group.Details[codec] = group.Details.GetValueOrDefault(codec) + 1; }
            }
            catch (Exception ex) { group.Errors++; group.Error ??= ex.Message; }
            if (total % 4000 == 0) Console.WriteLine($"Audited {total:N0} entries");
            if (!node.IsContainer) node.DropCache();
        }
        var options = new JsonSerializerOptions { WriteIndented = true };
        File.WriteAllText(Path.Combine(output, "formats.json"), JsonSerializer.Serialize(groups.Values.OrderBy(g => g.Format).ThenByDescending(g => g.Count), options));
        File.WriteAllText(Path.Combine(output, "models.json"), JsonSerializer.Serialize(models, options));
        Console.WriteLine($"Audit: {total:N0} entries, {groups.Count} format/source groups, {models.Count} models.");
    }
    private sealed class AuditGroup
    {
        public string Format { get; set; } = "";
        public string Magic { get; set; } = "";
        public string Pack { get; set; } = "";
        public string Sample { get; set; } = "";
        public string Header { get; set; } = "";
        public string? SampleFile { get; set; }
        public int Count { get; set; }
        public long Bytes { get; set; }
        public int Errors { get; set; }
        public string? Error { get; set; }
        public Dictionary<string, int> Details { get; set; } = [];
    }
}
