using MVPWorkshop.Core.Formats;

namespace MVPWorkshop.Core.Vfs;

/// <summary>
/// Maps SSH image tags (the SHAPENAME a model's TAR refers to, e.g. "face", "fglv") to the SSH packs that contain them.
/// Built lazily over the whole game; model viewers use it to find textures.
/// </summary>
public sealed class TextureIndex
{
    private readonly Dictionary<string, List<AssetNode>> _byTag = new(StringComparer.OrdinalIgnoreCase);
    public int PackCount { get; private set; }

    public IReadOnlyList<AssetNode> Find(string tag) => _byTag.TryGetValue(tag, out var l) ? l : [];

    public static TextureIndex Build(AssetNode root, IProgress<int>? progress = null, CancellationToken ct = default)
    {
        var idx = new TextureIndex();
        foreach (var n in root.EnumerateDeep())
        {
            ct.ThrowIfCancellationRequested();
            if (n.IsFolder || n.Format != FormatId.SshTexture) continue;
            try
            {
                var ssh = SshFile.Read(n.ReadAll());
                foreach (var tag in ssh.Images.Select(i => i.Tag).Distinct(StringComparer.OrdinalIgnoreCase))
                {
                    if (!idx._byTag.TryGetValue(tag, out var list)) idx._byTag[tag] = list = [];
                    list.Add(n);
                }
                idx.PackCount++;
                if (idx.PackCount % 250 == 0) progress?.Report(idx.PackCount);
            }
            catch { /* unreadable pack: skip */ }
            n.DropCache();
        }
        return idx;
    }

    /// <summary>SSH packs ranked by how many of <paramref name="tags"/> they contain.</summary>
    public List<(AssetNode Pack, int Hits)> Rank(IEnumerable<string> tags)
    {
        var score = new Dictionary<AssetNode, int>();
        foreach (var t in tags.Distinct(StringComparer.OrdinalIgnoreCase))
            foreach (var n in Find(t)) score[n] = score.GetValueOrDefault(n) + 1;
        return score.OrderByDescending(kv => kv.Value).ThenBy(kv => kv.Key.Path, StringComparer.OrdinalIgnoreCase)
            .Select(kv => (kv.Key, kv.Value)).ToList();
    }
}
