using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Input;
using MVPWorkshop.Core.Vfs;

namespace MVPWorkshop.App;

public partial class MainWindow
{
    internal async void RunNascarCatalogCheck(string reportPath)
    {
        var report = new StringBuilder();
        void Check(string label, bool ok) => report.AppendLine($"{(ok ? "PASS" : "FAIL")} {label}");
        IEnumerable<TreeItem> Walk(IEnumerable<TreeItem> items)
        {
            foreach (var item in items) { yield return item; if (item.Node is null) foreach (var child in Walk(item.Children)) yield return child; }
        }
        try
        {
            var deadline = DateTime.UtcNow.AddSeconds(50);
            while (_categoryRoots is null && DateTime.UtcNow < deadline) await Task.Delay(100);
            Check("NASCAR starts in Categories", BrowseMode.SelectedIndex == 0 && _categoryRoots is not null);
            if (_categoryRoots is null) throw new InvalidOperationException("Category indexing timed out.");
            var leaves = Walk(_roots).Where(i => i.Node is not null).ToArray();
            var expected = NascarAssetCatalog.Build(await GetAssetIndex());
            Check("displayed categories contain every asset exactly once", leaves.Length == expected.Count && leaves.Select(i => i.Node).Distinct().Count() == expected.Count);
            Check("friendly labels survive tree projection", leaves.All(i => i.Name == NascarAssetCatalog.Describe(i.Node).Label));
            Check("large packs split into groups of at most 100 leaves", Walk(_roots).Where(i => i.Node is null && i.Children.Any(c => c.Node is not null)).All(i => i.Children.Count <= 100));
            var node = FindNode("BACKPICT.DAT/00000.bin")!;
            Reveal(node); OpenAsset(node);
            Check("category aliases reveal and open the original editable asset", BrowseMode.SelectedIndex == 0 && _docs.Any(d => d.Node == node) && Walk(_roots).Any(i => i.Node == node && i.IsSelected));
            SearchBox.Text = "Drivers numbers";
            SearchBox_KeyDown(SearchBox, new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(this), 0, Key.Enter));
            for (int i = 0; i < 100 && SearchResults.ItemsSource is null; i++) await Task.Delay(100);
            var matches = (SearchResults.ItemsSource as IEnumerable<AssetNode>)?.ToArray() ?? [];
            Check("search finds assets by category terms", matches.Length >= 434 && matches.All(n => NascarAssetCatalog.Category(n).StartsWith("Drivers/Numbers/")));
            BrowseMode.SelectedIndex = 1;
            Check("original folder and archive view remains available", _roots.Any(i => i.Node == GameRoot));
            BrowseMode.SelectedIndex = 0;
            Check("switching back restores category aliases", Walk(_roots).Count(i => i.Node is not null) == expected.Count);
        }
        catch (Exception ex) { report.AppendLine("FAIL " + ex); }
        File.WriteAllText(reportPath, report.ToString());
        Application.Current.Shutdown(report.ToString().Contains("FAIL") ? 1 : 0);
    }
}
