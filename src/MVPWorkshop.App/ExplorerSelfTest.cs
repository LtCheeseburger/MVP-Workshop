using System.IO;
using System.Runtime.InteropServices;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using MVPWorkshop.Core.Vfs;
using MVPWorkshop.App.Viewers;

namespace MVPWorkshop.App;

/// <summary>
/// Interactive regression check: --game &lt;disposable loose fixture&gt; --explorer-selftest &lt;report&gt;.
/// The fixture contains default.xbe, sample_big/_big.json, a.txt and b.txt inside sample_big,
/// and a loose logo0.xsh texture folder with 00_logo.dds. Output goes beside the report, outside the fixture.
/// </summary>
public static class ExplorerSelfTest
{
    [DllImport("user32.dll")] private static extern IntPtr GetLastActivePopup(IntPtr window);
    [DllImport("user32.dll")] private static extern bool PostMessage(IntPtr window, uint message, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowText(IntPtr window, StringBuilder text, int count);
    public static async void Run(MainWindow w, string reportPath)
    {
        var report = new StringBuilder();
        int failures = 0;
        void Check(string label, bool ok) { report.AppendLine($"{(ok ? "PASS" : "FAIL")} {label}"); if (!ok) failures++; File.WriteAllText(reportPath, report.ToString()); }
        IEnumerable<T> Find<T>(DependencyObject parent) where T : DependencyObject
        {
            if (parent is T item) yield return item;
            for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
                foreach (var child in Find<T>(VisualTreeHelper.GetChild(parent, i))) yield return child;
        }
        async Task Settle() { await Task.Delay(150); w.UpdateLayout(); }
        void Click(MenuItem item) { ((ContextMenu)item.Parent).IsOpen = false; item.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent)); }
        MenuItem Item(ContextMenu menu, string prefix) => menu.Items.OfType<MenuItem>().Single(i => i.Header.ToString()!.StartsWith(prefix));
        async Task<ContextMenu> Menu(FrameworkElement surface, FrameworkElement row)
        {
            row.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, Environment.TickCount, MouseButton.Right) { RoutedEvent = Mouse.PreviewMouseDownEvent });
            var opening = (ContextMenuEventArgs)Activator.CreateInstance(typeof(ContextMenuEventArgs), BindingFlags.Instance | BindingFlags.NonPublic, null, new object[] { row, true }, null)!;
            row.RaiseEvent(opening);
            if (!opening.Handled) { surface.ContextMenu.PlacementTarget = surface; surface.ContextMenu.IsOpen = true; }
            Check(surface.Name + " right-click events open menu", surface.ContextMenu.IsOpen);
            await Settle();
            return surface.ContextMenu;
        }
        // The timer runs inside the native dialog's modal dispatcher loop.
        async Task Dialog(MenuItem item, string expectedTitle = "")
        {
            var interacted = new TaskCompletionSource();
            var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
            timer.Tick += (_, _) =>
            {
                timer.Stop();
                var dialog = GetLastActivePopup(new WindowInteropHelper(w).Handle);
                var title = new StringBuilder(512); GetWindowText(dialog, title, title.Capacity);
                Check(item.Header + " opens expected dialog", title.ToString().Contains(expectedTitle) && !title.ToString().Contains("MVP-Workshop"));
                PostMessage(dialog, 0x10, IntPtr.Zero, IntPtr.Zero);
                interacted.SetResult();
            };
            timer.Start(); Click(item); await interacted.Task; await Settle();
        }
        try
        {
            await Settle();
            var root = w.GameRoot ?? throw new InvalidOperationException("No fixture loaded");
            var s = Editor.Session!;
            var before = Directory.EnumerateFiles(root.DiskPath!, "*", SearchOption.AllDirectories).ToDictionary(p => p, p => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(p))));
            var a = root.Resolve("sample.big/a.txt")!;
            var b = root.Resolve("sample.big/b.txt")!;
            var folder = a.Parent!;
            var output = Path.GetDirectoryName(Path.GetFullPath(reportPath))!;
            var replacement = Path.Combine(output, "replacement.txt"); File.WriteAllText(replacement, "replacement contents\n");
            w.BrowseMode.SelectedIndex = 1;
            foreach (bool search in new[] { false, true })
            {
                w.SearchPanel.Visibility = search ? Visibility.Visible : Visibility.Collapsed;
                w.Reveal(a); w.OpenAsset(b);
                if (search) { w.SearchResults.ItemsSource = new[] { a, b }; w.SearchResults.SelectedItem = b; }
                await Settle();
                FrameworkElement surface = search ? w.SearchResults : w.Tree;
                FrameworkElement Row(AssetNode node) => search
                    ? (FrameworkElement)w.SearchResults.ItemContainerGenerator.ContainerFromItem(node)
                    : Find<TreeViewItem>(w.Tree).Single(i => i.DataContext is TreeItem t && t.Node == node);
                async Task<ContextMenu> Open(AssetNode node) { await Settle(); return await Menu(surface, Row(node)); }
                var menu = await Open(a);
                Check(surface.Name + " file actions", new[] { "Open", "Import file over this…", "Add new file here…", "Export raw…", "Dependencies", "Copy name", "Copy path", "Copy SHA-1" }.All(h => menu.Items.OfType<MenuItem>().Any(i => Equals(i.Header, h))));
                Click(Item(menu, "Copy name")); Check(surface.Name + " copies clicked name despite other document/selection", Clipboard.GetText() == a.Name);
                menu = await Open(a); Click(Item(menu, "Copy path")); Check(surface.Name + " copy path", Clipboard.GetText() == a.Path);
                menu = await Open(a); Click(Item(menu, "Open")); Check(surface.Name + " opens clicked asset", ((DocumentTab)w.Docs.SelectedItem).Node == a);
                menu = await Open(a); await Dialog(Item(menu, "Import file"), "Import over a.txt");
                w.ImportRaw(a, replacement);
                Check(surface.Name + " import commits clicked asset", s.IsModified(a) && !s.IsModified(b) && s.GetBytes(a).SequenceEqual(File.ReadAllBytes(replacement)));
                menu = await Open(a); Click(Item(menu, "Copy SHA")); Check(surface.Name + " SHA-1 uses edited bytes", Clipboard.GetText() == Convert.ToHexString(SHA1.HashData(s.GetBytes(a))));
                var raw = Path.Combine(output, $"{(search ? "search" : "tree")}-export-{Guid.NewGuid():N}.txt");
                menu = await Open(a); await Dialog(Item(menu, "Export raw"));
                Exporter.SaveBytesTo(a.ReadAll(), raw);
                Check(surface.Name + " exports edited bytes", File.Exists(raw) && File.ReadAllBytes(raw).SequenceEqual(s.GetBytes(a)));
                menu = await Open(a); Click(Item(menu, "Show in Changes")); Check(surface.Name + " opens Changes", ((DocumentTab)w.Docs.SelectedItem).Title == "Changes");
                menu = await Open(a); Click(Item(menu, "Dependencies")); Check(surface.Name + " opens Dependencies", ((DocumentTab)w.Docs.SelectedItem).Title == "Dependencies");
                menu = await Open(a); Click(Item(menu, "Revert")); Check(surface.Name + " reverts clicked asset", !s.IsModified(a));
                s.Undo(); Check(surface.Name + " revert undo", s.IsModified(a)); s.Redo();
                menu = await Open(a); await Dialog(Item(menu, "Add new"), "Add files to sample.big");
                w.AddFile(a, new[] { replacement });
                Check(surface.Name + " adds under clicked folder", s.Added.ContainsKey("sample_big/replacement.txt")); s.RemoveAdded("sample_big/replacement.txt");
                if (search) w.SearchResults.ItemsSource = new[] { folder, a, b };
                s.Commit(a, File.ReadAllBytes(replacement), "Fixture edit"); s.Commit(b, File.ReadAllBytes(replacement), "Fixture edit");
                var extract = Path.Combine(output, "extract-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(extract);
                menu = await Open(folder); await Dialog(Item(menu, "Extract folder"), "Extract");
                await Exporter.ExtractTreeTo(folder, Path.Combine(extract, "sample.big"));
                Check(surface.Name + " extracts edited folder contents", File.Exists(Path.Combine(extract, "sample.big", "a.txt")) && File.ReadAllBytes(Path.Combine(extract, "sample.big", "a.txt")).SequenceEqual(s.GetBytes(a)));
                Check(surface.Name + " extracts loose pack and DDS without path collision", File.Exists(Path.Combine(extract, "sample.big", "logo0.xsh.raw")) && File.Exists(Path.Combine(extract, "sample.big", "logo0.xsh", "00_logo.dds")));
                menu = await Open(folder); await Dialog(Item(menu, "Revert everything"), "Revert");
                Check(surface.Name + " folder revert cancellation", s.ModifiedAssetCount == 2);
                menu = await Open(folder);
                var accept = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(400) };
                accept.Tick += (_, _) => { accept.Stop(); PostMessage(GetLastActivePopup(new WindowInteropHelper(w).Handle), 0x111, (IntPtr)1, IntPtr.Zero); }; accept.Start(); Click(Item(menu, "Revert everything"));
                Check(surface.Name + " reverts folder edits", s.ModifiedAssetCount == 0);
                var pack = root.Resolve("sample.big/logo0.xsh") ?? throw new InvalidOperationException("Fixture needs a loose logo0.xsh texture pack");
                foreach (var texture in new[] { pack, pack.Children[0] })
                {
                    if (search) w.SearchResults.ItemsSource = new[] { texture, b };
                    else w.Reveal(texture);
                    w.OpenAsset(b); await Settle();
                    menu = await Open(texture); await Dialog(Item(menu, "Replace image"), "Replace");
                    Check(surface.Name + " texture opens clicked asset", ((DocumentTab)w.Docs.SelectedItem).Node == texture);
                }
            }
            IEnumerable<TreeItem> Rows(IEnumerable<TreeItem> items)
            {
                foreach (var row in items)
                {
                    yield return row;
                    row.EnsureLoaded();
                    foreach (var child in Rows(row.Children)) yield return child;
                }
            }
            void Filter(bool modified, bool added)
            {
                w.ModifiedFilter.IsChecked = modified; w.AddedFilter.IsChecked = added;
                w.ModifiedFilter.RaiseEvent(new RoutedEventArgs(CheckBox.ClickEvent));
            }
            List<TreeItem> AllRows() => Rows(w.Tree.Items.Cast<TreeItem>()).ToList();
            foreach (var mode in new[] { 1, 0 })
            {
                w.BrowseMode.SelectedIndex = mode; await Task.Delay(600);
                Filter(true, false); await Settle();
                Check("Modified empty tree " + mode, w.Tree.Items.Count == 0);
                s.AddFile("sample_big/new/sub/new.txt", [1, 2, 3]);
                s.Commit(a, File.ReadAllBytes(replacement), "Fixture edit");
                await Settle();
                Check("Modified includes asset and parents " + mode, AllRows().Any(r => r.Node == a) && AllRows().Any(r => r.Node is null || r.Node == folder));
                Check("Modified excludes unrelated asset " + mode, !AllRows().Any(r => r.Node == b || r.IsAdded));
                Filter(false, true); await Settle();
                Check("Added includes marked node " + mode, AllRows().Count(r => r.IsAdded) == 1);
                Check("Added excludes modified " + mode, !AllRows().Any(r => r.Node == a));
                Filter(true, true); await Settle();
                Check("Combined union " + mode, AllRows().Any(r => r.Node == a) && AllRows().Any(r => r.IsAdded));
                s.Revert(a); await Settle(); Check("Live revert " + mode, !AllRows().Any(r => r.Node == a));
                s.Undo(); await Settle(); Check("Live undo " + mode, AllRows().Any(r => r.Node == a));
                s.Redo(); await Settle(); Check("Live redo " + mode, !AllRows().Any(r => r.Node == a));
                var addedRow = AllRows().Single(r => r.IsAdded);
                foreach (var row in AllRows()) row.IsExpanded = true;
                addedRow.IsSelected = true; await Settle();
                var visual = Find<TreeViewItem>(w.Tree).First(r => r.DataContext == addedRow);
                var addedMenu = await Menu(w.Tree, visual);
                Click(Item(addedMenu, "Remove")); await Settle();
                Check("Remove updates session and tree " + mode, s.Added.Count == 0 && !AllRows().Any(r => r.IsAdded));
                Filter(false, false); await Settle();
                Check("Unchecked shows all " + mode, AllRows().Any(r => r.Node == b));
            }            try { await Exporter.ExtractTreeTo(folder, Path.Combine(root.DiskPath!, "blocked-extraction")); Check("Extraction rejects game directory", false); }
            catch (InvalidOperationException) { Check("Extraction rejects game directory", true); }
            try { Exporter.CheckOutput(Path.Combine(root.DiskPath!, "blocked-export.txt")); Check("Raw export rejects game directory", false); }
            catch (InvalidOperationException) { Check("Raw export rejects game directory", true); }
            await Settle();
            Check("Fixture files unchanged", before.All(kv => File.Exists(kv.Key) && Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(kv.Key))) == kv.Value) && Directory.EnumerateFiles(root.DiskPath!, "*", SearchOption.AllDirectories).Count() == before.Count);
            var dpi = VisualTreeHelper.GetDpi(w);
            var bitmap = new RenderTargetBitmap((int)(w.ActualWidth * dpi.DpiScaleX), (int)(w.ActualHeight * dpi.DpiScaleY), dpi.PixelsPerInchX, dpi.PixelsPerInchY, PixelFormats.Pbgra32);
            bitmap.Render(w); var png = new PngBitmapEncoder(); png.Frames.Add(BitmapFrame.Create(bitmap));
            using (var file = File.Create(reportPath + ".png")) png.Save(file);
            s.MarkSaved();
        }
        catch (Exception ex) { Check(ex.ToString(), false); }
        report.AppendLine($"Failures: {failures}"); File.WriteAllText(reportPath, report.ToString());
        Editor.Session?.MarkSaved();
        Application.Current.Shutdown(failures == 0 ? 0 : 1);
    }
}
