using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using MVPWorkshop.App.Viewers;
using MVPWorkshop.Core.Formats;

namespace MVPWorkshop.App;

public static class AptTextureSelfTest
{
    private static T? Find<T>(DependencyObject root, Func<T, bool>? match = null) where T : DependencyObject
    {
        if (root is T found && (match is null || match(found))) return found;
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
            if (Find(VisualTreeHelper.GetChild(root, i), match) is T child) return child;
        return null;
    }
    public static async void Run(MainWindow window, string reportPath)
    {
        var report = new StringBuilder(); int failures = 0;
        void Check(string name, bool ok) { report.AppendLine($"{(ok ? "PASS" : "FAIL")} {name}"); if (!ok) failures++; }
        try
        {
            var nodes = await window.GetAssetIndex();
            var movie = nodes.First(n => n.Path.Contains("/EASOAPT.BIG/") && n.Name == "stdPopup.apt");
            window.OpenAsset(movie); await Task.Delay(600);
            var document = window.Docs.Items.OfType<DocumentTab>().First(d => d.Node == movie);
            var apt = Find<AptView>((DependencyObject)document.Content)!;
            var tabs = Find<TabControl>(apt)!;
            var textureTab = tabs.Items.OfType<TabItem>().First(t => (string)t.Header == "Textures");
            tabs.SelectedItem = textureTab;
            AptTextureEditorView? editor = null;
            for (int i = 0; i < 120; i++)
            {
                await Task.Delay(100);
                editor = Find<AptTextureEditorView>(apt);
                if (editor?.TextureEditor is not null) break;
            }
            Check("APT embeds texture replacement editor", editor?.TextureEditor is not null);
            if (editor?.TextureEditor is null) throw new InvalidOperationException("No embedded texture editor");
            var paired = AptView.Sibling(movie, ".ssh");
            Check("paired SSH selected when present; import-only movies supported", paired is null ? editor.TextureSource is not null : editor.TextureSource == paired);
            Check("resolved imported texture sources available", editor.PackCount > 1);
            var picker = Find<ComboBox>(editor, c => c.Name == "AptTextureSources")!;
            if (editor.PackCount > 1) picker.SelectedIndex = 1;
            var source = editor.TextureSource!;
            report.AppendLine("Movie: " + movie.Path); report.AppendLine("Edited pack: " + source.Path);
            var original = source.ReadAll(); var file = SshFile.Read(original);
            var image = file.Images.First(i => SshWriter.Unsupported(file, i) is null);
            editor.TextureEditor!.SelectedImageIndex = image.Index;
            var pixels = new byte[image.Header.Width * image.Header.Height * 4];
            for (int i = 0; i < pixels.Length; i += 4) { pixels[i] = 40; pixels[i + 1] = 80; pixels[i + 2] = 120; pixels[i + 3] = 255; }
            var oldStage = apt.StagePreview;
            editor.TextureEditor.ReplaceWith(image.Index, pixels, "APT editor validation"); await Task.Delay(800);
            var edited = source.ReadAll(); var changed = SshFile.Read(edited);
            Check("imported image replacement saved through project history", Editor.Session!.IsModified(source) && changed.Decode(changed.Images[image.Index]).Bgra.SequenceEqual(pixels));
            Check("APT stage refreshed for imported texture edit", !ReferenceEquals(oldStage, apt.StagePreview));
            Check("texture source and image selection retained after refresh", editor.TextureSource == source && editor.TextureEditor.SelectedImageIndex == image.Index);
            Check("unselected images unchanged", changed.Images.Where(i => i.Index != image.Index).All(i => changed.Decode(i).Bgra.SequenceEqual(file.Decode(file.Images[i.Index]).Bgra)));
            Editor.Session.Undo(); await Task.Delay(600);
            Check("texture undo restores original pack", source.ReadAll().SequenceEqual(original));
            Editor.Session.Redo(); await Task.Delay(600);
            Check("texture redo restores edited pack", source.ReadAll().SequenceEqual(edited));
            if (AptView.Sibling(source, ".apt") is { } localMovie)
            {
                window.OpenAsset(localMovie); await Task.Delay(500);
                var localDoc = window.Docs.Items.OfType<DocumentTab>().First(d => d.Node == localMovie);
                var localView = Find<AptView>((DependencyObject)localDoc.Content)!;
                var localTabs = Find<TabControl>(localView)!;
                localTabs.SelectedItem = localTabs.Items.OfType<TabItem>().First(t => (string)t.Header == "Textures");
                AptTextureEditorView? localEditor = null;
                for (int i = 0; i < 100; i++) { await Task.Delay(100); localEditor = Find<AptTextureEditorView>(localView); if (localEditor?.TextureEditor is not null) break; }
                Check("local paired pack opens inside its APT editor", localEditor?.TextureSource == source);
            }
            else Check("local paired movie found for source", false);
            window.UpdateLayout();
            var bitmap = new RenderTargetBitmap((int)window.ActualWidth, (int)window.ActualHeight, 96, 96, PixelFormats.Pbgra32); bitmap.Render(window);
            var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap)); using (var stream = File.Create(reportPath + ".png")) encoder.Save(stream);
            Editor.Session.Undo();
        }
        catch (Exception ex) { failures++; report.AppendLine("FAIL " + ex); }
        report.AppendLine($"Failures: {failures}"); File.WriteAllText(reportPath, report.ToString()); Application.Current.Shutdown(failures == 0 ? 0 : 1);
    }
}
