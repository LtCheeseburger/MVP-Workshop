using System.Windows;
using System.Windows.Controls;
using MVPWorkshop.Core.Vfs;

namespace MVPWorkshop.App.Viewers;

public sealed class DependencyView : UserControl
{
    private readonly MainWindow _owner;
    private readonly ComboBox _subject = new() { Width = 470, IsEditable = true, IsTextSearchEnabled = true, DisplayMemberPath = "Label" };
    private readonly DataGrid _grid = B.Grid();
    private readonly TextBlock _status = B.Text("Build the dependency index to begin.");
    private readonly TextBox _details = new() { IsReadOnly = true, TextWrapping = TextWrapping.Wrap, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
    private int _direction;
    private string _filter = "";
    private int _kind;
    private DependencyIndex? _index;
    private AssetNode? _initial;
    public DependencyView(MainWindow owner, AssetNode? initial)
    {
        _owner = owner; _initial = initial;
        _grid.IsReadOnly = true;
        foreach (var column in new[] { B.Col("Class", "Kind", 155), B.Col("Source", "From.Label", 300), B.Col("Target", "Target", 300), B.Col("Missing", "Missing", 65), B.Col("Evidence", "Evidence", 450), B.Col("Source location", "SourceLocation", 300) }) _grid.Columns.Add(column);
        _subject.SelectionChanged += (_, _) => Refresh();
        _grid.SelectionChanged += (_, _) => _details.Text = _grid.SelectedItem is AssetRelationship r ? $"{r.Kind}\n{r.From.Label} → {r.To?.Label ?? r.Target}\n{r.Evidence}\nSource: {r.From.Source.Path}\n{r.SourceLocation}\n{(r.Missing ? "Reference unresolved in its declared scope." : "")}" : "";
        _grid.MouseDoubleClick += (_, _) => Navigate();
        Content = B.Rows(
            (B.Toolbar(B.Button("Build / refresh", null, async (_, _) => await Build()), B.Button("Cancel", null, (_, _) => _owner.CancelDependencies()), _subject), GridLength.Auto),
            (B.Toolbar(B.Segments("View", ["Depends on", "Used by", "Missing references", "Impact preview"], 0, i => { _direction = i; Refresh(); }), B.SearchBox("Filter relationships", value => { _filter = value; Refresh(); }), B.Button("Follow relationship", null, (_, _) => Navigate()), B.Button("Open source", null, (_, _) => { if (_grid.SelectedItem is AssetRelationship r) _owner.OpenAsset(r.From.Source); }), B.Button("Open target", null, (_, _) => { if (_grid.SelectedItem is AssetRelationship { To: { } target }) _owner.OpenAsset(target.Source); })), GridLength.Auto),
            (B.Toolbar(B.Segments("Evidence", ["All", "Explicit references", "Decoded assignments", "Heuristic candidates"], 0, i => { _kind = i; Refresh(); })), GridLength.Auto),
            (_status, GridLength.Auto), (_grid, new GridLength(1, GridUnitType.Star)), (_details, new GridLength(130)));
        Loaded += OnLoaded;
        Unloaded += (_, _) => _owner.DependenciesInvalidated -= Invalidate;
    }
    private async void OnLoaded(object sender, RoutedEventArgs e) { _owner.DependenciesInvalidated += Invalidate; await Build(); }
    private void Invalidate() { _index = null; _grid.ItemsSource = null; _details.Clear(); _status.Text = "Index invalidated by edits or project change. Build / refresh before using relationships."; }
    private async Task Build()
    {
        try
        {
            _status.Text = "Building…";
            var index = await _owner.GetDependencies(new Progress<string>(s => _status.Text = s));
            _index = index;
            var selected = (_subject.SelectedItem as DependencySubject)?.Id;
            _subject.ItemsSource = index.Subjects;
            _subject.SelectedItem = index.Subjects.FirstOrDefault(s => s.Id == selected) ?? index.Subjects.FirstOrDefault(s => s.Source == _initial) ?? index.Subjects.FirstOrDefault();
            _initial = null; Refresh();
        }
        catch (OperationCanceledException) { _status.Text = "Build cancelled. No partial index published."; }
        catch (Exception ex) { _status.Text = "Index failed: " + ex.Message; }
    }
    private void Refresh()
    {
        if (_index == null || _subject.SelectedItem is not DependencySubject subject) return;
        if (_direction == 3)
        {
            _grid.ItemsSource = null;
            var impact = _index.Impact(subject.Id);
            _details.Text = "Potentially affected via explicit/decoded relationships (transitive):\n" + string.Join("\n", impact.Select(s => s.Label));
            _status.Text = $"{impact.Count} confirmed dependents. Candidates are excluded. Unknown runtime relationships may exist.";
            return;
        }
        var rows = _direction == 0 ? _index.DependsOn(subject.Id) : _direction == 1 ? _index.UsedBy(subject.Id) : _index.Relationships.Where(r => r.Missing);
        _grid.ItemsSource = rows.Where(r => (_kind == 0 || (int)r.Kind == _kind - 1) && $"{r.From.Label} {r.Target} {r.Evidence} {r.Kind}".Contains(_filter, StringComparison.OrdinalIgnoreCase)).ToList();
        _status.Text = $"{_index.Subjects.Count:N0} subjects · {_index.Relationships.Count:N0} relationships · {_index.Problems.Count} parser/load problems. Candidates are unverified.";
        _status.ToolTip = string.Join("\n", _index.Problems);
    }
    private void Navigate()
    {
        if (_grid.SelectedItem is not AssetRelationship row) return;
        var target = _direction == 1 ? row.From : row.To;
        if (target != null) { _subject.SelectedItem = target; Refresh(); }
    }
}
