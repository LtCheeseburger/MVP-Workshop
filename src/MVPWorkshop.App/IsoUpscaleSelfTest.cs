using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using MVPWorkshop.App.Viewers;
using MVPWorkshop.Core.Formats;
using MVPWorkshop.Core.Editing;
using MVPWorkshop.Core.Vfs;

namespace MVPWorkshop.App;

public static class IsoUpscaleSelfTest
{
    private static T? Find<T>(DependencyObject root) where T : DependencyObject
    {
        if (root is T found) return found;
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++) if (Find<T>(VisualTreeHelper.GetChild(root, i)) is { } child) return child;
        return null;
    }
    public static async void Run(MainWindow window, string reportPath)
    {
        var report = new StringBuilder(); int failures = 0;
        void Check(string name, bool ok) { report.AppendLine($"{(ok ? "PASS" : "FAIL")} {name}"); if (!ok) failures++; }
        try
        {
            var root = window.GameRoot ?? throw new InvalidOperationException("Use --game <ISO fixture>");
            Check("ISO source loaded in app", root.DiscImage is not null);
            var nodes = await window.GetAssetIndex(); var node = nodes.First(n => n.IsTexturePack);
            window.OpenAsset(node); await Task.Delay(500);
            var doc = window.Docs.Items.OfType<DocumentTab>().First(d => d.Node == node);
            var view = Find<TextureView>((DependencyObject)doc.Content)!;
            Check("ISO texture editable through existing viewer", view is not null && Editor.CapabilityOf(node).Level == EditLevel.Savable);
            var file = SshFile.Read(node.ReadAll()); var image = file.Images.First(i => SshWriter.ResizeUnsupported(file, i, i.Header.Width * 2, i.Header.Height * 2) is null);
            var decoded = file.Decode(image); var pixels = ImageOps.Resize(decoded.Bgra, decoded.Width, decoded.Height, decoded.Width * 2, decoded.Height * 2, ResizeFilter.Nearest);
            for (int i = 0; i < pixels.Length; i += 4) { pixels[i] = 40; pixels[i + 1] = 80; pixels[i + 2] = 120; pixels[i + 3] = 255; }
            var review = new TextureImportReview(file, image, pixels, decoded.Width * 2, decoded.Height * 2, PaletteMode.Rebuild, null) { Owner = window };
            review.Show(); await review.PreviewAsync(); await Task.Delay(200);
            Check("native upscale review retains dimensions and displays encoded preview", review.KeepImportedSize && review.PreviewReady && review.PreviewStatus.Contains($"{decoded.Width * 2}×{decoded.Height * 2}"));
            review.UpdateLayout();
            var reviewBitmap = new RenderTargetBitmap((int)review.ActualWidth, (int)review.ActualHeight, 96, 96, PixelFormats.Pbgra32); reviewBitmap.Render(review);
            var reviewEncoder = new PngBitmapEncoder(); reviewEncoder.Frames.Add(BitmapFrame.Create(reviewBitmap)); using (var stream = File.Create(reportPath + ".review.png")) reviewEncoder.Save(stream);
            review.Close();
            view!.ReplaceWith(image.Index, pixels, "native upscale selftest", decoded.Width * 2, decoded.Height * 2); await Task.Delay(500);
            Check("native upscale retained in project", SshFile.Read(node.ReadAll()).Images[image.Index].Header.Width == decoded.Width * 2 && Editor.Session!.IsModified(node));
            Editor.Session!.Undo(); await Task.Delay(100); Check("native upscale undo restores dimensions", SshFile.Read(node.ReadAll()).Images[image.Index].Header.Width == decoded.Width);
            Editor.Session.Redo();
            var output = reportPath + "." + Guid.NewGuid().ToString("N") + ".iso";
            try
            {
                await Task.Run(() => IsoModExporter.Export(Editor.Session!, output));
                var exported = GameFileSystem.OpenIso(output).EnumerateDeep().First(n => n.IsTexturePack);
                Check("app upscale exports at higher resolution into ISO", SshFile.Read(exported.ReadAll()).Images[image.Index].Header.Width == decoded.Width * 2);
            }
            finally { if (File.Exists(output)) File.Delete(output); }
            window.UpdateLayout(); var bitmap = new RenderTargetBitmap((int)window.ActualWidth, (int)window.ActualHeight, 96, 96, PixelFormats.Pbgra32); bitmap.Render(window);
            var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap)); using (var stream = File.Create(reportPath + ".png")) encoder.Save(stream);
            Editor.Session.Undo();
        }
        catch (Exception ex) { failures++; report.AppendLine("FAIL " + ex); }
        report.AppendLine($"Failures: {failures}"); File.WriteAllText(reportPath, report.ToString()); Application.Current.Shutdown(failures == 0 ? 0 : 1);
    }
}
