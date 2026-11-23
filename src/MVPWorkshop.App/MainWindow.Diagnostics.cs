using System.Windows;
using System.Windows.Threading;
using MVPWorkshop.App.Viewers;
using MVPWorkshop.Core.Editing;

namespace MVPWorkshop.App;

public partial class MainWindow
{
    private readonly DispatcherTimer _diagnosticTimer = new() { Interval = TimeSpan.FromMilliseconds(450) };
    private CancellationTokenSource? _diagnosticCancellation;
    private int _diagnosticGeneration;
    internal IReadOnlyList<ModDiagnostic> Diagnostics { get; private set; } = [];
    private IReadOnlyList<ModDiagnostic> _validationDiagnostics = [];
    internal event Action? DiagnosticsChanged;

    private void InitializeDiagnostics() => _diagnosticTimer.Tick += async (_, _) =>
    {
        _diagnosticTimer.Stop();
        if (Editor.Session is not { } session) return;
        var snapshot = session.Snapshot();
        var generation = _diagnosticGeneration;
        var token = _diagnosticCancellation!.Token;
        try
        {
            var dependencies = await GetDependencies();
            var info = DiagnosticModInfo();
            var directory = InstalledModsDirectory();
            var compatibility = ReadPortCompatibility();
            var rows = await Task.Run(() => ModDiagnostics.Check(snapshot, info, dependencies, token, InstalledModCatalog.ReadForGame(snapshot.GameRoot, _settings.GameExePath, token)), token);
            rows.AddRange(CompatibilityDiagnostics(compatibility));
            if (generation == _diagnosticGeneration && !token.IsCancellationRequested) SetDiagnostics(rows);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            if (generation == _diagnosticGeneration) SetDiagnostics([new(DiagnosticSeverity.Error, "", "Diagnostics", "Checks could not complete: " + ex.Message)]);
        }
    };

    private ModInfo DiagnosticModInfo()
    {
        var info = _gameRoot?.DiskPath is { } game && _settings.ModInfos.TryGetValue(game, out var saved) ? saved : new ModInfo { Id = "workshop-mod", Name = "Untitled mod" };
        return new ModInfo { Id = info.Id, Name = info.Name, Version = info.Version, Author = info.Author, Description = info.Description, AffectsSaves = info.AffectsSaves, IconPath = info.IconPath };
    }

    private void ScheduleDiagnostics()
    {
        if (_gameLogSession is not null && !ReferenceEquals(_gameLogSession, Editor.Session)) StopGameLogWatch(clear: true);
        _diagnosticGeneration++;
        _diagnosticCancellation?.Cancel();
        _diagnosticCancellation?.Dispose();
        _diagnosticCancellation = new CancellationTokenSource();
        _diagnosticTimer.Stop();
        DiagnosticsButton.Content = "Diagnostics (…)";
        _diagnosticTimer.Start();
    }

    private void SetDiagnostics(IReadOnlyList<ModDiagnostic> rows)
    {
        _validationDiagnostics = rows;
        PublishDiagnostics();
    }

    private void PublishDiagnostics()
    {
        Diagnostics = _validationDiagnostics.Concat(_gameLogRows).ToList();
        DiagnosticsButton.Content = $"Diagnostics ({Diagnostics.Count})";
        DiagnosticsChanged?.Invoke();
    }

    private void Diagnostics_Click(object sender, RoutedEventArgs e) => ShowDiagnostics();
    private void ShowDiagnostics()
    {
        var existing = _docs.FirstOrDefault(d => d.Content is DiagnosticsView);
        if (existing is not null) Docs.SelectedItem = existing;
        else AddTab(new DocumentTab { Title = "Diagnostics", Content = new DiagnosticsView(this) });
    }

    internal void GoToDiagnostic(ModDiagnostic row)
    {
        if (row.Origin == "Port compatibility") { Docs.SelectedItem = _docs.FirstOrDefault(d => !d.CanClose); return; }
        if (row.Asset == "Mod details" && _gameRoot?.DiskPath is { } game)
        {
            var info = DiagnosticModInfo();
            if (new ModInfoDialog(info) { Owner = this }.ShowDialog() == true) { _settings.ModInfos[game] = info; _settings.Save(); ScheduleDiagnostics(); }
        }
        else if (row.AssetKey.Length > 0 && !row.AssetKey.StartsWith("added:") && _gameRoot?.Resolve(row.AssetKey) is { } node) { Reveal(node); OpenAsset(node); }
        else if (row.AssetKey.StartsWith("added:") && _addedPaths.FirstOrDefault(p => p.Value.Equals(row.AssetKey[6..], StringComparison.OrdinalIgnoreCase)).Key is { } added) OpenAsset(added);
        else ShowChanges_Click(this, new RoutedEventArgs());
    }

    private async Task<bool> ValidateModExport(EditSession snapshot, ModInfo info)
    {
        var generation = _diagnosticGeneration;
        try
        {
            var dependencies = await GetDependencies();
            var directory = InstalledModsDirectory();
            var compatibility = ReadPortCompatibility();
            var rows = await Task.Run(() => ModDiagnostics.Check(snapshot, info, dependencies, installed: InstalledModCatalog.ReadForGame(snapshot.GameRoot, _settings.GameExePath)));
            rows.AddRange(CompatibilityDiagnostics(compatibility));
            if (generation != _diagnosticGeneration) { SetStatus("Edits changed while validating. Export again to include the current project."); return false; }
            SetDiagnostics(rows);
            if (!rows.Any(r => r.Severity == DiagnosticSeverity.Error)) return true;
            ShowDiagnostics();
            MessageBox.Show(this, $"Fix the {rows.Count(r => r.Severity == DiagnosticSeverity.Error)} error(s) in Diagnostics before exporting or testing in game.", "Mod diagnostics", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        catch (OperationCanceledException) { SetStatus("The project changed during validation. Try again."); }
        catch (Exception ex) { SetDiagnostics([new(DiagnosticSeverity.Error, "", "Diagnostics", ex.Message)]); ShowDiagnostics(); }
        return false;
    }

    private string InstalledModsDirectory()
    {
        var executable = string.IsNullOrWhiteSpace(_settings.GameExePath) ? _gameRoot?.DiskPath is { } game ? MvpModExporter.GameExe(game) : null : _settings.GameExePath;
        var parent = executable is not null ? System.IO.Path.GetDirectoryName(executable) : System.IO.Path.GetDirectoryName(_gameRoot?.DiskPath ?? "");
        var portMods = System.IO.Path.Combine(parent ?? "", "mods");
        var gameMods = System.IO.Path.Combine(_gameRoot?.DiskPath ?? "", "mods");
        return !System.IO.Directory.Exists(portMods) && System.IO.Directory.Exists(gameMods) ? gameMods : portMods;
    }

    private static IEnumerable<ModDiagnostic> CompatibilityDiagnostics(MVPWorkshop.Core.Install.PortCompatibilityInfo info) =>
        info.Warnings.Select(warning => new ModDiagnostic(DiagnosticSeverity.Warning, "", "Port compatibility", warning + " Update links are on Start: " + MVPWorkshop.Core.Install.PortCompatibilityInfo.WorkshopUpdates, "Port compatibility"));
}
