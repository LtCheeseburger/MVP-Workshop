using System.Windows;
using System.Windows.Controls;
using MVPWorkshop.Core.Formats;
using MVPWorkshop.Core.Vfs;

namespace MVPWorkshop.App.Viewers;

public sealed class TiburonSkeletonView : DockPanel
{
    public TiburonSkeletonView(AssetNode node)
    {
        var bones = TiburonSkeleton.Read(node.ReadAll());
        var note = B.Text($"{bones.Count} bones · local translations and parent hierarchy · rotations, skin bindings and animation remain undecoded", "Muted");
        note.TextWrapping = TextWrapping.Wrap; note.Margin = new Thickness(12);
        SetDock(note, Dock.Top); Children.Add(note);
        var tree = new TreeView { MinWidth = 220 };
        var items = new Dictionary<int, TreeViewItem>();
        foreach (var bone in bones)
        {
            var item = new TreeViewItem { Header = $"{bone.Index}: {bone.Name}", IsExpanded = true };
            items[bone.Index] = item;
            if (bone.Parent < 0) tree.Items.Add(item); else items[bone.Parent].Items.Add(item);
        }
        SetDock(tree, Dock.Left); Children.Add(tree);
        Children.Add(new DataGrid { ItemsSource = bones, IsReadOnly = true, AutoGenerateColumns = true, CanUserAddRows = false });
    }
}
