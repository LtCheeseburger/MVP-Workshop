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
    public async Task<bool> PreviewReplacementImpact(AssetNode node)
    {
        ShowDependencies(node);
        try
        {
            var generation = _dependencyGeneration;
            var index = await GetDependencies();
            var subject = index.Subjects.FirstOrDefault(s => s.Source == node && s.Id.StartsWith("asset:"));
            if (subject == null) throw new InvalidOperationException("Asset is outside the indexed project.");
            var affected = index.Impact(subject.Id);
            var candidates = index.UsedBy(subject.Id).Count(r => r.Kind == RelationshipKind.HeuristicCandidate);
            var text = $"Replace {node.Path}?\n\n{affected.Count} explicit/decoded dependents may be affected:\n" + string.Join("\n", affected.Take(25).Select(s => s.Label)) +
                (affected.Count > 25 ? "\n…see Impact preview for the complete list." : "") + $"\n\n{candidates} additional unverified candidate relationships. Unknown runtime relationships may exist.\nInspect evidence in the Dependencies tab. Continue with replacement?";
            return MessageBox.Show(this, text, "Replacement impact preview", MessageBoxButton.OKCancel, MessageBoxImage.Information) == MessageBoxResult.OK && generation == _dependencyGeneration;
        }
        catch (OperationCanceledException) { return false; }
        catch (Exception ex) { MessageBox.Show(this, ex.Message, "Impact preview unavailable"); return false; }
    }
}
