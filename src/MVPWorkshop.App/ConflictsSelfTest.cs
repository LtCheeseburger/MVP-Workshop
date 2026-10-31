using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using MVPWorkshop.Core.Editing;

namespace MVPWorkshop.App;

public partial class MainWindow
{
    internal async void RunConflictsCheck(string reportPath)
    {
        var report = new StringBuilder();
        var previousExe = _settings.GameExePath;
        void Check(string label, bool value) => report.AppendLine($"{(value ? "PASS" : "FAIL")} {label}");
        try
        {
            await Task.Delay(300);
            var folder = Path.Combine(Path.GetDirectoryName(reportPath)!, "conflict-runtime");
            Directory.CreateDirectory(Path.Combine(folder, "mods"));
            _settings.GameExePath = Path.Combine(folder, "MVP06.exe");
            var session = Editor.Session!;
            var node = GameRoot!.Resolve("sample.big/a.txt")!;
            session.Commit(node, [9, 8, 7], "conflict fixture");
            var installed = Path.Combine(folder, "mods", "other.mvpmod");
            Check("installed fixture exports", MvpModExporter.Export(session.Snapshot(), new ModInfo { Id = "other-mod", Name = "Other mod" }, installed, false).Ok);
            File.WriteAllText(Path.Combine(folder, "mods", "mods.json"), "{\"format\":1,\"order\":[{\"id\":\"other-mod\",\"enabled\":true}]}");
            var info = new ModInfo { Id = "new-project", Name = "New project" };
            Check("installed-mod conflicts do not block export", await ValidateModExport(session.Snapshot(), info));
            var row = Diagnostics.Single(d => d.Origin == "Installed mod" && d.AssetKey == node.Key);
            Check("Diagnostics names the other mod and load-order rules", row.Severity == DiagnosticSeverity.Warning && row.Message.Contains("Other mod") && row.Message.Contains("enabled") && row.Message.Contains("Later entries win"));
            GoToDiagnostic(row);
            Check("conflict Go to opens the affected asset", _docs.Any(d => d.Node == node));
            var output = Path.Combine(folder, "new-project.mvpmod");
            Check("validated conflicting project still exports", MvpModExporter.Export(session.Snapshot(), info, output, false).Ok);
            ShowDiagnostics(); await Task.Delay(150); UpdateLayout();
            var image = new RenderTargetBitmap((int)ActualWidth, (int)ActualHeight, 96, 96, PixelFormats.Pbgra32); image.Render(this);
            var png = new PngBitmapEncoder(); png.Frames.Add(BitmapFrame.Create(image));
            using var stream = File.Create(reportPath + ".png"); png.Save(stream);
        }
        catch (Exception ex) { report.AppendLine("FAIL " + ex); }
        finally { _settings.GameExePath = previousExe; Editor.Session?.MarkSaved(); File.WriteAllText(reportPath, report.ToString()); Application.Current.Shutdown(); }
    }
}
