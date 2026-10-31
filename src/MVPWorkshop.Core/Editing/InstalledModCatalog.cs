using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using MVPWorkshop.Core.Vfs;

namespace MVPWorkshop.Core.Editing;

public sealed record InstalledMod(string Id, string Name, string Version, string Source, int Order, bool Enabled,
    IReadOnlyList<string> Files, IReadOnlyList<string> Removed, string? Problem);
public sealed record ModFileConflict(string Path, InstalledMod Other, bool Removed)
{
    public string Message => $"{Path} is also {(Removed ? "removed" : "changed")} by {Other.Name} ({Other.Id}, {Other.Version}); " +
        $"launcher position {Other.Order + 1}, {(Other.Enabled ? "enabled" : "disabled")}. " +
        (Other.Problem is null ? "" : $"Runtime compatibility: {Other.Problem}. ") +
        "Later entries win. Test in Game places this project above the enabled list; an installed export must be ordered after conflicting mods to win.";
}

/// <summary>The launcher's mods folder and format-1 mods.json, read without changing its settings or extracting mods.</summary>
public sealed class InstalledModCatalog
{
    public List<InstalledMod> Mods { get; } = [];
    public List<string> Problems { get; } = [];

    public static InstalledModCatalog Read(string directory, AssetNode root, CancellationToken cancellation = default)
    {
        var result = new InstalledModCatalog();
        if (!Directory.Exists(directory)) return result;
        var order = new List<(string Id, bool Enabled)>();
        var settings = Path.Combine(directory, "mods.json");
        try
        {
            if (File.Exists(settings))
            {
                using var json = JsonDocument.Parse(File.ReadAllText(settings));
                if (json.RootElement.GetProperty("format").GetInt32() != 1) throw new InvalidDataException("Unsupported mods.json format.");
                foreach (var entry in json.RootElement.GetProperty("order").EnumerateArray())
                {
                    var id = entry.GetProperty("id").GetString() ?? "";
                    if (order.Any(e => e.Id.Equals(id, StringComparison.OrdinalIgnoreCase))) result.Problems.Add($"Duplicate launcher order entry: {id}.");
                    else order.Add((id, entry.GetProperty("enabled").GetBoolean()));
                }
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException) { result.Problems.Add("Cannot read launcher mod order; discovered mods are treated as disabled: " + ex.Message); order.Clear(); }
        var xbe = Path.Combine(root.DiskPath!, "default.xbe");
        var sha = File.Exists(xbe) ? Convert.ToHexString(SHA1.HashData(File.ReadAllBytes(xbe))) : "";
        List<string> sources;
        try { sources = Directory.EnumerateFileSystemEntries(directory).OrderBy(p => p, StringComparer.OrdinalIgnoreCase).ToList(); }
        catch (Exception ex) { result.Problems.Add("Cannot list installed mods: " + ex.Message); return result; }
        foreach (var source in sources)
        {
            cancellation.ThrowIfCancellationRequested();
            try
            {
                if ((File.GetAttributes(source) & FileAttributes.ReparsePoint) != 0) continue;
                if (!(Directory.Exists(source) && File.Exists(Path.Combine(source, "mod.json"))) && !source.EndsWith(".mvpmod", StringComparison.OrdinalIgnoreCase)) continue;
                using var zip = Directory.Exists(source) ? null : ZipFile.OpenRead(source);
                string manifest;
                List<string> files;
                if (zip is not null)
                {
                    var entry = zip.Entries.SingleOrDefault(e => e.FullName.Equals("mod.json", StringComparison.OrdinalIgnoreCase)) ?? throw new InvalidDataException("No mod.json.");
                    if (entry.Length > 1024 * 1024) throw new InvalidDataException("Oversized mod.json.");
                    using var reader = new StreamReader(entry.Open()); manifest = reader.ReadToEnd();
                    files = zip.Entries.Where(e => e.FullName.StartsWith("files/", StringComparison.OrdinalIgnoreCase) && !e.FullName.EndsWith('/')).Select(e => e.FullName[6..].Replace('\\', '/')).ToList();
                }
                else
                {
                    if (new FileInfo(Path.Combine(source, "mod.json")).Length > 1024 * 1024) throw new InvalidDataException("Oversized mod.json.");
                    manifest = File.ReadAllText(Path.Combine(source, "mod.json"));
                    var basePath = Path.Combine(source, "files");
                    files = Directory.Exists(basePath) ? Directory.EnumerateFiles(basePath, "*", new EnumerationOptions { RecurseSubdirectories = true, AttributesToSkip = FileAttributes.ReparsePoint }).Select(p => Path.GetRelativePath(basePath, p).Replace('\\', '/')).ToList() : [];
                }
                if (files.Any(p => p.StartsWith('/') || p.Split('/').Any(s => s is "" or "." or "..") || p.Contains(':'))) throw new InvalidDataException("Unsafe mod paths.");
                using var json = JsonDocument.Parse(manifest);
                var j = json.RootElement;
                string Text(string key) => j.TryGetProperty(key, out var value) ? value.GetString() ?? "" : "";
                var id = Text("id");
                if (id.Length == 0 || id.Any(c => !(char.IsAsciiLetterLower(c) || char.IsAsciiDigit(c) || c == '-'))) throw new InvalidDataException("Invalid mod id.");
                var position = order.FindIndex(e => e.Id.Equals(id, StringComparison.OrdinalIgnoreCase));
                var problem = j.GetProperty("format").GetInt32() != MvpModExporter.Format ? "unsupported mod format" :
                    j.GetProperty("layout").GetInt32() != MvpModExporter.Layout ? "different loose layout" :
                    !j.GetProperty("game").GetProperty("xbeSha1").GetString()!.Equals(sha, StringComparison.OrdinalIgnoreCase) ? "XBE SHA-1 mismatch" : null;
                var removed = j.TryGetProperty("remove", out var removals) ? removals.EnumerateArray().Select(v => v.GetString() ?? "").ToList() : [];
                result.Mods.Add(new(id, Text("name") is { Length: > 0 } name ? name : id, Text("version"), source,
                    position >= 0 ? position : order.Count + result.Mods.Count, position >= 0 && order[position].Enabled, files.Distinct(StringComparer.OrdinalIgnoreCase).ToList(), removed, problem));
            }
            catch (Exception ex) when (ex is not OperationCanceledException) { result.Problems.Add($"{Path.GetFileName(source)}: {ex.Message}"); }
        }
        foreach (var entry in order.Where(e => result.Mods.All(m => m.Id != e.Id))) result.Problems.Add($"Launcher mod {entry.Id} was not found on disk.");
        foreach (var duplicate in result.Mods.GroupBy(m => m.Id).Where(g => g.Count() > 1)) result.Problems.Add($"Mod id {duplicate.Key} is installed more than once; the runtime may refuse a duplicate.");
        result.Mods.Sort((a, b) => a.Order.CompareTo(b.Order));
        return result;
    }

    public IReadOnlyList<ModFileConflict> Conflicts(IEnumerable<string> files, string ownId)
    {
        var paths = files.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var result = new List<ModFileConflict>();
        static string? TextureFolder(string path) => path.EndsWith(".xsh/xsh.meta", StringComparison.OrdinalIgnoreCase) ? path[..^8] : null;
        foreach (var mod in Mods.Where(m => !m.Id.Equals(ownId, StringComparison.OrdinalIgnoreCase)))
        {
            var other = mod.Files.ToHashSet(StringComparer.OrdinalIgnoreCase);
            var replacementFolders = mod.Files.Select(TextureFolder).OfType<string>().ToList();
            var ownFolders = paths.Select(TextureFolder).OfType<string>().ToList();
            foreach (var path in paths)
            {
                bool removed = mod.Removed.Any(p => path.Equals(p, StringComparison.OrdinalIgnoreCase) || path.StartsWith(p.TrimEnd('/') + "/", StringComparison.OrdinalIgnoreCase));
                if (removed || other.Contains(path) || replacementFolders.Any(folder => path.StartsWith(folder, StringComparison.OrdinalIgnoreCase)) || ownFolders.Any(folder => path.StartsWith(folder, StringComparison.OrdinalIgnoreCase) && mod.Files.Any(p => p.StartsWith(folder, StringComparison.OrdinalIgnoreCase)))) result.Add(new(path, mod, removed));
            }
        }
        return result;
    }
}
