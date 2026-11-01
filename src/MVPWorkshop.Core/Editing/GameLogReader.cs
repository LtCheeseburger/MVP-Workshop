using System.Text;
using MVPWorkshop.Core.Vfs;

namespace MVPWorkshop.Core.Editing;

/// <summary>Reads the MVP06 runtime's release/debug logs without locking files held by the game.</summary>
public sealed class GameLogReader
{
    private readonly string _directory;
    private readonly DateTime _started;
    private readonly Dictionary<string, (long Offset, string Pending)> _positions = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, Decoder> _decoders = new(StringComparer.OrdinalIgnoreCase);
    private readonly AssetNode _root;
    private readonly Dictionary<string, AssetNode> _assets;

    public GameLogReader(string directory, AssetNode root, DateTime? started = null)
    {
        _directory = directory; _root = root; _started = started ?? DateTime.UtcNow;
        _assets = root.EnumerateDeep().Where(n => n.DiskPath is not null && !n.IsFolder).DistinctBy(n => n.DiskPath!, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(n => Path.GetRelativePath(root.DiskPath!, n.DiskPath!).Replace('\\', '/'), StringComparer.OrdinalIgnoreCase);
    }

    public static string LogDirectory(string executable, string? documents = null)
    {
        var beside = Path.GetDirectoryName(Path.GetFullPath(executable))!;
        if (File.Exists(Path.Combine(beside, "portable.txt"))) return Path.Combine(beside, "logs");
        documents ??= Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
        var user = Path.Combine(documents, "MVP06 Recomp");
        return string.IsNullOrEmpty(documents) ? Path.Combine(beside, "logs") : Path.Combine(user, "logs");
    }

    public IReadOnlyList<ModDiagnostic> ReadNew()
    {
        var rows = new List<ModDiagnostic>();
        if (!Directory.Exists(_directory)) return rows;
        foreach (var file in Directory.EnumerateFiles(_directory, "mvp06*.log").OrderBy(File.GetLastWriteTimeUtc))
        {
            if (File.GetLastWriteTimeUtc(file) < _started) continue;
            try
            {
                var position = _positions.GetValueOrDefault(file);
                using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                if (stream.Length < position.Offset) { position = default; _decoders.Remove(file); }
                stream.Position = position.Offset;
                // Bound each poll, retaining the cursor so a large log is consumed over subsequent polls.
                var buffer = new byte[(int)Math.Min(1024 * 1024, stream.Length - stream.Position)];
                var count = stream.Read(buffer);
                if (!_decoders.TryGetValue(file, out var decoder)) _decoders[file] = decoder = Encoding.UTF8.GetDecoder();
                var chars = new char[Encoding.UTF8.GetMaxCharCount(count)];
                var charCount = decoder.GetChars(buffer, 0, count, chars, 0, flush: false);
                var text = (position.Pending ?? "") + new string(chars, 0, charCount);
                var lines = text.Split('\n');
                _positions[file] = (stream.Position, lines[^1].Length > 16384 ? lines[^1][^16384..] : lines[^1]);
                foreach (var line in lines[..^1]) if (Parse(line.TrimEnd('\r'), _root, _assets) is { } row) rows.Add(row);
            }
            catch (IOException) { } // rotation or a partial write: retry at the next tick
            catch (UnauthorizedAccessException) { }
        }
        return rows;
    }

    public static ModDiagnostic? Parse(string line, AssetNode root, IReadOnlyDictionary<string, AssetNode> assets)
    {
        var lower = line.Replace('\\', '/').ToLowerInvariant();
        if (!new[] { "mods:", "textures:", "loosebig:", "modvfs:", "workshop launch:" }.Any(lower.Contains)) return null;
        bool error = lower.Contains("[error]") || lower.Contains("error:") || lower.Contains("workshop launch: failed:") || lower.Contains("workshop reload rejected:");
        bool warning = lower.Contains("[warn]") || lower.Contains("warning:") || lower.Contains("shown blank") || lower.Contains("skipped") || lower.Contains("left off") || lower.Contains("cannot read") || lower.Contains("no readable") || lower.Contains(" (over ") || lower.Contains("workshop reload restart-required:") || lower.Contains("workshop launch: cancelled:");
        if (!error && !warning) return null;
        var match = assets.Where(p => p.Key != "." && lower.Contains(p.Key, StringComparison.OrdinalIgnoreCase)).OrderByDescending(p => p.Key.Length).FirstOrDefault();
        return new(error ? DiagnosticSeverity.Error : DiagnosticSeverity.Warning, match.Value?.Key ?? "", match.Value?.Path ?? "Game log", line, "Game log");
    }
}
