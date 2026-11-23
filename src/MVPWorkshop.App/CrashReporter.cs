using System.Diagnostics;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using MVPWorkshop.App.Viewers;

namespace MVPWorkshop.App;

internal static class CrashReporter
{
    private static int _reporting;
    internal static string ReportDirectory { get; set; } = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), AppInfo.Name, "crashes");

    internal static string WriteReport(Exception exception, string origin, string? project, string? autosave, IEnumerable<LogLine> lines)
    {
        var report = new StringBuilder().AppendLine($"{AppInfo.Name} {AppInfo.Version}")
            .AppendLine($"UTC: {DateTime.UtcNow:O}").AppendLine($"Origin: {origin}")
            .AppendLine($"Runtime: {Environment.Version}; OS: {Environment.OSVersion}")
            .AppendLine($"Open project: {project ?? "Untitled"}").AppendLine($"Autosave: {autosave ?? "No dirty project, or autosave unavailable"}")
            .AppendLine().AppendLine(exception.ToString()).AppendLine().AppendLine("Log tail:");
        foreach (var line in lines.TakeLast(100)) report.AppendLine($"{line.Time:O} [{line.Level}] {line.Text}");
        Directory.CreateDirectory(ReportDirectory);
        var path = Path.Combine(ReportDirectory, $"crash-{DateTime.UtcNow:yyyyMMdd-HHmmss-fff}-{Guid.NewGuid():N}.txt");
        File.WriteAllText(path, report.ToString());
        return path;
    }

    internal static void Handle(Exception exception, string origin)
    {
        // Automated snapshots must fail with a diagnostic artifact instead of waiting on a modal dialog.
        var arguments = Environment.GetCommandLineArgs();
        int snapshot = Array.IndexOf(arguments, "--snapshot");
        if (snapshot >= 0 && snapshot + 1 < arguments.Length)
        {
            try { File.WriteAllText(arguments[snapshot + 1] + ".error.txt", exception.ToString()); } catch { }
            Environment.Exit(1);
            return;
        }
        if (Interlocked.Exchange(ref _reporting, 1) != 0) return;
        try
        {
            var app = Application.Current;
            string? autosave = null, project = null;
            LogLine[] lines = [];
            void Capture()
            {
                project = Editor.Session?.ProjectPath;
                autosave = MainWindow.Instance?.AutosaveProject();
                lines = Log.Lines.ToArray();
            }
            // Fatal worker exceptions can arrive while the dispatcher is blocked; never wait indefinitely.
            if (app?.Dispatcher is { HasShutdownStarted: false } dispatcher)
            {
                try
                {
                    if (dispatcher.CheckAccess()) Capture();
                    else dispatcher.Invoke(Capture, System.Windows.Threading.DispatcherPriority.Send, default, TimeSpan.FromSeconds(3));
                }
                catch { }
            }
            string? path = null;
            try { path = WriteReport(exception, origin, project, autosave, lines); }
            catch (Exception reportError) { Debug.WriteLine(reportError); }
            if (app?.Dispatcher is { HasShutdownStarted: false } ui)
            {
                void Show() => CreateDialog(exception, path, autosave).ShowDialog();
                if (ui.CheckAccess()) Show();
                else if (origin != "AppDomain") ui.BeginInvoke(Show);
                else ui.Invoke(Show, System.Windows.Threading.DispatcherPriority.Send, default, TimeSpan.FromSeconds(5));
            }
        }
        catch (Exception reportError) { Debug.WriteLine(reportError); }
        finally { Interlocked.Exchange(ref _reporting, 0); }
    }

    internal static Window CreateDialog(Exception exception, string? path, string? autosave)
    {
        var panel = new StackPanel { Margin = new Thickness(20) };
        var dialog = new Window { Title = AppInfo.Name + " crash report", Width = 620, SizeToContent = SizeToContent.Height,
            Content = panel, WindowStartupLocation = WindowStartupLocation.CenterScreen, Background = Ui.Res("WindowBg"), Foreground = Ui.Res("Text") };
        panel.Children.Add(new TextBlock { Text = "An unexpected error occurred.", FontSize = 18 });
        panel.Children.Add(new TextBlock { Text = exception.Message, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 12, 0, 12) });
        panel.Children.Add(new TextBlock { Text = $"{(autosave is null ? "No new autosave was written." : "Your unsaved project was autosaved: " + autosave)}\n\n{(path is null ? "The report could not be written." : "Report: " + path)}", TextWrapping = TextWrapping.Wrap });
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 16, 0, 0) };
        void Button(string label, Action action)
        {
            var button = new Button { Content = label, Margin = new Thickness(0, 0, 8, 0) };
            button.Click += (_, _) => { try { action(); } catch (Exception ex) { MessageBox.Show(dialog, ex.Message, "Crash report"); } };
            buttons.Children.Add(button);
        }
        Button("Copy report", () => Clipboard.SetText(path is null ? exception.ToString() : File.ReadAllText(path)));
        Button("Open folder", () => { Directory.CreateDirectory(ReportDirectory); Process.Start(new ProcessStartInfo(ReportDirectory) { UseShellExecute = true }); });
        Button("Close", dialog.Close);
        panel.Children.Add(buttons);
        return dialog;
    }
}
