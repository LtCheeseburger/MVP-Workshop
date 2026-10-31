using System.Text.Json;
using MVPWorkshop.Core.Editing;

namespace MVPWorkshop.Core.Install;

public sealed record PortCompatibilityInfo(string Version, int? ModFormat, int? ExpectedLayout, int? InstalledLayout,
    bool RequiresNewerWorkshop, IReadOnlyList<string> Warnings)
{
    public const string WorkshopUpdates = "https://github.com/LtCheeseburger/MVP-Workshop/releases/latest";
    public const string PortUpdates = "https://github.com/LtCheeseburger/MVP-College-Baseball/releases";
    public string Summary => $"Port {Version} · mod format {ModFormat?.ToString() ?? "not advertised"} · expected layout {ExpectedLayout?.ToString() ?? "not advertised"} · installed layout {InstalledLayout?.ToString() ?? "unknown"}. Workshop writes mod format {MvpModExporter.Format}, layout {MvpModExporter.Layout}.";
}

public static class PortCompatibility
{
    /// <summary>Release manifest format is its packaging schema, never the runtime's mod format.</summary>
    public static PortCompatibilityInfo Read(string portDirectory, string gameDirectory)
    {
        var warnings = new List<string>();
        string version = "unknown";
        int? format = null, expected = null, installed = null;
        var versionPath = Path.Combine(portDirectory, "version.txt");
        try
        {
            if (File.Exists(versionPath))
            {
                if (new FileInfo(versionPath).Length > 1024) throw new InvalidDataException("version.txt is oversized.");
                version = File.ReadAllText(versionPath).Trim();
                if (version.Length == 0) version = "unknown";
            }
        }
        catch (Exception ex) { warnings.Add("Cannot read the installed port version: " + ex.Message); }
        JsonDocument? Manifest(string path)
        {
            if (!File.Exists(path)) return null;
            if (new FileInfo(path).Length > 16 * 1024 * 1024) throw new InvalidDataException("Manifest is oversized.");
            // Windows PowerShell release packaging writes UTF-8 with a BOM.
            return JsonDocument.Parse(File.ReadAllText(path));
        }
        static int? Number(JsonElement root, string name)
        {
            if (!root.TryGetProperty(name, out var value)) return null;
            if (!value.TryGetInt32(out var number) || number < 1) throw new InvalidDataException($"{name} must be a positive integer.");
            return number;
        }
        try
        {
            using var manifest = Manifest(Path.Combine(portDirectory, "port-manifest.json"));
            if (manifest is not null)
            {
                var j = manifest.RootElement;
                if (j.TryGetProperty("version", out var value) && value.GetString() is { Length: > 0 } manifestVersion)
                {
                    if (version != "unknown" && !version.Equals(manifestVersion, StringComparison.OrdinalIgnoreCase)) warnings.Add($"version.txt ({version}) and port-manifest.json ({manifestVersion}) disagree; the port installation may be mixed.");
                    else version = manifestVersion;
                }
                format = Number(j, "modFormat"); expected = Number(j, "layout");
            }
        }
        catch (Exception ex) { warnings.Add("Cannot read port-manifest.json compatibility metadata: " + ex.Message); }
        try
        {
            using var manifest = Manifest(Path.Combine(gameDirectory, "manifest.json"));
            if (manifest is not null) installed = Number(manifest.RootElement, "format");
        }
        catch (Exception ex) { warnings.Add("Cannot read installed loose-layout metadata: " + ex.Message); }
        bool newer = false;
        if (format > MvpModExporter.Format) { newer = true; warnings.Add($"This port expects mod format {format}; Workshop writes {MvpModExporter.Format}. Update Workshop before relying on exported mods."); }
        if (expected > MvpModExporter.Layout) { newer = true; warnings.Add($"This port expects loose layout {expected}; Workshop writes {MvpModExporter.Layout}. Update Workshop before relying on exported mods."); }
        if (installed > MvpModExporter.Layout) { newer = true; warnings.Add($"The installed game uses loose layout {installed}; Workshop supports {MvpModExporter.Layout}. Update Workshop."); }
        if (expected is not null && installed is not null && expected != installed) warnings.Add($"The port expects layout {expected}, but the installed game advertises {installed}. Update or reinstall matching port/game files.");
        if (format is null || expected is null) warnings.Add("The port does not advertise its expected mod format/layout. Compatibility cannot be confirmed from this installation's release metadata.");
        return new(version, format, expected, installed, newer, warnings);
    }
}
