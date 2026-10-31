using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using MVPWorkshop.Core.Editing;

namespace MVPWorkshop.App;

/// <summary>Runs the project lifecycle in the live WPF app with a disposable loose fixture.</summary>
public static class ProjectSelfTest
{
    [DllImport("user32.dll")] private static extern IntPtr GetLastActivePopup(IntPtr window);
    [DllImport("user32.dll")] private static extern bool PostMessage(IntPtr window, uint message, IntPtr wParam, IntPtr lParam);

    public static async void Run(MainWindow w, string reportPath)
    {
        var report = new StringBuilder();
        int failures = 0;
        void Check(string label, bool ok) { report.AppendLine($"{(ok ? "PASS" : "FAIL")} {label}"); if (!ok) failures++; }
        IEnumerable<T> Find<T>(DependencyObject parent) where T : DependencyObject
        {
            if (parent is T item) yield return item;
            for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
                foreach (var child in Find<T>(VisualTreeHelper.GetChild(parent, i))) yield return child;
        }
        void Answer(string label, Action action)
        {
            var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(100) };
            timer.Tick += (_, _) =>
            {
                var dialog = Application.Current.Windows.OfType<Window>().FirstOrDefault(x => x.Owner == w);
                if (dialog is null) return;
                var button = Find<Button>(dialog).First(b => Find<TextBlock>(b).Any(t => t.Text == label));
                timer.Stop(); button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            };
            timer.Start(); action(); timer.Stop();
        }
        var settingsPath = Settings.StoragePath;
        var settingsBytes = File.Exists(settingsPath) ? File.ReadAllBytes(settingsPath) : null;
        try
        {
            await Task.Delay(700);
            MainWindow.AutosaveRoot = Path.Combine(Path.GetDirectoryName(reportPath)!, "Autosave-" + Guid.NewGuid().ToString("N"));
            var s = Editor.Session!;
            var root = w.GameRoot!;
            var before = Directory.GetFiles(root.DiskPath!, "*", SearchOption.AllDirectories).ToDictionary(p => p, p => SHA256.HashData(File.ReadAllBytes(p)));
            var node = root.Resolve("sample.big/a.txt")!;
            Check("Opening game creates clean untitled project", s.ProjectPath is null && !s.IsDirty);
            s.CommitPreviewBytes(node, [9], "preview", "test");
            Check("Preview-only does not dirty or autosave", !s.IsDirty && w.AutosaveProject() is null);
            s.Commit(node, [1, 2, 3], "saved edit");
            s.AddFile("new.txt", [4, 5]);
            Check("Dirty title uses asterisk", w.Title.StartsWith("* ") && w.ProjectLabel.Text.EndsWith('*'));
            var timer = (DispatcherTimer)typeof(MainWindow).GetField("_autosaveTimer", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(w)!;
            timer.Interval = TimeSpan.FromMilliseconds(50);
            await Task.Delay(200); timer.Stop();
            Check("Dispatcher timer writes autosave", Directory.GetFiles(MainWindow.AutosaveRoot, "Project_*.mvpwproj", SearchOption.AllDirectories).Length > 0);
            string backup = "";
            for (int i = 0; i < 6; i++) { backup = w.AutosaveProject()!; await Task.Delay(30); }
            Check("Five rotating backups", Directory.GetFiles(MainWindow.AutosaveRoot, "Project_*.mvpwproj", SearchOption.AllDirectories).Length == 5);
            Check("Autosave preserves dirty and untitled state", s.IsDirty && s.ProjectPath is null && s.CanUndo);
            var loaded = ProjectFile.Load(backup, root, out var warnings);
            Check("Backup includes saved and Added, excludes previews", warnings.Count == 0 && loaded.Overrides[node.Key].SequenceEqual(new byte[] { 1, 2, 3 }) && loaded.Added["new.txt"].Length == 2 && loaded.PreviewOnlyCount == 0);
            // ProjectFile.Load installs its overlay; reattach the current editor session for the remaining checks.
            var project = Path.Combine(Path.GetDirectoryName(reportPath)!, "saved.mvpwproj");
            w.SaveProjectTo(project);
            Check("Explicit save marks clean and removes recovery notice", !s.IsDirty && !Directory.GetFiles(MainWindow.AutosaveRoot, "recovery.txt", SearchOption.AllDirectories).Any());
            Check("Recent projects persisted", Settings.Load().RecentProjects.Contains(project));
            typeof(MainWindow).GetMethod("RecentProjects_Opened", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(w, new object[] { w.RecentProjectsMenu, new RoutedEventArgs() });
            Check("File menu has recent project", w.RecentProjectsMenu.Items.OfType<MenuItem>().Any(i => (string)i.ToolTip == project));
            try { ProjectFile.Save(s, Path.Combine(root.DiskPath!, "blocked.mvpwproj")); Check("Project save rejects game folder", false); }
            catch (InvalidOperationException) { Check("Project save rejects game folder", true); }
            s.AddFile("another.txt", [6]);
            Answer("Cancel", () => w.LoadGame(root.DiskPath!));
            Check("Cancel game switch preserves session", ReferenceEquals(Editor.Session, s) && s.IsDirty);
            Answer("Cancel", () => w.LoadProject(project));
            Check("Cancel project switch preserves session", ReferenceEquals(Editor.Session, s));
            Answer("Cancel", () => w.Close());
            Check("Cancel close keeps window open", w.IsVisible);
            Answer("Save", () => Check("Save prompt continues", w.ConfirmDiscard("Test save")));
            Check("Save prompt writes project", !s.IsDirty && ProjectFile.Load(project, root, out _).Added.ContainsKey("another.txt"));
            s.AddFile("recover.txt", [7]);
            backup = w.AutosaveProject()!;
            Answer("Don't save", () => w.LoadProject(project));
            Check("Don't save opens clean project", Editor.Session is { IsDirty: false } && !Editor.Session.Added.ContainsKey("recover.txt"));
            // Simulate a previous process that exited without clearing its recovery notice.
            var marker = Path.Combine(Path.GetDirectoryName(backup)!, "recovery.txt");
            File.WriteAllText(marker, backup);
            var recoveryTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(100) };
            recoveryTimer.Tick += (_, _) => { var handle = GetLastActivePopup(new WindowInteropHelper(w).Handle); if (handle == new WindowInteropHelper(w).Handle) return; recoveryTimer.Stop(); PostMessage(handle, 0x111, (IntPtr)6, IntPtr.Zero); };
            recoveryTimer.Start();
            Check("Crash recovery offered and restored", w.OfferRecovery());
            recoveryTimer.Stop();
            Check("Recovery is dirty untitled with Added files", Editor.Session is { IsDirty: true, ProjectPath: null } recovered && recovered.Added.ContainsKey("recover.txt"));
            Check("Restored project retains crash recovery notice", File.Exists(marker));
            Check("Game files unchanged", before.Count == Directory.GetFiles(root.DiskPath!, "*", SearchOption.AllDirectories).Length && before.All(kv => SHA256.HashData(File.ReadAllBytes(kv.Key)).SequenceEqual(kv.Value)));
            w.Docs.SelectedIndex = 0;
            await Task.Delay(300); w.UpdateLayout();
            var bitmap = new RenderTargetBitmap((int)w.ActualWidth, (int)w.ActualHeight, 96, 96, PixelFormats.Pbgra32);
            bitmap.Render(w); var png = new PngBitmapEncoder(); png.Frames.Add(BitmapFrame.Create(bitmap));
            using (var file = File.Create(reportPath + ".png")) png.Save(file);
        }
        catch (Exception ex) { Check(ex.ToString(), false); }
        finally
        {
            report.AppendLine($"Failures: {failures}"); File.WriteAllText(reportPath, report.ToString());
            Editor.Session?.MarkSaved();
            Application.Current.Shutdown(failures == 0 ? 0 : 1);
            if (settingsBytes is not null) File.WriteAllBytes(settingsPath, settingsBytes);
        }
    }
}
