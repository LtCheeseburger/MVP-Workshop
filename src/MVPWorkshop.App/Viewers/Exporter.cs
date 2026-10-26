using System.IO;
using System.Windows;
using System.Windows.Input;
using MVPWorkshop.Core.Vfs;
using Microsoft.Win32;

namespace MVPWorkshop.App.Viewers;

/// <summary>Writes copies of game data to a user-chosen location. Never writes into the game folder itself.</summary>
public static class Exporter
{
    public static string? AskSave(string fileName, string filter)
    {
        var dlg = new SaveFileDialog { FileName = fileName, Filter = filter };
        return dlg.ShowDialog(Application.Current.MainWindow) == true ? dlg.FileName : null;
    }

    public static void SaveBytes(byte[] data, string fileName, string filter = "All files|*.*")
    {
        var path = AskSave(fileName, filter);
        if (path is null) return;
        File.WriteAllBytes(path, data);
        Log.Info($"Exported {Path.GetFileName(path)} ({data.Length:N0} bytes) → {path}");
    }

    public static void SaveRaw(AssetNode n)
    {
        try { SaveBytes(n.ReadAll(), n.Name); }
        catch (Exception ex) { Log.Error($"Export failed: {ex.Message}"); }
    }

    public static async void ExtractTree(AssetNode n)
    {
        var dlg = new OpenFolderDialog { Title = $"Extract “{n.Name}” into…" };
        if (dlg.ShowDialog(Application.Current.MainWindow) != true) return;
        var target = Path.Combine(dlg.FolderName, n.Name + (n.IsFolder ? "" : "_extracted"));
        Mouse.OverrideCursor = Cursors.AppStarting;
        try
        {
            var count = await Task.Run(() =>
            {
                var c = 0;
                foreach (var x in n.EnumerateDeep())
                {
                    if (x == n || x.IsFolder) continue;
                    var rel = x.Path[(n.Path.Length + 1)..];
                    var outPath = Path.Combine(target, string.Concat(rel.Select(ch => ch is ':' or '*' or '?' or '"' or '<' or '>' or '|' ? '_' : ch)));
                    // A container file and its unpacked contents can't share a path; keep the file as "<name>.bin".
                    if (x.IsContainer) outPath += ".raw";
                    Directory.CreateDirectory(Path.GetDirectoryName(outPath)!);
                    File.WriteAllBytes(outPath, x.ReadAll());
                    x.DropCache();
                    c++;
                }
                return c;
            });
            Log.Info($"Extracted {count:N0} files from {n.Path} → {target}");
        }
        catch (Exception ex) { Log.Error($"Extract failed: {ex.Message}"); }
        finally { Mouse.OverrideCursor = null; }
    }
}
