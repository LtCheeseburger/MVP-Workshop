using MVPWorkshop.Core.Formats;
using MVPWorkshop.Core.IO;

namespace MVPWorkshop.Core.Editing;

public static class IsoModExporter
{
    public static void Export(EditSession session, string destination, string? templateIso = null, Action<string>? progress = null)
    {
        var iso = session.GameRoot.DiscImage ?? IsoImage.Read(templateIso ?? throw new ArgumentException("Choose the original ISO as the layout template for an extracted-folder project."));
        // An exported ISO must not replace any input from the extracted game either.
        if (session.GameRoot.DiscImage is null)
        {
            var rootPath = Path.TrimEndingDirectorySeparator(Path.GetFullPath(session.GameRoot.DiskPath!));
            if (Path.GetFullPath(destination).StartsWith(rootPath + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("Export the ISO outside the extracted game folder.");
        }
        var files = iso.Entries.Where(e => !e.Directory).ToDictionary(e => e.Path, StringComparer.OrdinalIgnoreCase);
        var nodes = session.Overrides.Keys.Select(k => session.GameRoot.Resolve(k) ?? throw new InvalidDataException("Missing edited asset: " + k));
        var replacements = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
        foreach (var group in nodes.GroupBy(ModExporter.DiskFileOf))
        {
            var file = group.Key; var relative = ModExporter.RelativePath(session.GameRoot, file).Replace('\\', '/');
            if (!files.TryGetValue(relative, out var entry)) throw new InvalidDataException("File absent from base ISO: " + relative);
            if (session.GameRoot.DiscImage is null && !iso.Open(entry).ReadAll().AsSpan().SequenceEqual(file.ReadStored())) throw new InvalidDataException("Extracted original differs from base ISO: " + relative);
            progress?.Invoke("Rebuilding " + relative);
            var bytes = ModExporter.RebuildStored(file, session.Overrides, false);
            var rebuilt = new Vfs.AssetNode(file.Name, null, false, bytes.Length, () => new MemoryByteSource(bytes), "rebuilt ISO file");
            var problems = ArchiveVerifier.Verify(file, rebuilt, session.Overrides, out _);
            if (problems.Count != 0) throw new InvalidDataException(string.Join("\n", problems));
            replacements[relative] = bytes;
        }
        iso.Export(destination, replacements, progress);
    }
}
