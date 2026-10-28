using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using MVPWorkshop.Core.Formats;
using MVPWorkshop.Core.IO;

namespace MVPWorkshop.Core.Install;

/// <summary>Where the game's files come from: a disc image or an already extracted game folder.</summary>
public interface IGameSource
{
    string Name { get; }
    /// <summary>Every file, paths from the game root with backslashes, and its size.</summary>
    IEnumerable<(string Path, long Size)> Files();
    byte[] Load(string path);
}

public sealed class DiscSource(XboxDisc disc) : IGameSource
{
    public string Name => System.IO.Path.GetFileName(disc.Path);
    public IEnumerable<(string, long)> Files() => disc.Files().Select(f => (f.Path, (long)f.File.Size));
    public byte[] Load(string path) => disc.Load(disc.Find(path) ?? throw new FileNotFoundException(path));
}

public sealed class FolderSource(string root) : IGameSource
{
    public string Name => root;
    public IEnumerable<(string, long)> Files() =>
        Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
            .Select(f => (Path.GetRelativePath(root, f).Replace('/', '\\'), new FileInfo(f).Length));
    // a running game keeps its files open for writing
    public byte[] Load(string path)
    {
        using var f = new FileStream(Path.Combine(root, path), FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        var data = new byte[f.Length];
        f.ReadExactly(data);
        return data;
    }
}

/// <summary>
/// Installs the game's files in the loose layout of the port's docs/LOOSE_ASSETS.md (format 1): archives become
/// <c>name_big/</c> folders with <c>_big.json</c>, members stored unpacked, textures become folders of DDS files
/// (<see cref="XshFolder"/>), and <c>manifest.json</c> lists every file with its hash and origin.
/// </summary>
public sealed class LooseInstaller
{
    public const int Format = 1;
    public const string ManifestName = "manifest.json";
    const string StateName = ".install-state";    // one finished source file per line
    const string PartialName = ".install-files";  // their manifest entries, one JSON object per line

    public sealed record Progress(long Done, long Total, string Current);

    public sealed record FileRecord(string Path, string Sha1, long Size, string Source, string? Member, string? Part);

    readonly IGameSource source;
    readonly string gameDir;

    public LooseInstaller(IGameSource source, string gameDir)
    {
        this.source = source;
        this.gameDir = gameDir;
    }

    /// <summary>
    /// Writes every file, resuming after a cancelled run, then the manifest. Source files are worked on in parallel;
    /// <paramref name="info"/> goes into the manifest's "source" object.
    /// </summary>
    public void Install(IReadOnlyDictionary<string, string> info, IProgress<Progress>? progress, CancellationToken cancel)
    {
        Directory.CreateDirectory(gameDir);
        var statePath = Path.Combine(gameDir, StateName);
        var partialPath = Path.Combine(gameDir, PartialName);
        var done = File.Exists(statePath) ? File.ReadAllLines(statePath).ToHashSet(StringComparer.OrdinalIgnoreCase) : [];
        var records = new ConcurrentBag<FileRecord>();
        if (File.Exists(partialPath))
            foreach (var line in File.ReadAllLines(partialPath))
                if (line.Length > 0 && JsonSerializer.Deserialize<FileRecord>(line) is { } r && done.Contains(r.Source)) records.Add(r);

        var files = source.Files().ToList();
        var total = files.Sum(f => f.Size);
        long finished = files.Where(f => done.Contains(f.Path)).Sum(f => f.Size);
        var gate = new object();
        using var state = new StreamWriter(statePath, append: true);
        using var partial = new StreamWriter(partialPath, append: true);
        // biggest first, so one large archive doesn't run alone at the end
        var todo = files.Where(f => !done.Contains(f.Path)).OrderByDescending(f => f.Size).ToList();
        Parallel.ForEach(todo, new ParallelOptions { CancellationToken = cancel, MaxDegreeOfParallelism = Math.Max(1, Environment.ProcessorCount - 1) }, f =>
        {
            var mine = new List<FileRecord>();
            ExtractFile(f.Path, mine, null);
            lock (gate)
            {
                foreach (var r in mine) partial.WriteLine(JsonSerializer.Serialize(r));
                partial.Flush();
                state.WriteLine(f.Path);
                state.Flush();
                foreach (var r in mine) records.Add(r);
                finished += f.Size;
                progress?.Report(new Progress(finished, total, f.Path));
            }
        });
        state.Close();
        partial.Close();
        WriteManifest(info, records);
        File.Delete(statePath);
        File.Delete(partialPath);
    }

    /// <summary>Re-extracts the files whose path passes <paramref name="want"/> from their source files; nothing else is touched.</summary>
    public void Restore(IEnumerable<FileRecord> wanted, IProgress<Progress>? progress, CancellationToken cancel)
    {
        var bySource = wanted.GroupBy(r => r.Source, StringComparer.OrdinalIgnoreCase).ToList();
        var total = bySource.Count;
        var n = 0;
        foreach (var group in bySource)
        {
            cancel.ThrowIfCancellationRequested();
            var paths = group.Select(r => r.Path).ToHashSet(StringComparer.OrdinalIgnoreCase);
            ExtractFile(group.Key, [], paths);
            progress?.Report(new Progress(++n, total, group.Key));
        }
    }

    // ---- extraction ----

    sealed class Sink(LooseInstaller owner, List<FileRecord> records, HashSet<string>? only, string source)
    {
        public void Write(string rel, byte[] data, string? member, string? part)
        {
            if (only != null && !only.Contains(rel)) return;
            var full = Path.Combine(owner.gameDir, rel.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            File.WriteAllBytes(full, data);
            records.Add(new FileRecord(rel, Convert.ToHexStringLower(SHA1.HashData(data)), data.Length, source, member, part));
        }
    }

    void ExtractFile(string path, List<FileRecord> records, HashSet<string>? only)
    {
        var data = source.Load(path);
        var rel = path.Replace('\\', '/');
        var sink = new Sink(this, records, only, path);
        var lower = rel.ToLowerInvariant();
        if (lower.EndsWith(".big") && IsBig(data) && Layout(data, out _))
        {
            ExtractArchive(data, FolderName(rel), null, sink);
            return;
        }
        if (lower.EndsWith(".xsh") && XshFolder.Split(data) is { } parts)
        {
            foreach (var (name, bytes) in parts) sink.Write(rel + "/" + name, bytes, null, name);
            return;
        }
        sink.Write(rel, data, null, null);
    }

    static bool IsBig(byte[] d) => d.Length >= 16 && d[0] == 'B' && d[1] == 'I' && d[2] == 'G' && d[3] == 'F';

    // "data/models.big" -> "data/models_big", the name written in lower case
    static string FolderName(string rel)
    {
        var slash = rel.LastIndexOf('/');
        var dir = slash < 0 ? "" : rel[..(slash + 1)];
        var name = rel[(slash + 1)..].ToLowerInvariant();
        var dot = name.LastIndexOf('.');
        return dir + (dot < 0 ? name + "_big" : name[..dot] + "_" + name[(dot + 1)..]);
    }

    /// <summary>
    /// Whether an archive can be laid out as files: a repeated name must repeat identical bytes, and no member may
    /// collide with a nested archive's folder (docs/LOOSE_ASSETS.md 2). Nested archives must pass too.
    /// </summary>
    static bool Layout(byte[] data, out BigArchive big)
    {
        big = BigArchive.Read(new MemoryByteSource(data));
        var seen = new Dictionary<string, (uint Off, uint Size)>(StringComparer.OrdinalIgnoreCase);
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var e in big.Entries)
        {
            var key = e.Name.Replace('\\', '/');
            if (seen.TryGetValue(key, out var first))
            {
                if (!data.AsSpan((int)first.Off, (int)first.Size).SequenceEqual(data.AsSpan((int)e.Offset, (int)e.Size))) return false;
                continue;
            }
            seen[key] = (e.Offset, e.Size);
            names.Add(key);
        }
        foreach (var e in big.Entries)
        {
            var body = Unpacked(data.AsSpan((int)e.Offset, (int)e.Size), out _);
            if (!IsBig(body)) continue;
            if (names.Contains(FolderName(e.Name.Replace('\\', '/'))) || !Layout(body, out _)) return false;
        }
        return true;
    }

    static byte[] Unpacked(ReadOnlySpan<byte> raw, out bool packed)
    {
        packed = RefPack.IsCompressed(raw);
        return packed ? RefPack.Decompress(raw) : raw.ToArray();
    }

    void ExtractArchive(byte[] data, string folder, string? outer, Sink sink)
    {
        var big = BigArchive.Read(new MemoryByteSource(data));
        var order = new List<string>();
        var refpack = new List<string>();
        var written = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        uint align = 2048;
        foreach (var e in big.Entries)
        {
            order.Add(e.Name);
            while (align > 1 && e.Offset % align != 0) align /= 2;
            var key = e.Name.Replace('\\', '/');
            if (!written.Add(key)) continue;  // an identical repeat, listed again in order
            var member = outer == null ? e.Name : outer + "\\" + e.Name;
            var body = Unpacked(data.AsSpan((int)e.Offset, (int)e.Size), out var packed);
            if (IsBig(body))
            {
                ExtractArchive(body, folder + "/" + FolderName(key), member, sink);  // rebuilt, never packed again
                continue;
            }
            if (packed) refpack.Add(e.Name);
            var rel = folder + "/" + key.ToLowerInvariant();
            if (key.EndsWith(".xsh", StringComparison.OrdinalIgnoreCase) && XshFolder.Split(body) is { } parts)
                foreach (var (name, bytes) in parts) sink.Write(rel + "/" + name, bytes, member, name);
            else
                sink.Write(rel, body, member, null);
        }
        var meta = new JsonObject
        {
            ["magic"] = big.Magic,
            ["headerSize"] = big.HeaderSize,
            ["align"] = align,
            ["order"] = new JsonArray(order.Select(n => (JsonNode)n!).ToArray()),
            ["refpack"] = new JsonArray(refpack.Select(n => (JsonNode)n!).ToArray()),
        };
        sink.Write(folder + "/_big.json", Encoding.UTF8.GetBytes(meta.ToJsonString(new JsonSerializerOptions { WriteIndented = true })), outer, "_big.json");
    }

    // ---- manifest ----

    void WriteManifest(IReadOnlyDictionary<string, string> info, IEnumerable<FileRecord> records)
    {
        var files = new JsonObject();
        foreach (var r in records.OrderBy(r => r.Path, StringComparer.Ordinal))
        {
            var origin = new JsonObject { ["disc"] = r.Source.Replace('\\', '/') };
            if (r.Member != null) origin["member"] = r.Member;
            if (r.Part != null) origin["part"] = r.Part;
            files[r.Path] = new JsonObject { ["sha1"] = r.Sha1, ["size"] = r.Size, ["origin"] = origin };
        }
        var src = new JsonObject();
        foreach (var (k, v) in info) src[k] = v;
        var root = new JsonObject
        {
            ["format"] = Format,
            ["source"] = src,
            ["created"] = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ"),
            ["files"] = files,
        };
        File.WriteAllText(Path.Combine(gameDir, ManifestName), root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
    }

    public static List<FileRecord> ReadManifest(string gameDir, out JsonObject? root)
    {
        root = JsonNode.Parse(File.ReadAllText(Path.Combine(gameDir, ManifestName))) as JsonObject;
        var list = new List<FileRecord>();
        if (root?["files"] is not JsonObject files) return list;
        foreach (var (path, node) in files)
        {
            var o = node!["origin"]!;
            list.Add(new FileRecord(path, (string)node["sha1"]!, (long)node["size"]!, ((string)o["disc"]!).Replace('/', '\\'),
                (string?)o["member"], (string?)o["part"]));
        }
        return list;
    }

    /// <summary>Files missing or changed against the manifest, checked in parallel.</summary>
    public static List<FileRecord> Verify(string gameDir, IProgress<Progress>? progress, CancellationToken cancel)
    {
        var all = ReadManifest(gameDir, out _);
        var bad = new ConcurrentBag<FileRecord>();
        long done = 0, total = all.Sum(r => r.Size);
        Parallel.ForEach(all, new ParallelOptions { CancellationToken = cancel }, r =>
        {
            var full = Path.Combine(gameDir, r.Path.Replace('/', Path.DirectorySeparatorChar));
            var ok = File.Exists(full) && new FileInfo(full).Length == r.Size &&
                     Convert.ToHexStringLower(SHA1.HashData(File.ReadAllBytes(full))) == r.Sha1;
            if (!ok) bad.Add(r);
            var d = Interlocked.Add(ref done, r.Size);
            progress?.Report(new Progress(d, total, r.Path));
        });
        return [.. bad];
    }
}
