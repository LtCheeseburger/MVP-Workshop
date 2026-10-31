using System.IO;
using System.Windows;
using MVPWorkshop.Core.Formats;
using MVPWorkshop.Core.Vfs;
using Microsoft.Win32;

namespace MVPWorkshop.App.Viewers;

public static class TexturePackExport
{
    public static async void ExportPack(AssetNode node)
    {
        var picker = new OpenFolderDialog { Title = $"Export {node.Name} as slot-named PNGs into…" };
        if (picker.ShowDialog(Application.Current.MainWindow) != true) return;
        try
        {
            var rows = await ExportPackTo(node, picker.FolderName);
            Log.Info($"Exported {rows.Count} texture slots from {node.Path} to {picker.FolderName}. Import folder reads these names unchanged.");
        }
        catch (Exception ex) { MessageBox.Show(Application.Current.MainWindow, ex.Message, "Export pack as PNGs", MessageBoxButton.OK, MessageBoxImage.Warning); }
    }

    internal static Task<IReadOnlyList<ExportedTextureSlot>> ExportPackTo(AssetNode node, string folder)
    {
        Exporter.CheckOutput(folder, node.Root);
        var bytes = node.ReadAll().ToArray();
        return Task.Run(() => TextureFolderExport.Export(bytes, folder));
    }

    public static async void ExportSchool(TeamProfile profile)
    {
        var packs = profile.Assets.Where(a => a.Category.StartsWith("Uniforms") && a.Asset.IsTexturePack).Select(a => a.Asset).DistinctBy(n => n.Key).ToList();
        if (packs.Count == 0) { MessageBox.Show("No uniform texture packs are linked to this school.", "Export uniform set"); return; }
        var picker = new OpenFolderDialog { Title = $"Export all {profile.Label} uniform packs into…" };
        if (picker.ShowDialog(Application.Current.MainWindow) != true) return;
        try
        {
            var total = await ExportSchoolTo(profile, picker.FolderName);
            Log.Info($"Exported {total} uniform slots for {profile.Label}. Use Import folder on each matching pack subfolder.");
        }
        catch (Exception ex) { MessageBox.Show(Application.Current.MainWindow, ex.Message, "Export uniform set", MessageBoxButton.OK, MessageBoxImage.Warning); }
    }

    internal static async Task<int> ExportSchoolTo(TeamProfile profile, string folder)
    {
        Exporter.CheckOutput(folder, profile.Roster.Root);
        var exports = new List<(byte[] Data, string Folder)>();
        var packs = profile.Assets.Where(a => a.Category.StartsWith("Uniforms") && a.Asset.IsTexturePack).Select(a => a.Asset).DistinctBy(n => n.Key).ToList();
        foreach (var group in packs.GroupBy(n => Path.GetFileNameWithoutExtension(n.Name), StringComparer.OrdinalIgnoreCase))
        {
            int index = 0;
            foreach (var pack in group)
            {
                var safe = new string(group.Key.Select(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' ? c : '_').ToArray());
                var name = $"pack-{safe}{(group.Count() > 1 ? "-" + index++ : "")}";
                var destination = Path.Combine(folder, name);
                if (Directory.Exists(destination) && Directory.EnumerateFileSystemEntries(destination).Any()) throw new IOException($"{name} is not empty. Choose an empty export folder.");
                exports.Add((pack.ReadAll().ToArray(), destination));
            }
        }
        return await Task.Run(() => exports.Sum(export => TextureFolderExport.Export(export.Data, export.Folder).Count));
    }
}
