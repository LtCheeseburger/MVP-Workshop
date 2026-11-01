using System.Text.RegularExpressions;
using MVPWorkshop.Core.Formats;
using MVPWorkshop.Core.Vfs;

namespace MVPWorkshop.Core.Editing;

/// <summary>A complete decoded family copy; unknown runtime bindings are never guessed.</summary>
public sealed class SchoolAssetFork
{
    public required TeamProfile School { get; init; }
    public required AssetNode Source { get; init; }
    public required int Family { get; init; }
    public required bool Uniform { get; init; }
    public required byte[] RosterBaseline { get; init; }
    public required byte[] RosterAfter { get; init; }
    public required IReadOnlyList<(AssetNode Source, string Destination, byte[] Baseline)> Copies { get; init; }
    public string Summary => $"Copy {Copies.Count} family files to slot {Family:D3}; repoint only {School.Team.Name}'s {(Uniform ? "uniform" : "logo")} assignment. Other schools retain the original family.";

    public static SchoolAssetFork Plan(EditSession session, AssetNode source, TeamProfile school)
    {
        if (MvpModExporter.SessionProblem(session) is { } problem) throw new InvalidOperationException(problem);
        if (session.SaveBlocker(source) is { } blocked) throw new InvalidOperationException(blocked);
        var link = school.Assets.FirstOrDefault(a => a.Asset.Key == source.Key && a.Status == "Confirmed");
        bool uniform = link?.Category.StartsWith("Uniforms/") == true;
        if (!uniform && link?.Category != "Logos") throw new InvalidOperationException("A writable school assignment is not decoded for this asset. Shared models, audio and stadium definitions cannot yet be isolated safely.");
        var pattern = uniform ? "^(?:[uf])?([0-9]{3})[a-z]?\\.[sx](?:sh|fn)$" : "^l([0-9]{3}).*\\.[sx]sh$";
        var regex = new Regex(pattern, RegexOptions.IgnoreCase);
        int oldFamily = uniform ? school.Team.UniformId : school.Team.LogoId;
        var nodes = session.GameRoot.EnumerateDeep().Where(n => !n.IsFolder && regex.IsMatch(n.Name))
            .Where(n => uniform ? n.Path.Contains("/MODELS.BIG/", StringComparison.OrdinalIgnoreCase) || n.Path.Contains("/UNIFORMS.BIG/", StringComparison.OrdinalIgnoreCase) : n.Path.Contains("/LOGOS.BIG/", StringComparison.OrdinalIgnoreCase)).ToList();
        var used = nodes.Select(n => int.Parse(regex.Match(n.Name).Groups[1].Value)).ToHashSet();
        var roster = session.GetBytes(school.Roster).ToArray();
        var teams = TeamRoster.Read(roster, TeamRoster.IsXbox(school.Roster));
        foreach (var team in teams) used.Add(uniform ? team.UniformId : team.LogoId);
        int family = Enumerable.Range(0, 512).FirstOrDefault(id => !used.Contains(id), -1);
        if (family < 0) throw new InvalidOperationException("No unused 9-bit family slot remains.");
        var members = nodes.Where(n => int.Parse(regex.Match(n.Name).Groups[1].Value) == oldFamily).ToList();
        if (!members.Any(n => n.Key == source.Key)) throw new InvalidOperationException("The source is not a decoded family member.");
        var copies = members.Select(n =>
        {
            var rel = ModExporter.RelativePath(session.GameRoot, n).Replace('\\', '/');
            var digits = regex.Match(n.Name).Groups[1];
            var name = n.Name[..digits.Index] + family.ToString("D3") + n.Name[(digits.Index + 3)..];
            return (Source: n, Destination: rel[..(rel.Length - n.Name.Length)] + name, Baseline: session.GetBytes(n).ToArray());
        }).ToList();
        var current = teams.Single(t => t.RosterIndex == school.Team.RosterIndex);
        if ((uniform ? current.UniformId : current.LogoId) != oldFamily) throw new InvalidOperationException("School assignments changed; reopen the school before reviewing.");
        var after = TeamAssignmentEditor.Patch(roster, current.RosterIndex, uniform ? current.LogoId : family,
            uniform ? family : current.UniformId, current.StadiumId, TeamRoster.IsXbox(school.Roster));
        return new() { School = school, Source = source, Family = family, Uniform = uniform, RosterBaseline = roster, RosterAfter = after, Copies = copies };
    }

    public void Apply(EditSession session, byte[] replacement, string description)
    {
        foreach (var copy in Copies)
            if (!session.GetBytes(copy.Source).AsSpan().SequenceEqual(copy.Baseline)) throw new InvalidOperationException("A family file changed; review the replacement again.");
        var files = Copies.ToDictionary(c => c.Destination, c => c.Source.Key == Source.Key ? replacement : c.Baseline, StringComparer.OrdinalIgnoreCase);
        session.CommitFork(School.Roster, RosterBaseline, RosterAfter, files, description + " · " + School.Team.Name + " only");
    }
}
