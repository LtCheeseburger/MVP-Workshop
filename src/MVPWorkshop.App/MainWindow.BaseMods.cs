using System.Windows;
using System.Windows.Controls;
using MVPWorkshop.App.Viewers;
using MVPWorkshop.Core.Editing;

namespace MVPWorkshop.App;

public partial class MainWindow
{
    private void ResetBaseViews()
    {
        foreach (var document in _docs.Where(d => d.CanClose).ToList()) { DisposeTree(document.Content); _docs.Remove(document); }
        _searchCts?.Cancel(); SearchPanel.Visibility = Visibility.Collapsed; SearchResults.ItemsSource = null;
        _assetIndexTask = null; _searchIndex = null; _categoryRoots = null; _catalogGeneration++;
        _teamTabs.Clear(); _teamRefreshSession = null; _refreshTeamGrid = null;
        _roots.Clear();
    }

    private void ApplyBaseLayer(BaseModLayer? layer)
    {
        ResetBaseViews(); Editor.Session!.SetBaseLayer(layer, markDirty: true);
        if (_docs.FirstOrDefault(d => !d.CanClose) is { } start) start.Content = StartPage.Build(this, _settings);
    }

    private async void BaseMods_Click(object sender, RoutedEventArgs e)
    {
        if (_exporting || Editor.Session is not { } session || _gameRoot is not { } root || !CheckPlayerDrafts()) return;
        var directory = InstalledModsDirectory();
        var catalog = await Task.Run(() => InstalledModCatalog.ReadForGame(root, _settings.GameExePath));
        if (Editor.Session != session) return;
        var window = new Window { Owner = this, Title = "Installed mods as a base", Width = 720, Height = 510,
            WindowStartupLocation = WindowStartupLocation.CenterOwner, Background = Ui.Res("PanelBg"), Foreground = Ui.Res("Text") };
        var panel = new DockPanel { Margin = new Thickness(16) };
        var explanation = B.Text("Select read-only base mods in launcher order; later entries win. Only your own edits are exported.\nSelections are saved with the project. Launcher settings are unchanged.");
        explanation.TextWrapping = TextWrapping.Wrap; explanation.Margin = new Thickness(0, 0, 0, 12); DockPanel.SetDock(explanation, Dock.Top); panel.Children.Add(explanation);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        buttons.Children.Add(B.Button("Cancel", null, (_, _) => window.DialogResult = false));
        buttons.Children.Add(B.Button("Apply base", null, (_, _) => window.DialogResult = true));
        DockPanel.SetDock(buttons, Dock.Bottom); panel.Children.Add(buttons);
        var list = new StackPanel(); var choices = new List<(InstalledMod Mod, CheckBox Check)>();
        foreach (var problem in catalog.Problems) { var text = B.Text(problem, "Muted"); text.TextWrapping = TextWrapping.Wrap; list.Children.Add(text); }
        if (catalog.Mods.Count == 0) { var text = B.Text("No installed mods found in " + directory); text.TextWrapping = TextWrapping.Wrap; list.Children.Add(text); }
        foreach (var mod in catalog.Mods)
        {
            var selected = session.BaseLayer?.References.Any(r => r.Source.Equals(mod.Source, StringComparison.OrdinalIgnoreCase)) ?? mod.Enabled;
            var check = new CheckBox { Content = $"{mod.Order + 1}. {mod.Name} ({mod.Version})" + (mod.Problem is null ? "" : " — " + mod.Problem),
                IsChecked = selected && mod.Problem is null, IsEnabled = mod.Problem is null, Foreground = Ui.Res("Text"), Margin = new Thickness(0, 8, 0, 8), ToolTip = mod.Source };
            choices.Add((mod, check)); list.Children.Add(check);
        }
        panel.Children.Add(new ScrollViewer { Content = list }); window.Content = panel;
        if (window.ShowDialog() != true || Editor.Session != session) return;
        try
        {
            _exporting = true;
            var sources = choices.Where(c => c.Check.IsChecked == true).Select(c => c.Mod.Source).ToList();
            // keep the adjacent overhaul (not an installed mod) unless an installed copy with its id was chosen
            if (OverhaulPackage.Loaded(session) is { } overhaul && !choices.Any(c => c.Check.IsChecked == true && c.Mod.Id == overhaul.Id) && !choices.Any(c => c.Mod.Source.Equals(overhaul.Source, StringComparison.OrdinalIgnoreCase))) sources.Add(overhaul.Source);
            var layer = sources.Count == 0 ? null : await Task.Run(() => BaseModLayer.Build(root, sources));
            if (Editor.Session != session) return;
            ApplyBaseLayer(layer);
            foreach (var warning in layer?.Warnings ?? []) Log.Warn(warning);
            SetStatus($"Base layer: {sources.Count} mod(s). Only project edits are exported.");
        }
        catch (Exception ex) { Log.Error(ex.Message); MessageBox.Show(this, ex.Message, "Base mods", MessageBoxButton.OK, MessageBoxImage.Error); }
        finally { _exporting = false; }
    }
}
