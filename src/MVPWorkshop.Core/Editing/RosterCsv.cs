using System.Globalization;
using System.Text;
using MVPWorkshop.Core.Formats;
using MVPWorkshop.Core.Vfs;

namespace MVPWorkshop.Core.Editing;

public sealed record RosterCsvChange(int CsvRow, string School, int Slot, string Player, string Source, string Field, string Before, string After, string Problem = "");

public sealed class RosterCsvPlan
{
    public List<RosterCsvChange> Changes { get; } = [];
    public List<string> Problems { get; } = [];
    internal Dictionary<AssetNode, byte[]> Baselines { get; } = [];
    internal Dictionary<AssetNode, byte[]> Outputs { get; } = [];
    public int ChangedRows => Changes.Where(c => c.Problem.Length == 0).Select(c => (c.Source, c.Player)).Distinct().Count();
    public int Apply(EditSession session)
    {
        if (Problems.Count != 0) throw new InvalidDataException(string.Join("\n", Problems));
        foreach (var (node, baseline) in Baselines)
            if (!session.GetBytes(node).AsSpan().SequenceEqual(baseline)) throw new InvalidOperationException($"{node.Name} changed after CSV review. Import it again.");
        foreach (var (node, _) in Outputs)
        {
            if (session.SaveBlocker(node) is { } blocker) throw new InvalidOperationException(blocker);
            if (!session.GetBytes(node).AsSpan().SequenceEqual(Baselines[node])) throw new InvalidOperationException($"{node.Name} changed after CSV review. Import it again.");
        }
        foreach (var (node, bytes) in Outputs) session.Commit(node, bytes, $"Roster CSV: {Changes.Count(c => c.Source == node.Key)} field change(s) in {node.Name}");
        return Outputs.Count;
    }
}

/// <summary>One row per school slot and source DAT; identities are read-only and edits splice existing value spans.</summary>
public static class RosterCsv
{
    private static readonly string[] Identity = ["School index", "School", "Roster slot", "Player key", "Source"];
    private sealed record SourceRow(TeamProfile School, TeamPlayer Player, AssetNode Node, byte[] Baseline, PlayerAttributes Table);
    private static List<SourceRow> Sources(IReadOnlyList<TeamProfile> schools)
    {
        var bytes = schools.SelectMany(s => s.Players).SelectMany(p => p.Sources).Select(s => s.Asset).Distinct().ToDictionary(n => n, n => n.ReadAll().ToArray());
        var tables = bytes.ToDictionary(p => p.Key, p => PlayerAttributes.Read(p.Value));
        return schools.SelectMany(s => s.Players.SelectMany(p => p.Sources.DistinctBy(a => a.Asset.Key).Select(a => new SourceRow(s, p, a.Asset, bytes[a.Asset], tables[a.Asset])))).ToList();
    }
    private static string Column(int index, string label)
    {
        var readable = label switch { "first_name" => "First name", "last_name" => "Last name", "playerattrib_jerseynum" => "Jersey number", _ => CultureInfo.InvariantCulture.TextInfo.ToTitleCase(label.Replace("playerattrib_", "").Replace("pitchattrib_", "").Replace("lrattrib_", "").Replace('_', ' ')) };
        return $"{readable} [{index}:{label}]";
    }
    private static Dictionary<string, (int Index, string Label)> Columns(IEnumerable<SourceRow> rows) => rows.SelectMany(r => r.Table.Labels).Distinct().ToDictionary(p => Column(p.Key, p.Value), p => (p.Key, p.Value), StringComparer.Ordinal);
    private static string Quote(string text) => text.IndexOfAny([',', '"', '\r', '\n']) < 0 ? text : "\"" + text.Replace("\"", "\"\"") + "\"";
    public static string Export(IReadOnlyList<TeamProfile> schools)
    {
        var rows = Sources(schools);
        var columns = Columns(rows).OrderBy(c => c.Value.Index).ThenBy(c => c.Value.Label, StringComparer.Ordinal).ToList();
        var output = new StringBuilder();
        output.AppendLine(string.Join(',', Identity.Concat(columns.Select(c => c.Key)).Select(Quote)));
        foreach (var row in rows)
        {
            var record = row.Table.Records[row.Player.Id];
            var values = new[] { row.School.Team.RosterIndex.ToString(CultureInfo.InvariantCulture), row.School.Label, row.Player.Slot.ToString(CultureInfo.InvariantCulture), "0x" + row.Player.Id.ToString("X8"), row.Node.Key };
            output.AppendLine(string.Join(',', values.Concat(columns.Select(c => row.Table.Labels.GetValueOrDefault(c.Value.Index) == c.Value.Label ? record.Fields.GetValueOrDefault(c.Value.Index, "") : "")).Select(Quote)));
        }
        foreach (var school in schools)
            foreach (var player in school.Players.Where(p => p.Sources.Count == 0))
                output.AppendLine(string.Join(',', new[] { school.Team.RosterIndex.ToString(CultureInfo.InvariantCulture), school.Label, player.Slot.ToString(CultureInfo.InvariantCulture), "0x" + player.Id.ToString("X8"), "(unresolved)" }.Concat(columns.Select(_ => "")).Select(Quote)));
        return output.ToString();
    }

    public static RosterCsvPlan Plan(string csv, IReadOnlyList<TeamProfile> schools)
    {
        var plan = new RosterCsvPlan();
        List<List<string>> parsed;
        try { parsed = Parse(csv); } catch (Exception ex) { plan.Problems.Add(ex.Message); return plan; }
        if (parsed.Count < 1) { plan.Problems.Add("CSV is empty."); return plan; }
        var header = parsed[0];
        if (header.Distinct(StringComparer.Ordinal).Count() != header.Count) { plan.Problems.Add("Duplicate CSV column names."); return plan; }
        if (Identity.Any(name => !header.Contains(name))) { plan.Problems.Add("Keep all five identity columns: " + string.Join(", ", Identity)); return plan; }
        var columnIndices = header.Select((name, index) => (name, index)).ToDictionary(p => p.name, p => p.index, StringComparer.Ordinal);
        var rows = Sources(schools);
        foreach (var roster in schools.Select(s => s.Roster).Distinct()) plan.Baselines[roster] = roster.ReadAll().ToArray();
        var columns = Columns(rows);
        foreach (var name in header.Except(Identity)) if (!columns.ContainsKey(name)) plan.Problems.Add("Unknown or renamed CSV column: " + name);
        if (plan.Problems.Count > 0) return plan;
        var expected = rows.ToDictionary(r => (r.School.Team.RosterIndex, r.Player.Slot, r.Player.Id, r.Node.Key.ToLowerInvariant()));
        var seen = new HashSet<(int, int, uint, string)>();
        var values = new Dictionary<(AssetNode Node, uint Player, int Field), string>();
        var edits = new Dictionary<(AssetNode Node, uint Player), Dictionary<int, string>>();
        for (int i = 1; i < parsed.Count; i++)
        {
            var cells = parsed[i];
            if (cells.All(string.IsNullOrEmpty)) continue;
            void Problem(string text) => plan.Problems.Add($"CSV row {i + 1}: {text}");
            if (cells.Count != header.Count) { Problem("Column count does not match the header."); continue; }
            string Cell(string name) => cells[columnIndices[name]];
            var playerText = Cell(Identity[3]);
            if (playerText.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) playerText = playerText[2..];
            if (!int.TryParse(Cell(Identity[0]), out var school) || !int.TryParse(Cell(Identity[2]), out var slot) || !uint.TryParse(playerText, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var player)) { Problem("Invalid school, slot or hexadecimal player identity."); continue; }
            var key = (school, slot, player, Cell(Identity[4]).ToLowerInvariant());
            if (!seen.Add(key)) { Problem("Duplicate school/player/source row."); continue; }
            if (key.Item4 == "(unresolved)")
            {
                var unresolved = schools.FirstOrDefault(s => s.Team.RosterIndex == school && s.Label == Cell(Identity[1]))?.Players.FirstOrDefault(p => p.Slot == slot && p.Id == player && p.Sources.Count == 0);
                if (unresolved is null) Problem("Unresolved player identity is outside the selected roster.");
                else if (header.Except(Identity).Any(name => Cell(name).Length != 0)) Problem("Unresolved player source rows are read-only.");
                continue;
            }
            if (!expected.TryGetValue(key, out var source) || source.School.Label != Cell(Identity[1])) { Problem("Identity is outside the selected school's roster, or an identity column was edited."); continue; }
            if (!plan.Baselines.ContainsKey(source.Node)) plan.Baselines[source.Node] = source.Baseline;
            var record = source.Table.Records[player];
            foreach (var name in header.Except(Identity))
            {
                var field = columns[name]; var after = Cell(name);
                if (source.Table.Labels.GetValueOrDefault(field.Index) != field.Label || !record.Fields.TryGetValue(field.Index, out var before))
                { if (after.Length > 0) Problem($"{name} has no editable value in {source.Node.Name}."); continue; }
                var target = (source.Node, player, field.Index);
                if (values.TryGetValue(target, out var prior) && prior != after) { Problem($"Conflicting values for shared player {player:X8}, {name}. Keep repeated rows consistent."); continue; }
                values[target] = after;
                if (before == after) continue;
                string problem = "";
                try { PlayerAttributeEditor.Patch(plan.Baselines[source.Node], player, new Dictionary<int, string> { [field.Index] = after }); }
                catch (Exception ex) { problem = ex.Message; Problem($"{name}: {problem}"); }
                plan.Changes.Add(new(i + 1, source.School.Label, slot, player.ToString("X8"), source.Node.Key, name, before, after, problem));
                if (problem.Length == 0)
                {
                    var editKey = (source.Node, player);
                    if (!edits.TryGetValue(editKey, out var fields)) edits[editKey] = fields = [];
                    fields[field.Index] = after;
                }
            }
        }
        if (plan.Problems.Count == 0)
            foreach (var group in edits.GroupBy(e => e.Key.Node))
            {
                var bytes = plan.Baselines[group.Key];
                foreach (var edit in group) bytes = PlayerAttributeEditor.Patch(bytes, edit.Key.Player, edit.Value);
                plan.Outputs[group.Key] = bytes;
            }
        return plan;
    }

    private static List<List<string>> Parse(string text)
    {
        var rows = new List<List<string>>(); var row = new List<string>(); var cell = new StringBuilder();
        bool quoted = false, closed = false;
        text = text.TrimStart('\uFEFF');
        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];
            if (quoted)
            {
                if (c == '"' && i + 1 < text.Length && text[i + 1] == '"') { cell.Append('"'); i++; }
                else if (c == '"') { quoted = false; closed = true; }
                else cell.Append(c);
            }
            else if (c == ',' || c is '\r' or '\n')
            {
                row.Add(cell.ToString()); cell.Clear(); closed = false;
                if (c != ',') { rows.Add(row); row = []; if (c == '\r' && i + 1 < text.Length && text[i + 1] == '\n') i++; }
            }
            else if (c == '"' && cell.Length == 0 && !closed) quoted = true;
            else if (closed || c == '"') throw new InvalidDataException("Malformed quoted CSV cell.");
            else cell.Append(c);
        }
        if (quoted) throw new InvalidDataException("Unclosed quoted CSV cell.");
        if (cell.Length > 0 || row.Count > 0 || closed) { row.Add(cell.ToString()); rows.Add(row); }
        return rows;
    }
}
