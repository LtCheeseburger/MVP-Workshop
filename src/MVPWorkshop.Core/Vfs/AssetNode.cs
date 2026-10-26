using MVPWorkshop.Core.Formats;
using MVPWorkshop.Core.IO;

namespace MVPWorkshop.Core.Vfs;

/// <summary>
/// A node in the unified game file tree: disk folders, disk files, and entries nested inside containers
/// (BIG archives, SCHl stream packs). Data is loaded lazily; RefPack compression is removed transparently.
/// </summary>
public sealed class AssetNode
{
    private readonly Func<IByteSource>? _openStored;
    private List<AssetNode>? _children;
    private FormatId? _format;
    private WeakReference<byte[]>? _cache;
    private readonly object _lock = new();

    public AssetNode(string name, AssetNode? parent, bool isFolder, long storedSize, Func<IByteSource>? openStored, string location)
    {
        Name = name; Parent = parent; IsFolder = isFolder; StoredSize = storedSize; _openStored = openStored; Location = location;
    }

    public string Name { get; }
    public AssetNode? Parent { get; }
    public bool IsFolder { get; }
    public long StoredSize { get; }
    /// <summary>Human readable origin, e.g. "disk" or "BIGF entry #12 @0x1A00".</summary>
    public string Location { get; }
    public bool IsVirtualFolder { get; init; }
    /// <summary>Directory index when this node is an entry of a BIGF archive; lets edits find the entry again even if names repeat.</summary>
    public int? BigIndex { get; init; }
    public string? DiskPath { get; init; }
    public IsoImage? DiscImage { get; init; }
    public IsoEntry? DiscEntry { get; init; }
    public bool IsCompressed { get; private set; }
    public long? UnpackedSize { get; private set; }
    public string? LoadError { get; private set; }

    public string Path => Parent is null ? Name : Parent.Path + "/" + Name;
    public string Extension => System.IO.Path.GetExtension(Name).ToLowerInvariant();
    /// <summary>PS2 <c>.ssh</c> or Xbox <c>.xsh</c> texture pack.</summary>
    public bool IsTexturePack => Extension is ".ssh" or ".xsh";
    public int Depth => Parent is null ? 0 : Parent.Depth + 1;

    /// <summary>Nearest archive/container ancestor's name, for display.</summary>
    public string Container
    {
        get
        {
            for (var p = Parent; p is not null; p = p.Parent) if (!p.IsFolder) return p.Name;
            return "";
        }
    }

    public FormatId Format
    {
        get
        {
            if (IsFolder) return FormatId.Folder;
            if (_format is { } f) return f;
            try
            {
                _format = FormatDetector.Detect(Name, Open());
                if (_format is FormatId.Binary or FormatId.Text && Extension == ".big" && OffsetIndexNode() is not null) _format = FormatId.OffsetPack;
            }
            catch (Exception ex) { LoadError = ex.Message; _format = FormatId.Unknown; }
            return _format.Value;
        }
    }

    public FormatInfo FormatInfo => FormatDetector.Info[Format];
    public bool IsContainer => IsFolder || Format is FormatId.BigArchive or FormatId.SchlContainer or FormatId.OffsetPack;

    /// <summary>
    /// Supplies edited contents for a node (the active edit session). Everything that reads assets through <see cref="Open"/> — viewers,
    /// sibling lookups such as an APT movie's .ssh/.const — therefore sees edits immediately. Rebuild and verification code reads the
    /// untouched game data through <see cref="OpenOriginal"/> instead.
    /// </summary>
    public static Func<AssetNode, byte[]?>? Overlay { get; set; }

    /// <summary>Open the logical (decompressed) bytes, including any edits applied by the active session.</summary>
    public IByteSource Open()
    {
        if (!IsFolder && Overlay?.Invoke(this) is { } edited) return new MemoryByteSource(edited);
        return OpenOriginal();
    }

    /// <summary>The logical (decompressed) bytes as they are in the game files, ignoring edits.</summary>
    public IByteSource OpenOriginal()
    {
        if (IsFolder || _openStored is null) return new MemoryByteSource([]);
        lock (_lock)
        {
            if (_cache is not null && _cache.TryGetTarget(out var cached)) return new MemoryByteSource(cached);
            var stored = _openStored();
            var head = stored.ReadBytes(0, 8);
            if (RefPack.IsCompressed(head))
            {
                var all = stored.ReadAll();
                byte[] data;
                try { data = RefPack.Decompress(all); IsCompressed = true; UnpackedSize = data.Length; }
                catch (Exception ex) { LoadError = "RefPack: " + ex.Message; data = all; }
                _cache = new WeakReference<byte[]>(data);
                return new MemoryByteSource(data);
            }
            UnpackedSize = stored.Length;
            if (stored is MemoryByteSource m) _cache = new WeakReference<byte[]>(m.Data);
            return stored;
        }
    }

    public byte[] ReadAll() => Open().ReadAll();
    public byte[] ReadOriginal() => OpenOriginal().ReadAll();

    /// <summary>The bytes exactly as stored in the parent (still RefPack-compressed when they are).</summary>
    public byte[] ReadStored() => _openStored is null ? [] : _openStored().ReadAll();

    public AssetNode Root => Parent is null ? this : Parent.Root;

    /// <summary>
    /// Stable identity of this node below the game root, e.g. <c>DATA/GHEAD.BIG/art/x.ssh#12</c>. BIG entries carry <c>#index</c> so
    /// duplicate names stay distinct; the root itself is the empty string. Used as the key for edits and project files.
    /// </summary>
    public string Key
    {
        get
        {
            if (Parent is null) return "";
            var seg = BigIndex is { } i ? $"{Name}#{i}" : Name;
            return Parent.Parent is null ? seg : Parent.Key + "/" + seg;
        }
    }

    /// <summary>Find a node from a <see cref="Key"/> relative to this root.</summary>
    public AssetNode? Resolve(string key)
    {
        var n = this;
        foreach (var part in key.Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            var hash = part.LastIndexOf('#');
            int? idx = hash > 0 && int.TryParse(part.AsSpan(hash + 1), out var v) ? v : null;
            var name = idx is null ? part : part[..hash];
            n = n.IsContainer || n.IsFolder ? n.Children.FirstOrDefault(c => c.Name.Equals(name, StringComparison.OrdinalIgnoreCase) && (idx is null || c.BigIndex == idx)) : null;
            if (n is null) return null;
        }
        return n;
    }

    /// <summary>Why the contents of this node cannot be written back into game files, or null when they can.</summary>
    public string? WriteBlocker
    {
        get
        {
            if (IsFolder) return "Folders are not assets.";
            for (var p = Parent; p is not null; p = p.Parent)
            {
                if (p.IsFolder) continue;
                if (p.Format != FormatId.BigArchive)
                    return $"Stored inside {p.Name} ({p.FormatInfo.Name}); only BIG archives and loose files can be rebuilt.";
            }
            return null;
        }
    }

    /// <summary>For split EA objects (.ord/.orl pairs) return the joined ELF; otherwise this node's bytes.</summary>
    public (byte[] Data, AssetNode? Partner) ReadElfJoined()
    {
        var self = ReadAll();
        var ext = Extension;
        if (Parent is null || ext is not (".ord" or ".orl")) return (self, null);
        var stem = System.IO.Path.GetFileNameWithoutExtension(Name);
        var partner = Parent.Children.FirstOrDefault(c => c.Name.Equals(stem + (ext == ".ord" ? ".orl" : ".ord"), StringComparison.OrdinalIgnoreCase));
        if (partner is null) return (self, null);
        var (ord, orl) = ext == ".ord" ? (self, partner.ReadAll()) : (partner.ReadAll(), self);
        return ElfFile.NeedsLinkPartner(ord) ? (ElfFile.JoinSplit(ord, orl), partner) : (self, null);
    }

    public IReadOnlyList<AssetNode> Children
    {
        get
        {
            if (_children is not null) return _children;
            lock (_lock)
            {
                if (_children is not null) return _children;
                try { _children = BuildChildren(); }
                catch (Exception ex) { LoadError = ex.Message; _children = []; }
                return _children;
            }
        }
    }

    public bool ChildrenLoaded => _children is not null;

    internal void SetChildren(List<AssetNode> c) => _children = c;

    private List<AssetNode> BuildChildren()
    {
        if (IsFolder && DiskPath is not null) return GameFileSystem.ListDirectory(this, DiskPath);
        if (IsFolder) return [];
        return Format switch
        {
            FormatId.BigArchive => BuildBigChildren(),
            FormatId.SchlContainer => BuildSchlChildren(),
            FormatId.OffsetPack => BuildOffsetChildren(),
            _ => [],
        };
    }

    /// <summary>The sibling <c>NAME.OFF</c> text index of a headerless <c>NAME.BIG</c> pack (PROJx DAT/HDR).</summary>
    public AssetNode? OffsetIndexNode()
    {
        if (Parent is null) return null;
        var off = System.IO.Path.GetFileNameWithoutExtension(Name) + ".OFF";
        return Parent.Children.FirstOrDefault(c => c.Name.Equals(off, StringComparison.OrdinalIgnoreCase));
    }

    private List<AssetNode> BuildOffsetChildren()
    {
        var index = OffsetIndex.Parse(System.Text.Encoding.ASCII.GetString(OffsetIndexNode()!.ReadAll()));
        var src = Open();
        var baseName = System.IO.Path.GetFileNameWithoutExtension(Name);
        var ext = baseName.EndsWith("HDR", StringComparison.OrdinalIgnoreCase) ? ".hdr" : ".bin";
        return index.Where(e => e.Offset >= 0 && e.Offset + e.Size <= src.Length).Select((e, i) =>
        {
            var (o, len) = e;
            Func<IByteSource> open = src is FileRangeByteSource fr
                ? () => new FileRangeByteSource(fr.Path, fr.Start + o, len)
                : () => new MemoryByteSource(Open().ReadBytes(o, (int)len));
            return new AssetNode($"{baseName}_{i:D4}{ext}", this, false, len, open, $"OFF record #{i} @0x{o:X}");
        }).ToList();
    }

    private List<AssetNode> BuildBigChildren()
    {
        var src = Open();
        var big = BigArchive.Read(src);
        var root = new List<AssetNode>();
        var folders = new Dictionary<string, (AssetNode Node, List<AssetNode> Kids)>(StringComparer.OrdinalIgnoreCase);

        List<AssetNode> KidsFor(string dir, out AssetNode parent)
        {
            parent = this;
            if (dir.Length == 0) return root;
            if (folders.TryGetValue(dir, out var f)) { parent = f.Node; return f.Kids; }
            var idx = dir.LastIndexOf('/');
            var parentKids = KidsFor(idx < 0 ? "" : dir[..idx], out var pp);
            var node = new AssetNode(idx < 0 ? dir : dir[(idx + 1)..], pp, true, 0, null, "BIG sub-path") { IsVirtualFolder = true };
            var kids = new List<AssetNode>();
            node.SetChildren(kids);
            folders[dir] = (node, kids);
            parentKids.Add(node);
            parent = node;
            return kids;
        }

        foreach (var e in big.Entries)
        {
            var norm = e.Name.Replace('\\', '/').Trim('/');
            var slash = norm.LastIndexOf('/');
            var list = KidsFor(slash < 0 ? "" : norm[..slash], out var parent);
            var entry = e;
            Func<IByteSource> open = src is FileRangeByteSource fr
                ? () => new FileRangeByteSource(fr.Path, fr.Start + entry.Offset, entry.Size)
                : () => new MemoryByteSource(Open().ReadBytes(entry.Offset, (int)entry.Size));
            list.Add(new AssetNode(slash < 0 ? norm : norm[(slash + 1)..], parent, false, e.Size, open, $"BIG entry #{e.Index} @0x{e.Offset:X}") { BigIndex = e.Index });
        }
        return root;
    }

    private List<AssetNode> BuildSchlChildren()
    {
        var src = Open();
        var streams = SchlStream.ScanAll(src);
        var baseName = System.IO.Path.GetFileNameWithoutExtension(Name);
        return streams.Select((s, i) =>
        {
            var st = s;
            Func<IByteSource> open = src is FileRangeByteSource fr
                ? () => new FileRangeByteSource(fr.Path, fr.Start + st.Offset, st.Length)
                : () => new MemoryByteSource(Open().ReadBytes(st.Offset, (int)st.Length));
            return new AssetNode($"{baseName}_{i:D4}.asf", this, false, s.Length, open, $"SCHl stream #{i} @0x{s.Offset:X}");
        }).ToList();
    }

    /// <summary>Depth-first enumeration including container contents (expands archives).</summary>
    public IEnumerable<AssetNode> EnumerateDeep(Func<AssetNode, bool>? descend = null)
    {
        var stack = new Stack<AssetNode>();
        stack.Push(this);
        while (stack.Count > 0)
        {
            var n = stack.Pop();
            yield return n;
            if (n.IsContainer && (descend?.Invoke(n) ?? true))
                for (var i = n.Children.Count - 1; i >= 0; i--) stack.Push(n.Children[i]);
        }
    }

    public void DropCache() { _cache = null; }

    public override string ToString() => Path;
}

public static class GameFileSystem
{
    public static AssetNode OpenGame(string path) => File.Exists(path) ? OpenIso(path) : OpenFolder(path);

    public static AssetNode OpenIso(string path)
    {
        var iso = IsoImage.Read(path);
        var root = new AssetNode(System.IO.Path.GetFileNameWithoutExtension(path), null, true, 0, null, "ISO9660") { DiskPath = iso.SourcePath, DiscImage = iso };
        var folders = new Dictionary<string, AssetNode>(StringComparer.OrdinalIgnoreCase) { [""] = root };
        var children = new Dictionary<AssetNode, List<AssetNode>> { [root] = [] };
        foreach (var entry in iso.Entries.OrderBy(e => e.Path.Count(c => c == '/')).ThenByDescending(e => e.Directory))
        {
            var slash = entry.Path.LastIndexOf('/'); var parentPath = slash < 0 ? "" : entry.Path[..slash];
            var parent = folders[parentPath]; var name = slash < 0 ? entry.Path : entry.Path[(slash + 1)..];
            var node = new AssetNode(name, parent, entry.Directory, entry.Length, entry.Directory ? null : () => iso.Open(entry), $"ISO9660 LBA {entry.Sector}") { DiscEntry = entry };
            children[parent].Add(node);
            if (entry.Directory) { folders[entry.Path] = node; children[node] = []; }
        }
        foreach (var (folder, contents) in children) folder.SetChildren(contents);
        return root;
    }

    public static AssetNode OpenFolder(string path)
    {
        var di = new DirectoryInfo(path);
        return new AssetNode(di.Name, null, true, 0, null, "disk") { DiskPath = di.FullName };
    }

    public static AssetNode OpenFile(string path)
    {
        var fi = new FileInfo(path);
        return new AssetNode(fi.Name, null, false, fi.Length, () => new FileRangeByteSource(fi.FullName, 0, fi.Length), "disk") { DiskPath = fi.FullName };
    }

    internal static List<AssetNode> ListDirectory(AssetNode parent, string path)
    {
        var di = new DirectoryInfo(path);
        var list = new List<AssetNode>();
        foreach (var d in di.EnumerateDirectories().OrderBy(d => d.Name, StringComparer.OrdinalIgnoreCase))
            list.Add(new AssetNode(d.Name, parent, true, 0, null, "disk") { DiskPath = d.FullName });
        foreach (var f in di.EnumerateFiles().OrderBy(f => f.Name, StringComparer.OrdinalIgnoreCase))
        {
            var fi = f;
            list.Add(new AssetNode(f.Name, parent, false, f.Length, () => new FileRangeByteSource(fi.FullName, 0, fi.Length), "disk") { DiskPath = f.FullName });
        }
        return list;
    }
}
