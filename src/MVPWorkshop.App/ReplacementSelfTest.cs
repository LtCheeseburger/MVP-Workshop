using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using MVPWorkshop.App.Viewers;
using MVPWorkshop.Core.Editing;
using MVPWorkshop.Core.Formats;
using MVPWorkshop.Core.Vfs;

namespace MVPWorkshop.App;

public partial class MainWindow
{
    internal async void RunReplacementCheck(string reportPath)
    {
        var report = new StringBuilder();
        void Check(string label, bool value) => report.AppendLine($"{(value ? "PASS" : "FAIL")} {label}");
        try
        {
            var session = Editor.Session!; var root = session.GameRoot;
            var source = root.EnumerateDeep().Single(n => n.Name == "u000a.xsh");
            await GetSchoolProfiles();
            _diagnosticTimer.Stop(); CancelDependencies(); _dependencyTask = null;
            string action = "ReplacementSchoolOnly";
            var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(40) };
            timer.Tick += (_, _) =>
            {
                var dialog = OwnedWindows.OfType<Window>().FirstOrDefault(w => w.Title == "Replacement impact");
                if (dialog?.Content is not ScrollViewer { Content: StackPanel panel }) return;
                var button = panel.Children.OfType<Button>().First(b => Equals(b.Tag, action));
                Check("review offers a school-only fork", button.IsEnabled);
                button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            };
            timer.Start(); MainWindow.ReplacementDecision? decision;
            try { decision = await PrepareReplacement(source); } finally { timer.Stop(); }
            Check("review returns a concrete school family plan", decision?.Fork is { Copies.Count: 3 });
            Check("texture replacement review does not request full dependency scan", _dependencyTask is null);
            var ssh = SshFile.Read(source.ReadAll()); var pixels = ssh.Decode(ssh.Images[0]); pixels.Bgra[0] ^= 127;
            var bytes = SshWriter.ReplaceImage(ssh, ssh.Images[0], pixels.Bgra, pixels.Width, pixels.Height).File;
            decision!.Apply(bytes, "UI school-only replacement");
            Check("school-only replacement opens edited copy instead of unchanged source", Docs.SelectedItem is DocumentTab { Node: { } shown } && session.AddedPathOf(shown) is not null && shown.ReadAll().SequenceEqual(bytes));
            Check("school-only application adds family without replacing source", session.Added.Count == 3 && !session.IsModified(source));
            var project = Path.Combine(Path.GetDirectoryName(reportPath)!, "school-fork.mvpwork"); ProjectFile.Save(session, project);
            var loaded = ProjectFile.Load(project, root, out var warnings);
            var replacementPath = decision.Fork!.Copies.Single(c => c.Source.Key == source.Key).Destination;
            Check("project reload retains exact replaced texture bytes", loaded.Added[replacementPath].SequenceEqual(bytes));
            Check("project reload restores added school asset resolution", warnings.Count == 0 && loaded.Added.Count == 3 && TeamAssetCatalog.Build(root.EnumerateDeep().ToList()).Any(p => p.Team.UniformId == decision.Fork!.Family && p.Assets.Any(a => a.Asset.Name.StartsWith("u" + decision.Fork.Family.ToString("D3")))));
            Editor.Attach(session); // Restore the active overlay as well as the editor session.
            session.SetBaseLayer(session.BaseLayer);
            var package = MvpModExporter.Export(loaded, new ModInfo { Id = "school-fork-test", Name = "School fork test" }, Path.Combine(Path.GetDirectoryName(reportPath)!, "school-fork.mvpmod"), false);
            Check("school fork exports complete texture family and roster", package.Ok && package.Files.Count > 3);
            session.Undo(); Check("single undo removes the whole UI fork", session.Added.Count == 0 && session.Overrides.Count == 0);
            action = "ReplacementEveryone"; timer.Start();
            try { decision = await PrepareReplacement(source); } finally { timer.Stop(); }
            decision!.Apply(bytes, "UI shared replacement");
            Check("everyone replacement edits the source without additions", session.IsModified(source) && session.Added.Count == 0);
            session.Undo();
        }
        catch (Exception ex) { report.AppendLine("FAIL " + ex); }
        File.WriteAllText(reportPath, report.ToString()); Application.Current.Shutdown();
    }
}
