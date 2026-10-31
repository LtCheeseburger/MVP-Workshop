using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using MVPWorkshop.Core.Formats;
using MVPWorkshop.Core.Vfs;

namespace MVPWorkshop.Core.Editing;

public sealed class MvpModPackage
{
    public required ModInfo Info { get; init; }
    public string XbeSha1 { get; init; } = "";
    public byte[]? Icon { get; init; }
    public IReadOnlyList<string> Removed { get; init; } = [];
    public IReadOnlyList<string> Refpack { get; init; } = [];
    public Dictionary<string, byte[]> Files { get; } = new(StringComparer.OrdinalIgnoreCase);
    public bool MatchesGame(AssetNode root) => XbeSha1.Equals(Convert.ToHexString(SHA1.HashData(File.ReadAllBytes(Path.Combine(root.DiskPath!, "default.xbe")))) , StringComparison.OrdinalIgnoreCase);
}

/// <summary>Reads a mod without extracting archive paths or writing to the game installation.</summary>
public static class MvpModImporter
{
    public static MvpModPackage Read(string path)
    {
        if (Directory.Exists(path)) return ReadFolder(path);
        using var zip = ZipFile.OpenRead(path);
        var entries = new Dictionary<string, ZipArchiveEntry>(StringComparer.OrdinalIgnoreCase);
        long size = 0;
        foreach (var entry in zip.Entries)
        {
            if (entry.FullName.EndsWith('/')) continue;
            var name = entry.FullName.Replace('\\', '/');
            if (name.StartsWith('/') || name.Split('/').Any(p => p is "" or "." or ".." || p.Any(c => c < 32 || ":*?\"<>|".Contains(c)))) throw new InvalidDataException($"Unsafe mod path: {name}");
            if (!entries.TryAdd(name, entry)) throw new InvalidDataException($"Duplicate mod path: {name}");
            size = checked(size + entry.Length);
            if (entry.Length > 512L * 1024 * 1024 || size > 2L * 1024 * 1024 * 1024) throw new InvalidDataException("The mod exceeds the supported import size.");
        }
        if (!entries.TryGetValue("mod.json", out var manifest) || manifest.Length > 1024 * 1024) throw new InvalidDataException("Missing or oversized mod.json.");
        using var manifestStream = manifest.Open();
        using var json = JsonDocument.Parse(manifestStream);
        var j = json.RootElement;
        if (j.GetProperty("format").GetInt32() != MvpModExporter.Format || j.GetProperty("layout").GetInt32() != MvpModExporter.Layout) throw new InvalidDataException("This mod uses an unsupported format or loose layout.");
        string Text(string key) => j.TryGetProperty(key, out var value) ? value.GetString() ?? "" : "";
        var info = new ModInfo { Id = Text("id"), Name = Text("name"), Version = Text("version"), Author = Text("author"), Description = Text("description"), AffectsSaves = j.TryGetProperty("affectsSaves", out var affects) && affects.GetBoolean() };
        if (info.Problem() is { } problem) throw new InvalidDataException(problem);
        byte[] Bytes(ZipArchiveEntry entry) { using var source = entry.Open(); using var target = new MemoryStream(); source.CopyTo(target); return target.ToArray(); }
        if (entries.TryGetValue("icon.png", out var iconEntry) && iconEntry.Length > 8 * 1024 * 1024) throw new InvalidDataException("Oversized mod icon.");
        var package = new MvpModPackage { Info = info, XbeSha1 = j.GetProperty("game").GetProperty("xbeSha1").GetString() ?? "", Icon = entries.TryGetValue("icon.png", out var icon) ? Bytes(icon) : null,
            Removed = Paths(j, "remove"), Refpack = Paths(j, "refpack") };
        foreach (var (name, entry) in entries)
            if (name.StartsWith("files/", StringComparison.OrdinalIgnoreCase)) package.Files.Add(name[6..], Bytes(entry));
        if (package.Files.Count == 0 && package.Removed.Count == 0) throw new InvalidDataException("The mod contains no files or removals.");
        return package;
    }

    private static IReadOnlyList<string> Paths(JsonElement json, string name) => json.TryGetProperty(name, out var value)
        ? value.EnumerateArray().Select(v => (v.GetString() ?? "").Replace('\\', '/')).ToList() : [];

    private static MvpModPackage ReadFolder(string path)
    {
        var manifest = Path.Combine(path, "mod.json");
        if (new FileInfo(manifest).Length > 1024 * 1024) throw new InvalidDataException("Oversized mod.json.");
        using var document = JsonDocument.Parse(File.ReadAllText(manifest));
        var j = document.RootElement;
        if (j.GetProperty("format").GetInt32() != MvpModExporter.Format || j.GetProperty("layout").GetInt32() != MvpModExporter.Layout) throw new InvalidDataException("Unsupported mod format or layout.");
        string Text(string key) => j.TryGetProperty(key, out var value) ? value.GetString() ?? "" : "";
        var info = new ModInfo { Id = Text("id"), Name = Text("name"), Version = Text("version"), Author = Text("author"), Description = Text("description"), AffectsSaves = j.TryGetProperty("affectsSaves", out var affects) && affects.GetBoolean() };
        if (info.Problem() is { } problem) throw new InvalidDataException(problem);
        var icon = Path.Combine(path, "icon.png");
        if (File.Exists(icon) && new FileInfo(icon).Length > 8 * 1024 * 1024) throw new InvalidDataException("Oversized mod icon.");
        var package = new MvpModPackage { Info = info, XbeSha1 = j.GetProperty("game").GetProperty("xbeSha1").GetString() ?? "", Icon = File.Exists(icon) ? File.ReadAllBytes(icon) : null, Removed = Paths(j, "remove"), Refpack = Paths(j, "refpack") };
        var files = Path.Combine(path, "files");
        long total = 0;
        if (Directory.Exists(files))
            foreach (var file in Directory.EnumerateFiles(files, "*", new EnumerationOptions { RecurseSubdirectories = true, AttributesToSkip = FileAttributes.ReparsePoint }))
            {
                var size = new FileInfo(file).Length; total = checked(total + size);
                if (size > 512L * 1024 * 1024 || total > 2L * 1024 * 1024 * 1024) throw new InvalidDataException("The mod exceeds the supported import size.");
                package.Files.Add(Path.GetRelativePath(files, file).Replace('\\', '/'), File.ReadAllBytes(file));
            }
        if (package.Files.Count == 0 && package.Removed.Count == 0) throw new InvalidDataException("The mod contains no files or removals.");
        return package;
    }

    public static string NextVersion(string version)
    {
        var parts = version.Split('.');
        if (parts.Length != 3 || !int.TryParse(parts[0], out var major) || !int.TryParse(parts[1], out var minor) || !int.TryParse(parts[2], out var patch) || major < 0 || minor < 0 || patch < 0) throw new InvalidDataException("Invalid mod version.");
        return $"{major}.{minor}.{checked(patch + 1)}";
    }

    public static EditSession Open(MvpModPackage package, AssetNode root, string outputFolder, BaseModLayer? baseLayer = null)
    {
        var previousOverlay = AssetNode.Overlay;
        var previousChildren = AssetNode.ChildrenOverlay;
        try
        {
            var session = new EditSession(root, outputFolder);
            session.SetBaseLayer(baseLayer);
            if (MvpModExporter.SessionProblem(session) is { } problem) throw new InvalidDataException(problem);
            var nodes = root.EnumerateDeep().Where(n => n.DiskPath is not null).ToDictionary(n => Path.GetRelativePath(root.DiskPath!, n.DiskPath!).Replace('\\', '/'), StringComparer.OrdinalIgnoreCase);
            var handled = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            // DDS folders are represented by one rebuilt XSH override, including mod-supplied layout metadata.
            foreach (var group in package.Files.Where(f => f.Key.Contains(".xsh/", StringComparison.OrdinalIgnoreCase)).GroupBy(f => f.Key[..(f.Key.IndexOf(".xsh/", StringComparison.OrdinalIgnoreCase) + 4)], StringComparer.OrdinalIgnoreCase))
            {
                nodes.TryGetValue(group.Key, out var pack);
                var replacesLayout = group.Any(f => f.Key.Equals(group.Key + "/" + XshFolder.MetaName, StringComparison.OrdinalIgnoreCase));
                var parts = pack?.IsLooseTexturePack == true && !replacesLayout
                    ? new Dictionary<string, byte[]>(XshFolder.Split(session.BaseBytes(pack)) ?? throw new InvalidDataException($"{group.Key}: the installed texture layout is invalid."), StringComparer.OrdinalIgnoreCase)
                    : new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
                foreach (var (path, data) in group) { parts[path[(group.Key.Length + 1)..]] = data; handled.Add(path); }
                if (!parts.TryGetValue(XshFolder.MetaName, out var meta)) throw new InvalidDataException($"{group.Key}: missing texture layout metadata.");
                var bytes = XshFolder.Build(meta, n => parts.GetValueOrDefault(n), out var error) ?? throw new InvalidDataException($"{group.Key}: {error}");
                if (pack is not null) session.Commit(pack, bytes, "Open mod: " + group.Key); else session.AddFile(group.Key, bytes);
            }
            foreach (var (path, bytes) in package.Files.Where(f => !handled.Contains(f.Key)))
            {
                if (nodes.TryGetValue(path, out var node))
                {
                    if (node.IsFolder) throw new InvalidDataException($"{path} replaces a folder with a file.");
                    session.Commit(node, bytes, "Open mod: " + path);
                }
                else
                {
                    if (File.Exists(Path.Combine(root.DiskPath!, path))) throw new InvalidDataException($"{path} is installation metadata and cannot be edited as an asset.");
                    session.AddFile(path, bytes);
                }
            }
            return session;
        }
        catch { AssetNode.Overlay = previousOverlay; AssetNode.ChildrenOverlay = previousChildren; throw; }
    }
}
