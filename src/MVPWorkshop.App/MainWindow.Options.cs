using System.IO;
using System.Windows;
using System.Windows.Controls;
using MVPWorkshop.App.Viewers;
using Microsoft.Win32;

namespace MVPWorkshop.App;

public partial class MainWindow
{
    private void Options_Click(object sender, RoutedEventArgs e) => CreateOptionsWindow().ShowDialog();

    internal Window CreateOptionsWindow()
    {
        var panel = new StackPanel { Margin = new Thickness(20) };
        var dialog = new Window { Title = "Options", Owner = this, Content = panel, Width = 540,
            SizeToContent = SizeToContent.Height, ResizeMode = ResizeMode.NoResize,
            WindowStartupLocation = WindowStartupLocation.CenterOwner, Background = Ui.Res("WindowBg"), Foreground = Ui.Res("Text") };
        TextBox Field(string label, string value)
        {
            panel.Children.Add(new TextBlock { Text = label, Margin = new Thickness(0, 8, 0, 4) });
            var box = new TextBox { Text = value }; panel.Children.Add(box); return box;
        }
        var exe = Field("Game executable (blank uses automatic detection)", _settings.GameExePath ?? "");
        var browseExe = new Button { Content = "Choose executable…", HorizontalAlignment = HorizontalAlignment.Right };
        browseExe.Click += (_, _) => { var picker = new OpenFileDialog { Filter = "Executable|*.exe" }; if (picker.ShowDialog(dialog) == true) exe.Text = picker.FileName; };
        panel.Children.Add(browseExe);
        var interval = Field("Autosave interval in minutes (1–60)", _settings.AutosaveMinutes.ToString());
        var count = Field("Autosave backup count (1–20)", _settings.AutosaveCount.ToString());
        var output = Field("Default mod output folder", _settings.DefaultModOutputFolder ?? "");
        var browseOutput = new Button { Content = "Choose output folder…", HorizontalAlignment = HorizontalAlignment.Right };
        browseOutput.Click += (_, _) => { var picker = new OpenFolderDialog(); if (picker.ShowDialog(dialog) == true) output.Text = picker.FolderName; };
        panel.Children.Add(browseOutput);
        panel.Children.Add(new TextBlock { Text = "Log level", Margin = new Thickness(0, 8, 0, 4) });
        var level = new ComboBox { ItemsSource = new[] { "INFO", "WARN", "ERROR" }, SelectedItem = _settings.LogLevel };
        panel.Children.Add(level);
        var reset = new Button { Content = "Reset layout", Margin = new Thickness(0, 12, 0, 0) };
        reset.Click += (_, _) => ResetLayout(); panel.Children.Add(reset);
        var problem = new TextBlock { Foreground = Ui.Res("Bad"), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 10, 0, 0) };
        panel.Children.Add(problem);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 12, 0, 0) };
        var save = new Button { Content = "Save", IsDefault = true, MinWidth = 80 };
        var cancel = new Button { Content = "Cancel", IsCancel = true, MinWidth = 80 };
        buttons.Children.Add(save); buttons.Children.Add(cancel); panel.Children.Add(buttons);
        save.Click += (_, _) =>
        {
            if (!int.TryParse(interval.Text, out var minutes) || minutes is < 1 or > 60 || !int.TryParse(count.Text, out var backups) || backups is < 1 or > 20)
            { problem.Text = "Enter an interval from 1 to 60 and a backup count from 1 to 20."; return; }
            if (!string.IsNullOrWhiteSpace(exe.Text) && !File.Exists(exe.Text.Trim())) { problem.Text = "Choose an existing game executable."; return; }
            if (!string.IsNullOrWhiteSpace(output.Text) && !Directory.Exists(output.Text.Trim())) { problem.Text = "Choose an existing output folder."; return; }
            if (_gameRoot?.DiskPath is { } game && !string.IsNullOrWhiteSpace(output.Text))
            {
                var destination = Path.GetFullPath(output.Text.Trim()).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
                var source = Path.GetFullPath(game).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
                if (destination.StartsWith(source, StringComparison.OrdinalIgnoreCase)) { problem.Text = "Choose an output folder outside the game installation."; return; }
            }
            _settings.GameExePath = exe.Text.Trim(); _settings.DefaultModOutputFolder = output.Text.Trim();
            _settings.AutosaveMinutes = minutes; _settings.AutosaveCount = backups; _settings.LogLevel = level.SelectedItem as string ?? "INFO";
            Log.MinimumLevel = _settings.LogLevel;
            _autosaveTimer.Interval = TimeSpan.FromMinutes(minutes);
            _settings.Save(); dialog.DialogResult = true;
        };
        return dialog;
    }

    private void ResetLayout()
    {
        Width = 1560; Height = 920; WindowState = WindowState.Normal;
        LogToggle.IsChecked = true; PropsToggle.IsChecked = true;
        LogToggle_Click(this, new RoutedEventArgs()); PropsToggle_Click(this, new RoutedEventArgs());
        // Explorer and document column sizes are stored directly on their shared workspace grid.
        if (Tree.Parent is Grid explorer && explorer.Parent is Border border && border.Parent is Grid workspace)
        { workspace.ColumnDefinitions[0].Width = new GridLength(320); workspace.ColumnDefinitions[2].Width = new GridLength(1, GridUnitType.Star); }
    }
}
