using System.Windows;
using MVPWorkshop.App.Viewers;
using MVPWorkshop.Core.Vfs;

namespace MVPWorkshop.App;

public partial class MainWindow
{
    private CancellationTokenSource? _dependencyCancellation;
    private Task<DependencyIndex>? _dependencyTask;
    private int _dependencyGeneration;
    public event Action? DependenciesInvalidated;
    public void CancelDependencies() => _dependencyCancellation?.Cancel();
    private void InvalidateDependencies()
    {
        _dependencyGeneration++; _dependencyCancellation?.Cancel(); _dependencyTask = null;
        DependenciesInvalidated?.Invoke();
    }
    public async Task<DependencyIndex> GetDependencies(IProgress<string>? progress = null)
    {
        var root = _gameRoot ?? throw new InvalidOperationException("Open a game folder first.");
        var generation = _dependencyGeneration;
        if (_dependencyTask == null || _dependencyTask.IsCanceled || _dependencyTask.IsFaulted)
        {
            _dependencyCancellation = new CancellationTokenSource();
            _dependencyTask = DependencyIndex.BuildAsync(root, _dependencyCancellation.Token, progress);
        }
        var result = await _dependencyTask;
        if (generation != _dependencyGeneration || root != _gameRoot) throw new OperationCanceledException();
        return result;
    }
    private void Dependencies_Click(object sender, RoutedEventArgs e) => ShowDependencies(SelectedNode);
    public void ShowDependencies(AssetNode? initial)
    {
        if (_gameRoot == null) { Log.Warn("Open a game folder first."); return; }
        AddTab(new DocumentTab { Title = "Dependencies", Content = new DependencyView(this, initial) });
    }
    public sealed record ReplacementDecision(MVPWorkshop.Core.Editing.EditSession Session, AssetNode Node, byte[] Baseline, MVPWorkshop.Core.Editing.SchoolAssetFork? Fork)
    {
        public void Apply(byte[] bytes, string description)
        {
            if (!ReferenceEquals(Editor.Session, Session)) throw new InvalidOperationException("The project changed; review the replacement again.");
            if (!Session.GetBytes(Node).AsSpan().SequenceEqual(Baseline)) throw new InvalidOperationException("The asset changed; review the replacement again.");
            if (Fork is not null) Fork.Apply(Session, bytes, description);
            else Editor.Apply(Node, bytes, description);
        }
    }

    public async Task<bool> PreviewReplacementImpact(AssetNode node) => await PrepareReplacement(node, allowFork: false) is not null;

    public async Task<ReplacementDecision?> PrepareReplacement(AssetNode node, TeamProfile? preferred = null, bool allowFork = true)
    {
        try
        {
            var session = Editor.Session ?? throw new InvalidOperationException("Open a project first.");
            var generation = _dependencyGeneration;
            var baseline = session.GetBytes(node).ToArray();
            var index = await GetDependencies();
            var subject = index.Subjects.FirstOrDefault(s => s.Source.Key == node.Key && s.Id.StartsWith("asset:"));
            var affected = subject is null ? [] : index.Impact(subject.Id);
            var schoolIds = affected.Where(s => s.Id.StartsWith("team:")).Select(s => s.Id).ToHashSet();
            var profiles = MVPWorkshop.Core.Vfs.TeamAssetCatalog.Build(session.GameRoot.EnumerateDeep().ToList());
            var schools = profiles.Where(p => schoolIds.Contains($"team:{p.Roster.Key}:{p.Team.RosterIndex}")).ToList();
            var dialog = new Window { Owner = this, Title = "Replacement impact", Width = 640, SizeToContent = SizeToContent.Height, MaxHeight = 760,
                WindowStartupLocation = WindowStartupLocation.CenterOwner, Background = Ui.Res("PanelBg"), Foreground = Ui.Res("Text") };
            var panel = new System.Windows.Controls.StackPanel { Margin = new Thickness(18) };
            panel.Children.Add(B.Text(node.Name, "Text", 20));
            panel.Children.Add(new System.Windows.Controls.TextBlock { Text = $"Used by {schools.Count} confirmed school(s).\n" + string.Join(", ", schools.Select(p => p.Team.Name)) + "\nOther runtime consumers may be unresolved. Inspect Dependencies for the reference evidence.", TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 10, 0, 10) });
            var pick = new System.Windows.Controls.ComboBox { ItemsSource = schools, DisplayMemberPath = "Label", Tag = "ReplacementSchool", Margin = new Thickness(0, 5, 0, 10) };
            pick.SelectedItem = schools.FirstOrDefault(p => p.Team.RosterIndex == preferred?.Team.RosterIndex) ?? schools.FirstOrDefault();
            panel.Children.Add(pick);
            var detail = new System.Windows.Controls.TextBlock { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 5, 0, 12) }; panel.Children.Add(detail);
            MVPWorkshop.Core.Editing.SchoolAssetFork? fork = null;
            var only = B.Button("Replace for school only", null, (_, _) => { dialog.Tag = fork; dialog.DialogResult = true; }, "Primary"); only.Tag = "ReplacementSchoolOnly";
            void Preview()
            {
                fork = null;
                try
                {
                    if (!allowFork) throw new InvalidOperationException("This operation is preview-only; it cannot create a saved school assignment.");
                    if (pick.SelectedItem is not TeamProfile school) throw new InvalidOperationException("No confirmed school assignment is available.");
                    fork = MVPWorkshop.Core.Editing.SchoolAssetFork.Plan(session, node, school);
                    detail.Text = fork.Summary + "\n" + string.Join("\n", fork.Copies.Select(c => c.Destination));
                    only.Content = "Replace for " + school.Team.Name + " only";
                }
                catch (Exception ex) { detail.Text = ex.Message; }
                only.IsEnabled = fork is not null;
            }
            pick.SelectionChanged += (_, _) => Preview(); Preview();
            panel.Children.Add(only);
            var everyone = B.Button(schools.Count > 0 ? $"Replace for all {schools.Count} schools" : "Replace shared asset · school usage unresolved", null, (_, _) => { dialog.Tag = null; dialog.DialogResult = true; }, "Outline"); everyone.Tag = "ReplacementEveryone";
            panel.Children.Add(everyone);
            panel.Children.Add(B.Button("Inspect Dependencies", null, (_, _) => { dialog.DialogResult = false; ShowDependencies(node); }, "Outline"));
            panel.Children.Add(B.Button("Cancel", null, (_, _) => dialog.DialogResult = false, "Outline"));
            dialog.Content = new System.Windows.Controls.ScrollViewer { Content = panel, VerticalScrollBarVisibility = System.Windows.Controls.ScrollBarVisibility.Auto };
            if (dialog.ShowDialog() != true || generation != _dependencyGeneration || !ReferenceEquals(session, Editor.Session)) return null;
            return new(session, node, baseline, dialog.Tag as MVPWorkshop.Core.Editing.SchoolAssetFork);
        }
        catch (OperationCanceledException) { return null; }
        catch (Exception ex) { MessageBox.Show(this, ex.Message, "Impact preview unavailable"); return null; }
    }
}