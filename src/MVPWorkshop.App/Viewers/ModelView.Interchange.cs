using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Media.Media3D;
using MVPWorkshop.Core.Editing;
using MVPWorkshop.Core.Formats.Eagl;

namespace MVPWorkshop.App.Viewers;

public sealed partial class ModelView
{
    private readonly byte[] _openedElf = [];
    private bool _wireframe, _showNormals;
    private ListBox? _materialList;
    private string? _materialSignature;
    private readonly Model3DGroup _inspectionOverlay = new();
    private Dictionary<string, byte[]> TexturePngs()
    {
        var result = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
        foreach (var (tag, image) in _textures) if (image is BitmapSource source)
        { var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(source)); using var output = new MemoryStream(); encoder.Save(output); result[tag] = output.ToArray(); }
        return result;
    }
    private void ExportGltf(bool clip = false)
    {
        var dialog = new Microsoft.Win32.SaveFileDialog { Filter = "glTF 2.0|*.gltf", FileName = (clip ? _anim?.Name : _model.Name) + ".gltf" };
        if (dialog.ShowDialog(Window.GetWindow(this)) != true) return;
        try { Exporter.CheckOutput(dialog.FileName, _node.Root); if (clip && _anim is null) throw new InvalidOperationException("Select an animation first."); GltfInterchange.Export(dialog.FileName, _model, TexturePngs(), Skeleton, clip ? _anim : null, clip && _withRoot ? _animRoot : null); Log.Info("Exported glTF: " + dialog.FileName); }
        catch (Exception ex) { MessageBox.Show(Window.GetWindow(this), ex.Message, "glTF export"); }
    }
    private async void ImportGltf()
    {
        var dialog = new Microsoft.Win32.OpenFileDialog { Filter = "glTF 2.0 JSON|*.gltf", Title = "Replace native mesh positions · keep topology and vertex order" };
        if (dialog.ShowDialog(Window.GetWindow(this)) != true) return;
        try
        {
            if (_variant != 0 || _uniformPreview) throw new InvalidOperationException("Import into the base model's ORD viewer.");
            var baseline = _node.ReadAll(); var joined = _node.ReadElfJoined().Data;
            if (!joined.AsSpan().SequenceEqual(_openedElf)) throw new InvalidOperationException("Model or link partner changed; reopen before importing.");
            var positions = GltfInterchange.ReadPositions(dialog.FileName, _model.Mesh);
            var patched = MeshPositionPatch.Apply(baseline, joined, _model, positions);
            var review = MainWindow.Instance is { } window ? await window.PrepareReplacement(_node) : null;
            if (review is null) return;
            if (!_node.ReadAll().AsSpan().SequenceEqual(baseline) || !_node.ReadElfJoined().Data.AsSpan().SequenceEqual(joined)) throw new InvalidOperationException("Model or link partner changed; repeat the import.");
            review.Apply(patched, "Import mesh positions from " + Path.GetFileName(dialog.FileName));
            Log.Info("Saved position replacement; native topology, UVs, skin and bounds preserved. Reopen the model to refresh geometry.");
        }
        catch (Exception ex) { MessageBox.Show(Window.GetWindow(this), ex.Message, "Mesh import unavailable"); }
    }
    private FrameworkElement BuildMaterialsPanel()
    {
        _materialList = new ListBox { Background = Ui.Res("PanelBg") };
        _materialList.Loaded += (_, _) => RefreshMaterials();
        return _materialList;
    }
    private void RefreshMaterials()
    {
        if (_materialList is null || _uniformPreview) return;
        var signature = _model.Name + string.Join(";", _textures.Select(p => p.Key + p.Value.GetHashCode()));
        if (_materialSignature == signature) return; _materialSignature = signature;
        _materialList.Items.Clear();
        foreach (var group in _model.Mesh.Draws.GroupBy(d => d.Texture ?? "untextured"))
        {
            var panel = new StackPanel { Margin = new Thickness(6) };
            panel.Children.Add(B.Text($"{group.Key} · {group.Count()} draws · {group.Sum(d => d.TriangleCount):N0} triangles", "Muted"));
            if (_textures.TryGetValue(group.Key, out var image)) panel.Children.Add(new Image { Source = image, Width = 160, Height = 100, Stretch = Stretch.Uniform });
            else panel.Children.Add(B.Text("Texture unresolved", "Faint"));
            panel.Children.Add(B.Button("Show UVs", null, (_, _) => ShowUvs(group.Key), "Outline")); _materialList.Items.Add(panel);
        }
    }
    private void ShowUvs(string? material = null)
    {
        var canvas = new Canvas { Width = 640, Height = 640, Background = Ui.Res("PanelBg") };
        if (material is not null && _textures.TryGetValue(material, out var image)) canvas.Children.Add(new Image { Source = image, Width = 640, Height = 640, Opacity = 0.6 });
        int count = 0;
        foreach (var draw in _model.Mesh.Draws.Where(d => material is null || d.Texture == material)) for (int t = draw.FirstTriangle; t < draw.FirstTriangle + draw.TriangleCount && count < 150000; t++)
        { var ids = _model.Mesh.Indices.Skip(t * 3).Take(3).ToArray(); for (int k = 0; k < 3 && count++ < 150000; k++) { int a = ids[k], b = ids[(k + 1) % 3]; if (a >= _model.Mesh.Uvs.Count || b >= _model.Mesh.Uvs.Count) continue; var u = _model.Mesh.Uvs[a]; var v = _model.Mesh.Uvs[b]; canvas.Children.Add(new System.Windows.Shapes.Line { X1 = u.X * 640, Y1 = u.Y * 640, X2 = v.X * 640, Y2 = v.Y * 640, Stroke = Brushes.Orange, StrokeThickness = 0.6 }); } }
        new Window { Owner = Window.GetWindow(this), Title = "UV layout · " + (material ?? "all materials") + " · native repeat coordinates", Width = 700, Height = 740, Content = new ScrollViewer { Content = canvas, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto, VerticalScrollBarVisibility = ScrollBarVisibility.Auto } }.Show();
    }
    private ComboBox LodPicker()
    {
        if (_uniformPreview) return new ComboBox { Visibility = Visibility.Collapsed };
        var choices = new List<MVPWorkshop.Core.Vfs.AssetNode> { _node };
        var match = System.Text.RegularExpressions.Regex.Match(_node.Name, "^(hi|me|lo)(body|head)\\.ord$", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        if (match.Success) foreach (var level in new[] { "hi", "me", "lo" })
        { var candidate = _node.Parent?.Children.FirstOrDefault(n => n.Name.Equals(level + match.Groups[2].Value + ".ord", StringComparison.OrdinalIgnoreCase)); if (candidate is not null && !choices.Contains(candidate)) choices.Add(candidate); }
        var picker = new ComboBox { ItemsSource = choices, DisplayMemberPath = "Name", SelectedIndex = 0, MinWidth = 110, ToolTip = "Available LOD source files; opens the selected native model" };
        picker.SelectionChanged += (_, _) => { if (picker.SelectedItem is MVPWorkshop.Core.Vfs.AssetNode node && node != _node) MainWindow.Instance?.OpenAsset(node); }; return picker;
    }
    private void AddInspectionOverlay()
    {
        _inspectionOverlay.Children.Clear(); RefreshMaterials();
        if (!_wireframe && !_showNormals) return;
        var geometry = new MeshGeometry3D(); var positions = CurrentPositions(); var visible = _parts.Where(p => p.Visible).Select(p => p.Part.Index).ToHashSet();
        float radius = (float)_radius * 0.0008f; int segments = 0;
        if (_wireframe) foreach (var draw in _model.Mesh.Draws.Where(d => visible.Contains(d.Part))) for (int t = draw.FirstTriangle; t < draw.FirstTriangle + draw.TriangleCount && segments < 100000; t++)
        { var ids = _model.Mesh.Indices.Skip(t * 3).Take(3).ToArray(); for (int k = 0; k < 3 && segments++ < 100000; k++) AddBoneStick(geometry, positions[ids[k]], positions[ids[(k + 1) % 3]], radius); }
        if (_showNormals) { var normals = _model.Mesh.ComputeSmoothNormals(positions); for (int i = 0; i < positions.Count; i += Math.Max(1, positions.Count / 4000)) AddBoneStick(geometry, positions[i], positions[i] + normals[i] * (float)_radius * 0.025f, radius); }
        var material = new EmissiveMaterial(Brushes.Orange); _inspectionOverlay.Children.Add(new GeometryModel3D(geometry, material) { BackMaterial = material }); if (!_scene.Children.Contains(_inspectionOverlay)) _scene.Children.Add(_inspectionOverlay);
    }
}
