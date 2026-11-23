using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using MVPWorkshop.App.Viewers;
using MVPWorkshop.Core.Editing;
using Microsoft.Win32;

namespace MVPWorkshop.App;

// The MVP 27 overhaul beside MVP06.exe, layered over the opened game as a read-only base so its teams, rosters,
// uniforms, stadiums and UI appear in the normal categories; edits stay in the project, and Build Overhaul writes a
// new package. This never launches the game, installs the mod or changes launcher enablement.
public partial class MainWindow
{
    private string? _overhaulNotice;
    /// <summary>Self-tests and scripted runs report overhaul failures in the log/status bar instead of a dialog.</summary>
    internal bool QuietOverhaulErrors = Environment.GetEnvironmentVariable("MVPW_QUIET_OVERHAUL") == "1";

    /// <summary>After Open Game: find, validate and layer the adjacent overhaul (or <paramref name="chosen"/>).</summary>
    private async Task AttachOverhaul(EditSession session, string? chosen = null)
    {
        _overhaulNotice = null;
        if (session.GameRoot.DiskPath is not { } game || File.Exists(game) || MvpModExporter.SessionProblem(session) is not null) return;
        var path = chosen ?? OverhaulPackage.FindAdjacent(game, _settings.GameExePath);
        while (true)
        {
            if (path is null)
            {
                _overhaulNotice = $"No {OverhaulPackage.FileName} beside MVP06.exe; opened the base game. Use Tools → Locate overhaul… to choose it.";
                Log.Info(_overhaulNotice);
                return;
            }
            if (OverhaulPackage.Loaded(session, path) is { } already)
            {
                Log.Info($"The overhaul is already in the base layer ({already.Source}); not loaded twice.");
                _overhaulNotice = $"{already.Id} {already.Version} is already loaded";
                return;
            }
            try
            {
                SetStatus($"Loading {Path.GetFileName(path)}…");
                var root = session.GameRoot;
                var sources = (session.BaseLayer?.References.Select(r => r.Source) ?? []).Append(path).ToList();
                var layer = await Task.Run(() => { OverhaulPackage.Validate(path, root); return BaseModLayer.Build(root, sources); });
                if (Editor.Session != session) return;
                session.SetBaseLayer(layer);
                foreach (var w in layer.Warnings) Log.Warn(w);
                var reference = layer.References.Last();
                Log.Info($"Overhaul layered for editing: {OverhaulLabel(session)} (id {reference.Id}) from {reference.Source}");
                return;
            }
            catch (Exception ex) when (ex is InvalidDataException or IOException or UnauthorizedAccessException or InvalidOperationException)
            {
                Log.Error($"Overhaul {path}: {ex.Message}");
                if (QuietOverhaulErrors || Editor.Session != session) { _overhaulNotice = "Overhaul not loaded: " + ex.Message; return; }
                var answer = MessageBox.Show(this,
                    $"The overhaul package can't be used:\n{path}\n\n{ex.Message}\n\nYes: choose another .mvpmod\nNo: keep editing the base game",
                    "MVP 27 Overhaul", MessageBoxButton.YesNo, MessageBoxImage.Warning);
                if (answer != MessageBoxResult.Yes) { _overhaulNotice = "Editing the base game without the overhaul."; return; }
                var dlg = new OpenFileDialog { Title = "Choose the MVP 27 overhaul package", Filter = "MVP mod (*.mvpmod)|*.mvpmod", InitialDirectory = Path.GetDirectoryName(path) ?? "" };
                if (dlg.ShowDialog(this) != true) { _overhaulNotice = "Editing the base game without the overhaul."; return; }
                path = dlg.FileName;
            }
        }
    }

    private static string OverhaulLabel(EditSession? session) =>
        session is not null && OverhaulPackage.Loaded(session) is { } r && session.BaseLayer is { } layer
            ? $"{OverhaulName(r.Source)} {r.Version}" : "";

    private static readonly Dictionary<string, string> _overhaulNames = new(StringComparer.OrdinalIgnoreCase);
    private static string OverhaulName(string source)
    {
        if (_overhaulNames.TryGetValue(source, out var n)) return n;
        try { n = MvpModImporter.ReadInfo(source).Name; } catch { n = OverhaulPackage.Id; }
        return _overhaulNames[source] = n;
    }

    private void ShowOverhaulState()
    {
        var s = Editor.Session;
        if (s is not null && OverhaulPackage.Loaded(s) is { } r)
        {
            if (!GameLabel.Text.Contains("  ·  overhaul ", StringComparison.Ordinal)) GameLabel.Text += $"  ·  overhaul {OverhaulLabel(s)} [{r.Id}] from {r.Source}";
            SetStatus($"Loaded with {OverhaulLabel(s)} ({r.Id}) for editing");
        }
        else if (_overhaulNotice is not null) SetStatus(_overhaulNotice);
    }

    private async void LocateOverhaul_Click(object sender, RoutedEventArgs e)
    {
        if (Editor.Session is not { } session || MvpModExporter.SessionProblem(session) is not null) { MessageBox.Show(this, "Open the port's loose game installation first.", "Locate overhaul"); return; }
        var game = session.GameRoot.DiskPath!;
        var dlg = new OpenFileDialog { Title = "Choose the MVP 27 overhaul package to edit with this game", Filter = "MVP mod (*.mvpmod)|*.mvpmod", InitialDirectory = Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(game)) ?? "" };
        if (dlg.ShowDialog(this) != true) return;
        if (OverhaulPackage.Loaded(session, dlg.FileName) is { } already)
        {
            var same = already.Source.Equals(Path.GetFullPath(dlg.FileName), StringComparison.OrdinalIgnoreCase);
            Log.Info(same ? $"{dlg.FileName} is already loaded; not loaded twice." : $"An overhaul ({already.Source}) is already loaded; choose Tools → Installed mods as base… to change the base layer.");
            SetStatus(same ? "That overhaul is already loaded" : "An overhaul is already loaded");
            return;
        }
        ResetBaseViews();
        await AttachOverhaul(session, dlg.FileName);
        session.SetBaseLayer(session.BaseLayer, markDirty: true);
        if (_docs.FirstOrDefault(d => !d.CanClose) is { } start) start.Content = StartPage.Build(this, _settings);
        await UpdateBrowserAsync();
        ShowOverhaulState();
    }

    /// <summary>Rebuild the overhaul package with the project's edits, to a new file unless replacing is confirmed.</summary>
    private async void BuildOverhaul_Click(object sender, RoutedEventArgs e)
    {
        if (_exporting || !CheckPlayerDrafts()) return;
        if (Editor.Session is not { } s || OverhaulPackage.Loaded(s) is not { } reference) { MessageBox.Show(this, "No overhaul is loaded. Open the game with MVP27-Overhaul.mvpmod beside MVP06.exe, or use Tools → Locate overhaul….", "Build overhaul"); return; }
        if (!MvpModExporter.HasChanges(s)) { MessageBox.Show(this, "There are no edited or added files yet.", "Build overhaul"); return; }
        var info = MvpModImporter.ReadInfo(reference.Source);
        var edit = new ManifestEdit { Name = info.Name, Version = info.Version, Author = info.Author, Description = info.Description, AffectsSaves = info.AffectsSaves };
        if (new OverhaulManifestDialog(reference, edit) { Owner = this }.ShowDialog() != true) return;
        var outDir = ModExporter.DefaultOutputFolder(s.GameRoot.DiskPath!);
        var dlg = new SaveFileDialog { Title = "Save the rebuilt overhaul (a new file; the loaded package stays unchanged)", Filter = "MVP mod (*.mvpmod)|*.mvpmod", FileName = OverhaulPackage.FileName, InitialDirectory = Directory.Exists(outDir) ? outDir : Path.GetDirectoryName(reference.Source) ?? "" };
        if (dlg.ShowDialog(this) != true) return;
        var output = Path.GetFullPath(dlg.FileName);
        var replace = output.Equals(reference.Source, StringComparison.OrdinalIgnoreCase);
        if (replace && MessageBox.Show(this, $"Replace the loaded overhaul?\n{output}\n\nThe previous package is kept as {Path.GetFileName(output)}.bak.", "Build overhaul", MessageBoxButton.OKCancel, MessageBoxImage.Warning) != MessageBoxResult.OK) return;
        _exporting = true; Mouse.OverrideCursor = Cursors.AppStarting;
        try
        {
            var report = await Task.Run(() => OverhaulPackage.Rebuild(s, reference.Source, output, edit, replace, m => Dispatcher.BeginInvoke(() => SetStatus(m))));
            foreach (var p in report.Problems) Log.Error("Build overhaul: " + p);
            if (!report.Ok) { SetStatus("The overhaul wasn't written, see the log"); MessageBox.Show(this, string.Join("\n", report.Problems.Take(10)), "Build overhaul", MessageBoxButton.OK, MessageBoxImage.Warning); return; }
            foreach (var (path, size) in report.Changed) Log.Info($"Build overhaul: files/{path} ({size:N0} bytes)");
            foreach (var path in report.Removed) Log.Info($"Build overhaul: dropped files/{path} (texture layout replaced)");
            Log.Info($"Build overhaul: wrote {output} — {report.Entries:N0} entries, mod.json {(report.ManifestChanged ? "updated" : "unchanged")}");
            SetStatus($"Built {Path.GetFileName(output)}: {report.Changed.Count} changed file(s)");
            if (MessageBox.Show(this, $"Built {output}\n\n{report.Changed.Count} changed file(s), {report.Entries:N0} entries; mod.json {(report.ManifestChanged ? "updated" : "kept byte-for-byte")}.\n{(replace ? "The previous package was kept as .bak." : "The loaded package was not modified.")}\n\nOpen the folder?", "Build overhaul", MessageBoxButton.YesNo, MessageBoxImage.Information) == MessageBoxResult.Yes)
                Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{output}\""));
        }
        catch (Exception ex) { Log.Error("Build overhaul failed: " + ex.Message); MessageBox.Show(this, ex.Message, "Build overhaul", MessageBoxButton.OK, MessageBoxImage.Error); }
        finally { _exporting = false; Mouse.OverrideCursor = null; }
    }
}

/// <summary>mod.json fields for the rebuilt overhaul; the id and every other field stay as the package has them.</summary>
sealed class OverhaulManifestDialog : Window
{
    public OverhaulManifestDialog(BaseModReference reference, ManifestEdit edit)
    {
        Title = "Build overhaul"; Width = 560; SizeToContent = SizeToContent.Height; ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterOwner; Background = Ui.Res("PanelBg"); Foreground = Ui.Res("Text");
        var panel = new StackPanel { Margin = new Thickness(14) };
        TextBox Field(string label, string value) { panel.Children.Add(new TextBlock { Text = label, Foreground = Ui.Res("Text"), Margin = new Thickness(0, 6, 0, 2) }); var t = new TextBox { Text = value }; panel.Children.Add(t); return t; }
        panel.Children.Add(new TextBlock { Text = $"Id {reference.Id} (kept) · source {reference.Source}", Foreground = Ui.Res("Text"), TextWrapping = TextWrapping.Wrap });
        var name = Field("Name", edit.Name ?? "");
        var version = Field("Version (change it when shipped data changes; save stamps compare it)", edit.Version ?? "");
        var author = Field("Author", edit.Author ?? "");
        var description = Field("Description", edit.Description ?? "");
        var saves = new CheckBox { Content = "Changes game data that saves depend on (affectsSaves)", IsChecked = edit.AffectsSaves, Margin = new Thickness(0, 10, 0, 0), Foreground = Ui.Res("Text") };
        panel.Children.Add(saves);
        var error = new TextBlock { Foreground = System.Windows.Media.Brushes.IndianRed, TextWrapping = TextWrapping.Wrap }; panel.Children.Add(error);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 12, 0, 0) };
        var ok = new Button { Content = "Build…", IsDefault = true, MinWidth = 90, Margin = new Thickness(0, 0, 8, 0), Padding = new Thickness(10, 4, 10, 4) };
        var cancel = new Button { Content = "Cancel", IsCancel = true, MinWidth = 80, Padding = new Thickness(10, 4, 10, 4) };
        ok.Click += (_, _) =>
        {
            var probe = new ModInfo { Id = reference.Id, Name = name.Text, Version = version.Text.Trim() };
            if (probe.Problem() is { } why) { error.Text = why; return; }
            edit.Name = name.Text; edit.Version = version.Text.Trim(); edit.Author = author.Text; edit.Description = description.Text; edit.AffectsSaves = saves.IsChecked == true;
            DialogResult = true;
        };
        buttons.Children.Add(ok); buttons.Children.Add(cancel); panel.Children.Add(buttons);
        Content = panel;
    }
}
