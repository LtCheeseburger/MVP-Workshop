using MVPWorkshop.Core.Formats;

namespace MVPWorkshop.Core.Install;

/// <summary>
/// The port's loose install (docs/LOOSE_ASSETS.md in the port): <c>MVP06.exe</c> beside <c>assets/</c> (or <c>game/</c>), every BIG archive
/// written out as a <c>name_big/</c> folder with a <c>_big.json</c>, and every SHPX <c>.xsh</c> as a folder of DDS files.
/// </summary>
public static class LooseInstall
{
    public const string ArchiveMetaName = "_big.json";
    /// <summary>Folders beside MVP06.exe that hold the game files, in the order the runtime checks them.</summary>
    public static readonly string[] GameFolderNames = ["assets", "game"];

    /// <summary>The game folder for the picked executable (assets/ or game/ beside MVP06.exe, or the folder of a picked default.xbe); null with the reason.</summary>
    public static string? GameFolderFor(string exePath, out string? error)
    {
        error = null;
        var dir = Path.GetDirectoryName(Path.GetFullPath(exePath))!;
        foreach (var candidate in GameFolderNames.Select(n => Path.Combine(dir, n)).Append(dir))
            if (File.Exists(Path.Combine(candidate, "default.xbe"))) return candidate;
        error = $"No assets/default.xbe next to {Path.GetFileName(exePath)}. Pick the MVP06.exe of an installed game.";
        return null;
    }

    /// <summary>Whether a game folder holds loose archive folders (as opposed to the disc's packed .big files).</summary>
    public static bool IsLoose(string gameFolder) =>
        Directory.Exists(gameFolder) && Directory.EnumerateFiles(gameFolder, ArchiveMetaName, SearchOption.AllDirectories).Any();

    /// <summary>The archive name a loose archive folder stands for (<c>models_big</c> → <c>models.big</c>), or null.</summary>
    public static string? ArchiveName(string folder)
    {
        var name = Path.GetFileName(folder);
        var cut = name.LastIndexOf('_');
        if (cut <= 0 || cut == name.Length - 1 || !File.Exists(Path.Combine(folder, ArchiveMetaName))) return null;
        return name[..cut] + "." + name[(cut + 1)..];
    }

    public static bool IsTextureFolder(string folder) =>
        folder.EndsWith(".xsh", StringComparison.OrdinalIgnoreCase) && File.Exists(Path.Combine(folder, XshFolder.MetaName));

    /// <summary>The .xsh as the game sees it, rebuilt from the folder's xsh.meta and image files.</summary>
    public static byte[] BuildTexturePack(string folder)
    {
        var meta = File.ReadAllBytes(Path.Combine(folder, XshFolder.MetaName));
        var bytes = XshFolder.Build(meta, name => { var f = Path.Combine(folder, name); return File.Exists(f) ? File.ReadAllBytes(f) : null; }, out var error);
        return bytes ?? throw new InvalidDataException($"{Path.GetFileName(folder)}: {error}");
    }

    /// <summary>Writes an .xsh as a loose texture folder; false when it can't be laid out that way.</summary>
    public static bool WriteTexturePack(byte[] xsh, string folder, out string? error)
    {
        error = null;
        var parts = XshFolder.Split(xsh);
        if (parts is null) { error = "this texture pack can't be laid out as DDS files"; return false; }
        if (XshFolder.Build(parts[XshFolder.MetaName], n => parts.GetValueOrDefault(n), out error) is not { } back || !back.AsSpan().SequenceEqual(xsh))
        {
            error ??= "the DDS layout does not rebuild the texture pack exactly";
            return false;
        }
        if (Directory.Exists(folder)) Directory.Delete(folder, true);
        Directory.CreateDirectory(folder);
        foreach (var (name, data) in parts) File.WriteAllBytes(Path.Combine(folder, name), data);
        return true;
    }
}
