using System.IO;
using System.Text;
using System.Windows;
using MVPWorkshop.App.Viewers;
using MVPWorkshop.Core.Editing;
using MVPWorkshop.Core.Vfs;
using Microsoft.Win32;

namespace MVPWorkshop.App;

public partial class MainWindow
{
    private async Task<List<TeamProfile>> RosterCsvScope(TeamProfile? school)
    {
        if (_gameRoot is null) throw new InvalidOperationException("Open a game first.");
        var game = _gameRoot;
        var generation = _diagnosticGeneration;
        var nodes = await GetAssetIndex();
        var profiles = await Task.Run(() => TeamAssetCatalog.Build(nodes));
        if (_gameRoot != game || generation != _diagnosticGeneration) throw new InvalidOperationException("The game or project changed while reading roster data. Try again.");
        if (school is not null) profiles = profiles.Where(p => p.Roster.Key == school.Roster.Key && p.Team.RosterIndex == school.Team.RosterIndex).ToList();
        if (profiles.Count == 0) throw new InvalidOperationException("No decoded school roster is available for this selection.");
        return profiles;
    }
    private void ExportAllRosters_Click(object sender, RoutedEventArgs e) => ExportRosterCsv(null);
    private void ImportAllRosters_Click(object sender, RoutedEventArgs e) => ImportRosterCsv(null);
    public async void ExportRosterCsv(TeamProfile? school)
    {
        try
        {
            if (!CheckPlayerDrafts()) return;
            var scope = await RosterCsvScope(school);
            var picker = new SaveFileDialog { Title = "Export roster CSV", Filter = "CSV|*.csv", FileName = school is null ? "all-rosters.csv" : new string(school.Team.Abbreviation.Select(c => char.IsAsciiLetterOrDigit(c) ? c : '_').ToArray()) + "-roster.csv" };
            if (picker.ShowDialog(this) != true) return;
            Exporter.CheckOutput(picker.FileName, scope[0].Roster.Root);
            var generation = _diagnosticGeneration;
            var csv = await Task.Run(() => RosterCsv.Export(scope));
            if (generation != _diagnosticGeneration) throw new InvalidOperationException("The project changed during CSV export. Export again.");
            File.WriteAllText(picker.FileName, csv, new UTF8Encoding(true));
            Log.Info($"Exported roster CSV for {scope.Count} school(s): {picker.FileName}. One row per school slot/source DAT.");
        }
        catch (Exception ex) { MessageBox.Show(this, ex.Message, "Roster CSV", MessageBoxButton.OK, MessageBoxImage.Warning); }
    }
    public async void ImportRosterCsv(TeamProfile? school)
    {
        try
        {
            if (!CheckPlayerDrafts()) return;
            var session = Editor.Session ?? throw new InvalidOperationException("Open a game first.");
            var scope = await RosterCsvScope(school);
            var picker = new OpenFileDialog { Title = "Import roster CSV", Filter = "CSV|*.csv" };
            if (picker.ShowDialog(this) != true) return;
            var generation = _diagnosticGeneration;
            var csv = File.ReadAllText(picker.FileName);
            SetStatus("Reviewing roster CSV…");
            var plan = await Task.Run(() => RosterCsv.Plan(csv, scope));
            if (generation != _diagnosticGeneration || Editor.Session != session) throw new InvalidOperationException("The project changed while preparing CSV review. Import again.");
            if (new RosterCsvReview(plan) { Owner = this }.ShowDialog() != true) return;
            if (Editor.Session != session) throw new InvalidOperationException("The project changed during CSV review. Import again.");
            var files = plan.Apply(session);
            SetStatus($"Roster CSV: updated {plan.ChangedRows} player/source rows in {files} files");
            Log.Info($"Roster CSV applied: {plan.Changes.Count} field edits. Unknown DAT bytes and roster identities were retained.");
        }
        catch (Exception ex) { MessageBox.Show(this, ex.Message, "Roster CSV", MessageBoxButton.OK, MessageBoxImage.Warning); }
    }
}
