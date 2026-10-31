using System.IO;
using System.Security.Cryptography;
using System.Windows;
using MVPWorkshop.Core.Editing;
using Microsoft.Win32;

namespace MVPWorkshop.App;

public partial class MainWindow
{
    private void OpenMod_Click(object sender, RoutedEventArgs e)
    {
        if (Editor.Session is not { } current || MvpModExporter.SessionProblem(current) is { })
        { MessageBox.Show(this, "Open the port's loose game installation first.", "Open mod"); return; }
        var picker = new OpenFileDialog { Filter = "MVP mod|*.mvpmod", Title = "Open mod for editing" };
        if (picker.ShowDialog(this) == true) OpenMod(picker.FileName);
    }

    internal bool OpenMod(string path)
    {
        try
        {
            var current = Editor.Session ?? throw new InvalidOperationException("Open a loose installation first.");
            var package = MvpModImporter.Read(path);
            var nextVersion = MvpModImporter.NextVersion(package.Info.Version);
            if (!package.MatchesGame(current.GameRoot) && MessageBox.Show(this, "This mod was built for a different default.xbe (SHA-1 mismatch). Its files may be incompatible with this installation. Open it anyway?", "Mod compatibility", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return false;
            // Keep the icon in durable application storage so future exports and project reloads retain it.
            if (package.Icon is { } icon)
            {
                var folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "MVP-Workshop", "ModIcons");
                Directory.CreateDirectory(folder);
                var iconPath = Path.Combine(folder, Convert.ToHexString(SHA256.HashData(icon)) + ".png");
                File.WriteAllBytes(iconPath, icon);
                if (ModInfo.IconProblem(iconPath) is { } problem) throw new InvalidDataException(problem);
                package.Info.IconPath = iconPath;
            }
            if (!ConfirmDiscard("Open another mod")) return false;
            var imported = MvpModImporter.Open(package, current.GameRoot, current.OutputFolder, current.BaseLayer);
            package.Info.Version = nextVersion;
            foreach (var tab in _docs.Where(d => d.CanClose).ToList()) CloseDocument(tab);
            _settings.ModInfos[current.GameRoot.DiskPath!] = package.Info;
            _settings.ModPaths[current.GameRoot.DiskPath!] = Path.GetFullPath(path);
            _settings.Save();
            Editor.Attach(imported);
            ShowChanges_Click(this, new RoutedEventArgs());
            Log.Info($"Opened {package.Info.Name} for editing; next export version {package.Info.Version}.");
            SetStatus($"Opened mod: {package.Info.Name}");
            return true;
        }
        catch (Exception ex) { Log.Error("Open mod: " + ex.Message); MessageBox.Show(this, ex.Message, "Open mod", MessageBoxButton.OK, MessageBoxImage.Error); return false; }
    }
}
