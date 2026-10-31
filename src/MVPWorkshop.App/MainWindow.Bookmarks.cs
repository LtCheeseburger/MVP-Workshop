using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using MVPWorkshop.Core.Vfs;

namespace MVPWorkshop.App;

public sealed class AssetBookmark
{
    public string Key { get; set; } = "";
    public string Name { get; set; } = "";
    public string Folder { get; set; } = "Bookmarks";
}

public partial class MainWindow
{
    private string BookmarkGame => _gameRoot?.DiskPath ?? _gameRoot?.Key ?? "";
    private List<AssetBookmark> GameBookmarks()
    {
        if (!_settings.Bookmarks.TryGetValue(BookmarkGame, out var list))
            _settings.Bookmarks[BookmarkGame] = list = [];
        return list;
    }

    private void BookmarkSelection(bool remove = false)
    {
        if (_gameRoot is null || SelectedNode is not { } node) return;
        var list = GameBookmarks();
        if (remove) list.RemoveAll(b => b.Key == node.Key);
        else if (list.All(b => b.Key != node.Key)) list.Add(new AssetBookmark { Key = node.Key, Name = node.Name });
        _settings.Save();
        ShowBookmarks();
    }

    private int _bookmarkRefresh;
    private async void ShowBookmarks()
    {
        BookmarkPane.Visibility = Visibility.Visible;
        BookmarkTree.Items.Clear();
        if (_gameRoot is null) return;
        var game = _gameRoot;
        var refresh = ++_bookmarkRefresh;
        List<AssetNode> assets;
        try { assets = await GetAssetIndex(); }
        catch (Exception ex) { Log.Warn("Bookmarks: " + ex.Message); return; }
        if (_gameRoot != game || refresh != _bookmarkRefresh) return;
        var byKey = assets.Prepend(game).DistinctBy(n => n.Key).ToDictionary(n => n.Key);
        var query = BookmarkSearch.Text.Trim();
        foreach (var folder in GameBookmarks().Where(b => (b.Name + " " + b.Folder).Contains(query, StringComparison.OrdinalIgnoreCase)).GroupBy(b => b.Folder).OrderBy(g => g.Key))
        {
            var group = new TreeViewItem { Header = folder.Key, IsExpanded = true };
            foreach (var bookmark in folder)
            {
                byKey.TryGetValue(bookmark.Key, out var node);
                var row = new TreeViewItem { Header = bookmark.Name, ToolTip = node?.Path ?? "Asset missing from this game", Opacity = node is null ? .4 : 1 };
                row.MouseDoubleClick += (_, e) => { if (node is not null) { OpenAsset(node); e.Handled = true; } };
                var menu = new ContextMenu();
                void Action(string label, System.Action action, bool enabled = true)
                {
                    var item = new MenuItem { Header = label, IsEnabled = enabled };
                    item.Click += (_, _) => action(); menu.Items.Add(item);
                }
                Action("Open", () => OpenAsset(node!), node is not null);
                Action("Find in explorer", () => { BookmarkPane.Visibility = Visibility.Collapsed; SearchPanel.Visibility = Visibility.Collapsed; Reveal(node!); }, node is not null);
                Action("Rename / move to folder…", () => EditBookmark(bookmark));
                Action("Remove", () => { GameBookmarks().Remove(bookmark); _settings.Save(); ShowBookmarks(); });
                row.ContextMenu = menu;
                group.Items.Add(row);
            }
            BookmarkTree.Items.Add(group);
        }
    }

    private void EditBookmark(AssetBookmark bookmark)
    {
        var name = new TextBox { Text = bookmark.Name, Margin = new Thickness(0, 4, 0, 12) };
        var folder = new TextBox { Text = bookmark.Folder, Margin = new Thickness(0, 4, 0, 12) };
        var panel = new StackPanel { Margin = new Thickness(16) };
        panel.Children.Add(new TextBlock { Text = "Name" }); panel.Children.Add(name);
        panel.Children.Add(new TextBlock { Text = "Folder" }); panel.Children.Add(folder);
        var dialog = new Window { Title = "Edit bookmark", Owner = this, Content = panel, Width = 360, SizeToContent = SizeToContent.Height, WindowStartupLocation = WindowStartupLocation.CenterOwner, Background = Background, Foreground = Foreground };
        var save = new Button { Content = "Save", IsDefault = true };
        save.Click += (_, _) => { if (string.IsNullOrWhiteSpace(name.Text) || string.IsNullOrWhiteSpace(folder.Text)) return; bookmark.Name = name.Text.Trim(); bookmark.Folder = folder.Text.Trim(); dialog.DialogResult = true; };
        panel.Children.Add(save);
        if (dialog.ShowDialog() == true) { _settings.Save(); ShowBookmarks(); }
    }

    private void Bookmarks_Click(object sender, RoutedEventArgs e) => ShowBookmarks();
    private void BookmarkSearch_Changed(object sender, TextChangedEventArgs e) { if (BookmarkPane?.IsVisible == true) ShowBookmarks(); }
    private void CloseBookmarks_Click(object sender, RoutedEventArgs e) => BookmarkPane.Visibility = Visibility.Collapsed;
}
