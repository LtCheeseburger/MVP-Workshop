using System.IO;
using System.Text;
using System.Windows;
using MVPWorkshop.Core.Editing;

namespace MVPWorkshop.App;

public partial class MainWindow
{
    internal async void RunHotReloadCheck(string reportPath)
    {
        var report = new StringBuilder(); var previousExe = _settings.GameExePath;
        void Check(string label, bool value) => report.AppendLine($"{(value ? "PASS" : "FAIL")} {label}");
        async Task WaitExport() { for (int i = 0; i < 300 && _exporting; i++) await Task.Delay(50); if (_exporting) throw new TimeoutException("Test export timed out."); }
        try
        {
            await Task.Delay(300);
            var configuration = new DirectoryInfo(AppContext.BaseDirectory).Parent!.Name;
            var source = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../tests/HotReload/bin", configuration, "net9.0"));
            var port = Path.Combine(Path.GetDirectoryName(reportPath)!, "mock-reload-port"); Directory.CreateDirectory(port);
            foreach (var file in Directory.GetFiles(source).Where(f => Path.GetExtension(f) is ".exe" or ".dll" or ".json")) File.Copy(file, Path.Combine(port, Path.GetFileName(file)), true);
            File.WriteAllText(Path.Combine(port, "port-manifest.json"), "{\"modFormat\":1,\"layout\":1,\"workshopReload\":1}");
            _settings.GameExePath = Path.Combine(port, "HotReload.exe"); TestModFolder = Path.Combine(port, "published");
            var session = Editor.Session!; var node = GameRoot!.Resolve("sample.big/a.txt")!;
            session.Commit(node, [1, 2, 3], "first test revision"); ExportMvpMod(test: true); await WaitExport();
            var process = _testGameProcess ?? throw new InvalidOperationException("Synthetic protocol peer did not start.");
            Check("Test in Game launches the protocol peer", !process.HasExited);
            var directory = _testRunDirectory!; var first = Directory.GetDirectories(Path.Combine(directory, "generations")).Single();
            session.Commit(node, [4, 5, 6], "second test revision"); ExportMvpMod(test: true); await WaitExport();
            Check("second Test reuses the same running process", ReferenceEquals(process, _testGameProcess) && !process.HasExited);
            Check("runtime acknowledgement confirms reload", Log.Lines.Any(l => l.Text.Contains("Game reload: applied")) && !Diagnostics.Any(d => d.Origin == "Game reload"));
            Check("old generation remains readable", File.ReadAllBytes(Path.Combine(first, "files", "sample_big", "a.txt")).SequenceEqual(new byte[] { 1, 2, 3 }));
            session.Revert(node); ExportMvpMod(test: true); await WaitExport();
            Check("revert-all reloads an empty layer without relaunch", ReferenceEquals(process, _testGameProcess) && Directory.GetDirectories(Path.Combine(directory, "generations")).Length == 3 && !process.HasExited);
            StopGameLogWatch(clear: true);
            Check("stopping log watch retains running process identity", ReferenceEquals(process, _testGameProcess) && TestGameRunning);
            File.WriteAllText(Path.Combine(directory, "stop"), ""); await process.WaitForExitAsync();
        }
        catch (Exception ex) { report.AppendLine("FAIL " + ex); }
        finally
        {
            if (_testRunDirectory is { } directory) File.WriteAllText(Path.Combine(directory, "stop"), "");
            _settings.GameExePath = previousExe; Editor.Session?.MarkSaved(); File.WriteAllText(reportPath, report.ToString()); Application.Current.Shutdown();
        }
    }
}
