using System.IO;
using System.Text;
using System.Windows;
using MVPWorkshop.Core.Editing;

namespace MVPWorkshop.App;

public partial class MainWindow
{
    internal async void RunOpenModCheck(string reportPath)
    {
        var report = new StringBuilder();
        void Check(string label, bool value) => report.AppendLine($"{(value ? "PASS" : "FAIL")} {label}");
        var settingsPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "MVP-Workshop", "settings.json");
        var previous = File.Exists(settingsPath) ? File.ReadAllBytes(settingsPath) : null;
        try
        {
            await Task.Delay(300);
            var session = Editor.Session!;
            var node = GameRoot!.Resolve("sample.big/a.txt")!;
            session.Commit(node, [7, 6, 5, 4], "mod fixture");
            session.AddFile("new-mod.txt", [1, 2]);
            var path = Path.Combine(Path.GetDirectoryName(reportPath)!, "open-mod.mvpmod");
            Check("fixture exports", MvpModExporter.Export(session, new ModInfo { Id = "open-test", Name = "Open test", Version = "2.3.4" }, path, false).Ok);
            session.MarkSaved();
            Check("File Open mod applies package", OpenMod(path));
            var reopened = Editor.Session!;
            Check("replaces project and restores overrides", reopened != session && reopened.Overrides[node.Key].SequenceEqual(new byte[] { 7, 6, 5, 4 }));
            Check("new paths become Added", reopened.Added["new-mod.txt"].SequenceEqual(new byte[] { 1, 2 }));
            var info = _settings.ModInfos[GameRoot.DiskPath!];
            Check("saved metadata keeps id and name and increments version", info.Id == "open-test" && info.Name == "Open test" && info.Version == "2.3.5");
            Check("opened mod is dirty untitled project", reopened.ProjectPath is null && reopened.IsDirty && Title.StartsWith("* "));
            Check("Changes tab opens", _docs.Any(t => t.Title == "Changes"));
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
