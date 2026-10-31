using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using MVPWorkshop.Core.Editing;
using MVPWorkshop.Core.Install;

namespace MVPWorkshop.App;

public partial class MainWindow
{
    internal async void RunPortCompatibilityCheck(string reportPath)
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
            var folder = Path.Combine(Path.GetDirectoryName(reportPath)!, "future-port"); Directory.CreateDirectory(folder);
            File.WriteAllText(Path.Combine(folder, "version.txt"), "99.0-test");
            var manifest = Path.Combine(folder, "port-manifest.json");
            File.WriteAllText(manifest, "{\"format\":1,\"version\":\"99.0-test\",\"modFormat\":2,\"layout\":2}");
            _settings.GameExePath = Path.Combine(folder, "MVP06.exe");
            PortSettingsChanged?.Invoke();
            var start = _docs.First(d => !d.CanClose); Docs.SelectedItem = start; UpdateLayout();
            Check("Start warns when the port expects a newer layout", Find<TextBlock>(start.Content).Any(t => t.Text.Contains("expects loose layout 2")));
            Check("Start exposes the correct Workshop update link", Find<Button>(start.Content).Any(b => b.Tag as string == PortCompatibilityInfo.WorkshopUpdates));
            var session = Editor.Session!;
            session.Commit(GameRoot!.Resolve("sample.big/a.txt")!, [1, 2, 3], "compatibility fixture");
            var info = new ModInfo { Id = "compatibility-test", Name = "Compatibility test" };
            Check("compatibility warnings remain nonblocking at export", await ValidateModExport(session.Snapshot(), info));
            var warning = Diagnostics.First(d => d.Origin == "Port compatibility" && d.Message.Contains("expects loose layout 2"));
            Check("export diagnostics include update guidance", warning.Severity == DiagnosticSeverity.Warning && warning.Message.Contains(PortCompatibilityInfo.WorkshopUpdates));
            ShowDiagnostics(); GoToDiagnostic(warning);
            Check("compatibility Go to returns to Start update links", Docs.SelectedItem == start);
            var summary = CreateExportSummary("Synthetic export compatibility warning", compatibilityWarnings: true);
            summary.Show(); summary.UpdateLayout();
            Check("export summary offers a clickable Workshop update link", Find<Button>(summary).Any(b => b.Tag as string == PortCompatibilityInfo.WorkshopUpdates));
            summary.Close();
            await Task.Delay(150); UpdateLayout();
            var image = new RenderTargetBitmap((int)ActualWidth, (int)ActualHeight, 96, 96, PixelFormats.Pbgra32); image.Render(this);
            var png = new PngBitmapEncoder(); png.Frames.Add(BitmapFrame.Create(image));
            using (var stream = File.Create(reportPath + ".png")) png.Save(stream);
            File.WriteAllText(manifest, "{\"format\":1,\"version\":\"99.0-test\",\"modFormat\":1,\"layout\":1}");
            PortSettingsChanged?.Invoke();
            Check("matching metadata clears the Start warning", !Find<TextBlock>(start.Content).Any(t => t.Text.Contains("expects loose layout 2")));
            await ValidateModExport(session.Snapshot(), info);
            Check("export rereads changed metadata and clears obsolete warnings", !Diagnostics.Any(d => d.Origin == "Port compatibility"));
        }
        catch (Exception ex) { report.AppendLine("FAIL " + ex); }
        finally { _settings.GameExePath = previousExe; Editor.Session?.MarkSaved(); File.WriteAllText(reportPath, report.ToString()); Application.Current.Shutdown(); }
    }
}
