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
    internal static string TestModFolder { get; set; } = Path.Combine(Path.GetTempPath(), "MVP-Workshop", "test-mod");
    // ---- asset right-click (tree and search results) ----

    private void Tree_RightButtonDown(object sender, MouseButtonEventArgs e)
    {
        // right-click selects the item under the mouse, so the menu acts on it
        var source = e.OriginalSource as DependencyObject;
        if (sender == SearchResults && source.FindAncestor<ListBoxItem>() is { } result)
        { result.IsSelected = true; result.Focus(); }
        else if (sender == Tree && source.FindAncestor<TreeViewItem>() is { } item)
        { item.IsSelected = true; item.Focus(); }
        else e.Handled = true;
    }

    private void Tree_ContextMenuOpening(object sender, ContextMenuEventArgs e)
    {
        var menu = ((FrameworkElement)sender).ContextMenu!;
        menu.Items.Clear();
        // Context menus belong to their explorer, even when a document has keyboard focus.
        var node = sender == SearchResults ? SearchResults.SelectedItem as AssetNode : SelectedTreeItem?.Node;
        if (node is not { } n) { e.Handled = true; return; }
        void Add(string text, Action act) { var i = new MenuItem { Header = text }; i.Click += (_, _) => act(); menu.Items.Add(i); }
        if (sender == Tree && SelectedTreeItem?.AddedPath is { } added)
        {
            Add("Open", () => OpenAsset(n));
            AddExplorerActions(menu, n);
            Add("Export raw…", () => Exporter.SaveRaw(n));
            Add("Bookmark", () => { var list = GameBookmarks(); if (list.All(b => b.Key != n.Key)) list.Add(new AssetBookmark { Key = n.Key, Name = n.Name }); _settings.Save(); ShowBookmarks(); });
            Add("Remove", () =>
            {
                foreach (var tab in _docs.Where(d => d.Node == n).ToList()) CloseDocument(tab);
                Editor.Session?.RemoveAdded(added);
            });
            return;
        }
        var texture = n.IsLooseTexturePack || n.Parent?.IsLooseTexturePack == true || n.Format is FormatId.SshTexture;
        void Sep() { if (menu.Items.Count > 0 && menu.Items[^1] is not Separator) menu.Items.Add(new Separator()); }
        var s = Editor.Session;
        if (!n.IsFolder || n.IsLooseTexturePack) Add("Open", () => OpenAsset(n));
        AddExplorerActions(menu, n);
        Sep();
        if (texture) Add("Replace image… (PNG, TGA, DDS…)", () => ReplaceImage(n));
        if (texture) Add("Export pack as PNGs…", () => TexturePackExport.ExportPack(n.Parent?.IsLooseTexturePack == true ? n.Parent : n));
        if (!n.IsFolder && s is not null) Add("Import file over this…", () => ImportRaw(n));
        if (s is not null && MvpModExporter.SessionProblem(s) is null) Add("Add new file here…", () => AddFile(n));
        if (!n.IsFolder) Add("Export raw…", () => Exporter.SaveRaw(n));
        if (n.IsContainer) Add("Extract folder…", () => Exporter.ExtractTree(n));
        Sep();
        if (s is not null && s.IsModified(n)) Add("Revert", () => s.Revert(n));
        if (s is not null && (n.IsFolder || n.IsContainer) && s.HasOverrideBelow(n)) Add("Revert everything in here", () => RevertBelow(s, n));
        if (s is not null && (s.IsModified(n) || s.HasOverrideBelow(n))) Add("Show in Changes", () => ShowChanges_Click(this, new RoutedEventArgs()));
        Sep();
        Add("Dependencies", () => ShowDependencies(n));
        Add("Bookmark", () => { var list = GameBookmarks(); if (list.All(b => b.Key != n.Key)) list.Add(new AssetBookmark { Key = n.Key, Name = n.Name }); _settings.Save(); ShowBookmarks(); });
        Add("Copy name", () => Copy(n.Name));
        Add("Copy path", () => Copy(n.Path));
        if (!n.IsFolder) Add("Copy SHA-1", () => Copy(Convert.ToHexString(System.Security.Cryptography.SHA1.HashData(s?.GetBytes(n) ?? n.ReadOriginal()))));
        if (menu.Items.Count > 0 && menu.Items[^1] is Separator) menu.Items.RemoveAt(menu.Items.Count - 1);
        if (menu.Items.Count == 0) e.Handled = true;
    }

    private static void Copy(string text)
    {
        try { Clipboard.SetText(text); } catch (Exception ex) when (ex is System.Runtime.InteropServices.ExternalException or UnauthorizedAccessException) { Log.Warn("The clipboard is unavailable; try again."); }
    }

    /// <summary>Replaces an asset's bytes with a file from disk, as one undoable change.</summary>
    internal async void ImportRaw(AssetNode n, string? path = null)
    {
        var s = Editor.Session!;
        if (s.SaveBlocker(n) is { } why) { MessageBox.Show(this, why, "Import", MessageBoxButton.OK, MessageBoxImage.Information); return; }
        if (path is null)
        {
            var dlg = new OpenFileDialog { Title = $"Import over {n.Name}", FileName = n.Name };
            if (dlg.ShowDialog(this) != true) return;
            path = dlg.FileName;
        }
        try
        {
        var bytes = File.ReadAllBytes(path);
        if (n.FormatInfo.Category is FormatCategory.Audio or FormatCategory.Texture or FormatCategory.Model || n.IsTexturePack || n.Extension is ".ord" or ".orl" or ".o" or ".asf" or ".bnk" or ".wav" or ".csi" or ".off")
        {
            var decision = await PrepareReplacement(n, SelectedTreeItem?.SchoolScope);
            if (decision is null) return;
            decision.Apply(bytes, $"Import {Path.GetFileName(path)}");
        }
        else s.Commit(n, bytes, $"Import {Path.GetFileName(path)}");
        Log.Info($"Imported {path} over {n.Path}");
        }
        catch (Exception ex) { Log.Warn("Import: " + ex.Message); MessageBox.Show(this, ex.Message, "Import unavailable"); }
    }

    /// <summary>Reverts every edited asset inside a folder or archive.</summary>
    private void RevertBelow(EditSession s, AssetNode folder)
    {
        var prefix = folder.Key.Length == 0 ? "" : folder.Key + "/";
        var nodes = s.Overrides.Keys.Where(k => k.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            .Select(k => s.GameRoot.Resolve(k)).OfType<AssetNode>().ToList();
        if (MessageBox.Show(this, $"Revert {nodes.Count} edited asset(s) in {folder.Name}?", "Revert", MessageBoxButton.OKCancel, MessageBoxImage.Question) != MessageBoxResult.OK) return;
        foreach (var node in nodes) s.Revert(node);
        Log.Info($"Reverted {nodes.Count} asset(s) in {folder.Path}");
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
        => AddFile(SelectedNode);

    internal void AddFile(AssetNode? node, string[]? files = null)
    {
        if (Editor.Session is not { } s) { Log.Warn("Open a game folder first."); return; }
        if (MvpModExporter.SessionProblem(s) is { } why) { MessageBox.Show(this, why, "Add File", MessageBoxButton.OK, MessageBoxImage.Information); return; }
        // the folder on disk the new file goes into: the selected folder or archive, or the one holding the selection
        var at = node;
        while (at is not null && !(at.DiskPath is not null && Directory.Exists(at.DiskPath) && !at.IsLooseTexturePack)) at = at.Parent;
        if (at is null) { MessageBox.Show(this, "Select the folder or archive the new file goes into.", "Add File", MessageBoxButton.OK, MessageBoxImage.Information); return; }
        if (files is null)
        {
            var dlg = new OpenFileDialog { Title = $"Add files to {at.Name} in the mod", Multiselect = true };
            if (dlg.ShowDialog(this) != true) return;
            files = dlg.FileNames;
        }
        var folder = Path.GetRelativePath(s.GameRoot.DiskPath!, at.DiskPath!).Replace('\\', '/');
        foreach (var file in files)
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
        ShowChanges_Click(this, new RoutedEventArgs());
    }

    /// <summary>
    /// Packs the edits as a .mvpmod (asking for its details), or for a test run writes them as a mod folder in %TEMP%
    /// and starts the game with --mod, which uses it for that run only.
    /// </summary>
    private async void ExportMvpMod(bool test)
    {
        var s = Editor.Session!;
        if (_exporting) return;
        var running = test && TestGameRunning;
        if (!MvpModExporter.HasChanges(s) && !running)
        {
            MessageBox.Show(this, s.PreviewOnlyCount > 0 ? "Nothing can be packed: the only changes are preview-only (they can't be written into game files)." : "There are no edited assets yet.", "Mod", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        var game = s.GameRoot.DiskPath!;
        var exe = string.IsNullOrWhiteSpace(_settings.GameExePath) ? MvpModExporter.GameExe(game) : _settings.GameExePath;
        if (test && exe is not null && !File.Exists(exe)) { MessageBox.Show(this, "The configured game executable does not exist. Check Tools > Options.", "Test in Game"); return; }
        if (test && exe is null) { MessageBox.Show(this, $"There's no MVP06.exe beside {game}, so the game can't be started from here.", "Test in Game", MessageBoxButton.OK, MessageBoxImage.Information); return; }
        var reloadSupported = test && TestModPublisher.Supported(exe!);
        var launchTarget = DefaultLaunchTarget();
        if (running && _explicitLaunchTarget is not null && _testRunTarget != launchTarget)
        { MessageBox.Show(this, "Close the running game before launching at a different target. To hot-reload this run, select Last edited asset or its original target.", "Test in Game"); return; }
        if (test && launchTarget.Kind != GameLaunchKind.MainMenu && !LaunchTargetsSupported(exe!))
        { MessageBox.Show(this, "This port does not advertise launch-target support. Update the port or select Main menu.", "Test in Game"); return; }
        if (running && (!reloadSupported || _testRunSession != s || !string.Equals(_testRunExecutable, exe, StringComparison.OrdinalIgnoreCase)))
        {
            MessageBox.Show(this, "A game started by Workshop is still running. Close it before starting this project. Reusing the running game requires the same project and a port advertising workshopReload protocol 1.", "Test in Game"); return;
        }

        var info = _settings.ModInfos.TryGetValue(game, out var saved) ? saved : new ModInfo();
        string output;
        if (test)
        {
            info = new ModInfo { Id = "workshop-test", Name = "Workshop test", Version = "0.0.0" };
            output = running ? _testRunDirectory! : Path.Combine(TestModFolder, Guid.NewGuid().ToString("N"));
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
                InitialDirectory = initial is null ? _settings.DefaultModOutputFolder ?? "" : Path.GetDirectoryName(initial),
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
            var snapshot = s.Snapshot();
            if (!await ValidateModExport(snapshot, info)) return;
            var conflicts = Diagnostics.Where(d => d.Origin == "Installed mod").ToList();
            var compatibilityWarnings = Diagnostics.Where(d => d.Origin == "Port compatibility").ToList();
            foreach (var warning in compatibilityWarnings) Log.Warn("Port compatibility: " + warning.Message);
            foreach (var conflict in conflicts) Log.Warn("Mod conflict: " + conflict.Message);
            TestModPublication? publication = null;
            var report = await Task.Run(() =>
            {
                void Progress(string message) => Dispatcher.BeginInvoke(() => SetStatus(message));
                if (!test) return MvpModExporter.Export(snapshot, info, output, asFolder: false, Progress);
                publication = TestModPublisher.Publish(snapshot, info, output, Progress); return publication.Report;
            });
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
            if (conflicts.Count > 0) skipped += "\n\nInstalled mod conflicts / inventory warnings:\n" + string.Join("\n", conflicts.Take(12).Select(d => d.Message)) + (conflicts.Count > 12 ? "\n…see Diagnostics for the full list." : "");
            if (compatibilityWarnings.Count > 0) skipped += "\n\nPort compatibility warnings:\n" + string.Join("\n", compatibilityWarnings.Select(d => d.Message));
            if (test)
            {
                await LaunchOrReloadTest(exe!, s, publication!, running, reloadSupported, launchTarget);
                return;
            }
            SetStatus($"Saved {Path.GetFileName(output)} ({report.Files.Count} file(s))");
            if (CreateExportSummary($"Saved {output} with {report.Files.Count} changed file(s):\n\n{list}{skipped}\n\nAdd it in the launcher's Mods tab (Add mod…) to play with it.", compatibilityWarnings.Count > 0).ShowDialog() == true)
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
        var icon = Field("Icon (PNG)", info.IconPath);
        icon.Margin = new Thickness(0, 4, 150, 0);
        var iconButtons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 4, 0, 0) };
        var browse = new Button { Content = "Browse…", MinWidth = 70, Margin = new Thickness(6, 0, 0, 0), Padding = new Thickness(8, 2, 8, 2) };
        var clear = new Button { Content = "None", MinWidth = 60, Margin = new Thickness(6, 0, 0, 0), Padding = new Thickness(8, 2, 8, 2) };
        browse.Click += (_, _) =>
        {
            var dlg = new Microsoft.Win32.OpenFileDialog { Title = "Mod icon: a square PNG, 64-256 px", Filter = "PNG image (*.png)|*.png" };
            if (dlg.ShowDialog(this) == true) icon.Text = dlg.FileName;
        };
        clear.Click += (_, _) => icon.Text = "";
        iconButtons.Children.Add(browse);
        iconButtons.Children.Add(clear);
        Grid.SetRow(iconButtons, grid.RowDefinitions.Count - 1);
        Grid.SetColumn(iconButtons, 1);
        grid.Children.Add(iconButtons);
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
            var candidate = new ModInfo { Name = name.Text, Id = id.Text.Trim(), Version = version.Text.Trim(), Author = author.Text, Description = description.Text, AffectsSaves = saves.IsChecked == true, IconPath = icon.Text.Trim().Trim('"') };
            if (candidate.Problem() is { } why) { error.Text = why; return; }
            info.Name = candidate.Name;
            info.Id = candidate.Id;
            info.Version = candidate.Version;
            info.Author = candidate.Author;
            info.Description = candidate.Description;
            info.AffectsSaves = candidate.AffectsSaves;
            info.IconPath = candidate.IconPath;
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
