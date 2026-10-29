using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using MVPWorkshop.Core.Formats;
using MVPWorkshop.Core.Install;
using MVPWorkshop.Core.Vfs;

namespace MVPWorkshop.Core.Editing;

/// <summary>What goes into a mod's mod.json besides the files (the port's docs/MVPMOD.md).</summary>
public sealed class ModInfo
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string Version { get; set; } = "1.0.0";
    public string Author { get; set; } = "";
    public string Description { get; set; } = "";
    public bool AffectsSaves { get; set; }
    /// <summary>A PNG on disk that goes in as icon.png; empty for none.</summary>
    public string IconPath { get; set; } = "";

    /// <summary>Why the port would refuse these values; null when they're fine.</summary>
    public string? Problem()
    {
        if (Id == "" || Id.Any(c => !(char.IsAsciiLetterLower(c) || char.IsAsciiDigit(c) || c == '-'))) return "The id must be lower case letters, digits and '-' (for example uniforms-2007).";
        if (Name.Trim() == "") return "Give the mod a name.";
        var parts = Version.Split('.');
        if (parts.Length != 3 || parts.Any(p => !int.TryParse(p, out _))) return "The version must look like 1.0.0.";
        return IconProblem(IconPath);
    }

    /// <summary>Why the port wouldn't take this file as icon.png (a square PNG, 64-256 px); null when it's fine or empty.</summary>
    public static string? IconProblem(string path)
    {
        if (path == "") return null;
        if (!File.Exists(path)) return $"The icon {Path.GetFileName(path)} doesn't exist.";
        var head = new byte[24];
        using (var f = File.OpenRead(path)) if (f.Read(head) < head.Length) return "The icon isn't a PNG.";
        ReadOnlySpan<byte> sig = [0x89, (byte)'P', (byte)'N', (byte)'G', 13, 10, 26, 10];
        if (!head.AsSpan(0, 8).SequenceEqual(sig) || !head.AsSpan(12, 4).SequenceEqual("IHDR"u8)) return "The icon isn't a PNG.";
        var w = System.Buffers.Binary.BinaryPrimitives.ReadInt32BigEndian(head.AsSpan(16));
        var h = System.Buffers.Binary.BinaryPrimitives.ReadInt32BigEndian(head.AsSpan(20));
        if (w != h) return $"The icon must be square; it is {w}x{h}.";
        if (w < 64 || w > 256) return $"The icon must be 64 to 256 px; it is {w}x{h}.";
        return null;
    }

    public static string IdFrom(string name) =>
        string.Join("-", new string(name.ToLowerInvariant().Select(c => char.IsAsciiLetterOrDigit(c) ? c : ' ').ToArray()).Split(' ', StringSplitOptions.RemoveEmptyEntries));
}

public sealed class MvpModReport
{
    public string Output { get; init; } = "";
    /// <summary>Game paths in the mod (files/&lt;path&gt;) with their sizes.</summary>
    public List<(string Path, long Size)> Files { get; } = [];
    public List<string> Problems { get; } = [];
    public int PreviewOnlySkipped { get; set; }
    public bool Ok => Problems.Count == 0 && Files.Count > 0;
}

/// <summary>
/// Packs the edits made to a loose install into a <c>.mvpmod</c> (or a mod folder, for a test run): only the files that
/// differ from the install, at their loose paths. Archive members go in unpacked (the runtime packs them as the game
/// stored them); a texture pack contributes just the DDS images that changed, or the whole folder when its layout did.
/// The install itself is never written.
/// </summary>
public static class MvpModExporter
{
    public const int Format = 1;  // docs/MVPMOD.md
    public const int Layout = 1;  // docs/LOOSE_ASSETS.md

    /// <summary>Why this session can't be packed as a .mvpmod; null when it can.</summary>
    public static bool HasChanges(EditSession session) => session.Overrides.Count > 0 || session.Added.Count > 0;

    public static string? SessionProblem(EditSession session)
    {
        var root = session.GameRoot.DiskPath;
        if (root is null || !LooseInstall.IsLoose(root)) return "Mods are made from the port's loose install. Open the installed game (MVP06.exe or its assets folder) instead of a disc or packed folder.";
        if (!File.Exists(Path.Combine(root, "default.xbe"))) return "The game folder has no default.xbe.";
        return null;
    }

    /// <summary>The changed files as game path → bytes, in the loose layout.</summary>
    public static Dictionary<string, byte[]> Collect(EditSession session, List<string> problems, List<string> refpack)
    {
        var root = session.GameRoot;
        var files = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
        var edited = session.Overrides.Select(kv => (Node: root.Resolve(kv.Key), kv.Key)).ToList();
        foreach (var (_, key) in edited.Where(e => e.Node is null)) problems.Add($"Edited asset '{key}' no longer exists in the game folder.");
        foreach (var group in edited.Where(e => e.Node is not null).GroupBy(e => ModExporter.DiskFileOf(e.Node!)))
        {
            var file = group.Key;
            var rel = ModExporter.RelativePath(root, file).Replace('\\', '/');
            try
            {
                var bytes = ModExporter.RebuildStored(file, session.Overrides, reencodeAll: false);
                if (file.IsLooseTexturePack)
                {
                    var parts = XshFolder.Split(bytes);
                    if (parts is null || XshFolder.Build(parts[XshFolder.MetaName], n => parts.GetValueOrDefault(n), out _) is not { } back || !back.AsSpan().SequenceEqual(bytes))
                    {
                        problems.Add($"{rel}: the edited texture pack can't be laid out as DDS files.");
                        continue;
                    }
                    var folder = file.DiskPath!;
                    var sameLayout = DiskEquals(Path.Combine(folder, XshFolder.MetaName), parts[XshFolder.MetaName])
                                     && parts.Keys.All(k => File.Exists(Path.Combine(folder, k)))
                                     && Directory.EnumerateFiles(folder).Count() == parts.Count;
                    // same layout: only the images that changed; else the whole folder, which then replaces the game's
                    foreach (var (name, data) in parts)
                        if (!sameLayout || !DiskEquals(Path.Combine(folder, name), data)) files[rel + "/" + name] = data;
                    continue;
                }
                if (rel.EndsWith(".big", StringComparison.OrdinalIgnoreCase))
                {
                    problems.Add($"{rel} is a packed archive; the port only takes loose members in mods.");
                    continue;
                }
                if (!DiskEquals(file.DiskPath!, bytes)) files[rel] = bytes;
            }
            catch (Exception ex) { problems.Add($"{rel}: {ex.Message}"); }
        }

        // new files: a native .xsh becomes a texture folder; members added to an archive are RefPack-packed by the
        // runtime (mod.json "refpack"), as the disc stores nearly all of them
        foreach (var (path, data) in session.Added)
        {
            if (path.EndsWith(".big", StringComparison.OrdinalIgnoreCase)) { problems.Add($"{path} is a packed archive; add its members instead."); continue; }
            if (files.ContainsKey(path) || File.Exists(Path.Combine(root.DiskPath!, path)) || Directory.Exists(Path.Combine(root.DiskPath!, path)))
            {
                problems.Add($"{path} already exists in the game; edit it instead of adding it.");
                continue;
            }
            if (path.EndsWith(".xsh", StringComparison.OrdinalIgnoreCase) && XshFolder.Split(data) is { } parts)
                foreach (var (name, part) in parts) files[path + "/" + name] = part;
            else files[path] = data;
            if (path.Contains("_big/", StringComparison.OrdinalIgnoreCase)) refpack.Add(path);
        }
        return files;
    }

    static bool DiskEquals(string path, byte[] data)
    {
        if (!File.Exists(path)) return false;
        var info = new FileInfo(path);
        return info.Length == data.Length && File.ReadAllBytes(path).AsSpan().SequenceEqual(data);
    }

    public static string ModJson(ModInfo info, string xbeSha1, IReadOnlyList<string>? refpack = null)
    {
        var j = new JsonObject
        {
            ["format"] = Format,
            ["id"] = info.Id,
            ["name"] = info.Name.Trim(),
            ["version"] = info.Version,
            ["author"] = info.Author.Trim(),
            ["description"] = info.Description.Trim(),
            ["game"] = new JsonObject { ["xbeSha1"] = xbeSha1 },
            ["layout"] = Layout,
        };
        if (info.AffectsSaves) j["affectsSaves"] = true;
        if (refpack is { Count: > 0 }) j["refpack"] = new JsonArray([.. refpack.Select(r => (JsonNode)r)]);
        return j.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
    }

    /// <summary>Writes the mod: a .mvpmod zip, or a folder when <paramref name="asFolder"/> (replaced if it exists).</summary>
    public static MvpModReport Export(EditSession session, ModInfo info, string output, bool asFolder, Action<string>? progress = null)
    {
        var report = new MvpModReport { Output = output, PreviewOnlySkipped = session.PreviewOnlyCount };
        if (SessionProblem(session) is { } bad) { report.Problems.Add(bad); return report; }
        if (info.Problem() is { } why) { report.Problems.Add(why); return report; }
        var root = session.GameRoot.DiskPath!;
        var full = Path.GetFullPath(output);
        if (full.StartsWith(Path.GetFullPath(root).TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase)) { report.Problems.Add("The mod can't be written inside the game folder."); return report; }

        progress?.Invoke("Collecting changed files");
        var refpack = new List<string>();
        var files = Collect(session, report.Problems, refpack);
        if (report.Problems.Count > 0) return report;
        if (files.Count == 0) { report.Problems.Add("Nothing differs from the installed game."); return report; }
        var sha = Convert.ToHexStringLower(SHA1.HashData(File.ReadAllBytes(Path.Combine(root, "default.xbe"))));
        var json = ModJson(info, sha, refpack);

        progress?.Invoke($"Writing {Path.GetFileName(output)}");
        if (asFolder)
        {
            if (Directory.Exists(full)) Directory.Delete(full, true);
            Directory.CreateDirectory(full);
            File.WriteAllText(Path.Combine(full, "mod.json"), json);
            if (info.IconPath != "") File.Copy(info.IconPath, Path.Combine(full, "icon.png"));
            foreach (var (rel, data) in files)
            {
                var dest = Path.Combine(full, "files", rel.Replace('/', Path.DirectorySeparatorChar));
                Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
                File.WriteAllBytes(dest, data);
            }
        }
        else
        {
            Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            var tmp = full + ".tmp";
            using (var zip = ZipFile.Open(tmp, ZipArchiveMode.Create))
            {
                using (var w = new StreamWriter(zip.CreateEntry("mod.json").Open())) w.Write(json);
                if (info.IconPath != "") zip.CreateEntryFromFile(info.IconPath, "icon.png", CompressionLevel.NoCompression);
                foreach (var (rel, data) in files.OrderBy(f => f.Key, StringComparer.OrdinalIgnoreCase))
                {
                    using var s = zip.CreateEntry("files/" + rel, CompressionLevel.Optimal).Open();
                    s.Write(data);
                }
            }
            File.Move(tmp, full, true);
        }
        foreach (var (rel, data) in files.OrderBy(f => f.Key, StringComparer.OrdinalIgnoreCase)) report.Files.Add((rel, data.Length));
        return report;
    }

    /// <summary>MVP06.exe for a loose install's game folder (assets/ or game/ beside it); null when there's none.</summary>
    public static string? GameExe(string gameFolder)
    {
        var exe = Path.Combine(Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(Path.GetFullPath(gameFolder)))!, "MVP06.exe");
        return File.Exists(exe) ? exe : null;
    }
}
