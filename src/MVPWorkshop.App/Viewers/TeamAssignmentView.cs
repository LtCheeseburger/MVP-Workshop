using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using MVPWorkshop.Core.Editing;
using MVPWorkshop.Core.Formats;
using MVPWorkshop.Core.Vfs;

namespace MVPWorkshop.App.Viewers;

/// <summary>Selection is a draft until the resolved preview is applied to the project.</summary>
public sealed class TeamAssignmentView : Grid
{
    private sealed record Choice(int Id, string Label) { public override string ToString() => Label; }

    public TeamAssignmentView(TeamProfile profile)
    {
        var nodes = MainWindow.Instance!.GameRoot!.EnumerateDeep().ToList();
        var current = TeamRoster.Read(profile.Roster.ReadAll(), TeamRoster.IsXbox(profile.Roster)).Single(t => t.RosterIndex == profile.Team.RosterIndex);
        var source = profile.Roster.ReadAll();
        var box = new StackPanel { Margin = new Thickness(12) };
        var status = new TextBlock { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(8) };
        var preview = new ContentControl();
        var apply = B.Button("Apply previewed assignments to project", null, (_, _) => { }, "Primary");
        apply.IsEnabled = false;
        apply.Tag = "ApplyTeamAssignments";
        byte[]? draft = null;
        var families = TeamAssetCatalog.Build(nodes);
        IEnumerable<Choice> AssetFamilies(string pattern, string container, bool uniforms)
        {
            var regex = new Regex(pattern, RegexOptions.IgnoreCase);
            foreach (var group in nodes.Where(n => n.Path.Contains(container, StringComparison.OrdinalIgnoreCase) && regex.IsMatch(n.Name))
                         .GroupBy(n => int.Parse(regex.Match(n.Name).Groups[1].Value)))
            {
                if (group.Key > 511) continue;
                var owner = families.FirstOrDefault(p => (uniforms ? p.Team.UniformId : p.Team.LogoId) == group.Key);
                yield return new Choice(group.Key, $"{group.Key:D3} · {owner?.Team.Name ?? group.First().Name} ({group.Count()} assets)");
            }
        }
        ComboBox Pick(string title, IEnumerable<Choice> choices, int id)
        {
            box.Children.Add(B.Text(title, "Muted", 12));
            var list = choices.DistinctBy(c => c.Id).OrderBy(c => c.Id).ToList();
            if (list.All(c => c.Id != id)) list.Add(new(id, $"{id:D3} · current unresolved assignment (retain only)"));
            var pick = new ComboBox { ItemsSource = list, SelectedItem = list.Single(c => c.Id == id), Margin = new Thickness(4), MinWidth = 280 };
            box.Children.Add(pick);
            return pick;
        }
        // Compatibility is based on resolved native uniform sheets / logo assets, not an arbitrary numeric input.
        var uniform = Pick($"Uniform family: current {current.UniformId:D3} · roster @0x{current.Offset + 164:X}, {current.UniformBits}",
            AssetFamilies(@"^u(\d{3})[a-z]\.ssh$", "/MODELS.BIG/", true), current.UniformId);
        var logo = Pick($"Logo family: current {current.LogoId:D3} · roster @0x{current.Offset + 164:X}, {current.LogoBits}",
            AssetFamilies(@"^l(\d{3})\.ssh$", "/LOGOS.BIG/", false), current.LogoId);
        StadiumAssignments? map = null;
        string? mapError = null;
        try { map = StadiumAssignments.Read(GameExecutable.Find(nodes)!.ReadAll()); }
        catch (Exception ex) { mapError = ex.Message; }
        var stadium = Pick($"Roster stadium: current {current.StadiumId} · roster @0x{current.Offset + 168:X}, bits 4–9",
            map?.Definitions.Where(d => Enumerable.Range(0, 3).All(t => map.Resolve(d.Index, t) is not null))
                .Select(d => new Choice(d.Index, $"Enum {d.Index} · {map.Resolve(d.Index)?.Code} · registry @0x{d.FileOffset:X}")) ?? [], current.StadiumId);
        box.Children.Add(B.Text("Authentic venue ownership is a separate executable registry and is not edited here.\nReview the resolved assets and previews below, then apply. Save Project / Export Mod writes separate output files.", "Muted", 12));
        if (mapError is not null) box.Children.Add(B.Text("Stadium table unavailable: " + mapError, "Muted"));
        void Review()
        {
            draft = null; apply.IsEnabled = false;
            try
            {
                if (map is null) throw new InvalidOperationException("A decoded executable stadium table is required.");
                draft = TeamAssignmentEditor.Patch(source, current.RosterIndex, ((Choice)logo.SelectedItem).Id, ((Choice)uniform.SelectedItem).Id, ((Choice)stadium.SelectedItem).Id, TeamRoster.IsXbox(profile.Roster));
                var proposed = TeamAssetCatalog.Build(nodes, draft).Single(p => p.Team.RosterIndex == current.RosterIndex);
                preview.Content = new TeamView(proposed, assignmentPreview: true);
                status.Text = $"Current: logo {current.LogoId}, uniform {current.UniformId}, stadium {current.StadiumId}. Proposed: logo {proposed.Team.LogoId}, uniform {proposed.Team.UniformId}, stadium {proposed.Team.StadiumId}.\n" + proposed.StadiumEvidence + "\n" + (Editor.Session?.SaveBlocker(profile.Roster) ?? "Ready to apply to the project; unknown bytes and unrelated packed bits verified unchanged.");
                apply.IsEnabled = !source.AsSpan().SequenceEqual(draft) && Editor.Session is { } s && s.SaveBlocker(profile.Roster) is null;
            }
            catch (Exception ex) { status.Text = ex.Message; }
        }
        foreach (var pick in new[] { uniform, logo, stadium }) pick.SelectionChanged += (_, _) => Review();
        apply.Click += (_, _) =>
        {
            try
            {
                var session = Editor.Session ?? throw new InvalidOperationException("No project session.");
                if (!session.GetBytes(profile.Roster).AsSpan().SequenceEqual(source)) throw new InvalidOperationException("Roster changed since this preview. Reopen the team editor to review the latest assignments.");
                if (draft is null) return;
                session.Commit(profile.Roster, draft, $"{current.Name}: logo {((Choice)logo.SelectedItem).Id}, uniform {((Choice)uniform.SelectedItem).Id}, roster stadium {((Choice)stadium.SelectedItem).Id}");
                apply.IsEnabled = false;
            }
            catch (Exception ex) { status.Text = ex.Message; }
        };
        box.Children.Add(apply); box.Children.Add(status);
        preview.MinHeight = 440;
        Children.Add(new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Content = B.Rows((box, B.Auto), (preview, B.Px(440))) });
        Review();
    }
}



