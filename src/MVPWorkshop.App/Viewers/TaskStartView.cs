using System.Windows;
using System.Windows.Controls;
using MVPWorkshop.Core.Editing;
using MVPWorkshop.Core.Vfs;

namespace MVPWorkshop.App.Viewers;

/// <summary>A task entry point over confirmed school assignments; shared files retain their evidence.</summary>
public sealed class TaskStartView : ScrollViewer
{
    private readonly MainWindow _window;
    private readonly ComboBox _schools = new() { MinWidth = 300, HorizontalAlignment = HorizontalAlignment.Left };
    private readonly StackPanel _recent = new();
    private readonly StackPanel _projects = new();
    private readonly Settings _settings;
    private readonly TextBlock _status = B.Text("Reading schools…", "Muted");
    private readonly List<Button> _cards = [];
    private CancellationTokenSource? _refresh;
    private string? _selectedKey;
    internal IReadOnlyList<TeamProfile> Profiles { get; private set; } = [];
    internal TeamProfile? SelectedSchool { get => _schools.SelectedItem as TeamProfile; set => _schools.SelectedItem = value; }
    private static string Key(TeamProfile profile) => profile.Roster.Key + ":" + profile.Team.RosterIndex;

    public TaskStartView(MainWindow window, Settings settings)
    {
        _window = window;
        _settings = settings;
        VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
        Background = Ui.Res("WindowBg");
        var body = new StackPanel { Margin = new Thickness(28), MaxWidth = 1040, HorizontalAlignment = HorizontalAlignment.Left };
        Content = body;
        body.Children.Add(B.Text("What would you like to edit?", "Text", 28, FontWeights.SemiBold));
        body.Children.Add(B.Text("Choose a school, then open a task. Changes stay in your project.", "Muted", 13));
        body.Children.Add(new PortCompatibilityView(window));
        body.Children.Add(new TextBlock { Text = "SCHOOL", Style = Ui.Style("PanelHeader"), Margin = new Thickness(0, 22, 0, 8) });
        _schools.IsTextSearchEnabled = true;
        TextSearch.SetTextPath(_schools, nameof(TeamProfile.Label));
        var schoolLabel = new FrameworkElementFactory(typeof(TextBlock));
        schoolLabel.SetBinding(TextBlock.TextProperty, new System.Windows.Data.Binding(nameof(TeamProfile.Label)));
        _schools.ItemTemplate = new DataTemplate { VisualTree = schoolLabel };
        _schools.SelectionChanged += (_, _) => { if (SelectedSchool is { } school) _selectedKey = Key(school); UpdateRecent(); };
        body.Children.Add(_schools); body.Children.Add(_status);
        var cards = new System.Windows.Controls.Primitives.UniformGrid { Columns = 3, Margin = new Thickness(0, 20, 0, 0) };
        foreach (var (task, description) in new[]
        {
            ("Uniforms", "Home, away and alternate kits on the player model"), ("Logos", "School logo textures and assignments"),
            ("Stadium", "Assigned venue models, textures and artwork"), ("Roster", "Players, ratings and school assignments"),
            ("Menus/UI", "School menu artwork and logos"), ("Audio", "School audio fields, lookup rows and source library"),
            ("Textures", "Texture packs linked to this school")
        })
        {
            var label = new StackPanel();
            label.Children.Add(B.Text(task, "Text", 17, FontWeights.SemiBold));
            label.Children.Add(new TextBlock { Text = description, TextWrapping = TextWrapping.Wrap, Foreground = Ui.Res("Muted"), Margin = new Thickness(0, 8, 0, 0) });
            var button = new Button { Content = label, Tag = "SchoolTask:" + task, Padding = new Thickness(16), Margin = new Thickness(0, 0, 12, 12), MinHeight = 100, HorizontalContentAlignment = HorizontalAlignment.Stretch, IsEnabled = false };
            button.Click += (_, _) => { if (SelectedSchool is { } profile) window.OpenSchoolTask(profile, task); };
            cards.Children.Add(button); _cards.Add(button);
        }
        body.Children.Add(cards);
        body.Children.Add(new TextBlock { Text = "RECENT EDITS FOR THIS SCHOOL", Style = Ui.Style("PanelHeader"), Margin = new Thickness(0, 18, 0, 8) });
        body.Children.Add(_recent);
        body.Children.Add(new TextBlock { Text = "RECENT PROJECTS", Style = Ui.Style("PanelHeader"), Margin = new Thickness(0, 24, 0, 8) });
        body.Children.Add(_projects);
        Loaded += (_, _) => { Editor.Changed -= Refresh; Editor.Changed += Refresh; Refresh(); };
        Unloaded += (_, _) => { Editor.Changed -= Refresh; _refresh?.Cancel(); };
    }

    private async void Refresh()
    {
        _refresh?.Cancel(); _refresh?.Dispose();
        _refresh = new CancellationTokenSource();
        var token = _refresh.Token;
        var root = _window.GameRoot;
        try
        {
            var profiles = await _window.GetSchoolProfiles();
            if (token.IsCancellationRequested || _window.GameRoot != root) return;
            Profiles = profiles;
            _schools.ItemsSource = profiles;
            SelectedSchool = profiles.FirstOrDefault(p => Key(p) == _selectedKey) ?? profiles.FirstOrDefault();
            _status.Text = profiles.Count == 0 ? "No decoded school roster found. Browse assets in the explorer to continue." : $"{profiles.Count} schools · shared-source evidence remains visible in each workspace";
            foreach (var card in _cards) card.IsEnabled = profiles.Count > 0;
            UpdateRecent();
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { if (!token.IsCancellationRequested) _status.Text = "School tasks unavailable: " + ex.Message; }
    }

    private void UpdateRecent()
    {
        _projects.Children.Clear();
        foreach (var path in _settings.RecentProjects)
        {
            var button = B.Button(System.IO.Path.GetFileNameWithoutExtension(path), null, (_, _) => _window.LoadProject(path), "Outline", path);
            button.HorizontalAlignment = HorizontalAlignment.Left; _projects.Children.Add(button);
        }
        _recent.Children.Clear();
        if (SelectedSchool is not { } profile || Editor.Session is not { } session) return;
        var changes = SchoolRecentEdits.ForSchool(profile, session);
        if (changes.Count == 0) _recent.Children.Add(B.Text("No edits for this school in the current project.", "Muted"));
        foreach (var change in changes.Take(8))
        {
            var button = B.Button($"{change.Time:t} · {change.Description}", null, (_, _) => { if (session.GameRoot.Resolve(change.Key) is { } node) _window.OpenSchoolAsset(profile, node); }, "Outline", change.Path);
            button.HorizontalAlignment = HorizontalAlignment.Left;
            _recent.Children.Add(button);
        }
    }
}
