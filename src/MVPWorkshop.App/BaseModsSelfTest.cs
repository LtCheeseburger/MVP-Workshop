using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using MVPWorkshop.Core.Editing;

namespace MVPWorkshop.App;

public partial class MainWindow
{
    internal async void RunBaseModsCheck(string reportPath)
    {
        var report = new StringBuilder();
        var previousExe = _settings.GameExePath;
        void Check(string label, bool value) => report.AppendLine($"{(value ? "PASS" : "FAIL")} {label}");
        IEnumerable<T> Find<T>(DependencyObject parent) where T : DependencyObject
        {
            if (parent is T item) yield return item;
            for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++) foreach (var child in Find<T>(VisualTreeHelper.GetChild(parent, i))) yield return child;
        }
        try
        {
            await Task.Delay(300);
            var session = Editor.Session!; var root = GameRoot!; var node = root.Resolve("sample.big/a.txt")!;
            var port = Path.Combine(Path.GetDirectoryName(reportPath)!, "base-ui-port"); Directory.CreateDirectory(Path.Combine(port, "mods"));
            _settings.GameExePath = Path.Combine(port, "MVP06.exe");
            var mod = Path.Combine(port, "mods", "base-ui.mvpmod");
            session.Commit(node, Encoding.ASCII.GetBytes("Base viewer bytes"), "fixture base");
            Check("synthetic base exported", MvpModExporter.Export(session, new ModInfo { Id = "ui-base", Name = "UI base" }, mod, false).Ok);
            session.Revert(node); session.MarkSaved();
            OpenAsset(node); Check("original document opens", _docs.Any(d => d.Node == node));
            BaseMods_Click(this, new RoutedEventArgs());
            Window? selector = null;
            for (int i = 0; i < 60 && selector is null; i++) { await Task.Delay(50); selector = OwnedWindows.OfType<Window>().FirstOrDefault(w => w.Title == "Installed mods as a base"); }
            if (selector is null) throw new InvalidOperationException("Base selector did not open.");
            selector.UpdateLayout();
            var choice = Find<CheckBox>(selector).Single();
            Check("selector discovers compatible installed mod independently of launcher enable flag", choice.IsEnabled && choice.IsChecked == false);
            choice.IsChecked = true;
            var apply = Find<Button>(selector).First(b => b.Content is StackPanel p && p.Children.OfType<TextBlock>().Any(t => t.Text == "Apply base"));
            apply.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            for (int i = 0; i < 100 && session.BaseLayer is null; i++) await Task.Delay(50);
            Check("selector applies chosen base", session.BaseLayer?.References.Count == 1);
            Check("base switch closes stale documents", !_docs.Any(d => d.Node == node));
            Check("base selection dirties project without exporting inherited files", session.IsDirty && MvpModExporter.Collect(session, [], []).Count == 0);
            BrowseMode.SelectedIndex = 1; UpdateBrowser();
            var archive = _roots[0].Children.First(t => t.Node.Name == "sample.big"); archive.IsExpanded = true;
            Check("tree marks source mod", archive.Children.First(t => t.Node == node).BaseOrigin == "from UI base");
            OpenAsset(node); Docs.SelectedItem = _docs.First(d => d.Node == node); UpdateLayout();
            Check("viewer marks source mod and reads merged bytes", Find<TextBlock>(((DocumentTab)Docs.SelectedItem).Content).Any(t => t.Text == "from UI base") && Encoding.ASCII.GetString(node.ReadAll()) == "Base viewer bytes");
            session.Commit(node, Encoding.ASCII.GetBytes("own bytes"), "own edit");
            var project = Path.Combine(Path.GetDirectoryName(reportPath)!, "base-ui.mvpwproj"); ProjectFile.Save(session, project);
            var restored = ProjectFile.Load(project, root, out var warnings); ResetBaseViews(); Editor.Attach(restored);
            Check("project reload restores layer and own edits", warnings.Count == 0 && restored.BaseLayer?.References.Count == 1 && Encoding.ASCII.GetString(node.ReadAll()) == "own bytes");
            ApplyBaseLayer(null);
            Check("removing base retains explicit project edit", Encoding.ASCII.GetString(node.ReadAll()) == "own bytes" && MvpModExporter.Collect(restored, [], []).ContainsKey("sample_big/a.txt"));
        }
        catch (Exception ex) { report.AppendLine("FAIL " + ex); }
        finally { _settings.GameExePath = previousExe; Editor.Session?.MarkSaved(); File.WriteAllText(reportPath, report.ToString()); Application.Current.Shutdown(); }
    }
}
