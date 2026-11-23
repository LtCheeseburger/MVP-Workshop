using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Media.Media3D;
using MVPWorkshop.App.Viewers;

namespace MVPWorkshop.App;

public partial class MainWindow
{
    internal async void RunNascarReleaseCheck(string reportPath)
    {
        var report = new StringBuilder();
        void Check(string label, bool ok) => report.AppendLine($"{(ok ? "PASS" : "FAIL")} {label}");
        string output = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(reportPath))!, "scene-export-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(output);
        try
        {
            var mesh = new MeshGeometry3D { Positions = [new(0, 0, 0), new(1, 0, 0), new(0, 1, 0)], Normals = [new(0, 0, 1), new(0, 0, 1), new(0, 0, 1)], TextureCoordinates = [new(0, 0), new(1, 0), new(0, 1)], TriangleIndices = [0, 1, 2] };
            var bitmap = BitmapSource.Create(1, 1, 96, 96, PixelFormats.Bgra32, null, new byte[] { 20, 40, 80, 128 }, 4);
            var material = new DiffuseMaterial(new ImageBrush(bitmap));
            var scene = new Model3DGroup { Transform = new TranslateTransform3D(10, 0, 0) };
            scene.Children.Add(new GeometryModel3D(mesh, material));
            scene.Children.Add(new GeometryModel3D(mesh, material) { Transform = new ScaleTransform3D(-1, 2, 1) });
            string path = Path.Combine(output, "assembled.obj");
            Check("scene exporter deduplicates shared texture materials", WavefrontSceneExport.Write(scene, path) == 1);
            var lines = File.ReadAllLines(path);
            Check("scene exporter includes all transformed instances", lines.Count(l => l.StartsWith("v ")) == 6 && lines.Contains("v 10 0 0") && lines.Contains("v 9 0 0"));
            Check("mirrored instances reverse triangle winding", lines.Contains("f 4/4/4 6/6/6 5/5/5"));
            Check("OBJ exports texture coordinates and normals", lines.Count(l => l.StartsWith("vt ")) == 6 && lines.Count(l => l.StartsWith("vn ")) == 6);
            string mtl = Path.Combine(output, lines.Single(l => l.StartsWith("mtllib "))[7..].Replace('/', Path.DirectorySeparatorChar));
            string png = Path.Combine(Path.GetDirectoryName(mtl)!, "material_0000.png");
            var decoded = new PngBitmapDecoder(new Uri(png), BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
            var pixels = new byte[4]; new FormatConvertedBitmap(decoded.Frames[0], PixelFormats.Bgra32, null, 0).CopyPixels(pixels, 4, 0);
            Check("exported material references an existing PNG with preserved alpha", File.ReadAllText(mtl).Contains("map_Kd material_0000.png") && pixels.SequenceEqual(new byte[] { 20, 40, 80, 128 }));
            string original = File.ReadAllText(path); int directories = Directory.GetDirectories(output).Length;
            scene.Transform = new ScaleTransform3D(0, 1, 1);
            try { WavefrontSceneExport.Write(scene, path); Check("singular export rejected", false); }
            catch (InvalidDataException) { Check("failed export preserves existing OBJ and removes partial resources", File.ReadAllText(path) == original && Directory.GetDirectories(output).Length == directories && !Directory.EnumerateFiles(output, "*.tmp").Any()); }
            if (GameRoot is not null)
            {
                foreach (var key in new[] { "CARDATA.DAT/00203.bin", "TK_REND.DAT/00000.bin", "CHARDAT.DAT/00678.bin" })
                {
                    var node = GameRoot.Resolve(key)!;
                    var selected = node.Format == MVPWorkshop.Core.Formats.FormatId.MmapTexture ? node : null;
                    var model = selected is null ? node : MVPWorkshop.Core.Vfs.NascarModelResources.ModelsFor(node).First().Node;
                    var view = new TiburonModelView(model, selected);
                    string target = Path.Combine(output, key.Replace('/', '-').Replace('.', '-') + ".obj");
                    int count = view.ExportScene(target);
                    Check(key + " assembled scene exports with materials", count > 0 && new FileInfo(target).Length > 1000);
                }
                var textureNode = GameRoot.Resolve("CHARDAT.DAT/00680.bin")!;
                byte[] originalBytes = textureNode.ReadAll();
                OpenAsset(textureNode); await Task.Delay(200); UpdateLayout();
                try
                {
                    Editor.Apply(textureNode, new byte[40], "Release test invalid texture preview");
                    await Task.Delay(100); UpdateLayout();
                    Check("malformed texture refresh stays inside its viewer", Find<NascarAssetView>(this) is not null && Log.Lines.Any(l => l.Text.Contains("NASCAR preview refresh:")));
                }
                finally { Editor.Session!.Undo(); }
                await Task.Delay(100); UpdateLayout();
                Check("undo restores the textured model after a preview error", textureNode.ReadAll().SequenceEqual(originalBytes) && Find<TiburonModelView>(this) is not null);
                var trackNode = GameRoot.Resolve("TK_REND.DAT/00000.bin")!;
                byte[] originalTrack = trackNode.ReadAll();
                OpenAsset(trackNode); await Task.Delay(150); UpdateLayout();
                var trackTabs = Find<EmbeddedModelView>(this) ?? throw new InvalidOperationException("Track 3D/2D tabs did not open.");
                trackTabs.SelectedIndex = 1; await Task.Delay(100); UpdateLayout();
                Check("track 2D tab exposes embedded textures", Find<MmapView>(trackTabs) is not null);
                var section = MVPWorkshop.Core.Formats.TiburonModel.Read(new MVPWorkshop.Core.IO.MemoryByteSource(originalTrack)).Single(s => s.Tag == "Text");
                var pack = MVPWorkshop.Core.Formats.MmapTexture.Read(originalTrack.AsSpan((int)section.Offset, (int)section.Length).ToArray());
                var firstImage = pack.Images.First(i => i.Mip == 0); var flat = pack.Decode(firstImage);
                flat.Bgra[0] ^= 255;
                var replacement = originalTrack.ToArray(); pack.ReplaceFile(firstImage.Index, flat.Bgra, flat.Width, flat.Height).CopyTo(replacement, (int)section.Offset);
                try
                {
                    Editor.Apply(trackNode, replacement, "Release test embedded track texture");
                    trackTabs.SelectedIndex = 0; await Task.Delay(100); UpdateLayout();
                    Check("embedded track texture edits rebuild the 3D scene", Find<TiburonModelView>(trackTabs) is not null && trackNode.ReadAll().SequenceEqual(replacement));
                }
                finally { Editor.Session!.Undo(); }
                Check("embedded track texture undo restores original model bytes", trackNode.ReadAll().SequenceEqual(originalTrack));
                Editor.Session!.MarkSaved();
            }
        }
        catch (Exception ex) { report.AppendLine("FAIL " + ex); }
        File.WriteAllText(reportPath, report.ToString());
        Application.Current.Shutdown(report.ToString().Contains("FAIL") ? 1 : 0);
    }
}
