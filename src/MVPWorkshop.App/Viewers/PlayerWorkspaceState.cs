using MVPWorkshop.Core.Formats;
using MVPWorkshop.Core.Vfs;
using MVPWorkshop.Core.Editing;

namespace MVPWorkshop.App.Viewers;

/// <summary>Unapplied values belong to the school workspace, not to a temporary player selection.</summary>
public sealed class PlayerWorkspaceState
{
    public sealed record Draft(AssetNode Node, uint Player, byte[] Baseline, List<TeamWorkspaceView.EditableField> Fields);
    private readonly Dictionary<(string Asset, uint Player), Draft> _drafts = [];

    public Draft Get(PlayerSource source, uint player)
    {
        var key = (source.Asset.Key, player);
        var bytes = source.Asset.ReadAll();
        if (_drafts.TryGetValue(key, out var draft) && (draft.Fields.Any(f => f.IsChanged) || draft.Baseline.AsSpan().SequenceEqual(bytes))) return draft;
        var table = PlayerAttributes.Read(bytes);
        var fields = table.Records[player].Fields.OrderBy(f => f.Key).Select(f => new TeamWorkspaceView.EditableField
        {
            Id = f.Key, Name = table.Labels.GetValueOrDefault(f.Key, "Unknown field (read-only)"),
            Value = f.Value, Original = f.Value, Unknown = !table.Labels.ContainsKey(f.Key)
        }).ToList();
        return _drafts[key] = new(source.Asset, player, bytes, fields);
    }

    public void Remove(PlayerSource source, uint player) => _drafts.Remove((source.Asset.Key, player));
    public void Restore(PlayerSource source, uint player, Draft draft) => _drafts[(source.Asset.Key, player)] = draft;
    public int PendingFields => _drafts.Values.Sum(d => d.Fields.Count(f => f.IsChanged));
    public void ResetAll()
    {
        var current = _drafts.Values.Select(d => d.Node).Distinct().ToDictionary(n => n, n => n.ReadAll());
        var tables = current.ToDictionary(p => p.Key, p => PlayerAttributes.Read(p.Value));
        foreach (var key in _drafts.Keys.ToList())
        {
            var draft = _drafts[key];
            if (!tables[draft.Node].Records.TryGetValue(draft.Player, out var record)) { _drafts.Remove(key); continue; }
            foreach (var field in draft.Fields) { field.Original = record.Field(field.Id); field.Value = field.Original; }
            _drafts[key] = draft with { Baseline = current[draft.Node] };
        }
    }

    public int ApplyAll(EditSession session, string school)
    {
        var pending = _drafts.Where(p => p.Value.Fields.Any(f => f.IsChanged)).ToList();
        var planned = new Dictionary<AssetNode, byte[]>();
        // Validate every draft and container before committing any of them.
        foreach (var (_, draft) in pending)
        {
            if (session.SaveBlocker(draft.Node) is { } reason) throw new InvalidOperationException(reason);
            var bytes = planned.GetValueOrDefault(draft.Node) ?? session.GetBytes(draft.Node);
            var edits = draft.Fields.Where(f => f.IsChanged && !f.Unknown).ToDictionary(f => f.Id, f => f.Value);
            planned[draft.Node] = PlayerAttributeEditor.ApplyDraft(draft.Baseline, bytes, draft.Player, edits);
        }
        foreach (var pair in pending) _drafts.Remove(pair.Key);
        foreach (var (node, bytes) in planned)
        {
            var changes = pending.Select(p => p.Value).Where(d => d.Node == node).Select(d => $"{d.Player:X8}: " + string.Join(", ", d.Fields.Where(f => f.IsChanged).Select(f => $"{f.Name}: {f.Original} → {f.Value}")));
            session.Commit(node, bytes, $"{school} / {node.Name}: " + string.Join("; ", changes));
        }
        return pending.Count;
    }
}
