using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using MVPWorkshop.App.Viewers;
using MVPWorkshop.Core.Editing;
using MVPWorkshop.Core.IO;
using MVPWorkshop.Core.Formats;
using MVPWorkshop.Core.Vfs;
using Microsoft.Win32;

namespace MVPWorkshop.App;

// .mvpmod export and test runs for the port's loose install (docs/MVPMOD.md in the port)
public partial class MainWindow
{
    // ---- tree right-click: replace a texture image, add a file here, export raw ----

    private void Tree_RightButtonDown(object sender, MouseButtonEventArgs e)
    {
        // right-click selects the item under the mouse, so the menu acts on it
        if ((e.OriginalSource as DependencyObject).FindAncestor<TreeViewItem>() is { } item) item.IsSelected = true;
    }

    private void Tree_ContextMenuOpening(object sender, ContextMenuEventArgs e)
    {
        var menu = Tree.ContextMenu!;
        menu.Items.Clear();
        if (SelectedNode is not { } n) { e.Handled = true; return; }
        void Add(string text, Action act) { var i = new MenuItem { Header = text }; i.Click += (_, _) => act(); menu.Items.Add(i); }
        var texture = n.IsLooseTexturePack || n.Parent?.IsLooseTexturePack == true || n.Format is FormatId.SshTexture;
        if (texture) Add("Replace image… (PNG, TGA, DDS…)", () => ReplaceImage(n));
        if (!n.IsFolder || n.IsLooseTexturePack) Add("Open", () => OpenAsset(n));
        if (Editor.Session is { } s && MvpModExporter.SessionProblem(s) is null) Add("Add new file here…", () => AddFile_Click(this, new RoutedEventArgs()));
        if (!n.IsFolder) Add("Export raw…", () => Exporter.SaveRaw(n));
        if (menu.Items.Count == 0) e.Handled = true;
    }

    /// <summary>Opens the texture (a DDS file opens its pack with that image selected) and starts Replace image on it.</summary>
    private void ReplaceImage(AssetNode n)
    {
        OpenAsset(n);
        if (Docs.SelectedItem is not DocumentTab { Content: { } content }) return;
        Dispatcher.BeginInvoke(() =>
        {
            if (FindTextureView(content) is { } view) view.ReplaceSelectedImage();
            else Log.Warn($"{n.Name}: open it and use Replace image in the texture viewer.");
        }, System.Windows.Threading.DispatcherPriority.Loaded);
    }

    private static TextureView? FindTextureView(DependencyObject d)
    {
        if (d is TextureView v) return v;
        foreach (var child in LogicalTreeHelper.GetChildren(d).OfType<DependencyObject>())
            if (FindTextureView(child) is { } found) return found;
        return null;
    }

    private void TestMod_Click(object sender, RoutedEventArgs e)
    {
        if (!CheckPlayerDrafts()) return;
        if (Editor.Session is not { } s) { Log.Warn("Open a game folder first."); return; }
        if (MvpModExporter.SessionProblem(s) is { } why) { MessageBox.Show(this, why, "Test in Game", MessageBoxButton.OK, MessageBoxImage.Information); return; }
        ExportMvpMod(test: true);
    }

    /// <summary>
    /// A new file for the mod, in the selected folder, archive or texture's folder of the loose install: a new archive
    /// member, a new texture pack (.xsh, laid out as DDS files) or any other file. Listed under Changes.
    /// </summary>
    private void AddFile_Click(object sender, RoutedEventArgs e)
    {
        if (Editor.Session is not { } s) { Log.Warn("Open a game folder first."); return; }
        if (MvpModExporter.SessionProblem(s) is { } why) { MessageBox.Show(this, why, "Add File", MessageBoxButton.OK, MessageBoxImage.Information); return; }
        // the folder on disk the new file goes into: the selected folder or archive, or the one holding the selection
        var at = SelectedNode;
        while (at is not null && !(at.DiskPath is not null && Directory.Exists(at.DiskPath) && !at.IsLooseTexturePack)) at = at.Parent;
        if (at is null) { MessageBox.Show(this, "Select the folder or archive the new file goes into.", "Add File", MessageBoxButton.OK, MessageBoxImage.Information); return; }
        var dlg = new OpenFileDialog { Title = $"Add files to {at.Name} in the mod", Multiselect = true };
        if (dlg.ShowDialog(this) != true) return;
        var folder = Path.GetRelativePath(s.GameRoot.DiskPath!, at.DiskPath!).Replace('\\', '/');
        foreach (var file in dlg.FileNames)
        {
            var path = (folder == "." ? "" : folder + "/") + Path.GetFileName(file).ToLowerInvariant();
            if (File.Exists(Path.Combine(s.GameRoot.DiskPath!, path)) || Directory.Exists(Path.Combine(s.GameRoot.DiskPath!, path)))
            {
                MessageBox.Show(this, $"{path} already exists in the game. Open it and replace it instead.", "Add File", MessageBoxButton.OK, MessageBoxImage.Information);
                continue;
            }
            s.AddFile(path, File.ReadAllBytes(file));
            Log.Info($"Added {path} to the mod");
        }
        ShowChanges_Click(this, e);
    }

    /// <summary>
    /// Packs the edits as a .mvpmod (asking for its details), or for a test run writes them as a mod folder in %TEMP%
    /// and starts the game with --mod, which uses it for that run only.
    /// </summary>
    private async void ExportMvpMod(bool test)
    {
        var s = Editor.Session!;
        if (_exporting) return;
        if (!MvpModExporter.HasChanges(s))
        {
            MessageBox.Show(this, s.PreviewOnlyCount > 0 ? "Nothing can be packed: the only changes are preview-only (they can't be written into game files)." : "There are no edited assets yet.", "Mod", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        var game = s.GameRoot.DiskPath!;
        var exe = MvpModExporter.GameExe(game);
        if (test && exe is null) { MessageBox.Show(this, $"There's no MVP06.exe beside {game}, so the game can't be started from here.", "Test in Game", MessageBoxButton.OK, MessageBoxImage.Information); return; }

        var info = _settings.ModInfos.TryGetValue(game, out var saved) ? saved : new ModInfo();
        string output;
        if (test)
        {
            info = new ModInfo { Id = "workshop-test", Name = "Workshop test", Version = "0.0.0" };
            output = Path.Combine(Path.GetTempPath(), "MVP-Workshop", "test-mod");
        }
        else
        {
            if (new ModInfoDialog(info) { Owner = this }.ShowDialog() != true) return;
            var initial = _settings.ModPaths.TryGetValue(game, out var last) ? last : null;
            var dlg = new SaveFileDialog
            {
                Title = "Save the mod",
                Filter = "MVP mod (*.mvpmod)|*.mvpmod",
                FileName = initial is null ? info.Id + ".mvpmod" : Path.GetFileName(initial),
                InitialDirectory = initial is null ? "" : Path.GetDirectoryName(initial),
            };
            if (dlg.ShowDialog(this) != true) return;
            output = dlg.FileName;
            _settings.ModInfos[game] = info;
            _settings.ModPaths[game] = output;
            _settings.Save();
        }

        _exporting = true;
        Mouse.OverrideCursor = Cursors.AppStarting;
        try
        {
            var report = await Task.Run(() => MvpModExporter.Export(s, info, output, asFolder: test, msg => Dispatcher.BeginInvoke(() => SetStatus(msg))));
            foreach (var p in report.Problems) Log.Error("Mod: " + p);
            if (!report.Ok)
            {
                SetStatus("The mod wasn't written, see the log");
                MessageBox.Show(this, string.Join("\n", report.Problems.Take(10)), "Mod", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            foreach (var (path, size) in report.Files) Log.Info($"Mod file {path} ({Bin.Size(size)})");
            var list = string.Join("\n", report.Files.Take(12).Select(f => "  " + f.Path)) + (report.Files.Count > 12 ? $"\n  …and {report.Files.Count - 12} more" : "");
            var skipped = report.PreviewOnlySkipped > 0 ? $"\n\n{report.PreviewOnlySkipped} preview-only change(s) aren't in it." : "";
            if (test)
            {
                SetStatus($"Starting the game with {report.Files.Count} changed file(s)");
                Process.Start(new ProcessStartInfo(exe!, $"--mod \"{output}\"") { WorkingDirectory = Path.GetDirectoryName(exe)! });
                return;
            }
            SetStatus($"Saved {Path.GetFileName(output)} ({report.Files.Count} file(s))");
            if (MessageBox.Show(this, $"Saved {output} with {report.Files.Count} changed file(s):\n\n{list}{skipped}\n\nAdd it in the launcher's Mods tab (Add mod…) to play with it. Open the folder?", "Mod", MessageBoxButton.YesNo, MessageBoxImage.Information) == MessageBoxResult.Yes)
                Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{output}\""));
        }
        catch (Exception ex) { Log.Error("Mod failed: " + ex.Message); MessageBox.Show(this, ex.Message, "Mod", MessageBoxButton.OK, MessageBoxImage.Error); }
        finally { _exporting = false; Mouse.OverrideCursor = null; }
    }
}

/// <summary>The mod.json details: name, id, version, author, description, and whether saves depend on it.</summary>
sealed class ModInfoDialog : Window
{
    public ModInfoDialog(ModInfo info)
    {
        Title = "Mod details";
        Width = 520;
        SizeToContent = SizeToContent.Height;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = Ui.Res("WindowBg");
        Foreground = Ui.Res("Text");
        var grid = new Grid { Margin = new Thickness(14) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(110) });
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        TextBox Field(string label, string value, bool multi = false)
        {
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var row = grid.RowDefinitions.Count - 1;
            var l = new TextBlock { Text = label, Foreground = Ui.Res("Text"), VerticalAlignment = multi ? VerticalAlignment.Top : VerticalAlignment.Center, Margin = new Thickness(0, 6, 8, 0) };
            Grid.SetRow(l, row);
            grid.Children.Add(l);
            var t = new TextBox { Text = value, Margin = new Thickness(0, 4, 0, 0), AcceptsReturn = multi, TextWrapping = multi ? TextWrapping.Wrap : TextWrapping.NoWrap, Height = multi ? 70 : double.NaN };
            Grid.SetRow(t, row);
            Grid.SetColumn(t, 1);
            grid.Children.Add(t);
            return t;
        }
        var name = Field("Name", info.Name);
        var id = Field("Id", info.Id);
        var version = Field("Version", info.Version);
        var author = Field("Author", info.Author);
        var description = Field("Description", info.Description, multi: true);
        // the id follows the name until it's typed in
        var idTyped = info.Id != "";
        name.TextChanged += (_, _) => { if (!idTyped) id.Text = ModInfo.IdFrom(name.Text); };
        id.PreviewKeyDown += (_, _) => idTyped = true;

        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        var saves = new CheckBox { Content = new TextBlock { Text = "Changes game data that saves depend on (rosters, database)", Foreground = Ui.Res("Text"), TextWrapping = TextWrapping.Wrap }, IsChecked = info.AffectsSaves, Margin = new Thickness(0, 10, 0, 0) };
        Grid.SetRow(saves, grid.RowDefinitions.Count - 1);
        Grid.SetColumn(saves, 1);
        grid.Children.Add(saves);

        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        var error = new TextBlock { Foreground = System.Windows.Media.Brushes.IndianRed, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 0) };
        Grid.SetRow(error, grid.RowDefinitions.Count - 1);
        Grid.SetColumn(error, 1);
        grid.Children.Add(error);

        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 12, 0, 0) };
        var ok = new Button { Content = "Save mod…", IsDefault = true, MinWidth = 90, Margin = new Thickness(0, 0, 8, 0), Padding = new Thickness(10, 4, 10, 4) };
        var cancel = new Button { Content = "Cancel", IsCancel = true, MinWidth = 80, Padding = new Thickness(10, 4, 10, 4) };
        ok.Click += (_, _) =>
        {
            var candidate = new ModInfo { Name = name.Text, Id = id.Text.Trim(), Version = version.Text.Trim(), Author = author.Text, Description = description.Text, AffectsSaves = saves.IsChecked == true };
            if (candidate.Problem() is { } why) { error.Text = why; return; }
            info.Name = candidate.Name;
            info.Id = candidate.Id;
            info.Version = candidate.Version;
            info.Author = candidate.Author;
            info.Description = candidate.Description;
            info.AffectsSaves = candidate.AffectsSaves;
            DialogResult = true;
        };
        buttons.Children.Add(ok);
        buttons.Children.Add(cancel);
        Grid.SetRow(buttons, grid.RowDefinitions.Count - 1);
        Grid.SetColumnSpan(buttons, 2);
        grid.Children.Add(buttons);
        Content = grid;
    }
}
