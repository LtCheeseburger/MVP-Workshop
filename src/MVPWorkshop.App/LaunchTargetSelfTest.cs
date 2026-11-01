using System.IO;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using MVPWorkshop.App.Viewers;
using MVPWorkshop.Core.Editing;

namespace MVPWorkshop.App;

public partial class MainWindow
{
    internal async void RunLaunchTargetCheck(string reportPath)
    {
        var report = new StringBuilder(); var previousExe = _settings.GameExePath;
        void Check(string label, bool value) => report.AppendLine($"{(value ? "PASS" : "FAIL")} {label}");
        string? publicationDirectory = null;
        try
        {
            var view = (TaskStartView)_docs.First(d => !d.CanClose).Content;
            for (int i = 0; i < 200 && view.Profiles.Count < 2; i++) await Task.Delay(30);
            var first = view.Profiles[0]; var session = Editor.Session!;
            Check("target dropdown exposes automatic/menu/team/stadium/exhibition", LaunchTargetPicker.Items.Count == 5);
            Check("clean project defaults to main menu", DefaultLaunchTarget().Kind == GameLaunchKind.MainMenu);
            var bytes = first.Roster.ReadAll().ToArray(); bytes[first.Team.Offset + 112] ^= 1; session.Commit(first.Roster, bytes, "launch target school edit");
            Check("automatic target follows the last edited school", DefaultLaunchTarget() is { Kind: GameLaunchKind.Team } automatic && automatic.Home == first.Team.Name);
            var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(40) };
            timer.Tick += (_, _) =>
            {
                var dialog = OwnedWindows.OfType<Window>().FirstOrDefault(w => w.Title == "Test in Game target");
                if (dialog?.Content is not StackPanel panel) return;
                var button = panel.Children.OfType<Button>().Single(); button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            };
            timer.Start();
            try { LaunchTargetPicker.SelectedIndex = 2; Check("team dropdown configures a concrete school", _explicitLaunchTarget?.Kind == GameLaunchKind.Team && _explicitLaunchTarget.Problem is null); LaunchTargetPicker.SelectedIndex = 4; }
            finally { timer.Stop(); }
            var target = _explicitLaunchTarget ?? throw new InvalidOperationException("No exhibition target selected.");
            Check("exhibition review selects two different schools", target.Kind == GameLaunchKind.Exhibition && target.Home != target.Away && target.Problem is null);
            LaunchTargetPicker.SelectedIndex = 1; Check("explicit menu overrides automatic school targeting", DefaultLaunchTarget().Kind == GameLaunchKind.MainMenu);
            _explicitLaunchTarget = target;
            var configuration = new DirectoryInfo(AppContext.BaseDirectory).Parent!.Name;
            var source = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../tests/HotReload/bin", configuration, "net9.0"));
            var port = Path.Combine(Path.GetDirectoryName(reportPath)!, "launch-target-port"); Directory.CreateDirectory(port);
            foreach (var file in Directory.GetFiles(source).Where(f => Path.GetExtension(f) is ".exe" or ".dll" or ".json")) File.Copy(file, Path.Combine(port, Path.GetFileName(file)), true);
            File.WriteAllText(Path.Combine(port, "port-manifest.json"), "{\"workshopReload\":1,\"workshopLaunch\":1}");
            _settings.GameExePath = Path.Combine(port, "HotReload.exe");
            var publication = TestModPublisher.Publish(session.Snapshot(), new ModInfo { Id = "launch-target-test", Name = "Launch target test" }, Path.Combine(port, "published"));
            publicationDirectory = Path.GetDirectoryName(publication.RequestPath);
            await LaunchOrReloadTest(_settings.GameExePath, session, publication, false, true, target);
            var argumentsPath = Path.Combine(publicationDirectory!, "launch-args.json");
            for (int i = 0; i < 100 && !File.Exists(argumentsPath); i++) await Task.Delay(30);
            var arguments = JsonSerializer.Deserialize<string[]>(File.ReadAllText(argumentsPath))!;
            Check("native process receives selected target/team flags", arguments.Contains("exhibition") && arguments.Contains(target.Home) && arguments.Contains(target.Away));
            Check("native process receives the exact game and isolated user directories", arguments.Contains(session.GameRoot.DiskPath!) && arguments.Contains(Path.Combine(publicationDirectory!, "user")));
            Check("launch capabilities are explicitly gated", LaunchTargetsSupported(_settings.GameExePath));
            File.WriteAllText(Path.Combine(port, "port-manifest.json"), "{}"); Check("legacy port cannot silently accept target flags", !LaunchTargetsSupported(_settings.GameExePath));
            File.WriteAllText(Path.Combine(publicationDirectory!, "stop"), ""); await _testGameProcess!.WaitForExitAsync();
        }
        catch (Exception ex) { report.AppendLine("FAIL " + ex); }
        finally
        {
            if (publicationDirectory is not null) File.WriteAllText(Path.Combine(publicationDirectory, "stop"), "");
            _settings.GameExePath = previousExe; Editor.Session?.MarkSaved(); File.WriteAllText(reportPath, report.ToString()); Application.Current.Shutdown();
        }
    }
}
