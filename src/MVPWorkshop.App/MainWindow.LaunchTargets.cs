using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using MVPWorkshop.App.Viewers;
using MVPWorkshop.Core.Editing;
using MVPWorkshop.Core.Vfs;
using MVPWorkshop.Core.Formats;

namespace MVPWorkshop.App;

public partial class MainWindow
{
    private GameLaunchTarget? _explicitLaunchTarget;
    private bool _syncLaunchTarget;
    private IReadOnlyList<TeamProfile> LaunchSchools => (_docs.FirstOrDefault(d => !d.CanClose)?.Content as TaskStartView)?.Profiles ?? [];
    private IReadOnlyList<(int Id, string Name)> LaunchVenues()
    {
        var fallback = LaunchSchools.Where(p => p.StadiumName is { Length: > 0 }).Select(p => (p.Team.StadiumId, p.StadiumName!)).Distinct().ToList();
        try
        {
            if (_assetIndexTask is not { IsCompletedSuccessfully: true } task || GameExecutable.Find(task.Result) is not { } executable) return fallback;
            var names = new Dictionary<int, string>();
            if (task.Result.FirstOrDefault(n => n.Name.Equals("FEENG.LOC", StringComparison.OrdinalIgnoreCase)) is { } loc)
                foreach (var text in LocTable.Read(loc.ReadAll()).Strings) names[text.Id] = text.Text;
            return StadiumAssignments.Read(executable.ReadAll()).Definitions.Select(d => (d.Index, names.GetValueOrDefault(d.FullNameId, names.GetValueOrDefault(d.NameId, "Stadium " + d.Index)))).ToList();
        }
        catch { return fallback; }
    }
    private GameLaunchTarget DefaultLaunchTarget()
    {
        if (_explicitLaunchTarget is not null) return _explicitLaunchTarget;
        var session = Editor.Session;
        if (session is null) return new(GameLaunchKind.MainMenu);
        var latest = LaunchSchools.Select(p => (School: p, Change: SchoolRecentEdits.ForSchool(p, session).FirstOrDefault())).Where(p => p.Change is not null).OrderByDescending(p => p.Change!.Id).FirstOrDefault();
        if (latest.School is null) return new(GameLaunchKind.MainMenu);
        bool stadium = latest.School.Assets.Any(a => a.Category.StartsWith("Stadium") && a.Asset.Key == latest.Change!.Key);
        return stadium && latest.School.StadiumName is { Length: > 0 } venue
            ? new(GameLaunchKind.Stadium, Home: latest.School.Team.Name, Stadium: venue, StadiumId: latest.School.Team.StadiumId)
            : new(GameLaunchKind.Team, latest.School.Team.Name);
    }
    private static bool LaunchTargetsSupported(string executable)
    {
        try
        {
            var manifest = Path.Combine(Path.GetDirectoryName(executable)!, "port-manifest.json");
            if (new FileInfo(manifest).Length > 16 * 1024 * 1024) return false;
            using var json = JsonDocument.Parse(File.ReadAllText(manifest));
            return json.RootElement.TryGetProperty("workshopLaunch", out var value) && value.TryGetInt32(out var version) && version == 1;
        }
        catch { return false; }
    }
    private void LaunchTarget_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_syncLaunchTarget || LaunchTargetPicker is null) return;
        var selected = LaunchTargetPicker.SelectedIndex;
        if (selected == 0) { _explicitLaunchTarget = null; return; }
        if (selected == 1) { _explicitLaunchTarget = new(GameLaunchKind.MainMenu); return; }
        if (!ChooseLaunchTarget((GameLaunchKind)(selected - 1)))
        {
            _syncLaunchTarget = true; LaunchTargetPicker.SelectedIndex = _explicitLaunchTarget is null ? 0 : (int)_explicitLaunchTarget.Kind + 1; _syncLaunchTarget = false;
        }
    }
    private void ConfigureLaunchTarget_Click(object sender, RoutedEventArgs e) => ChooseLaunchTarget(_explicitLaunchTarget?.Kind ?? DefaultLaunchTarget().Kind);
    private bool ChooseLaunchTarget(GameLaunchKind kind)
    {
        if (_exporting) return false;
        if (kind == GameLaunchKind.MainMenu)
        {
            _explicitLaunchTarget = new(GameLaunchKind.MainMenu); _syncLaunchTarget = true;
            LaunchTargetPicker.SelectedIndex = 1; _syncLaunchTarget = false; return true;
        }
        var schools = LaunchSchools;
        if (kind != GameLaunchKind.MainMenu && schools.Count == 0) { SetStatus("Wait for the school's task page to load before choosing a target."); return false; }
        var current = _explicitLaunchTarget ?? DefaultLaunchTarget();
        var dialog = new Window { Owner = this, Title = "Test in Game target", Width = 520, SizeToContent = SizeToContent.Height,
            WindowStartupLocation = WindowStartupLocation.CenterOwner, Background = Ui.Res("PanelBg"), Foreground = Ui.Res("Text") };
        var panel = new StackPanel { Margin = new Thickness(18) };
        ComboBox Field(string title, IEnumerable<string> values, string value)
        {
            panel.Children.Add(B.Text(title, "Muted"));
            var field = new ComboBox { ItemsSource = values.Distinct().OrderBy(v => v).ToList(), Margin = new Thickness(0, 5, 0, 12), MinWidth = 400 };
            field.SelectedItem = value; if (field.SelectedIndex < 0 && field.Items.Count > 0) field.SelectedIndex = 0;
            panel.Children.Add(field); return field;
        }
        var home = Field(kind == GameLaunchKind.Exhibition ? "Home school" : "School", schools.Select(p => p.Team.Name), current.Home);
        var away = Field("Away school", schools.Select(p => p.Team.Name), current.Away);
        if (away.SelectedItem as string == home.SelectedItem as string && away.Items.Count > 1) away.SelectedIndex = (away.SelectedIndex + 1) % away.Items.Count;
        var venues = LaunchVenues();
        var stadium = Field("Stadium", venues.Select(v => v.Name), current.Stadium);
        if (kind != GameLaunchKind.Exhibition) { away.Visibility = Visibility.Collapsed; ((FrameworkElement)panel.Children[2]).Visibility = Visibility.Collapsed; }
        if (kind != GameLaunchKind.Stadium && kind != GameLaunchKind.Exhibition) { stadium.Visibility = Visibility.Collapsed; ((FrameworkElement)panel.Children[4]).Visibility = Visibility.Collapsed; }
        var error = B.Text("", "Bad"); error.TextWrapping = TextWrapping.Wrap; panel.Children.Add(error);
        panel.Children.Add(B.Button("Use target", null, (_, _) =>
        {
            var venueName = kind is GameLaunchKind.Stadium or GameLaunchKind.Exhibition ? stadium.SelectedItem as string ?? "" : "";
            var target = new GameLaunchTarget(kind, home.SelectedItem as string ?? "", kind == GameLaunchKind.Exhibition ? away.SelectedItem as string ?? "" : "", venueName, venueName.Length > 0 ? venues.First(v => v.Name == venueName).Id : null);
            if (target.Problem is { } problem) { error.Text = problem; return; }
            _explicitLaunchTarget = target; dialog.DialogResult = true;
        }, "Primary"));
        dialog.Content = panel;
        if (dialog.ShowDialog() != true) return false;
        _syncLaunchTarget = true; LaunchTargetPicker.SelectedIndex = (int)kind + 1; LaunchTargetPicker.ToolTip = _explicitLaunchTarget!.Label; _syncLaunchTarget = false;
        return true;
    }
}
