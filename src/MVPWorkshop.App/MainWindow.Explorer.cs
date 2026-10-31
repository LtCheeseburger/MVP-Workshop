using System.IO;
using System.Windows;
using MVPWorkshop.Core.IO;
using MVPWorkshop.Core.Vfs;

namespace MVPWorkshop.App;

public partial class MainWindow
{
    private readonly Dictionary<AssetNode, List<AssetNode>> _addedChildren = [];
    private readonly Dictionary<AssetNode, string> _addedPaths = [];
    private readonly HashSet<AssetNode> _addedAncestors = [];
    private bool Filtering => ModifiedFilter.IsChecked == true || AddedFilter.IsChecked == true;

    private void ExplorerFilter_Click(object sender, RoutedEventArgs e)
    {
        _searchCts?.Cancel();
        SearchPanel.Visibility = Visibility.Collapsed;
        UpdateBrowser();
    }

    // Project additions are a presentation overlay. Never insert them into the source VFS.
    private void BuildAddedTree()
    {
        _addedChildren.Clear(); _addedPaths.Clear(); _addedAncestors.Clear();
        if (Editor.Session is not { } session || _gameRoot is not { DiskPath: { } disk } root) return;
        foreach (var (path, bytes) in session.Added)
        {
            var parts = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
            var parent = root;
            var relative = "";
            for (var i = 0; i < parts.Length; i++)
            {
                relative = relative.Length == 0 ? parts[i] : relative + "/" + parts[i];
                var last = i == parts.Length - 1;
                var full = Path.GetFullPath(Path.Combine(disk, relative));
                var existing = parent.Children.FirstOrDefault(n => n.DiskPath is { } p && p.Equals(full, StringComparison.OrdinalIgnoreCase));
                if (existing is not null)
                {
                    if (last) { _addedPaths[existing] = path; for (var p = existing; p is not null; p = p.Parent) _addedAncestors.Add(p); }
                    parent = existing; continue;
                }
                if (!_addedChildren.TryGetValue(parent, out var children)) _addedChildren[parent] = children = [];
                var node = children.FirstOrDefault(n => n.Name.Equals(parts[i], StringComparison.OrdinalIgnoreCase));
                if (node is null)
                {
                    node = new AssetNode(parts[i], parent, !last, last ? bytes.Length : 0,
                        last ? () => new MemoryByteSource(bytes) : null, "Added to mod") { DiskPath = full };

                    children.Add(node);
                }
                if (last) { _addedPaths[node] = path; for (var p = node; p is not null; p = p.Parent) _addedAncestors.Add(p); }
                parent = node;
            }
        }
    }

    private bool Matches(AssetNode node)
    {
        if (!Filtering) return true;
        if (AddedFilter.IsChecked == true && _addedAncestors.Contains(node)) return true;
        return ModifiedFilter.IsChecked == true && Editor.Session is { } s &&
            (s.IsModified(node) || s.HasOverrideBelow(node));
    }

    private TreeItem FileItem(AssetNode node, int depth) => new(node, depth)
    {
        AddedPath = _addedPaths.GetValueOrDefault(node),
        ChildItems = parent => parent.Children.Concat(_addedChildren.GetValueOrDefault(parent) ?? [])
            .Where(Matches).Select(child => FileItem(child, depth + 1))
    };

    private void ShowExplorer(IEnumerable<TreeItem> source)
    {
        var expanded = new HashSet<string>();
        string Identity(TreeItem item, string parent) => parent + "/" + (item.Node?.Key ?? item.Name);
        void Remember(IEnumerable<TreeItem> items, string parent)
        {
            foreach (var item in items)
                if (item.IsExpanded)
                {
                    var key = Identity(item, parent); expanded.Add(key); Remember(item.Children, key);
                }
        }
        void Restore(IEnumerable<TreeItem> items, string parent)
        {
            foreach (var item in items)
            {
                var key = Identity(item, parent);
                if (expanded.Contains(key)) { item.IsExpanded = true; Restore(item.Children, key); }
            }
        }
        Remember(_roots, "");
        BuildAddedTree();
        TreeItem? Project(TreeItem item)
        {
            if (item.Node is { } node) return Matches(node) ? new TreeItem(node, item.Depth) { SchoolScope = item.SchoolScope, ChildItems = FileItem(node, item.Depth).ChildItems, IsExpanded = item.IsExpanded } : null;
            var children = item.Children.Select(Project).OfType<TreeItem>().ToList();
            if (Filtering && children.Count == 0) return null;
            return new TreeItem(item.Name, item.Depth, children)
            { Team = item.Team, SchoolScope = item.SchoolScope, IsExpanded = item.IsExpanded };
        }
        _roots.Clear();
        if (BrowseMode.SelectedIndex == 1)
        {
            if (_gameRoot is { } root && Matches(root))
                _roots.Add(FileItem(root, 0));
        }
        else
        {
            foreach (var item in source.Select(Project).OfType<TreeItem>()) _roots.Add(item);
            if (AddedFilter.IsChecked == true || !Filtering)
            {
                var additions = _addedPaths.Keys.Select(n => new TreeItem(n, n.Depth) { AddedPath = _addedPaths[n] }).ToList();
                if (additions.Count > 0) _roots.Add(new TreeItem("Added files", 0,
                    additions.GroupBy(n => n.Node.Parent!).Select(g => new TreeItem(g.Key.Path, 1, g))));
            }
        }
        foreach (var item in _roots)
        {
            item.RefreshEdited(Editor.Session);
            if (item.Node == _gameRoot) item.IsExpanded = true;
        }
        Restore(_roots, "");
    }
}

