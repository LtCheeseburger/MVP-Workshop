using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using MVPWorkshop.App.Viewers;
using MVPWorkshop.Core.Editing;

namespace MVPWorkshop.App;

public partial class MainWindow
{
    private readonly DispatcherTimer _autosaveTimer = new();
    private readonly HashSet<string> _recoveryMarkers = new(StringComparer.OrdinalIgnoreCase);
    internal static string AutosaveRoot { get; set; } = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "MVP-Workshop", "Autosave");

    private void InitializeProjects()
    {
        _autosaveTimer.Interval = TimeSpan.FromMinutes(Math.Clamp(_settings.AutosaveMinutes, 1, 60));
        _autosaveTimer.Tick += (_, _) => AutosaveProject();
        _autosaveTimer.Start();
    }

    internal string? AutosaveProject()
    {
        if (Editor.Session is not { IsDirty: true } session) return null;
        try
        {
            var game = Path.GetFullPath(session.GameRoot.DiskPath!).TrimEnd(Path.DirectorySeparatorChar).ToUpperInvariant();
            var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(game)))[..12];
            var name = Path.GetFileNameWithoutExtension(game);
            var folder = Path.Combine(AutosaveRoot, name + "-" + hash);
            Directory.CreateDirectory(folder);
            var slots = Enumerable.Range(1, Math.Clamp(_settings.AutosaveCount, 1, 20)).Select(i => Path.Combine(folder, $"Project_{i:000}.mvpwproj")).ToList();
            var path = slots.FirstOrDefault(p => !File.Exists(p)) ?? slots.OrderBy(File.GetLastWriteTimeUtc).First();
            // Write a complete candidate before replacing the oldest backup and its sidecar data.
            var staging = Path.Combine(folder, "pending.mvpwproj");
            ProjectFile.Save(session, staging, markSaved: false);
            if (Directory.Exists(ProjectFile.DataFolder(path))) Directory.Delete(ProjectFile.DataFolder(path), recursive: true);
            Directory.Move(ProjectFile.DataFolder(staging), ProjectFile.DataFolder(path));
            File.Move(staging, path, overwrite: true);
            var marker = Path.Combine(folder, "recovery.txt");
            File.WriteAllText(marker, path);
            _recoveryMarkers.Add(marker);
            // Apply a reduced retention setting only after the replacement backup is complete.
            foreach (var expired in Enumerable.Range(slots.Count + 1, 20 - slots.Count).Select(i => Path.Combine(folder, $"Project_{i:000}.mvpwproj")))
            {
                File.Delete(expired);
                if (Directory.Exists(ProjectFile.DataFolder(expired))) Directory.Delete(ProjectFile.DataFolder(expired), recursive: true);
            }
            SetStatus("Autosaved " + Path.GetFileName(path));
            return path;
        }
        catch (Exception ex) { Log.Warn("Autosave failed: " + ex.Message); return null; }
    }

    private void ClearRecovery()
    {
        foreach (var marker in _recoveryMarkers.ToList())
        {
            try { File.Delete(marker); _recoveryMarkers.Remove(marker); }
            catch (Exception ex) { Log.Warn("Could not clear recovery notice: " + ex.Message); }
        }
    }

    internal bool OfferRecovery()
    {
        try
        {
            if (!Directory.Exists(AutosaveRoot)) return false;
            var markers = Directory.GetFiles(AutosaveRoot, "recovery.txt", SearchOption.AllDirectories);
            var newest = markers.Select(m => (Marker: m, Path: File.ReadAllText(m)))
                .Where(x => File.Exists(x.Path) && Path.GetFullPath(x.Path).StartsWith(Path.GetFullPath(AutosaveRoot) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(x => File.GetLastWriteTimeUtc(x.Path)).FirstOrDefault();
            if (newest.Path is null) return false;
            var answer = MessageBox.Show(this, $"The previous session ended with unsaved changes.\n\nRestore the newest autosave from {File.GetLastWriteTime(newest.Path):g}?\n{ProjectFile.PeekGameFolder(newest.Path)}", "Restore autosave", MessageBoxButton.YesNo, MessageBoxImage.Question);
            foreach (var marker in markers) _recoveryMarkers.Add(marker);
            if (answer != MessageBoxResult.Yes) { ClearRecovery(); return false; }
            LoadProject(newest.Path, recovery: true);
            if (Editor.Session is not { IsDirty: true }) return false;
            File.WriteAllText(newest.Marker, newest.Path);
            _recoveryMarkers.Add(newest.Marker);
            return true;
        }
        catch (Exception ex) { Log.Warn("Recovery failed: " + ex.Message); return false; }
    }

    private void RecentProjects_Opened(object sender, RoutedEventArgs e)
    {
        RecentProjectsMenu.Items.Clear();
        foreach (var path in _settings.RecentProjects)
        {
            var item = new MenuItem { Header = Path.GetFileNameWithoutExtension(path).Replace("_", "__"), ToolTip = path };
            item.Click += (_, _) => LoadProject(path);
            RecentProjectsMenu.Items.Add(item);
        }
        if (RecentProjectsMenu.Items.Count == 0) RecentProjectsMenu.Items.Add(new MenuItem { Header = "No recent projects", IsEnabled = false });
    }

    private void RefreshRecentProjects()
    {
        if (_docs.FirstOrDefault(d => !d.CanClose)?.Content is TaskStartView) return;
        if (_docs.FirstOrDefault(d => !d.CanClose) is { } start) start.Content = StartPage.Build(this, _settings);
    }

    private MessageBoxResult SaveChangesPrompt(string action)
    {
        var result = MessageBoxResult.Cancel;
        var panel = new StackPanel { Margin = new Thickness(24) };
        panel.Children.Add(new TextBlock { Text = "Save changes to this project before continuing?", Foreground = Ui.Res("Text"), Margin = new Thickness(0, 0, 0, 20) });
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        var dialog = new Window { Title = action, Owner = this, Content = panel, SizeToContent = SizeToContent.WidthAndHeight, ResizeMode = ResizeMode.NoResize, WindowStartupLocation = WindowStartupLocation.CenterOwner, Background = Ui.Res("WindowBg"), ShowInTaskbar = false };
        foreach (var (label, answer) in new[] { ("Save", MessageBoxResult.Yes), ("Don't save", MessageBoxResult.No), ("Cancel", MessageBoxResult.Cancel) })
        {
            var button = B.Button(label, null, (_, _) => { result = answer; dialog.Close(); });
            button.IsDefault = answer == MessageBoxResult.Yes;
            button.IsCancel = answer == MessageBoxResult.Cancel;
            buttons.Children.Add(button);
        }
        panel.Children.Add(buttons);
        dialog.ShowDialog();
        return result;
    }
}
