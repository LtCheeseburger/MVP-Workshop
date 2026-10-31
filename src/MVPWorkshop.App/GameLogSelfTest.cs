using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using MVPWorkshop.Core.Editing;

namespace MVPWorkshop.App;

public partial class MainWindow
{
    internal async void RunGameLogCheck(string reportPath)
    {
        var report = new StringBuilder();
        void Check(string label, bool value) => report.AppendLine($"{(value ? "PASS" : "FAIL")} {label}");
        try
        {
            await Task.Delay(300);
            var folder = Path.Combine(Path.GetDirectoryName(reportPath)!, "game-log-runtime");
            Directory.CreateDirectory(Path.Combine(folder, "logs"));
            File.WriteAllText(Path.Combine(folder, "portable.txt"), "");
            var session = Editor.Session!;
            var node = GameRoot!.Resolve("sample.big/a.txt")!;
            session.Commit(node, [3, 2, 1], "valid project edit");
            StartGameLogWatch(Path.Combine(folder, "MVP06.exe"));
            File.WriteAllText(Path.Combine(folder, "logs", "mvp06.log"), "[1.0] error: mods: sample_big/a.txt failed to load\n");
            await ReadGameLogs();
            Check("runtime errors appear in Diagnostics with their source", Diagnostics.Any(d => d.Origin == "Game log" && d.Severity == DiagnosticSeverity.Error && d.AssetKey == node.Key));
            Check("badge includes game log rows", (string)DiagnosticsButton.Content == $"Diagnostics ({Diagnostics.Count})");
            GoToDiagnostic(Diagnostics.First(d => d.Origin == "Game log"));
            Check("game-log Go to opens the linked asset", _docs.Any(t => t.Node == node));
            Check("historical runtime errors do not block exporting a fix", await ValidateModExport(session.Snapshot(), new ModInfo { Id = "fix", Name = "Fix" }));
            Check("validation refresh retains runtime diagnostics", Diagnostics.Any(d => d.Origin == "Game log"));
            await ReadGameLogs();
            Check("repeated polls do not duplicate runtime rows", _gameLogRows.Count == 1);
            ShowDiagnostics(); await Task.Delay(150); UpdateLayout();
            var image = new RenderTargetBitmap((int)ActualWidth, (int)ActualHeight, 96, 96, PixelFormats.Pbgra32); image.Render(this);
            var png = new PngBitmapEncoder(); png.Frames.Add(BitmapFrame.Create(image));
            using (var stream = File.Create(reportPath + ".png")) png.Save(stream);
            Editor.Attach(new EditSession(GameRoot!, session.OutputFolder));
            Check("switching project clears old game logs and stops monitoring", !_gameLogTimer.IsEnabled && _gameLogRows.Count == 0);
        }
        catch (Exception ex) { report.AppendLine("FAIL " + ex); }
        finally { Editor.Session?.MarkSaved(); File.WriteAllText(reportPath, report.ToString()); Application.Current.Shutdown(); }
    }
}
