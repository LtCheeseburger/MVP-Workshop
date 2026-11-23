using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using MVPWorkshop.Core.Vfs;

namespace MVPWorkshop.Core.Editing;

/// <summary>What changes in mod.json when the overhaul is rebuilt; null keeps the archive's value.</summary>
public sealed class ManifestEdit
{
    public string? Name { get; set; }
    public string? Version { get; set; }
    public string? Author { get; set; }
    public string? Description { get; set; }
    public bool? AffectsSaves { get; set; }
}

public sealed class OverhaulBuildReport
{
    public string Output { get; init; } = "";
    public List<(string Path, long Size)> Changed { get; } = [];
    public List<string> Removed { get; } = [];
    public List<string> Problems { get; } = [];
    public int Entries { get; set; }
    public bool ManifestChanged { get; set; }
    public bool Ok => Problems.Count == 0;
}

/// <summary>
/// The MVP 27 overhaul (<c>MVP27-Overhaul.mvpmod</c>) kept beside the game's MVP06.exe. Workshop layers it over the
/// opened game as a read-only <see cref="BaseModLayer"/>, so its content appears in the normal categories while the
/// project holds only the user's edits. <see cref="Rebuild"/> writes a new package: every original entry (unknown
/// mod.json fields, files Workshop doesn't edit) carried over, with the edits replacing or adding files.
/// Nothing here launches the game or changes the launcher's mod settings.
/// </summary>
public static class OverhaulPackage
{
    public const string FileName = "MVP27-Overhaul.mvpmod";
    public const string Id = "mvp27-overhaul";

    /// <summary>The package beside MVP06.exe (the game folder's parent), else in the game folder; case-insensitive.</summary>
    public static string? FindAdjacent(string gameFolder, string? executable = null)
    {
        var game = Path.TrimEndingDirectorySeparator(Path.GetFullPath(gameFolder));
        var places = new List<string?>();
        if (!string.IsNullOrWhiteSpace(executable)) places.Add(Path.GetDirectoryName(Path.GetFullPath(executable)));
        places.Add(Path.GetDirectoryName(game)); places.Add(game);
        foreach (var dir in places.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (dir is null || !Directory.Exists(dir)) continue;
            var hit = Directory.EnumerateFiles(dir, "*.mvpmod").FirstOrDefault(f => Path.GetFileName(f).Equals(FileName, StringComparison.OrdinalIgnoreCase));
            if (hit is not null) return Path.GetFullPath(hit);
        }
        return null;
    }

    /// <summary>Checks the package's identity and target game; throws <see cref="InvalidDataException"/> with the reason.</summary>
    public static MvpModPackage Validate(string path, AssetNode root)
    {
        MvpModPackage package;
        try { package = MvpModImporter.Read(path); }
        catch (Exception ex) when (ex is InvalidDataException or IOException or JsonException or KeyNotFoundException or InvalidOperationException)
        { throw new InvalidDataException($"{Path.GetFileName(path)} can't be read as a .mvpmod: {ex.Message}", ex); }
        if (package.Info.Id != Id) throw new InvalidDataException($"{Path.GetFileName(path)} has id '{package.Info.Id}', not '{Id}'; it is not the MVP 27 overhaul.");
        if (!package.MatchesGame(root)) throw new InvalidDataException($"{Path.GetFileName(path)} was built for a different default.xbe (xbeSha1 {package.XbeSha1}).");
        return package;
    }

    /// <summary>Whether the session's base layer already includes this package (same file, or same id).</summary>
    public static BaseModReference? Loaded(EditSession session, string? path = null) =>
        session.BaseLayer?.References.FirstOrDefault(r => r.Id == Id || path is not null && r.Source.Equals(Path.GetFullPath(path), StringComparison.OrdinalIgnoreCase));

    public static (byte[] Bytes, bool Changed) Manifest(byte[] original, ManifestEdit? edit, IEnumerable<string> newRefpack)
    {
        var j = JsonNode.Parse(original) as JsonObject ?? throw new InvalidDataException("mod.json is not an object.");
        var changed = false;
        void Set(string key, JsonNode? value) { if (JsonNode.DeepEquals(j[key], value)) return; j[key] = value; changed = true; }
        if (edit?.Name is { } n) Set("name", n.Trim());
        if (edit?.Version is { } v) Set("version", v.Trim());
        if (edit?.Author is { } a) Set("author", a.Trim());
        if (edit?.Description is { } d) Set("description", d.Trim());
        if (edit?.AffectsSaves is { } s && s != (j["affectsSaves"] is JsonValue sv && sv.TryGetValue<bool>(out var was) && was)) Set("affectsSaves", s);
        var existing = (j["refpack"] as JsonArray)?.Select(x => (string?)x ?? "").ToHashSet(StringComparer.OrdinalIgnoreCase) ?? [];
        var add = newRefpack.Where(r => !existing.Contains(r)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (add.Count > 0) { if (j["refpack"] is not JsonArray arr) j["refpack"] = arr = []; foreach (var r in add) arr.Add(r); changed = true; }
        return changed ? (System.Text.Encoding.UTF8.GetBytes(j.ToJsonString(new JsonSerializerOptions { WriteIndented = true })), true) : (original, false);
    }

    /// <summary>
    /// Writes <paramref name="output"/>: the source package with the session's edits. The source is never written unless
    /// <paramref name="replaceSource"/> (then the previous file is kept as <c>.bak</c>).
    /// </summary>
    public static OverhaulBuildReport Rebuild(EditSession session, string source, string output, ManifestEdit? edit, bool replaceSource, Action<string>? progress = null)
    {
        source = Path.GetFullPath(source);
        var full = Path.GetFullPath(output);
        var report = new OverhaulBuildReport { Output = full };
        var game = Path.TrimEndingDirectorySeparator(Path.GetFullPath(session.GameRoot.DiskPath!));
        if (full.StartsWith(game + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) { report.Problems.Add("The mod can't be written inside the game folder."); return report; }
        if (full.Equals(source, StringComparison.OrdinalIgnoreCase) && !replaceSource) { report.Problems.Add($"{Path.GetFileName(full)} is the loaded overhaul; save to another file or confirm replacing it."); return report; }
        if (Loaded(session, source) is not { } reference || !reference.Source.Equals(source, StringComparison.OrdinalIgnoreCase)) { report.Problems.Add("The overhaul isn't the workspace's base layer; reopen the game with it."); return report; }
        progress?.Invoke("Collecting edited files");
        var refpack = new List<string>();
        var files = MvpModExporter.Collect(session, report.Problems, refpack);
        if (report.Problems.Count > 0) return report;
        // a texture folder written whole (its layout changed) replaces every image the package had for it
        var whole = files.Keys.Where(k => k.EndsWith(".xsh/" + Formats.XshFolder.MetaName, StringComparison.OrdinalIgnoreCase)).Select(k => k[..^(Formats.XshFolder.MetaName.Length + 1)]).ToHashSet(StringComparer.OrdinalIgnoreCase);
        progress?.Invoke($"Writing {Path.GetFileName(full)}");
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        var tmp = full + ".tmp";
        var written = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        using (var src = ZipFile.OpenRead(source))
        {
            if (src.GetEntry("mod.json") is not { } manifestEntry) { report.Problems.Add("The source package has no mod.json."); return report; }
            byte[] Read(ZipArchiveEntry e) { using var s = e.Open(); using var m = new MemoryStream(); s.CopyTo(m); return m.ToArray(); }
            var (manifest, manifestChanged) = Manifest(Read(manifestEntry), edit, refpack);
            report.ManifestChanged = manifestChanged;
            using var zip = ZipFile.Open(tmp, ZipArchiveMode.Create);
            void Put(string name, byte[] data) { using var s = zip.CreateEntry(name, CompressionLevel.Optimal).Open(); s.Write(data); report.Entries++; }
            foreach (var entry in src.Entries)
            {
                var name = entry.FullName;
                if (name == "mod.json") { Put(name, manifest); continue; }
                if (name.EndsWith('/')) { zip.CreateEntry(name); continue; }
                if (name.StartsWith("files/", StringComparison.Ordinal))
                {
                    var rel = name[6..];
                    if (files.TryGetValue(rel, out var data)) { Put(name, data); written.Add(rel); report.Changed.Add((rel, data.Length)); continue; }
                    var slash = rel.LastIndexOf('/');
                    if (slash > 0 && whole.Contains(rel[..slash])) { report.Removed.Add(rel); continue; }
                }
                Put(name, Read(entry));
            }
            foreach (var (rel, data) in files.Where(f => !written.Contains(f.Key)).OrderBy(f => f.Key, StringComparer.OrdinalIgnoreCase))
            { Put("files/" + rel, data); report.Changed.Add((rel, data.Length)); }
        }
        if (replaceSource && full.Equals(source, StringComparison.OrdinalIgnoreCase)) File.Copy(full, full + ".bak", overwrite: true);
        File.Move(tmp, full, true);
        return report;
    }

    public static string Sha256(string path) { using var f = File.OpenRead(path); return Convert.ToHexStringLower(SHA256.HashData(f)); }
}
