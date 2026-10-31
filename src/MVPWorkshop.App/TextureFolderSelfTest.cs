using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using MVPWorkshop.App.Viewers;
using MVPWorkshop.Core.Editing;
using MVPWorkshop.Core.Formats;
using MVPWorkshop.Core.IO;
using MVPWorkshop.Core.Vfs;

namespace MVPWorkshop.App;

public partial class MainWindow
{
    internal async void RunTextureFolderCheck(string reportPath)
    {
        var report = new StringBuilder();
        void Check(string label, bool value) => report.AppendLine($"{(value ? "PASS" : "FAIL")} {label}");
        IEnumerable<TextBlock> Labels(DependencyObject parent)
        {
            if (parent is TextBlock text) yield return text;
            for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++) foreach (var child in Labels(VisualTreeHelper.GetChild(parent, i))) yield return child;
        }
        try
        {
            await Task.Delay(300);
            var node = GameRoot!.Resolve("sample.big/logo0.xsh")!;
            var sourceHashes = Directory.GetFiles(GameRoot.DiskPath!, "*", SearchOption.AllDirectories).ToDictionary(p => p, p => SHA256.HashData(File.ReadAllBytes(p)));
            OpenAsset(node); UpdateLayout();
            Check("texture editor exposes Export pack as PNGs", Labels(((DocumentTab)Docs.SelectedItem).Content).Any(t => t.Text == "Export pack as PNGs"));
            var pack = SshFile.Read(node.ReadAll());
            var pixels = new byte[pack.Images[0].Header.Width * pack.Images[0].Header.Height * 4]; Array.Fill<byte>(pixels, 255);
            var edited = SshWriter.ReplaceImage(pack, pack.Images[0], pixels, pack.Images[0].Header.Width, pack.Images[0].Header.Height).File;
            Editor.Session!.Commit(node, edited, "PNG folder test edit");
            var exportRoot = Path.Combine(Path.GetDirectoryName(reportPath)!, "png-export-" + Guid.NewGuid().ToString("N"));
            var folder = Path.Combine(exportRoot, "pack-pngs");
            var slots = await TexturePackExport.ExportPackTo(node, folder);
            var current = SshFile.Read(node.ReadAll());
            var plan = TextureFolderImport.Plan(current, folder, TextureView.LoadPicture, image => SshWriter.Unsupported(current, image));
            var imported = TextureFolderImport.Apply(current.Data, plan.Rows, TextureView.LoadPicture, PaletteMode.Rebuild);
            Check("PNG export includes project edits and re-imports through the real image loader", slots.Count == current.Images.Count && plan.Rows.All(r => r.Replaces) && imported.SequenceEqual(edited));
            var historyCount = Editor.Session.History.Count;
            Check("unchanged folder import does not add an undo step", !TextureView.ApplyFolderResult(node, current.Data, imported, "unchanged import") && Editor.Session.History.Count == historyCount);
            bool concurrentBlocked = false;
            try { TextureView.ApplyFolderResult(node, new byte[] { 0 }, imported, "stale import"); } catch (InvalidOperationException) { concurrentBlocked = true; }
            Check("stale import cannot overwrite a newer texture", concurrentBlocked && node.ReadAll().SequenceEqual(edited));
            bool blocked = false;
            try { await TexturePackExport.ExportPackTo(node, Path.Combine(GameRoot.DiskPath!, "pngs")); } catch (InvalidOperationException) { blocked = true; }
            Check("PNG export rejects the game directory", blocked);
            var profile = new TeamProfile { Team = new GameTeam(0, 4, "Test", "Test school", "TST", "City", "Name", 0, 0, 0, 0, 0), Roster = GameRoot.Resolve("sample.big/a.txt")! };
            profile.Assets.Add(new("Uniforms/Player textures", node, "fixture"));
            var font = new AssetNode("f001h.xsh", node.Parent, false, edited.Length, () => new MemoryByteSource(edited), "fixture");
            profile.Assets.Add(new("Uniforms/Menu artwork", font, "fixture"));
            var schoolFolder = Path.Combine(exportRoot, "school-pngs");
            var total = await TexturePackExport.ExportSchoolTo(profile, schoolFolder);
            Check("school uniform set creates a separate re-import folder for each pack", total == slots.Count * 2 && Directory.GetDirectories(schoolFolder).Length == 2 && Directory.GetFiles(schoolFolder, "*.png", SearchOption.AllDirectories).Length == total);
            Check("PNG exports do not change source game files", sourceHashes.All(p => SHA256.HashData(File.ReadAllBytes(p.Key)).SequenceEqual(p.Value)) && Directory.GetFiles(GameRoot.DiskPath!, "*", SearchOption.AllDirectories).Length == sourceHashes.Count);
            UpdateLayout();
            var image = new RenderTargetBitmap((int)ActualWidth, (int)ActualHeight, 96, 96, PixelFormats.Pbgra32); image.Render(this);
            var png = new PngBitmapEncoder(); png.Frames.Add(BitmapFrame.Create(image));
            using var stream = File.Create(reportPath + ".png"); png.Save(stream);
        }
        catch (Exception ex) { report.AppendLine("FAIL " + ex); }
        finally { Editor.Session?.MarkSaved(); File.WriteAllText(reportPath, report.ToString()); Application.Current.Shutdown(); }
    }
}
