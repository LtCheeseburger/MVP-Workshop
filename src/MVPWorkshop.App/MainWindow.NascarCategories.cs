using System.Globalization;
using System.Windows;
using System.Windows.Data;
using MVPWorkshop.Core.Vfs;

namespace MVPWorkshop.App;

public partial class MainWindow
{
    private static List<TreeItem> BuildNascarCategories(IReadOnlyList<NascarAssetCatalog.Entry> entries)
    {
        IEnumerable<TreeItem> Leaves(IEnumerable<NascarAssetCatalog.Entry> assets, int depth)
        {
            var list = assets.ToArray();
            TreeItem Leaf(NascarAssetCatalog.Entry e, int d) => new(e.Asset, d) { BrowserName = e.Label };
            if (list.Length <= 100) return list.Select(e => Leaf(e, depth));
            return list.Chunk(100).Select((batch, i) => new TreeItem($"Assets {i * 100 + 1:N0}–{i * 100 + batch.Length:N0}", depth, batch.Select(e => Leaf(e, depth + 1))));
        }
        List<TreeItem> Categories(IEnumerable<NascarAssetCatalog.Entry> assets, int depth)
        {
            var groups = assets.GroupBy(e => e.Category.Split('/')[depth]);
            if (depth == 0) groups = groups.OrderBy(g => Array.IndexOf(NascarAssetCatalog.CategoryOrder, g.Key));
            var result = new List<TreeItem>();
            foreach (var category in groups)
            {
                var children = Categories(category.Where(e => e.Category.Split('/').Length > depth + 1), depth + 1);
                foreach (var pack in category.Where(e => e.Category.Split('/').Length == depth + 1).GroupBy(e => e.SourcePack))
                {
                    var kinds = pack.GroupBy(e => e.Kind).ToArray();
                    children.Add(new TreeItem(pack.Key, depth + 1, kinds.Length > 1
                        ? kinds.Select(kind => new TreeItem(kind.Key, depth + 2, Leaves(kind, depth + 3)))
                        : Leaves(pack, depth + 2)));
                }
                result.Add(new TreeItem(category.Key, depth, children) { IsExpanded = depth == 0 && category.Key == "Cars" });
            }
            return result;
        }
        return Categories(entries, 0);
    }
}

public sealed class AssetBrowserLabelConverter : IValueConverter
{
    public static AssetBrowserLabelConverter Instance { get; } = new();
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) => value is AssetNode node ? AppInfo.IsNascar ? NascarAssetCatalog.Describe(node).Label : node.Name : "";
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => DependencyProperty.UnsetValue;
}
