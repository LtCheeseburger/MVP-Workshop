using MVPWorkshop.Core.Formats;
using MVPWorkshop.Core.Vfs;

namespace MVPWorkshop.Core.Editing;

public enum DiagnosticSeverity { Error, Warning }
public sealed record ModDiagnostic(DiagnosticSeverity Severity, string AssetKey, string Asset, string Message, string Origin = "Validation");

public static class ModDiagnostics
{
    /// <summary>Validate a captured session; callers discard results if the active session changes.</summary>
    public static List<ModDiagnostic> Check(EditSession session, ModInfo info, DependencyIndex? dependencies = null, CancellationToken cancellation = default)
    {
        var rows = new List<ModDiagnostic>();
        void Error(string key, string asset, string message) => rows.Add(new(DiagnosticSeverity.Error, key, asset, message));
        void Warn(string key, string asset, string message) => rows.Add(new(DiagnosticSeverity.Warning, key, asset, message));
        if (info.Problem() is { } invalid) Error("", "Mod details", invalid);
        if (MvpModExporter.SessionProblem(session) is { } problem) Error("", "Installation", problem);
        cancellation.ThrowIfCancellationRequested();
        var edited = session.Overrides.Keys.Select(k => session.GameRoot.Resolve(k)).OfType<AssetNode>().ToList();
        var problems = new List<string>();
        if (session.GameRoot.DiskPath is not null)
        {
            try { MvpModExporter.Collect(session, problems, []); }
            catch (Exception ex) { problems.Add(ex.Message); }
        }
        foreach (var rejected in problems)
        {
            var added = session.Added.Keys.FirstOrDefault(p => rejected.StartsWith(p, StringComparison.OrdinalIgnoreCase));
            var asset = edited.FirstOrDefault(n => n.DiskPath is not null && rejected.StartsWith(ModExporter.RelativePath(session.GameRoot, n).Replace('\\', '/'), StringComparison.OrdinalIgnoreCase));
            Error(added is not null ? "added:" + added : asset?.Key ?? "", added ?? asset?.Path ?? "Export", rejected);
        }
        foreach (var change in session.History.Where(c => c.Kind == ChangeKind.PreviewOnly).DistinctBy(c => c.Key))
            Warn(change.Key, change.Path, "Preview-only changes are left out of the exported mod.");
        foreach (var node in edited)
        {
            cancellation.ThrowIfCancellationRequested();
            if (node.IsTexturePack || node.IsLooseTexturePack)
                ValidateTexture(node.Key, node.Path, session.Overrides[node.Key], Error);
            else if (node.Extension == ".dds") ValidateDds(node.Key, node.Path, session.Overrides[node.Key], Error);
            if (!info.AffectsSaves && IsSaveData(node.Path)) Warn(node.Key, node.Path, "Roster/save data changed while AffectsSaves is off. Enable it in the mod details if saves depend on this mod.");
            if (dependencies is not null)
            {
                var related = new List<AssetNode>();
                for (var ancestor = node; ancestor is not null && ancestor != session.GameRoot; ancestor = ancestor.Parent) related.Add(ancestor);
                var teams = related.SelectMany(n => dependencies.Impact("asset:" + n.Key)).Where(s => s.Id.StartsWith("team:")).DistinctBy(s => s.Id).ToList();
                if (teams.Count > 1) Warn(node.Key, node.Path, $"This shared asset changes for all {teams.Count} schools: {string.Join(", ", teams.Select(t => t.Label))}.");
            }
        }
        foreach (var (path, bytes) in session.Added)
        {
            cancellation.ThrowIfCancellationRequested();
            if (path.EndsWith(".xsh", StringComparison.OrdinalIgnoreCase) || path.EndsWith(".ssh", StringComparison.OrdinalIgnoreCase)) ValidateTexture("added:" + path, path, bytes, Error);
            else if (path.EndsWith(".dds", StringComparison.OrdinalIgnoreCase)) ValidateDds("added:" + path, path, bytes, Error);
            if (!info.AffectsSaves && IsSaveData(path)) Warn("added:" + path, path, "Added roster/save data may require AffectsSaves.");
        }
        if (dependencies is not null)
        {
            foreach (var reference in dependencies.Relationships.Where(r => r.Missing && r.Kind != RelationshipKind.HeuristicCandidate))
            {
                var directory = reference.From.Source.DiskPath is { } disk ? Path.GetDirectoryName(disk) : null;
                var relative = directory is null ? "" : Path.GetRelativePath(session.GameRoot.DiskPath!, Path.Combine(directory, reference.Target)).Replace('\\', '/');
                if (!session.Added.ContainsKey(relative)) Warn(reference.From.Source.Key, reference.From.Label, $"Missing loose reference: {reference.Target}. {reference.Evidence}");
            }
            foreach (var indexProblem in dependencies.Problems) Warn("", "Dependencies", indexProblem);
        }
        return rows.Distinct().OrderBy(r => r.Severity).ThenBy(r => r.Asset).ThenBy(r => r.Message).ToList();
    }

    private static bool IsSaveData(string path) => path.Contains("roster", StringComparison.OrdinalIgnoreCase) || path.Contains("save", StringComparison.OrdinalIgnoreCase) || path.Contains("database.big", StringComparison.OrdinalIgnoreCase) || path.Contains("database_big", StringComparison.OrdinalIgnoreCase);

    private static void ValidateTexture(string key, string path, byte[] bytes, Action<string, string, string> error)
    {
        try
        {
            var file = SshFile.Read(bytes);
            if (path.EndsWith(".xsh", StringComparison.OrdinalIgnoreCase) && !file.IsXbox) { error(key, path, "Xbox .xsh textures require SHPX storage."); return; }
            if (file.Images.Count == 0) error(key, path, "The texture pack contains no images.");
            foreach (var image in file.Images)
            {
                var h = image.Header;
                var issue = h.Width is < 1 or > 4096 || h.Height is < 1 or > 4096 ? "Texture sides must be between 1 and 4096 pixels." :
                    SshFile.Bpp(h.Type) == 0 ? $"Unsupported texture format 0x{h.Type:X2}." :
                    file.IsXbox && h.FlagSwizzled && (!XboxTexture.IsPowerOfTwo(h.Width) || !XboxTexture.IsPowerOfTwo(h.Height)) ? "Swizzled Xbox textures need power-of-two sides." :
                    file.IsXbox && image.TypeMasked is 0x60 or 0x61 or 0x62 && (h.Width % 4 != 0 || h.Height % 4 != 0) ? "DXT texture sides must be multiples of four." : null;
                if (issue is not null) error(key, path, $"{image.Tag}: {issue}");
                else if (image.DataOffset < 0 || image.DataLength <= 0 || (long)image.DataOffset + image.DataLength > bytes.Length) error(key, path, $"{image.Tag}: truncated pixel payload.");
                else if (file.IsXbox && image.TypeMasked is 0x60 or 0x61 or 0x62 or 0x7B or 0x7D && SshWriter.Unsupported(file, image) is { } unsupported) error(key, path, $"{image.Tag}: {unsupported}");
            }
        }
        catch (Exception ex) { error(key, path, "Invalid texture: " + ex.Message); }
    }

    private static void ValidateDds(string key, string path, byte[] bytes, Action<string, string, string> error)
    {
        try
        {
            var type = XshFolder.DdsType(bytes, out var w, out var h, out var levels, out var offset);
            if (type == 0 || w is < 1 or > 4096 || h is < 1 or > 4096 || levels > 13 || type is 0x60 or 0x61 or 0x62 && (w % 4 != 0 || h % 4 != 0)) { error(key, path, "DDS format, size or mip count is unsupported by the loose texture layout."); return; }
            long required = offset;
            for (int i = 0; i < levels; i++) required += type == 0x7D ? (long)Math.Max(1, w >> i) * Math.Max(1, h >> i) * 4 : XboxTexture.DxtSize(Math.Max(1, w >> i), Math.Max(1, h >> i), type == 0x60 ? 1 : 3);
            if (required > bytes.Length) error(key, path, "DDS pixel payload is truncated.");
        }
        catch (Exception ex) { error(key, path, "Invalid DDS: " + ex.Message); }
    }
}
