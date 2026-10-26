using MVPWorkshop.Core.Formats;
using MVPWorkshop.Core.IO;

namespace MVPWorkshop.Core.Vfs;

public sealed record PlayerSource(AssetNode Asset, PlayerAttributeRecord Record, IReadOnlyDictionary<int, string> Labels);
public sealed record TeamPlayer(int Slot, int Offset, uint Id, uint Packed, IReadOnlyList<PlayerSource> Sources)
{
    public string Key => $"{Id:X8}";
    public string Name => Sources.FirstOrDefault(s => s.Asset.Name == "attrib.dat") is { } s ? $"{s.Record.Field(0)} {s.Record.Field(1)}" : "Unresolved player key";
    public string Jersey => Sources.FirstOrDefault(s => s.Asset.Name == "attrib.dat")?.Record.Field(2) ?? "?";
    public string Position => Sources.FirstOrDefault(s => s.Asset.Name == "attrib.dat")?.Record.Field(5) ?? "?";
    public override string ToString() => $"{Name} · #{Jersey} · slot {Slot}";
    public string Evidence => $"roster.bin @0x{Offset:X}: LE32 key → " + (Sources.Count == 0 ? "no matching DAT row" : string.Join(", ", Sources.Select(s => $"{s.Asset.Name}:{s.Record.Line}")));
}

/// <summary>Evidence-backed roster joins plus explicitly shared discovery sources. Never infer portrait/equipment identity from school IDs.</summary>
public sealed class TeamWorkspaceSources
{
    private readonly List<(AssetNode Node, PlayerAttributes Table)> _tables = [];
    private readonly List<(AssetNode Node, string[] Lines)> _lookups = [];
    public List<TeamAssetLink> Shared { get; } = [];
    public List<string> Problems { get; } = [];

    public TeamWorkspaceSources(IReadOnlyList<AssetNode> nodes)
    {
        foreach (var node in nodes.Where(n => !n.IsFolder))
        {
            var path = node.Path;
            if (path.Contains("/DBMISC.BIG/", StringComparison.OrdinalIgnoreCase) && node.Name is "schoolnameaudio.csv" or "nicknameaudio.csv" or "teaminfo.csv")
                _lookups.Add((node, System.Text.Encoding.Latin1.GetString(node.ReadAll()).Split('\n')));
            if (path.Contains("/DATABASE.BIG/", StringComparison.OrdinalIgnoreCase) && node.Name is "attrib.dat" or "lhattrib.dat" or "rhattrib.dat" or "pitcher.dat")
                try { _tables.Add((node, PlayerAttributes.Read(node.ReadAll()))); }
                catch (Exception ex) { Problems.Add($"{node.Path}: {ex.Message}"); }
            if (path.Contains("/PORTRAIT.BIG/", StringComparison.OrdinalIgnoreCase))
                Shared.Add(new("Portraits/Shared library", node, "PORTRAIT.BIG source membership confirmed; player appearance → portrait filename mapping unresolved.", "Shared · unresolved selection"));
            else if (path.Contains("/GEOMETRY.BIG/", StringComparison.OrdinalIgnoreCase) || path.Contains("/GHEAD.BIG/", StringComparison.OrdinalIgnoreCase) ||
                path.Contains("/MODELS.BIG/", StringComparison.OrdinalIgnoreCase) && !System.Text.RegularExpressions.Regex.IsMatch(node.Name, "^[uf][0-9]{3}[a-z]\\.[sx]sh$", System.Text.RegularExpressions.RegexOptions.IgnoreCase))
                Shared.Add(new("Equipment/Shared player assets", node, "Shared player/model source; appearance and equipment fields are exposed on each player. Exact field → mesh/texture selection unresolved.", "Shared · unresolved selection"));
            else if (path.Contains("/AUDIO/", StringComparison.OrdinalIgnoreCase) && node.Extension is ".asf" or ".big" or ".bnk" or ".csi" or ".off")
                Shared.Add(new("Audio/Shared sources", node, "Audio source on disc. School, nickname, audioid and ditty → bank/stream selection unresolved; filename alone does not establish ownership.", "Shared · unresolved selection"));
        }
    }

    public void Populate(TeamProfile profile, byte[] roster)
    {
        var team = profile.Team;
        foreach (var (node, lines) in _lookups)
        {
            var wanted = node.Name == "nicknameaudio.csv" ? team.Nickname : node.Name == "teaminfo.csv" ? team.Abbreviation : team.Name;
            var matches = lines.Select((text, index) => (text: text.Trim(), line: index + 1)).Where(row => row.text.Split(',').Any(cell => cell.Trim().Equals(wanted, StringComparison.OrdinalIgnoreCase))).ToList();
            profile.Assets.Add(new(node.Name == "teaminfo.csv" ? "Roster/School metadata" : "Audio/Lookup tables", node,
                matches.Count > 0 ? string.Join("; ", matches.Select(m => $"{node.Name}:{m.line}: {m.text}")) + "; exact text match; runtime audio event/stream join unresolved."
                    : $"No exact '{wanted}' row in {node.Name}; runtime lookup unresolved.", matches.Count > 0 ? "Confirmed source row" : "Unresolved association"));
        }
        for (int slot = 0; slot < team.PlayerCount; slot++)
        {
            int offset = team.Offset + 196 + slot * 8;
            uint id = Bin.U32LE(roster, offset);
            var sources = _tables.Where(t => t.Table.Records.ContainsKey(id)).Select(t => new PlayerSource(t.Node, t.Table.Records[id], t.Table.Labels)).ToList();
            profile.Players.Add(new(slot + 1, offset, id, Bin.U32LE(roster, offset + 4), sources));
        }
        foreach (var node in profile.Players.SelectMany(p => p.Sources).Select(s => s.Asset).Distinct())
            profile.Assets.Add(new("Roster/Player databases", node, "Confirmed hexadecimal player-key joins from roster slots at record +196 (8-byte stride); open player details for exact DAT lines."));
        profile.Assets.Add(new("Roster/Team record", profile.Roster, $"Team record {team.RosterIndex} @0x{team.Offset:X}; {team.PlayerCount} slots; second slot word retained as unknown packed data."));
        profile.SharedAssets = Shared;
        profile.WorkspaceProblems = Problems;
    }
}
