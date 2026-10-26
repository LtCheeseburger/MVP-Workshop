using MVPWorkshop.Core.Vfs;

namespace MVPWorkshop.Core.Editing;

/// <summary>
/// Saved changes replace an asset's bytes and are written into rebuilt game archives on export.
/// Preview-only changes exist only in a viewer (the format or location cannot be written back) and are never exported.
/// </summary>
public enum ChangeKind { Saved, PreviewOnly }

public sealed class Change
{
    public required int Id { get; init; }
    public required string Key { get; init; }
    public required string Path { get; init; }
    public required ChangeKind Kind { get; init; }
    public required string Description { get; init; }
    public string? Reason { get; init; }
    public DateTime Time { get; init; } = DateTime.Now;
    /// <summary>Override bytes before / after this change (null = the asset has no override, i.e. original data).</summary>
    internal byte[]? Before { get; init; }
    internal byte[]? After { get; init; }
    internal Action<bool>? Preview { get; init; }
    /// <summary>For preview-only byte edits: the node whose viewer-visible contents change.</summary>
    internal AssetNode? PreviewNode { get; init; }
}

/// <summary>
/// All edits for one game folder: a set of per-asset replacement bytes (the logical, decompressed contents), a linear undo/redo history
/// and unsaved-change tracking. The game files themselves are never touched; <see cref="ModExporter"/> writes rebuilt copies elsewhere.
/// </summary>
public sealed class EditSession
{
    private readonly Dictionary<string, byte[]> _overrides = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<AssetNode, byte[]> _preview = [];          // preview-only contents, by node identity; never exported or saved
    private readonly List<Change> _undo = [], _redo = [];
    private int _cleanId;         // Id of the newest saved-kind change at the last project save (0 = none)
    private int _nextId = 1;

    public EditSession(AssetNode gameRoot, string outputFolder)
    {
        GameRoot = gameRoot; OutputFolder = outputFolder;
        AssetNode.Overlay = n =>
        {
            if (_overrides.Count > 0 && _overrides.TryGetValue(n.Key, out var edited) && n.Root == GameRoot) return edited;
            return _preview.Count > 0 && _preview.TryGetValue(n, out var shown) ? shown : null;
        };
    }

    public AssetNode GameRoot { get; }
    public string OutputFolder { get; set; }
    public string? ProjectPath { get; set; }
    public event Action? Changed;

    public IReadOnlyDictionary<string, byte[]> Overrides => _overrides;
    public IReadOnlyList<Change> History => _undo;
    public IReadOnlyList<Change> Undone => _redo;
    public bool CanUndo => _undo.Count > 0;
    public bool CanRedo => _redo.Count > 0;
    public string? UndoLabel => _undo.Count > 0 ? _undo[^1].Description : null;
    public string? RedoLabel => _redo.Count > 0 ? _redo[^1].Description : null;

    /// <summary>True when the set of saved changes differs from the last saved project (or from the start when never saved).</summary>
    public bool IsDirty => TopSavedId != _cleanId;
    private int TopSavedId => _undo.LastOrDefault(c => c.Kind == ChangeKind.Saved)?.Id ?? 0;
    public int ModifiedAssetCount => _overrides.Count;
    public int PreviewOnlyCount => _undo.Count(c => c.Kind == ChangeKind.PreviewOnly);

    public bool IsModified(AssetNode n) => _overrides.ContainsKey(n.Key);
    public bool HasOverrideBelow(AssetNode n)
    {
        var prefix = n.Key.Length == 0 ? "" : n.Key + "/";
        return _overrides.Keys.Any(k => k.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) && k.Length > prefix.Length);
    }

    /// <summary>Current logical bytes of an asset: the edited version when there is one, otherwise the original.</summary>
    public byte[] GetBytes(AssetNode n) => _overrides.TryGetValue(n.Key, out var b) ? b : n.ReadOriginal();
    public byte[]? GetOverride(AssetNode n) => _overrides.GetValueOrDefault(n.Key);

    /// <summary>Null when edits to <paramref name="n"/> can be saved into game assets; otherwise the reason they can only be previewed.</summary>
    public string? SaveBlocker(AssetNode n)
    {
        if (n.Root != GameRoot) return "This file was opened outside the project's game folder.";
        if (n.WriteBlocker is { } why) return why;
        for (var p = n.Parent; p is not null; p = p.Parent)
            if (_overrides.ContainsKey(p.Key)) return $"{p.Name} is replaced as a whole; revert that first.";
        if (HasOverrideBelow(n)) return $"{n.Name} contains edited assets; revert them before replacing the whole file.";
        return null;
    }

    /// <summary>Record a saved change: <paramref name="newBytes"/> become the asset's contents in the exported mod.</summary>
    public Change Commit(AssetNode node, byte[] newBytes, string description)
    {
        if (SaveBlocker(node) is { } why) throw new InvalidOperationException(why);
        var before = _overrides.GetValueOrDefault(node.Key);
        var original = node.ReadOriginal();
        byte[]? after = newBytes.AsSpan().SequenceEqual(original) ? null : newBytes;
        var c = new Change { Id = _nextId++, Key = node.Key, Path = node.Path, Kind = ChangeKind.Saved, Description = description, Before = before, After = after };
        Push(c);
        Apply(c, forward: true);
        Notify();
        return c;
    }

    /// <summary>
    /// Record a change that only lives in a viewer. <paramref name="preview"/> is called with true when the change (re)applies and false when it is undone.
    /// </summary>
    public Change CommitPreviewOnly(AssetNode node, string description, string reason, Action<bool> preview)
    {
        var c = new Change { Id = _nextId++, Key = node.Key, Path = node.Path, Kind = ChangeKind.PreviewOnly, Description = description, Reason = reason, Preview = preview };
        Push(c);
        preview(true);
        Notify();
        return c;
    }

    /// <summary>
    /// Record new contents that viewers show but that cannot be saved into game files (see <see cref="SaveBlocker"/>).
    /// They live only in this session: they are not part of projects or exports.
    /// </summary>
    public Change CommitPreviewBytes(AssetNode node, byte[] bytes, string description, string reason)
    {
        var c = new Change { Id = _nextId++, Key = node.Key, Path = node.Path, Kind = ChangeKind.PreviewOnly, Description = description, Reason = reason, Before = _preview.GetValueOrDefault(node), After = bytes, PreviewNode = node };
        Push(c);
        Apply(c, forward: true);
        Notify();
        return c;
    }

    /// <summary>Current preview-only contents of a node, if any.</summary>
    public byte[]? GetPreview(AssetNode n) => _preview.GetValueOrDefault(n);

    /// <summary>True when the node, or something inside it, has been edited (saved or preview-only).</summary>
    public bool IsAffected(AssetNode n)
    {
        if (_affectedKeys.Count == 0 && _affectedNodes.Count == 0) return false;
        return _affectedNodes.Contains(n) || n.Root == GameRoot && _affectedKeys.Contains(n.Key);
    }

    private readonly HashSet<string> _affectedKeys = new(StringComparer.OrdinalIgnoreCase);   // edited assets and every ancestor path
    private readonly HashSet<AssetNode> _affectedNodes = [];                                  // preview-edited nodes and their ancestors

    private void RebuildAffected()
    {
        _affectedKeys.Clear(); _affectedNodes.Clear();
        foreach (var key in _overrides.Keys)
        {
            for (var i = key.IndexOf('/'); i >= 0; i = key.IndexOf('/', i + 1)) _affectedKeys.Add(key[..i]);
            _affectedKeys.Add(key);
        }
        foreach (var n in _preview.Keys) for (var p = n; p is not null; p = p.Parent) _affectedNodes.Add(p);
    }

    /// <summary>Drop every saved edit of an asset (as one undoable step).</summary>
    public void Revert(AssetNode node)
    {
        if (!_overrides.TryGetValue(node.Key, out var current)) return;
        var c = new Change { Id = _nextId++, Key = node.Key, Path = node.Path, Kind = ChangeKind.Saved, Description = "Revert to original", Before = current, After = null };
        Push(c);
        Apply(c, forward: true);
        Notify();
    }

    public void Undo()
    {
        if (_undo.Count == 0) return;
        var c = _undo[^1]; _undo.RemoveAt(_undo.Count - 1); _redo.Add(c);
        Apply(c, forward: false);
        Notify();
    }

    public void Redo()
    {
        if (_redo.Count == 0) return;
        var c = _redo[^1]; _redo.RemoveAt(_redo.Count - 1); _undo.Add(c);
        Apply(c, forward: true);
        Notify();
    }

    /// <summary>Call after the project file was written so unsaved-change tracking restarts from here.</summary>
    public void MarkSaved() { _cleanId = TopSavedId; Notify(); }

    /// <summary>Replace the saved-change set wholesale (project load). Clears history.</summary>
    internal void Load(IEnumerable<(string Key, byte[] Bytes)> edits)
    {
        _overrides.Clear(); _undo.Clear(); _redo.Clear();
        foreach (var (k, b) in edits) _overrides[k] = b;
        _cleanId = 0;
        Notify();
    }

    private void Push(Change c)
    {
        _redo.Clear();
        _undo.Add(c);
    }

    private void Apply(Change c, bool forward)
    {
        if (c.Kind == ChangeKind.PreviewOnly)
        {
            if (c.PreviewNode is { } pn)
            {
                var shown = forward ? c.After : c.Before;
                if (shown is null) _preview.Remove(pn); else _preview[pn] = shown;
            }
            c.Preview?.Invoke(forward);
            return;
        }
        var target = forward ? c.After : c.Before;
        if (target is null) _overrides.Remove(c.Key); else _overrides[c.Key] = target;
    }

    private void Notify() { RebuildAffected(); Changed?.Invoke(); }
}
