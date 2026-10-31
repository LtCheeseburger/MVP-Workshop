using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace MVPWorkshop.App;

public partial class MainWindow
{
    private void CloseAllTabs()
    {
        if (!CheckPlayerDrafts()) return;
        foreach (var tab in _docs.Where(d => d.CanClose).ToList()) CloseDocument(tab);
    }

    private Point _tabDragStart;
    private DocumentTab? _dragTab;

    private void Tab_MouseDown(object sender, MouseButtonEventArgs e)
    {
        _dragTab = null;
        // A close-button click must never initiate a tab drag.
        var source = e.OriginalSource as DependencyObject;
        while (source is not null && source != sender)
        {
            if (source is Button) return;
            source = VisualTreeHelper.GetParent(source);
        }
        _dragTab = ((TabItem)sender).DataContext as DocumentTab;
        _tabDragStart = e.GetPosition(Docs);
    }

    private void Tab_MouseMove(object sender, MouseEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed || _dragTab is null) return;
        var point = e.GetPosition(Docs);
        if (Math.Abs(point.X - _tabDragStart.X) < SystemParameters.MinimumHorizontalDragDistance &&
            Math.Abs(point.Y - _tabDragStart.Y) < SystemParameters.MinimumVerticalDragDistance) return;
        var tab = _dragTab;
        _dragTab = null;
        DragDrop.DoDragDrop((TabItem)sender, new DataObject(typeof(DocumentTab), tab), DragDropEffects.Move);
    }

    private void Tab_DragOver(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetData(typeof(DocumentTab)) is DocumentTab tab && _docs.Contains(tab)
            ? DragDropEffects.Move : DragDropEffects.None;
        e.Handled = true;
    }

    private void Tab_Drop(object sender, DragEventArgs e)
    {
        if (e.Data.GetData(typeof(DocumentTab)) is DocumentTab tab && _docs.Contains(tab) &&
            ((TabItem)sender).DataContext is DocumentTab target && _docs.Contains(target))
        {
            _docs.Move(_docs.IndexOf(tab), _docs.IndexOf(target));
            Docs.SelectedItem = tab;
        }
        e.Handled = true;
    }

    private async void CheckUpdates_Click(object sender, RoutedEventArgs e) => await CheckForUpdates(manual: true);
    private void About_Click(object sender, RoutedEventArgs e) => MessageBox.Show(this,
        $"{AppInfo.Name} {AppInfo.Version}\nEdit assets, save projects and export mods. Original game files are never modified.",
        "About MVP Workshop", MessageBoxButton.OK, MessageBoxImage.Information);
}
