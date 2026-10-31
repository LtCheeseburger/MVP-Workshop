using System.Security.Cryptography;
using System.Text;
using MVPWorkshop.Core.Formats;
using MVPWorkshop.Core.IO;
using MVPWorkshop.Core.Vfs;

namespace MVPWorkshop.Core.Editing;

public sealed record BaseModReference(string Source, string Id, string Version, string Fingerprint);

/// <summary>Immutable merged loose-file view. No bytes are inserted into the project's overrides or Added files.</summary>
public sealed class BaseModLayer
{
    private readonly AssetNode _root;
    private readonly Dictionary<AssetNode, byte[]> _bytes = [];
    private readonly Dictionary<AssetNode, string> _origins = [];
    private readonly Dictionary<AssetNode, List<AssetNode>> _children = [];
    private readonly HashSet<string> _removed = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _refpack = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, AssetNode> _byPath = new(StringComparer.OrdinalIgnoreCase);
    public IReadOnlyList<BaseModReference> References { get; private set; } = [];
    public List<string> Warnings { get; } = [];
    private BaseModLayer(AssetNode root) => _root = root;
    public byte[]? GetBytes(AssetNode node) => _bytes.GetValueOrDefault(node);
    public string? Origin(AssetNode node) => _origins.GetValueOrDefault(node);
    public bool NeedsRefpack(string path) => _refpack.Contains(path);
    private string PathOf(AssetNode node) => node == _root ? "" : Path.GetRelativePath(_root.DiskPath!, node.DiskPath!).Replace('\\', '/');
    private bool Hidden(string path) => _removed.Any(p => path.Equals(p, StringComparison.OrdinalIgnoreCase) || path.StartsWith(p + "/", StringComparison.OrdinalIgnoreCase));
    public IReadOnlyList<AssetNode> Children(AssetNode parent, IReadOnlyList<AssetNode> original) => _children.GetValueOrDefault(parent) ?? original;
    public AssetNode? Resolve(string key)
    {
        var node = _root;
        foreach (var part in key.Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            var hash = part.LastIndexOf('#');
            int? index = hash > 0 && int.TryParse(part.AsSpan(hash + 1), out var value) ? value : null;
            var name = index is null ? part : part[..hash];
            node = Children(node, node.OriginalChildren).FirstOrDefault(c => c.Name.Equals(name, StringComparison.OrdinalIgnoreCase) && (index is null || c.BigIndex == index));
            if (node is null) return null;
        }
        return node;
    }

    public static BaseModLayer Build(AssetNode root, IEnumerable<string> sources, CancellationToken cancellation = default)
    {
        var result = new BaseModLayer(root);
        if (MvpModExporter.SessionProblem(new EditSession(root, "", activateOverlay: false)) is { } problem) throw new InvalidOperationException(problem);
        IEnumerable<AssetNode> Original(AssetNode parent)
        {
            yield return parent;
            if (parent.IsFolder || parent.IsLooseTexturePack) foreach (var child in parent.OriginalChildren) foreach (var descendant in Original(child)) yield return descendant;
        }
        var original = Original(root).ToList();
        foreach (var node in original.Where(n => n.DiskPath is not null)) result._byPath[result.PathOf(node)] = node;
        var files = new Dictionary<string, (byte[] Bytes, string Origin)>(StringComparer.OrdinalIgnoreCase);
        var resetTextures = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var references = new List<BaseModReference>(); var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var source in sources)
        {
            cancellation.ThrowIfCancellationRequested();
            var package = MvpModImporter.Read(source);
            if (!package.MatchesGame(root)) throw new InvalidDataException($"{package.Info.Name}: XBE SHA-1 mismatch.");
            if (!ids.Add(package.Info.Id)) throw new InvalidDataException($"Duplicate selected mod id: {package.Info.Id}");
            using var fingerprint = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            foreach (var removal in package.Removed)
            {
                ValidatePath(removal);
                var path = removal.Replace('\\', '/').TrimEnd('/');
                if (!path.Contains("_big/", StringComparison.OrdinalIgnoreCase) && !files.ContainsKey(path)) { result.Warnings.Add($"{package.Info.Name}: removal {path} is not an archive member or earlier added file; ignored."); continue; }
                result._removed.Add(path);
                foreach (var old in files.Keys.Where(p => p.Equals(path, StringComparison.OrdinalIgnoreCase) || p.StartsWith(path + "/", StringComparison.OrdinalIgnoreCase)).ToList()) files.Remove(old);
                fingerprint.AppendData(Encoding.UTF8.GetBytes("remove:" + path));
            }
            foreach (var layout in package.Files.Keys.Where(p => p.EndsWith(".xsh/xsh.meta", StringComparison.OrdinalIgnoreCase)))
            {
                var folder = layout[..^9]; resetTextures.Add(folder);
                foreach (var old in files.Keys.Where(p => p.StartsWith(folder + "/", StringComparison.OrdinalIgnoreCase)).ToList()) files.Remove(old);
            }
            foreach (var (path, bytes) in package.Files.OrderBy(p => p.Key, StringComparer.OrdinalIgnoreCase))
            {
                ValidatePath(path);
                if (path.EndsWith(".big", StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException($"{path}: packed archives are not loose mod files.");
                if (path.EndsWith("/_big.json", StringComparison.OrdinalIgnoreCase) && File.Exists(Path.Combine(root.DiskPath!, path))) { result.Warnings.Add($"{package.Info.Name}: existing archive metadata {path} is ignored by the runtime."); continue; }
                files[path] = (bytes, package.Info.Name);
                result._removed.RemoveWhere(p => path.Equals(p, StringComparison.OrdinalIgnoreCase));
                if (package.Refpack.Contains(path, StringComparer.OrdinalIgnoreCase)) result._refpack.Add(path); else result._refpack.Remove(path);
                fingerprint.AppendData(Encoding.UTF8.GetBytes(path.ToLowerInvariant())); fingerprint.AppendData(SHA256.HashData(bytes));
            }
            references.Add(new(Path.GetFullPath(source), package.Info.Id, package.Info.Version, Convert.ToHexString(fingerprint.GetHashAndReset())));
        }
        result.References = references;
        AssetNode DirectoryNode(string path)
        {
            if (path.Length == 0) return root;
            if (result._byPath.TryGetValue(path, out var found)) return found.IsFolder || found.IsLooseTexturePack ? found : throw new InvalidDataException($"{path} is a file, not a loose folder.");
            var slash = path.LastIndexOf('/'); var parent = DirectoryNode(slash < 0 ? "" : path[..slash]); var name = slash < 0 ? path : path[(slash + 1)..];
            bool texture = name.EndsWith(".xsh", StringComparison.OrdinalIgnoreCase) && files.ContainsKey(path + "/xsh.meta");
            bool archive = files.ContainsKey(path + "/_big.json") && name.LastIndexOf('_') > 0;
            var display = archive ? name[..name.LastIndexOf('_')] + "." + name[(name.LastIndexOf('_') + 1)..] : name;
            var size = texture ? files.Where(f => f.Key.StartsWith(path + "/", StringComparison.OrdinalIgnoreCase)).Sum(f => (long)f.Value.Bytes.Length) : 0;
            var node = new AssetNode(display, parent, !texture, size, texture ? () => new MemoryByteSource(result._bytes[result._byPath[path]]) : null, "base mod") { DiskPath = Path.Combine(root.DiskPath!, path), IsLooseTexturePack = texture, IsLooseArchive = archive };
            node.SetChildren([]); result._byPath[path] = node;
            result._origins[node] = files.First(f => f.Key.StartsWith(path + "/", StringComparison.OrdinalIgnoreCase)).Value.Origin;
            return node;
        }
        foreach (var (path, value) in files)
        {
            cancellation.ThrowIfCancellationRequested();
            var slash = path.LastIndexOf('/'); var parent = DirectoryNode(slash < 0 ? "" : path[..slash]); var name = slash < 0 ? path : path[(slash + 1)..];
            if (name.Equals("xsh.meta", StringComparison.OrdinalIgnoreCase) || name.Equals("_big.json", StringComparison.OrdinalIgnoreCase)) continue;
            if (!result._byPath.TryGetValue(path, out var node))
            {
                node = new AssetNode(name, parent, false, value.Bytes.Length, () => new MemoryByteSource(value.Bytes), "base mod") { DiskPath = Path.Combine(root.DiskPath!, path) };
                result._byPath[path] = node;
            }
            if (node.IsFolder || node.IsLooseTexturePack) throw new InvalidDataException($"{path}: a mod file replaces an asset folder.");
            result._bytes[node] = value.Bytes; result._origins[node] = value.Origin;
        }
        foreach (var pack in result._byPath.Values.Where(n => n.IsLooseTexturePack).ToList())
        {
            var path = result.PathOf(pack);
            var contributions = files.Where(f => f.Key.StartsWith(path + "/", StringComparison.OrdinalIgnoreCase)).ToList();
            if (result.Hidden(path) || contributions.Count == 0 && !result._removed.Any(p => p.StartsWith(path + "/", StringComparison.OrdinalIgnoreCase))) continue;
            var parts = !resetTextures.Contains(path) && Directory.Exists(pack.DiskPath) ? XshFolder.Split(pack.ReadOriginal()) ?? [] : new Dictionary<string, byte[]>();
            var merged = new Dictionary<string, byte[]>(parts, StringComparer.OrdinalIgnoreCase);
            foreach (var (file, value) in contributions) merged[file[(path.Length + 1)..]] = value.Bytes;
            foreach (var hidden in merged.Keys.Where(name => result.Hidden(path + "/" + name)).ToList()) merged.Remove(hidden);
            if (!merged.TryGetValue(XshFolder.MetaName, out var meta)) throw new InvalidDataException($"{path}: no texture layout metadata.");
            result._bytes[pack] = XshFolder.Build(meta, n => merged.GetValueOrDefault(n), out var error) ?? throw new InvalidDataException($"{path}: {error}");
            result._origins[pack] = string.Join(", ", contributions.Select(c => c.Value.Origin).Distinct());
        }
        foreach (var parent in result._byPath.Values.Where(n => n.IsFolder || n.IsLooseTexturePack).ToList())
        {
            var path = result.PathOf(parent);
            var children = result._byPath.Values.Where(n => n.Parent == parent && !result.Hidden(result.PathOf(n)) && (!resetTextures.Contains(path) || files.ContainsKey(result.PathOf(n)))).OrderBy(n => n.Name, StringComparer.OrdinalIgnoreCase).ToList();
            result._children[parent] = children;
        }
        return result;
    }

    private static void ValidatePath(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || path.StartsWith('/') || path.Contains('\\') || path.Contains(':') || path.Split('/').Any(p => p is "" or "." or "..")) throw new InvalidDataException("Unsafe base-mod path: " + path);
    }
}
