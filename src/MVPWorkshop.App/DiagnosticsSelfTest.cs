using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using MVPWorkshop.App.Viewers;
using MVPWorkshop.Core.Editing;

namespace MVPWorkshop.App;

public partial class MainWindow
{
    [DllImport("user32.dll")] private static extern IntPtr GetLastActivePopup(IntPtr window);
    [DllImport("user32.dll")] private static extern bool PostMessage(IntPtr window, uint message, IntPtr wParam, IntPtr lParam);
    internal async void RunDiagnosticsCheck(string reportPath)
    {
        var report = new StringBuilder();
        void Check(string label, bool value) => report.AppendLine($"{(value ? "PASS" : "FAIL")} {label}");
        try
        {
            await Task.Delay(300);
            var session = Editor.Session!;
            var node = GameRoot!.Resolve("sample.big/a.txt")!;
            session.CommitPreviewBytes(node, [1, 2], "preview fixture", "test");
            session.AddFile("blocked.big", [9]);
            for (int i = 0; i < 100 && !Diagnostics.Any(d => d.Message.Contains("packed archive")); i++) await Task.Delay(30);
            Check("debounced background checks publish errors and warnings", Diagnostics.Any(d => d.Severity == DiagnosticSeverity.Error) && Diagnostics.Any(d => d.Message.Contains("Preview-only")));
            Check("badge displays diagnostic count", (string)DiagnosticsButton.Content == $"Diagnostics ({Diagnostics.Count})");
            ShowDiagnostics(); UpdateLayout();
            var view = (DiagnosticsView)((DocumentTab)Docs.SelectedItem).Content;
            Check("Diagnostics pane shows severity, asset, message and action", view.Children.OfType<DataGrid>().Single().Columns.Count == 4);
            GoToDiagnostic(Diagnostics.First(d => d.AssetKey == node.Key));
            Check("Go to opens the asset", _docs.Any(d => d.Node == node));
            var closeWarning = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(100) };
            closeWarning.Tick += (_, _) => { var popup = GetLastActivePopup(new WindowInteropHelper(this).Handle); if (popup != IntPtr.Zero && popup != new WindowInteropHelper(this).Handle) PostMessage(popup, 0x10, IntPtr.Zero, IntPtr.Zero); };
            closeWarning.Start();
            try { Check("export validation blocks errors", !await ValidateModExport(session.Snapshot(), new ModInfo { Id = "test", Name = "Test" })); }
            finally { closeWarning.Stop(); }
            var directory = Path.GetDirectoryName(reportPath)!;
            _settings.GameExePath = Path.Combine(directory, "must-not-launch.exe");
            File.WriteAllText(_settings.GameExePath, "A test sentinel, never a runnable game.");
            TestModFolder = Path.Combine(directory, "blocked-test-mod");
            closeWarning.Start();
            try
            {
                ExportMvpMod(test: true);
                for (int i = 0; i < 100 && _exporting; i++) await Task.Delay(30);
                Check("Test in Game blocks errors before export or launch", !_exporting && !Directory.Exists(TestModFolder) && !Log.Lines.Any(l => l.Text.StartsWith("Mod failed:")));
            }
            finally { closeWarning.Stop(); }
            session.RemoveAdded("blocked.big");
            for (int i = 0; i < 100 && Diagnostics.Any(d => d.Severity == DiagnosticSeverity.Error); i++) await Task.Delay(30);
            Check("removing the invalid file clears errors", !Diagnostics.Any(d => d.Severity == DiagnosticSeverity.Error));
            Check("warnings allow export", await ValidateModExport(session.Snapshot(), new ModInfo { Id = "test", Name = "Test" }));
            ShowDiagnostics(); UpdateLayout();
            var bitmap = new RenderTargetBitmap((int)ActualWidth, (int)ActualHeight, 96, 96, PixelFormats.Pbgra32); bitmap.Render(this);
            var png = new PngBitmapEncoder(); png.Frames.Add(BitmapFrame.Create(bitmap));
            using (var output = File.Create(reportPath + ".png")) png.Save(output);
        }
        catch (Exception ex) { report.AppendLine("FAIL " + ex); }
        finally
        {
            Editor.Session?.MarkSaved();
            File.WriteAllText(reportPath, report.ToString());
            Application.Current.Shutdown();
        }
    }
}
