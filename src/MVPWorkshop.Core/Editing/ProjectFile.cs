using System.Security.Cryptography;
using System.Text.Json;
using MVPWorkshop.Core.Vfs;

namespace MVPWorkshop.Core.Editing;

/// <summary>
/// An editing project on disk: <c>name.mvpwproj</c> (JSON manifest) plus <c>name.mvpwproj.data/NNNN.bin</c> holding the replacement bytes
/// of every edited asset. Only saved changes are stored; preview-only changes are by definition not part of a project.
/// </summary>
public static class ProjectFile
{
    public const string Extension = ".mvpwproj";
    /// <summary>Extension used by the pre-1.0 MVP07 Asset Tool; such projects still open (the format is identical).</summary>
    public const string LegacyExtension = ".mvp07proj";

    private sealed class Manifest
    {
        public int Format { get; set; } = 1;
        public string GameFolder { get; set; } = "";
        public string OutputFolder { get; set; } = "";
        public DateTime Saved { get; set; }
        public List<Entry> Edits { get; set; } = [];
        public List<Entry> Added { get; set; } = [];  // new files for a mod: Path is the game path
    }

    private sealed class Entry
    {
        public string Key { get; set; } = "";
        public string Path { get; set; } = "";
        public string File { get; set; } = "";
        public string Sha256 { get; set; } = "";
        public string OriginalSha256 { get; set; } = "";
        public int OriginalSize { get; set; }
        public int Size { get; set; }
        public List<string> Notes { get; set; } = [];
    }

    public static string DataFolder(string projectPath) => projectPath + ".data";

    public static void Save(EditSession session, string projectPath)
    {
        var data = DataFolder(projectPath);
        Directory.CreateDirectory(data);
        var manifest = new Manifest { GameFolder = session.GameRoot.DiskPath ?? "", OutputFolder = session.OutputFolder, Saved = DateTime.Now };
        var keep = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var n = 0;
        foreach (var (key, bytes) in session.Overrides.OrderBy(k => k.Key, StringComparer.OrdinalIgnoreCase))
        {
            var node = session.GameRoot.Resolve(key);
            var file = $"{++n:D4}.bin";
            File.WriteAllBytes(Path.Combine(data, file), bytes);
            keep.Add(file);
            var original = node?.ReadOriginal() ?? [];
            manifest.Edits.Add(new Entry
            {
                Key = key, Path = node?.Path ?? key, File = file, Size = bytes.Length, Sha256 = Hash(bytes),
                OriginalSize = original.Length, OriginalSha256 = Hash(original),
                Notes = session.History.Where(c => c.Kind == ChangeKind.Saved && c.Key.Equals(key, StringComparison.OrdinalIgnoreCase)).Select(c => c.Description).ToList(),
            });
        }
        foreach (var (path, bytes) in session.Added)
        {
            var file = $"{++n:D4}.bin";
            File.WriteAllBytes(Path.Combine(data, file), bytes);
            keep.Add(file);
            manifest.Added.Add(new Entry { Path = path, File = file, Size = bytes.Length, Sha256 = Hash(bytes) });
        }
        foreach (var stale in Directory.EnumerateFiles(data, "*.bin").Where(f => !keep.Contains(Path.GetFileName(f)))) File.Delete(stale);

        var tmp = projectPath + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(manifest, new JsonSerializerOptions { WriteIndented = true }));
        File.Move(tmp, projectPath, overwrite: true);
        session.ProjectPath = projectPath;
        session.MarkSaved();
    }

    public static string? PeekGameFolder(string projectPath)
    {
        try { return JsonSerializer.Deserialize<Manifest>(File.ReadAllText(projectPath))?.GameFolder; } catch { return null; }
    }

    /// <summary>Load a project into a fresh session over <paramref name="gameRoot"/>. Problems (missing assets, changed originals, bad data) come back as warnings.</summary>
    public static EditSession Load(string projectPath, AssetNode gameRoot, out List<string> warnings)
    {
        warnings = [];
        var m = JsonSerializer.Deserialize<Manifest>(File.ReadAllText(projectPath)) ?? throw new InvalidDataException("Empty project file.");
        if (m.Format != 1) throw new InvalidDataException($"Unsupported project format {m.Format}.");
        var session = new EditSession(gameRoot, string.IsNullOrWhiteSpace(m.OutputFolder) ? ModExporter.DefaultOutputFolder(gameRoot.DiskPath!) : m.OutputFolder);
        var edits = new List<(string, byte[])>();
        foreach (var e in m.Edits)
        {
            var path = Path.Combine(DataFolder(projectPath), e.File);
            if (!File.Exists(path)) { warnings.Add($"{e.Path}: data file {e.File} is missing; edit skipped."); continue; }
            var bytes = File.ReadAllBytes(path);
            if (!Hash(bytes).Equals(e.Sha256, StringComparison.OrdinalIgnoreCase)) { warnings.Add($"{e.Path}: data file {e.File} fails its checksum; edit skipped."); continue; }
            var node = gameRoot.Resolve(e.Key);
            if (node is null) { warnings.Add($"{e.Path}: no longer exists in this game folder; edit skipped."); continue; }
            if (session.SaveBlocker(node) is { } why) { warnings.Add($"{e.Path}: {why}; edit skipped."); continue; }
            if (e.OriginalSha256.Length > 0 && !Hash(node.ReadOriginal()).Equals(e.OriginalSha256, StringComparison.OrdinalIgnoreCase))
                warnings.Add($"{e.Path}: the game file differs from the one this edit was made on; the edit is kept but check the result.");
            edits.Add((e.Key, bytes));
        }
        session.Load(edits);
        var added = new List<(string, byte[])>();
        foreach (var e in m.Added)
        {
            var path = Path.Combine(DataFolder(projectPath), e.File);
            var bytes = File.Exists(path) ? File.ReadAllBytes(path) : null;
            if (bytes is null || !Hash(bytes).Equals(e.Sha256, StringComparison.OrdinalIgnoreCase)) { warnings.Add($"{e.Path}: the added file's data is missing or damaged; skipped."); continue; }
            added.Add((e.Path, bytes));
        }
        session.LoadAdded(added);
        session.ProjectPath = projectPath;
        return session;
    }

    private static string Hash(byte[] b) => Convert.ToHexString(SHA256.HashData(b));
}
