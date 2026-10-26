using System.Security.Cryptography;
using System.Text.Json;
using MVPWorkshop.Core.Formats;
using MVPWorkshop.Core.IO;
using MVPWorkshop.Core.Vfs;

namespace MVPWorkshop.Core.Editing;

public sealed record ExportedFile(string RelativePath, long Size, string Sha256, int EditedAssets, int ComparedAssets);

public sealed class ExportReport
{
    public string OutputFolder { get; init; } = "";
    public List<ExportedFile> Files { get; } = [];
    public List<string> Problems { get; } = [];
    public int PreviewOnlySkipped { get; set; }
    public bool Ok => Problems.Count == 0;
}

/// <summary>
/// Writes edited game files into a separate output folder, mirroring the game's directory layout, and re-opens every rebuilt archive
/// to check it against the original. Edited entries are re-encoded the way they were stored (RefPack stays RefPack); every untouched entry,
/// header byte and padding byte is copied verbatim, so only the files that actually changed appear in the output.
/// </summary>
public static class ModExporter
{
    public static string DefaultOutputFolder(string gameFolder)
    {
        var parent = Directory.GetParent(Path.TrimEndingDirectorySeparator(gameFolder))?.FullName ?? gameFolder;
        return Path.Combine(parent, "mod_output");
    }

    /// <summary>The output folder must not be, contain or sit inside the game folder, so original files can never be overwritten.</summary>
    public static string? OutputProblem(AssetNode gameRoot, string outputFolder)
    {
        if (string.IsNullOrWhiteSpace(outputFolder)) return "Choose an output folder.";
        var game = Path.TrimEndingDirectorySeparator(Path.GetFullPath(gameRoot.DiskPath ?? ""));
        var output = Path.TrimEndingDirectorySeparator(Path.GetFullPath(outputFolder));
        bool Inside(string child, string parent) => child.Equals(parent, StringComparison.OrdinalIgnoreCase) || child.StartsWith(parent + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
        if (Inside(output, game)) return "The output folder is inside the game folder. Exports must go somewhere else so the original files stay untouched.";
        if (Inside(game, output)) return "The output folder contains the game folder.";
        return null;
    }

    /// <summary>The file on disk that holds <paramref name="n"/> (the node itself for loose files).</summary>
    public static AssetNode DiskFileOf(AssetNode n)
    {
        for (var p = n; p is not null; p = p.Parent) if (!p.IsFolder && (p.DiskPath is not null || p.DiscEntry is not null)) return p;
        throw new InvalidOperationException($"{n.Path} is not backed by a file on disk.");
    }

    public static string RelativePath(AssetNode root, AssetNode file) => file.DiscEntry?.Path.Replace('/', Path.DirectorySeparatorChar) ?? Path.GetRelativePath(root.DiskPath!, file.DiskPath!);

    public static ExportReport Export(EditSession session, string outputFolder, bool verify = true, Action<string>? progress = null)
    {
        if (OutputProblem(session.GameRoot, outputFolder) is { } bad) throw new InvalidOperationException(bad);
        var report = new ExportReport { OutputFolder = outputFolder };
        report.PreviewOnlySkipped = session.History.Count(c => c.Kind == ChangeKind.PreviewOnly);
        var root = session.GameRoot;

        var edited = session.Overrides.Select(kv => (Node: root.Resolve(kv.Key), kv.Key)).ToList();
        foreach (var (node, key) in edited.Where(e => e.Node is null)) report.Problems.Add($"Edited asset '{key}' no longer exists in the game folder.");
        var perFile = edited.Where(e => e.Node is not null).GroupBy(e => DiskFileOf(e.Node!)).ToList();

        Directory.CreateDirectory(outputFolder);
        var manifest = new List<object>();
        foreach (var group in perFile)
        {
            var file = group.Key;
            var rel = RelativePath(root, file);
            progress?.Invoke($"Rebuilding {rel}");
            try
            {
                var bytes = RebuildStored(file, session.Overrides, reencodeAll: false);
                var dest = Path.Combine(outputFolder, rel);
                Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
                var tmp = dest + ".tmp";
                File.WriteAllBytes(tmp, bytes);
                File.Move(tmp, dest, overwrite: true);

                var compared = 0;
                if (verify)
                {
                    progress?.Invoke($"Verifying {rel}");
                    var problems = ArchiveVerifier.Verify(file, GameFileSystem.OpenFile(dest), session.Overrides, out compared);
                    foreach (var p in problems) report.Problems.Add($"{rel}: {p}");
                }
                var hash = Convert.ToHexString(SHA256.HashData(bytes));
                report.Files.Add(new ExportedFile(rel, bytes.Length, hash, group.Count(), compared));
            }
            catch (Exception ex) { report.Problems.Add($"{rel}: {ex.Message}"); }
        }

        var manifestPath = Path.Combine(outputFolder, "mod_manifest.json");
        File.WriteAllText(manifestPath, JsonSerializer.Serialize(new
        {
            tool = "MVP-Workshop", exported = DateTime.Now, gameFolder = root.DiskPath,
            files = report.Files.Select(f => new { path = f.RelativePath, size = f.Size, sha256 = f.Sha256, editedAssets = f.EditedAssets, verifiedAssets = f.ComparedAssets }),
            edits = session.Overrides.Keys.Order(),
            verified = verify && report.Ok,
        }, new JsonSerializerOptions { WriteIndented = true }));
        return report;
    }

    /// <summary>
    /// Stored bytes (as they would sit in the parent) of <paramref name="node"/> after applying <paramref name="overrides"/>.
    /// Unaffected nodes return their original stored bytes untouched. With <paramref name="reencodeAll"/> every RefPack entry below is
    /// decompressed and compressed again, which exercises the encoder and layout code even when nothing was edited.
    /// </summary>
    public static byte[] RebuildStored(AssetNode node, IReadOnlyDictionary<string, byte[]> overrides, bool reencodeAll)
    {
        var affected = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var key in overrides.Keys)
            for (var n = node.Root.Resolve(key); n is not null && n != node.Parent; n = n.Parent) affected.Add(n.Key);
        return Rebuild(node, overrides, affected, reencodeAll);
    }

    private static byte[] Rebuild(AssetNode node, IReadOnlyDictionary<string, byte[]> overrides, HashSet<string> affected, bool reencodeAll)
    {
        var stored = node.ReadStored();
        var replaced = overrides.TryGetValue(node.Key, out var logical);
        var touched = replaced || affected.Contains(node.Key);
        var compressed = stored.Length >= 8 && RefPack.IsCompressed(stored);
        if (!touched && !(reencodeAll && (compressed || node.Format == FormatId.BigArchive))) return stored;

        if (!replaced)
        {
            if (node.Format != FormatId.BigArchive)
            {
                // untouched leaf, only re-encoded on request
                logical = node.ReadOriginal();
            }
            else
            {
                var original = node.ReadOriginal();
                var big = BigArchive.Read(new MemoryByteSource(original));
                var children = BigChildren(node);
                var parts = new Dictionary<int, byte[]>();
                foreach (var (index, child) in children)
                {
                    var childTouched = overrides.ContainsKey(child.Key) || affected.Contains(child.Key);
                    if (!childTouched && !reencodeAll) continue;
                    var rebuilt = Rebuild(child, overrides, affected, reencodeAll);
                    if (!rebuilt.AsSpan().SequenceEqual(child.ReadStored())) parts[index] = rebuilt;
                }
                logical = BigWriter.Write(original, big, parts);
                if (parts.Count == 0 && reencodeAll) logical = original;
            }
        }
        return compressed ? RefPack.Compress(logical!, RefPack.StyleOf(stored)) : logical!;
    }

    /// <summary>BIG entry index → node, looking through the virtual folders created for sub-paths.</summary>
    public static Dictionary<int, AssetNode> BigChildren(AssetNode archive)
    {
        var map = new Dictionary<int, AssetNode>();
        void Walk(AssetNode n)
        {
            foreach (var c in n.Children)
            {
                if (c.BigIndex is { } i) map[i] = c;
                else if (c.IsFolder) Walk(c);
            }
        }
        Walk(archive);
        return map;
    }
}
