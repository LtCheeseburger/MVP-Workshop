using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace MVPWorkshop.App;

public partial class MainWindow
{
    internal async void RunOptionsCheck(string reportPath)
    {
        var report = new StringBuilder();
        void Check(string label, bool ok) => report.AppendLine($"{(ok ? "PASS" : "FAIL")} {label}");
        var settingsPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "MVP-Workshop", "settings.json");
        var previous = File.Exists(settingsPath) ? File.ReadAllBytes(settingsPath) : null;
        try
        {
            await Task.Delay(300);
            var dialog = CreateOptionsWindow();
            var panel = (StackPanel)dialog.Content;
            var fields = panel.Children.OfType<TextBox>().ToArray();
            var save = panel.Children.OfType<StackPanel>().Single().Children.OfType<Button>().First();
            dialog.Loaded += (_, _) => Dispatcher.BeginInvoke(() =>
            {
                try
                {
                    fields[1].Text = "0";
                    save.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    Check("invalid autosave interval keeps Options open", dialog.IsVisible && _settings.AutosaveMinutes != 0);
                    fields[0].Clear(); fields[1].Text = "3"; fields[2].Text = "2"; fields[3].Text = Path.GetDirectoryName(reportPath)!;
                    save.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                }
                catch (Exception ex) { report.AppendLine("FAIL " + ex); dialog.Close(); }
            });
            Check("Options saves", dialog.ShowDialog() == true);
            var restored = Settings.Load();
            Check("Options persist autosave settings", restored.AutosaveMinutes == 3 && restored.AutosaveCount == 2);
            Check("autosave timer adopts interval", _autosaveTimer.Interval == TimeSpan.FromMinutes(3));
            var logCount = Log.Lines.Count;
            Log.MinimumLevel = "ERROR"; Log.Info("hidden information"); Log.Warn("hidden warning");
            Check("log level filters informational and warning entries", Log.Lines.Count == logCount);
            Log.MinimumLevel = _settings.LogLevel;
            PropsToggle.IsChecked = false; LogToggle.IsChecked = false;
            ResetLayout();
            Check("reset restores panels and window size", PropsToggle.IsChecked == true && LogToggle.IsChecked == true && Width == 1560 && Height == 920);
            MainWindow.AutosaveRoot = Path.Combine(Path.GetDirectoryName(reportPath)!, "OptionsAutosave");
            var session = Editor.Session!;
            var node = GameRoot!.Resolve("sample.big/a.txt")!;
            session.Commit(node, [4, 3, 2, 1], "Crash recovery test");
            string? backup = null;
            for (int i = 0; i < 4; i++) backup = AutosaveProject();
            Check("configured backup count rotates", Directory.GetFiles(AutosaveRoot, "Project_*.mvpwproj", SearchOption.AllDirectories).Length == 2);
            CrashReporter.ReportDirectory = Path.Combine(Path.GetDirectoryName(reportPath)!, "crashes");
            var path = CrashReporter.WriteReport(new InvalidOperationException("synthetic crash"), "test", "project.mvpwproj", backup, [new LogLine(DateTime.Now, "WARN", "test log tail")]);
            var text = File.ReadAllText(path);
            Check("report contains version, exception, project, autosave and log tail", text.Contains(AppInfo.Version) && text.Contains("synthetic crash") && text.Contains("project.mvpwproj") && text.Contains(backup!) && text.Contains("test log tail"));
            var crashDialog = CrashReporter.CreateDialog(new Exception("synthetic crash"), path, backup);
            crashDialog.Show(); crashDialog.UpdateLayout();
            var crashPanel = (StackPanel)crashDialog.Content;
            Check("crash dialog offers Copy report and Open folder", crashPanel.Children.OfType<StackPanel>().Single().Children.OfType<Button>().Select(b => b.Content).Contains("Copy report") && crashPanel.Children.OfType<StackPanel>().Single().Children.OfType<Button>().Select(b => b.Content).Contains("Open folder"));
            var image = new System.Windows.Media.Imaging.RenderTargetBitmap((int)crashDialog.ActualWidth, (int)crashDialog.ActualHeight, 96, 96, PixelFormats.Pbgra32);
            image.Render(crashDialog);
            var encoder = new System.Windows.Media.Imaging.PngBitmapEncoder(); encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(image));
            using (var stream = File.Create(reportPath + ".png")) encoder.Save(stream);
            crashDialog.Close();
            var closeCrash = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(100) };
            closeCrash.Tick += (_, _) =>
            {
                var window = Application.Current.Windows.OfType<Window>().FirstOrDefault(w => w.Title == "MVP Workshop crash report");
                if (window is null) return;
                closeCrash.Stop(); window.Close();
            };
            closeCrash.Start();
            CrashReporter.Handle(new Exception("handler integration check"), "Dispatcher test");
            closeCrash.Stop();
            var integrated = Directory.GetFiles(CrashReporter.ReportDirectory).OrderBy(File.GetLastWriteTimeUtc).Select(File.ReadAllText).Last(t => t.Contains("handler integration check"));
            Check("crash handler autosaves before reporting", integrated.Contains("Dispatcher test") && integrated.Contains("Project_") && !integrated.Contains("autosave unavailable"));
            session.MarkSaved();
        }
        catch (Exception ex) { report.AppendLine("FAIL " + ex); }
        finally
        {
            Editor.Session?.MarkSaved();
            if (previous is null) File.Delete(settingsPath); else File.WriteAllBytes(settingsPath, previous);
            File.WriteAllText(reportPath, report.ToString());
            Application.Current.Shutdown();
        }
    }
}
