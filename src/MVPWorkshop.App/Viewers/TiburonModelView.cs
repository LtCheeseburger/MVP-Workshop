using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Media3D;
using System.Windows.Media.Imaging;
using MVPWorkshop.Core.IO;
using Microsoft.Win32;
using MVPWorkshop.Core.Formats;
using MVPWorkshop.Core.Vfs;

namespace MVPWorkshop.App.Viewers;

public sealed class TiburonModelView : DockPanel
{
    private readonly Model3DGroup scene = new();
    private readonly List<(TiburonModel.Part Part, ComboBox Choice)> partSelectors = [];
    internal int ExportScene(string path) => WavefrontSceneExport.Write(scene, path);
    private readonly PerspectiveCamera camera = new() { FieldOfView = 45, NearPlaneDistance = 0.01, FarPlaneDistance = 1000000 };
    private Point3D target;
    private double yaw = 0.7, pitch = 0.25, distance = 10;
    private Point last;
    internal AssetNode ModelNode { get; }
    internal void CopyCamera(TiburonModelView previous)
    {
        yaw = previous.yaw; pitch = previous.pitch; distance = previous.distance; target = previous.target;
        foreach (var selector in partSelectors)
        {
            var old = previous.partSelectors.FirstOrDefault(s => s.Part.Name == selector.Part.Name);
            if (old.Choice is not null && old.Choice.SelectedIndex >= 0 && old.Choice.SelectedIndex < selector.Choice.Items.Count) selector.Choice.SelectedIndex = old.Choice.SelectedIndex;
        }
        UpdateCamera();
    }

    public TiburonModelView(AssetNode node, AssetNode? selectedTexture = null)
    {
        ModelNode = node;
        var data = node.ReadAll();
        var definition = NascarModelResources.IsRelevant(node) ? NascarModelResources.Get(node).Models.FirstOrDefault(m => ReferenceEquals(m.Node, node)) : null;
        var vehicle = NascarModelResources.VehicleFor(selectedTexture);
        string? body = vehicle is not null && definition is not null && NascarModelResources.SameVehicleFamily(vehicle.ModelName, definition.Name) ? vehicle.Body : null;
        var geometry = TiburonModel.ReadGeometry(data, body);
        var track = TiburonTrackScene.Read(data);
        if (track is not null) pitch = 0.65;
        IReadOnlyList<TiburonModel.Part> parts;
        try { parts = TiburonModel.ReadParts(data); }
        catch (InvalidDataException ex) { parts = []; Log.Warn($"{node.Name}: model parts: {ex.Message}"); }
        var assembly = TiburonCarPreview.Assemble(geometry, parts, body, definition?.Name.StartsWith("ModFLite", StringComparison.OrdinalIgnoreCase) == true);
        var unplaced = parts.Where(p => TiburonCarPreview.IsUnplacedPart(p) || assembly is null && TiburonCarPreview.IsTirePart(p))
            .SelectMany(p => p.Variants).SelectMany(v => v.Meshes).ToHashSet();
        var controlled = parts.SelectMany(p => p.Variants).SelectMany(v => v.Meshes).ToHashSet();
        var enabled = parts.Where(p => !TiburonCarPreview.IsUnplacedPart(p)).Select(p => p.Variants.ElementAtOrDefault(TiburonModel.DefaultVariant(p, body))).Where(v => v is not null).SelectMany(v => v!.Meshes).ToHashSet();
        var textures = new Dictionary<string, ImageSource>(StringComparer.OrdinalIgnoreCase);
        var decodedTextures = new Dictionary<(MmapTexture File, int Offset, int Palette), BitmapSource>();
        BitmapSource DecodeTexture(MmapTexture file, MmapTexture.Image image)
        {
            var key = (file, image.Offset, image.PaletteIndex);
            if (decodedTextures.TryGetValue(key, out var cached)) return cached;
            var decoded = file.Decode(image);
            var bitmap = BitmapSource.Create(decoded.Width, decoded.Height, 96, 96, PixelFormats.Bgra32, null, decoded.Bgra, decoded.Width * 4);
            bitmap.Freeze(); decodedTextures[key] = bitmap; return bitmap;
        }
        var paintTextures = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var opaqueTextures = new Dictionary<BitmapSource, ImageSource>();
        if (NascarModelResources.IsRelevant(node))
        {
            var model = definition;
            if (model is not null)
                foreach (var pair in NascarModelResources.Bind(model, selectedTexture))
                {
                    textures[pair.Key] = DecodeTexture(pair.Value.File, pair.Value.Image);
                    if (pair.Value.Image.Name.Equals("DECAL", StringComparison.OrdinalIgnoreCase)) paintTextures.Add(pair.Key);
                }
        }
        var textureSection = TiburonModel.Read(new MemoryByteSource(data)).FirstOrDefault(s => s.Tag == "Text");
        if (textureSection is { Length: > 0 })
        {
            try
            {
                var mmap = MmapTexture.Read(data.AsSpan((int)textureSection.Offset, (int)textureSection.Length).ToArray());
                foreach (var image in mmap.Images.Where(i => i.Mip == 0))
                {
                    textures[image.Name] = DecodeTexture(mmap, image);
                }
            }
            catch (Exception ex) when (ex is InvalidDataException or NotSupportedException or OverflowException)
            { Log.Warn($"{node.Name}: embedded textures: {ex.Message}"); }
        }
        int textured = geometry.Meshes.Count(m => textures.ContainsKey(m.Texture));
        var text = B.Text($"{geometry.Meshes.Count} meshes · {geometry.Meshes.Sum(m => m.Triangles.Length / 3):N0} triangles · {textured} textured meshes" + (track is null ? "" : $" · {track.Values.Sum(v => v.Count):N0} scene instances"), "Muted");
        var toolbar = B.Toolbar(text, B.Button("Export scene…", "", (_, _) =>
        {
            var picker = new SaveFileDialog { Filter = "Wavefront OBJ|*.obj", FileName = Path.GetFileNameWithoutExtension(node.Name) + ".obj" };
            if (picker.ShowDialog(Window.GetWindow(this)) != true) return;
            try { int count = ExportScene(picker.FileName); text.Text = $"Exported visible scene with {count} materials: {picker.FileName}"; }
            catch (Exception ex) { Log.Warn($"Scene export: {ex.Message}"); MessageBox.Show(Window.GetWindow(this), ex.Message, "Export scene", MessageBoxButton.OK, MessageBoxImage.Warning); }
        }));
        SetDock(toolbar, Dock.Top); Children.Add(toolbar);
        if (track is not null)
        {
            ((Panel)toolbar.Child).Children.Add(B.Button("Top view", "", (_, _) => { pitch = 1.5; UpdateCamera(); }));
            ((Panel)toolbar.Child).Children.Add(B.Button("Angled view", "", (_, _) => { pitch = 0.65; UpdateCamera(); }));
        }
        if (geometry.Limitations.Count > 0)
        {
            var limits = new TextBox { Text = "Partial geometry: " + string.Join("\n", geometry.Limitations), IsReadOnly = true, TextWrapping = TextWrapping.Wrap, MaxHeight = 120, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
            SetDock(limits, Dock.Bottom); Children.Add(limits);
        }
        scene.Children.Add(new AmbientLight(Color.FromRgb(100, 100, 100)));
        scene.Children.Add(new DirectionalLight(Colors.White, new Vector3D(-1, -1, -1)));
        var modelShapes = new Dictionary<int, Model3D>();
        foreach (var mesh in geometry.Meshes)
        {
            if (unplaced.Contains(mesh.Index)) continue;
            // Projected game shadow hulls are not visible body surfaces.
            if (mesh.MaterialName.Contains("shadow", StringComparison.OrdinalIgnoreCase)) continue;
            var shape = new MeshGeometry3D();
            foreach (var p in mesh.Positions) shape.Positions.Add(new(p.X, p.Y, p.Z));
            foreach (int i in mesh.Triangles) shape.TriangleIndices.Add(i);
            // WPF samples the brush in its tile. Authored character islands often use the
            // neighbouring negative/positive UV tile, which the game sampler wraps.
            double shiftU = mesh.Uv.Length > 0 && Math.Floor(mesh.Uv.Min(u => u.X)) == Math.Floor(mesh.Uv.Max(u => u.X)) ? Math.Floor(mesh.Uv.Min(u => u.X)) : 0;
            double shiftV = mesh.Uv.Length > 0 && Math.Floor(mesh.Uv.Min(u => u.Y)) == Math.Floor(mesh.Uv.Max(u => u.Y)) ? Math.Floor(mesh.Uv.Min(u => u.Y)) : 0;
            foreach (var uv in mesh.Uv) shape.TextureCoordinates.Add(new(uv.X - shiftU, uv.Y - shiftV));
            if (mesh.Normals.Any(n => n.LengthSquared() > 0)) foreach (var n in mesh.Normals) shape.Normals.Add(new(n.X, n.Y, n.Z));
            textures.TryGetValue(mesh.Texture, out var texture);
            // Paint alpha is a surface/shader mask, not holes through the opaque body.
            // Keep actual glass/cutout materials on the source-alpha path.
            if (texture is BitmapSource source && paintTextures.Contains(mesh.Texture) && !mesh.MaterialName.Contains("window", StringComparison.OrdinalIgnoreCase))
            {
                if (!opaqueTextures.TryGetValue(source, out var opaque))
                {
                    var pixels = new byte[source.PixelWidth * source.PixelHeight * 4]; source.CopyPixels(pixels, source.PixelWidth * 4, 0);
                    for (int i = 3; i < pixels.Length; i += 4) pixels[i] = 255;
                    var image = BitmapSource.Create(source.PixelWidth, source.PixelHeight, 96, 96, PixelFormats.Bgra32, null, pixels, source.PixelWidth * 4); image.Freeze();
                    opaqueTextures[source] = opaque = image;
                }
                texture = opaque;
            }
            Brush brush = texture is not null
                ? new ImageBrush(texture) { ViewportUnits = BrushMappingMode.Absolute, TileMode = TileMode.Tile, Viewport = new Rect(0, 0, 1, 1), Stretch = Stretch.Fill }
                : new SolidColorBrush(Color.FromRgb(185, 190, 200));
            var material = new DiffuseMaterial(brush);
            shape.Freeze(); material.Freeze();
            Model3D modelShape;
            if (track is not null && track.TryGetValue(mesh.Index, out var instances))
            {
                var group = new Model3DGroup();
                foreach (var instance in instances)
                {
                    var m = instance.Transform;
                    group.Children.Add(new GeometryModel3D(shape, material) { BackMaterial = material, Transform = new MatrixTransform3D(new Matrix3D(
                        m.M11, m.M12, m.M13, m.M14, m.M21, m.M22, m.M23, m.M24,
                        m.M31, m.M32, m.M33, m.M34, m.M41, m.M42, m.M43, m.M44)) });
                }
                modelShape = group;
            }
            else if (assembly?.Wheels.TryGetValue(mesh.Index, out var transforms) == true)
            {
                var wheelGroup = new Model3DGroup();
                foreach (var matrix in transforms)
                    wheelGroup.Children.Add(new GeometryModel3D(shape, material) { BackMaterial = material, Transform = new MatrixTransform3D(new Matrix3D(
                        matrix.M11, matrix.M12, matrix.M13, matrix.M14, matrix.M21, matrix.M22, matrix.M23, matrix.M24,
                        matrix.M31, matrix.M32, matrix.M33, matrix.M34, matrix.M41, matrix.M42, matrix.M43, matrix.M44)) });
                modelShape = wheelGroup;
            }
            else modelShape = new GeometryModel3D(shape, material) { BackMaterial = material };
            modelShapes[mesh.Index] = modelShape;
            if (!controlled.Contains(mesh.Index) || enabled.Contains(mesh.Index)) scene.Children.Add(modelShape);
        }
        if (parts.Any(p => p.Variants.Count > 1))
        {
            var options = new WrapPanel(); var selectors = partSelectors;
            void SelectParts()
            {
                var chosen = selectors.SelectMany(s => s.Part.Variants.ElementAtOrDefault(s.Choice.SelectedIndex)?.Meshes ?? []).ToHashSet();
                foreach (var part in parts.Where(p => p.Variants.Count == 1 && !TiburonCarPreview.IsUnplacedPart(p))) chosen.UnionWith(part.Variants[0].Meshes);
                foreach (var item in modelShapes)
                {
                    bool visible = !controlled.Contains(item.Key) || chosen.Contains(item.Key);
                    if (visible && !scene.Children.Contains(item.Value)) scene.Children.Add(item.Value);
                    else if (!visible) scene.Children.Remove(item.Value);
                }
            }
            foreach (var part in parts.Where(p => p.Variants.Count > 1 && !TiburonCarPreview.IsUnplacedPart(p)))
            {
                var choice = new ComboBox { MinWidth = 100, Margin = new Thickness(4), ItemsSource = part.Variants.Select(v => v.Name).ToArray() };
                choice.SelectedIndex = TiburonModel.DefaultVariant(part, body);
                options.Children.Add(B.Text(part.Name, "Muted")); options.Children.Add(choice);
                selectors.Add((part, choice)); choice.SelectionChanged += (_, _) => SelectParts();
            }
            var expander = new Expander { Header = "Model parts", Content = options, MaxHeight = 160 };
            SetDock(expander, Dock.Top); Children.Add(expander);
        }
        if (assembly is not null)
        {
            var note = B.Text("Static car preview · suspension and damage poses are not simulated", "Muted");
            note.Margin = new Thickness(8, 4, 8, 4); SetDock(note, Dock.Bottom); Children.Add(note);
        }
        if (track is not null)
        {
            var note = B.Text("Static track scene · drag to orbit · right-drag to pan · wheel to zoom", "Muted");
            note.Margin = new Thickness(8, 4, 8, 4); SetDock(note, Dock.Bottom); Children.Add(note);
        }
        var viewport = new Viewport3D { Camera = camera, ClipToBounds = true };
        viewport.Children.Add(new ModelVisual3D { Content = scene });
        var surface = new Border { Background = Ui.Res("WindowBg"), Child = viewport }; Children.Add(surface);
        var bounds = scene.Bounds;
        void Fit()
        {
            if (!bounds.IsEmpty) { target = new(bounds.X + bounds.SizeX / 2, bounds.Y + bounds.SizeY / 2, bounds.Z + bounds.SizeZ / 2); distance = Math.Max(0.1, Math.Max(bounds.SizeX, Math.Max(bounds.SizeY, bounds.SizeZ)) * (track is not null ? 1.35 : assembly is null ? 2.2 : 1.7)); }
            UpdateCamera();
        }
        ((Panel)toolbar.Child).Children.Add(B.Button("Fit view", "", (_, _) => Fit()));
        Fit();
        UpdateCamera();
        surface.MouseDown += (_, e) => { last = e.GetPosition(surface); surface.CaptureMouse(); };
        surface.MouseUp += (_, _) => surface.ReleaseMouseCapture();
        surface.MouseMove += (_, e) => { if (!surface.IsMouseCaptured) return; var now = e.GetPosition(surface);
            if (e.RightButton == MouseButtonState.Pressed)
            {
                var right = Vector3D.CrossProduct(camera.LookDirection, camera.UpDirection); right.Normalize();
                var up = Vector3D.CrossProduct(right, camera.LookDirection); up.Normalize();
                target += (right * (last.X - now.X) + up * (now.Y - last.Y)) * distance * 0.001;
            }
            else { yaw += (now.X - last.X) * 0.01; pitch = Math.Clamp(pitch + (now.Y - last.Y) * 0.01, -1.5, 1.5); }
            last = now; UpdateCamera(); };
        surface.MouseWheel += (_, e) => { distance = Math.Clamp(distance * Math.Exp(-e.Delta * 0.001), 0.01, 1000000); UpdateCamera(); };
    }
    private void UpdateCamera()
    {
        camera.Position = target + new Vector3D(Math.Cos(pitch) * Math.Sin(yaw), Math.Sin(pitch), Math.Cos(pitch) * Math.Cos(yaw)) * distance;
        camera.LookDirection = target - camera.Position; camera.UpDirection = new(0, 1, 0);
    }
}
