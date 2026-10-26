using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Media.Media3D;
using MVPWorkshop.Core.Formats;
using MVPWorkshop.Core.Formats.Eagl;
using MVPWorkshop.Core.IO;
using MVPWorkshop.Core.Vfs;

namespace MVPWorkshop.App.Viewers;

/// <summary>3D viewer for EAGL model objects (.ord+.orl), plus draw/VIF and ELF research tabs.</summary>
public sealed partial class ModelView : Grid
{
    private static TextureIndex? _index;
    private static Task<TextureIndex>? _indexTask;
    private static AssetNode? _indexRoot;

    public sealed class PartItem : Observable
    {
        private bool _visible = true;
        public required EaglPart Part { get; init; }
        public required string Label { get; init; }
        public required string Detail { get; init; }
        public required Brush Swatch { get; init; }
        public Action? Changed { get; set; }
        public bool Visible { get => _visible; set { if (Set(ref _visible, value)) Changed?.Invoke(); } }
    }

    private readonly AssetNode _node;
    private readonly RelocatedObject _obj;
    private EaglFile _file;
    private int _variant;
    private readonly Action<List<Prop>> _setExtra;
    private EaglModel _model;
    private readonly ObservableCollection<PartItem> _parts = [];
    private readonly Viewport3D _viewport = new() { ClipToBounds = true };
    private readonly PerspectiveCamera _camera = new() { FieldOfView = 45, NearPlaneDistance = 0.05, FarPlaneDistance = 100000 };
    private readonly DirectionalLight _headlight = new(Colors.White, new Vector3D(0, 0, -1));
    private readonly AmbientLight _ambient = new(Color.FromRgb(0x70, 0x70, 0x70));
    private readonly Model3DGroup _scene = new();
    private readonly ComboBox _texSource = new() { MinWidth = 200 };
    private readonly TextBlock _texStatus = B.Text("", "Faint", 11);
    private readonly TextBlock _stats = B.Text("", "Muted", 11);
    private readonly Dictionary<string, ImageSource> _textures = new(StringComparer.OrdinalIgnoreCase);
    private int _shading; // 0 textured, 1 clay, 2 part colours
    private Point3D _target;
    private double _distance = 10, _yaw = 0.6, _pitch = 0.25, _radius = 1;
    private Point _lastMouse;
    private bool _uniformPreview;

    private static readonly Color[] PartColors =
    [
        Color.FromRgb(0xF0, 0x91, 0x2E), Color.FromRgb(0x6A, 0xA8, 0xF0), Color.FromRgb(0x5C, 0xC4, 0x8A), Color.FromRgb(0xE0, 0x6A, 0xA8),
        Color.FromRgb(0xC8, 0xC0, 0x5A), Color.FromRgb(0x9A, 0x7C, 0xE8), Color.FromRgb(0x4C, 0xC8, 0xC8), Color.FromRgb(0xE8, 0x6A, 0x5A),
    ];

    public static bool TryCreate(AssetNode node, Action<List<Prop>> setExtra, out FrameworkElement? view)
    {
        view = null;
        var (data, partner) = node.ReadElfJoined();
        if (partner is null && ElfFile.NeedsLinkPartner(data)) return false;
        var obj = RelocatedObject.Load(data);
        if (!EaglReader.LooksLikeModel(obj)) return false;
        view = new ModelView(node, obj, EaglReader.Read(obj), data, setExtra);
        return true;
    }

    private ModelView(AssetNode node, RelocatedObject obj, EaglFile file, byte[] elf, Action<List<Prop>> setExtra, bool uniformPreview = false)
    {
        _uniformPreview = uniformPreview;
        _node = node; _obj = obj; _file = file; _setExtra = setExtra;
        LoadMorphs();
        _model = file.Models.FirstOrDefault(m => m.Mesh.Positions.Count > 0) ?? file.Models[0];
        var args = Environment.GetCommandLineArgs(); // dev aid for --snapshot runs: --shade N, --noblend
        if (Array.IndexOf(args, "--shade") is var si and > 0 && int.TryParse(args[si + 1], out var sh)) _shading = sh;
        if (args.Contains("--noblend")) _blending = false;

        var tabs = new TabControl { Tag = Ui.Res("PanelAlt") };
        tabs.Items.Add(new TabItem { Header = "3D Model", Content = Build3D() });
        if (!_uniformPreview)
        {
            tabs.Items.Add(new TabItem { Header = "Draws & VIF", Content = BuildDraws() });
            tabs.Items.Add(new TabItem { Header = "ELF", Content = new ElfView(ElfFile.Read(elf)) });
        }
        Children.Add(tabs);

        LoadModel(_model);
        ApplyCommandLine();
        ApplyAnimCommandLine();
        Loaded += (_, _) => { if (!_uniformPreview) _ = ResolveTexturesAsync(); };
    }

    // ------------------------------------------------------------------ layout

    private FrameworkElement Build3D()
    {
        _viewport.Camera = _camera;
        var lights = new Model3DGroup();
        lights.Children.Add(_ambient);
        lights.Children.Add(_headlight);
        _viewport.Children.Add(new ModelVisual3D { Content = lights });
        _viewport.Children.Add(new ModelVisual3D { Content = _scene });

        var host = new Border
        {
            Child = _viewport, ClipToBounds = true,
            Background = new RadialGradientBrush(Color.FromRgb(0x2A, 0x2C, 0x33), Color.FromRgb(0x14, 0x15, 0x18)) { RadiusX = 0.8, RadiusY = 0.8 },
        };
        host.MouseDown += (_, e) => { _lastMouse = e.GetPosition(host); host.CaptureMouse(); };
        host.MouseUp += (_, _) => host.ReleaseMouseCapture();
        host.MouseMove += (_, e) => OnDrag(e, host);
        host.MouseWheel += (_, e) => { _distance *= e.Delta > 0 ? 0.88 : 1.14; _distance = Math.Clamp(_distance, _radius * 0.05, _radius * 40); UpdateCamera(); };
        if (_uniformPreview) host.SizeChanged += (_, _) => FitUniformCamera();

        var modelPick = new ComboBox { MinWidth = 220, ItemsSource = _file.Models.Select(m => $"{m.Name}{(m.Variations.Count > 0 ? $"  (+{m.Variations.Count} variations)" : "")}").ToList(), SelectedIndex = _file.Models.IndexOf(_model) };
        modelPick.IsEnabled = !_uniformPreview;
        modelPick.SelectionChanged += (_, _) => { if (modelPick.SelectedIndex >= 0) LoadModel(_file.Models[modelPick.SelectedIndex]); };

        var toolbar = B.Toolbar(
            B.Segments("", ["Lit", "Unlit", "Clay", "Parts"], _shading, i => { _shading = i; Rebuild(); }),
            B.Toggle("Blending", _blending, v => { _blending = v; Rebuild(); }),
            B.Separator(),
            B.Button("Reset view", "", (_, _) => { ResetCamera(); }),
            B.Button("Front", null, (_, _) => { _yaw = 0; _pitch = 0; UpdateCamera(); }),
            B.Button("Side", null, (_, _) => { _yaw = Math.PI / 2; _pitch = 0; UpdateCamera(); }),
            B.Button("Back", null, (_, _) => { _yaw = Math.PI; _pitch = 0; UpdateCamera(); }),
            B.Button("Top", null, (_, _) => { _yaw = 0; _pitch = 1.5; UpdateCamera(); }),
            B.Separator(),
            B.Button("Export OBJ", "", (_, _) => ExportObj(), "Outline", "OBJ + MTL + PNG textures"),
            B.Separator(), _stats);

        var hint = new TextBlock { Text = "Drag: orbit · Right/middle drag: pan · Wheel: zoom", Foreground = Ui.Res("Faint"), FontSize = 11, Margin = new Thickness(12, 0, 0, 8), VerticalAlignment = VerticalAlignment.Bottom, IsHitTestVisible = false };
        var viewArea = new Grid();
        viewArea.Children.Add(host);
        viewArea.Children.Add(hint);

        // left panel
        var partsList = new ListBox { ItemsSource = _parts, ItemTemplate = PartTemplate(), Background = Ui.Res("PanelBg") };
        var allOn = B.Button("All", null, (_, _) => { foreach (var p in _parts) p.Visible = true; });
        var allOff = B.Button("None", null, (_, _) => { foreach (var p in _parts) p.Visible = false; });
        var partsHeader = new DockPanel { Margin = new Thickness(10, 6, 6, 4) };
        var ph = B.Text("PARTS", "Muted", 11, FontWeights.SemiBold);
        partsHeader.Children.Add(ph);
        var pb = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        pb.Children.Add(allOn); pb.Children.Add(allOff);
        partsHeader.Children.Add(pb);

        _texSource.SelectionChanged += (_, _) => { if (_texSource.SelectedItem is TexChoice c) _ = LoadTexturePackAsync(c.Pack); };
        var texPanel = new StackPanel { Margin = new Thickness(10, 8, 10, 10) };
        texPanel.Children.Add(B.Text("TEXTURE SOURCE", "Muted", 11, FontWeights.SemiBold));
        texPanel.Children.Add(new Border { Height = 6 });
        texPanel.Children.Add(_texSource);
        texPanel.Children.Add(new Border { Height = 4 });
        texPanel.Children.Add(_texStatus);
        var findBtn = B.Button("Search whole game", "", (_, _) => _ = ResolveTexturesAsync(force: true), "Outline", "Index every SSH pack in the game and rank packs by how many of this model's textures they contain");
        if (_uniformPreview) findBtn.Visibility = Visibility.Collapsed;
        findBtn.HorizontalAlignment = HorizontalAlignment.Left;
        findBtn.Margin = new Thickness(0, 6, 0, 0);
        texPanel.Children.Add(findBtn);

        var modelPanel = new StackPanel { Margin = new Thickness(10, 10, 10, 6) };
        modelPanel.Children.Add(B.Text("MODEL", "Muted", 11, FontWeights.SemiBold));
        modelPanel.Children.Add(new Border { Height = 6 });
        modelPanel.Children.Add(modelPick);
        if (_model.VariantCount > 1 && !_uniformPreview)
        {
            // Geometry variations come from draw jump tables; names follow __Model:::x / x.variationN.
            var names = Enumerable.Range(0, _model.VariantCount).Select(i => i == 0 ? $"{_model.Name} (base)" : i - 1 < _model.Variations.Count ? _model.Variations[i - 1] : $"variation {i}").ToList();
            var varPick = new ComboBox { ItemsSource = names, SelectedIndex = 0, Margin = new Thickness(0, 6, 0, 0) };
            varPick.SelectionChanged += (_, _) => { if (varPick.SelectedIndex >= 0) SetVariant(varPick.SelectedIndex); };
            modelPanel.Children.Add(varPick);
        }

        var leftTabs = new TabControl { Tag = Ui.Res("PanelBg") };
        leftTabs.Items.Add(new TabItem { Header = "Parts", Content = B.Rows((partsHeader, B.Auto), (partsList, B.Star())) });
        leftTabs.Items.Add(new TabItem { Header = "Pose", Content = BuildPosePanel() });
        leftTabs.Items.Add(new TabItem { Header = "Animate", Content = BuildAnimPanel() });
        leftTabs.Items.Add(new TabItem { Header = $"Shapes{(_morphs is null ? "" : $" ({_morphs.Shapes.Count})")}", Content = BuildShapesPanel() });
        var left = B.Rows((modelPanel, B.Auto), (texPanel, B.Auto), (leftTabs, B.Star()));
        left.Background = Ui.Res("PanelBg");

        if (_uniformPreview)
        {
            texPanel.Children.Remove(_texStatus);
            var uniformToolbar = B.Toolbar(
                B.Button("Fit", null, (_, _) => FitUniformCamera()),
                B.Button("Front", null, (_, _) => { _yaw = 0; _pitch = 0; UpdateCamera(); }),
                B.Button("Back", null, (_, _) => { _yaw = Math.PI; _pitch = 0; UpdateCamera(); }),
                B.Button("Side", null, (_, _) => { _yaw = Math.PI / 2; _pitch = 0; UpdateCamera(); }),
                B.Button("Three-quarter", null, (_, _) => { _yaw = 0.6; _pitch = 0.15; UpdateCamera(); }),
                B.Segments("", ["Lit", "Unlit"], 0, i => { _shading = i; Rebuild(); }));
            return B.Rows((uniformToolbar, B.Auto), (viewArea, B.Star()), (_texStatus, B.Auto));
        }
        var center = B.Rows((toolbar, B.Auto), (viewArea, B.Star()));
        return B.Cols((left, B.Px(270)), (B.VSplit(), B.Px(4)), (center, B.Star()));
    }

    private static DataTemplate PartTemplate()
    {
        var sp = new FrameworkElementFactory(typeof(DockPanel));
        var cb = new FrameworkElementFactory(typeof(CheckBox));
        cb.SetBinding(System.Windows.Controls.Primitives.ToggleButton.IsCheckedProperty, new System.Windows.Data.Binding(nameof(PartItem.Visible)) { Mode = System.Windows.Data.BindingMode.TwoWay });
        cb.SetValue(MarginProperty, new Thickness(0, 0, 6, 0));
        cb.SetValue(VerticalAlignmentProperty, VerticalAlignment.Center);
        var sw = new FrameworkElementFactory(typeof(Border));
        sw.SetValue(WidthProperty, 10.0); sw.SetValue(HeightProperty, 10.0);
        sw.SetValue(Border.CornerRadiusProperty, new CornerRadius(2));
        sw.SetBinding(Border.BackgroundProperty, new System.Windows.Data.Binding(nameof(PartItem.Swatch)));
        sw.SetValue(MarginProperty, new Thickness(0, 0, 8, 0));
        var text = new FrameworkElementFactory(typeof(StackPanel));
        var t1 = new FrameworkElementFactory(typeof(TextBlock)); t1.SetBinding(TextBlock.TextProperty, new System.Windows.Data.Binding(nameof(PartItem.Label)));
        t1.SetValue(TextBlock.TextTrimmingProperty, TextTrimming.CharacterEllipsis);
        var t2 = new FrameworkElementFactory(typeof(TextBlock)); t2.SetBinding(TextBlock.TextProperty, new System.Windows.Data.Binding(nameof(PartItem.Detail)));
        t2.SetValue(TextBlock.ForegroundProperty, Ui.Res("Faint")); t2.SetValue(TextBlock.FontSizeProperty, 10.5);
        text.AppendChild(t1); text.AppendChild(t2);
        sp.AppendChild(cb); sp.AppendChild(sw); sp.AppendChild(text);
        return new DataTemplate { VisualTree = sp };
    }

    private FrameworkElement BuildDraws()
    {
        var grid = B.Grid();
        grid.Columns.Add(B.Col("#", nameof(DrawRow.Index), 50));
        grid.Columns.Add(B.Col("Part", nameof(DrawRow.Part), 50));
        grid.Columns.Add(B.Col("Texture", nameof(DrawRow.Texture), 90, true));
        grid.Columns.Add(B.Col("Chain", nameof(DrawRow.Chain), 90, true));
        grid.Columns.Add(B.Col("Batches", nameof(DrawRow.Batches), 70));
        grid.Columns.Add(B.Col("Verts", nameof(DrawRow.Verts), 70));
        grid.Columns.Add(B.Col("Tris", nameof(DrawRow.Tris), 70));
        grid.Columns.Add(new DataGridTextColumn { Header = "Prim state", Binding = new System.Windows.Data.Binding(nameof(DrawRow.State)), Width = new DataGridLength(1, DataGridLengthUnitType.Star) });
        var listing = new TextBox
        {
            IsReadOnly = true, FontFamily = (FontFamily)Application.Current.Resources["Mono"], FontSize = 12, Background = Ui.Res("WindowBg"), BorderThickness = new Thickness(0),
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Padding = new Thickness(12, 8, 12, 8), VerticalContentAlignment = VerticalAlignment.Top,
        };
        grid.SelectionChanged += (_, _) =>
        {
            if (grid.SelectedItem is not DrawRow r) return;
            if (r.Draw.ChainOffset < 0) { listing.Text = r.Draw.StreamInfo ?? ""; return; }
            var single = _obj.U32(r.Draw.ChainOffset - 4) == 0x14000004;
            var bytes = EaglReader.FlattenChain(_obj, r.Draw.ChainOffset, single);
            var sb = new System.Text.StringBuilder();
            sb.AppendLine($"; draw {r.Index}  part {r.Part}  texture {r.Texture}  chain @0x{r.Draw.ChainOffset:X} ({(single ? "inline REF" : "DMA chain")}), {bytes.Length:N0} VIF bytes");
            foreach (var c in Vif.Parse(bytes).Take(4000))
            {
                sb.Append(c);
                if (c.IsUnpack && c.PayloadSize > 0) sb.Append("   ").Append(Convert.ToHexString(bytes, c.PayloadOffset, Math.Min(32, c.PayloadSize)));
                sb.AppendLine();
            }
            listing.Text = sb.ToString();
        };
        _drawsGrid = grid;
        return B.Rows((grid, B.Star(1)), (new GridSplitter { Height = 4, HorizontalAlignment = HorizontalAlignment.Stretch }, B.Px(4)), (listing, B.Star(1.2)));
    }

    private DataGrid? _drawsGrid;

    private sealed record DrawRow(EaglDraw Draw, int Index, int Part, string Texture, string Chain, int Batches, int Verts, int Tris, string State);

    // ------------------------------------------------------------------ model

    private void LoadModel(EaglModel m)
    {
        _model = m;
        _parts.Clear();
        foreach (var p in m.Parts)
        {
            var tris = p.Draws.Sum(d => d.TriangleCount);
            var item = new PartItem
            {
                Part = p, Label = string.Join(", ", p.Textures), Detail = $"part {p.Index} · {p.Draws.Count} draws · {tris:N0} tris",
                Swatch = new SolidColorBrush(PartColors[p.Index % PartColors.Length]),
            };
            item.Changed = Rebuild;
            _parts.Add(item);
        }
        if (_drawsGrid is not null)
            _drawsGrid.ItemsSource = m.Mesh.Draws.Select(d => new DrawRow(d, d.Index, d.Part, d.Texture ?? "", d.ChainOffset < 0 ? "streams" : $"0x{d.ChainOffset:X}", d.Batches, d.VertexCount, d.TriangleCount,
                d.PrimState is null ? "" : $"{d.State} · {d.PrimState.Replace("__EAGL::GeoPrimState:::RUNTIME_ALLOC::", "")}")).ToList();

        BindMorphs();
        RefreshShapes();
        var (mn, mx) = m.Mesh.Bounds();
        var c = (mn + mx) / 2;
        _target = new Point3D(c.X, c.Y, c.Z);
        _radius = Math.Max(0.01, (mx - mn).Length() / 2);
        ResetCamera();
        Rebuild();
        PublishProps();
    }

    private void PublishProps()
    {
        var m = _model;
        var (mn, mx) = m.Mesh.Bounds();
        var p = new List<Prop>
        {
            new("Model", "Name", m.Name),
            new("Model", "Symbol offset", $"0x{m.Offset:X}"),
            new("Model", "Parts", m.Parts.Count.ToString()),
            new("Model", "Draws", m.Mesh.Draws.Count.ToString()),
            new("Model", "Vertices", m.Mesh.Positions.Count.ToString("N0")),
            new("Model", "Triangles", (m.Mesh.Indices.Count / 3).ToString("N0")),
            new("Model", "Decoded bounds", $"({mn.X:0.##}, {mn.Y:0.##}, {mn.Z:0.##}) – ({mx.X:0.##}, {mx.Y:0.##}, {mx.Z:0.##})"),
            new("Model", "Stored bounds", $"({m.BoundsMin.X:0.##}, {m.BoundsMin.Y:0.##}, {m.BoundsMin.Z:0.##}) – ({m.BoundsMax.X:0.##}, {m.BoundsMax.Y:0.##}, {m.BoundsMax.Z:0.##})"),
            new("Model", "Quant scale / half", $"{m.QuantScale.X:0.###}, {m.QuantScale.Y:0.###}, {m.QuantScale.Z:0.###} / {m.QuantOffset.X:0.###}, {m.QuantOffset.Y:0.###}, {m.QuantOffset.Z:0.###}"),
            new("Model", "Textures", string.Join(", ", m.Mesh.Draws.Select(d => d.Texture).Where(t => t is not null).Distinct())),
        };
        if (m.Variations.Count > 0) p.Add(new("Model", "Variations", string.Join(", ", m.Variations)));
        if (m.Names.Count > 0) p.Add(new("Model", "Name table", string.Join(", ", m.Names.Take(40)) + (m.Names.Count > 40 ? " …" : "")));
        foreach (var w in m.Mesh.Warnings.Take(10)) p.Add(new("Warnings", "Decode", w));
        p.Add(new("Object", "Models", _file.Models.Count.ToString()));
        if (_file.SkeletonName is not null) p.Add(new("Object", "Skeleton", $"{_file.SkeletonName} ({_file.SkeletonBoneCount} bones)"));
        if (_file.Bones.Count > 0) p.Add(new("Object", "Bones", string.Join(", ", _file.Bones.Select(b => $"{b.Index}:{b.Name.Split('.').Last()}"))));
        if (_file.Microcode.Count > 0) p.Add(new("Object", "VU1 microcode", string.Join(", ", _file.Microcode.Select(s => s.Replace("__EAGLMicroCode", "")))));
        if (_file.ToolLibVersion is not null) p.Add(new("Object", "Tool lib", _file.ToolLibVersion));
        _setExtra(p);
        _stats.Text = $"{m.Mesh.Positions.Count:N0} verts · {m.Mesh.Indices.Count / 3:N0} tris · {m.Mesh.Draws.Count} draws";
    }

    private void Rebuild()
    {
        _scene.Children.Clear();
        _live.Clear();
        // Unlit = full white ambient, no headlight: textures show exactly as stored.
        _ambient.Color = _shading == 1 ? Colors.White : Color.FromRgb(0x70, 0x70, 0x70);
        _headlight.Color = _shading == 1 ? Colors.Black : Colors.White;
        var mesh = _model.Mesh;
        var positions = CurrentPositions();
        if (HasDeformation) { _normals = mesh.ComputeSmoothNormals(positions); _normalsFor = null; }
        else if (_normalsFor != _model) { _normals = mesh.ComputeSmoothNormals(); _normalsFor = _model; }
        var visible = _parts.Where(p => p.Visible).Select(p => p.Part.Index).ToHashSet();
        // Opaque passes first, then blended passes in program order (the GS draws them over what is already there).
        // Passes drawn with DTM_ALWAYS are painter-ordered ground layers; WPF always depth-tests, so lift each one
        // slightly along its normals (in program order) instead of letting coplanar layers z-fight.
        var layer = 0;
        foreach (var d in mesh.Draws.Where(d => !IsBlended(d)).Concat(mesh.Draws.Where(IsBlended)))
        {
            if (!visible.Contains(d.Part) || d.TriangleCount == 0) continue;
            if (_uniformPreview && d.Texture is not null && !_textures.ContainsKey(d.Texture)) continue;
            var lift = IsBlended(d) && !d.State.DepthWrite ? (float)(_radius * 1e-4 * ++layer) : 0f;
            // Each draw gets its own compact vertex set so WPF only processes what it shows.
            var g = new MeshGeometry3D();
            var remap = new Dictionary<int, int>();
            var order = new List<int>();
            int Map(int src)
            {
                if (remap.TryGetValue(src, out var dst)) return dst;
                dst = remap[src] = g.Positions.Count;
                order.Add(src);
                var p = positions[src]; var uv = mesh.Uvs[src]; var n = _normals![src];
                p += n * lift;
                g.Positions.Add(new Point3D(p.X, p.Y, p.Z));
                g.TextureCoordinates.Add(new Point(uv.X, uv.Y));
                g.Normals.Add(new Vector3D(n.X, n.Y, n.Z));
                return dst;
            }
            for (var t = d.FirstTriangle * 3; t < (d.FirstTriangle + d.TriangleCount) * 3; t += 3)
            {
                int a = mesh.Indices[t], b = mesh.Indices[t + 1], c = mesh.Indices[t + 2];
                // Strip winding isn't reliable; make each face agree with its (outward) smooth normal.
                var face = System.Numerics.Vector3.Cross(positions[b] - positions[a], positions[c] - positions[a]);
                if (System.Numerics.Vector3.Dot(face, _normals![a] + _normals[b] + _normals[c]) < 0) (b, c) = (c, b);
                g.TriangleIndices.Add(Map(a)); g.TriangleIndices.Add(Map(b)); g.TriangleIndices.Add(Map(c));
            }
            // Left unfrozen so animation frames can swap in new position/normal collections.
            g.TriangleIndices.Freeze();
            g.TextureCoordinates.Freeze();
            _live.Add((g, order.ToArray(), lift));
            var mat = MaterialFor(d);
            _scene.Children.Add(new GeometryModel3D(g, mat) { BackMaterial = mat });
        }
        _scene.Children.Add(_overlay);
        AddSkeletonOverlay();
    }

    private readonly List<(MeshGeometry3D Geometry, int[] Source, float Lift)> _live = [];
    private readonly Model3DGroup _overlay = new();

    /// <summary>Fast path for animation: recompute deformed positions/normals and update existing geometry.</summary>
    private void UpdateDeformation()
    {
        if (_live.Count == 0) { Rebuild(); return; }
        var positions = CurrentPositions();
        _normals = _model.Mesh.ComputeSmoothNormals(positions);
        _normalsFor = null;
        foreach (var (g, src, lift) in _live)
        {
            var pc = new Point3DCollection(src.Length);
            var nc = new Vector3DCollection(src.Length);
            foreach (var s in src)
            {
                var n = _normals[s]; var p = positions[s] + n * lift;
                pc.Add(new Point3D(p.X, p.Y, p.Z));
                nc.Add(new Vector3D(n.X, n.Y, n.Z));
            }
            pc.Freeze(); nc.Freeze();
            g.Positions = pc;
            g.Normals = nc;
        }
        AddSkeletonOverlay();
    }

    private System.Numerics.Vector3[]? _normals;
    private EaglModel? _normalsFor;

    private bool _blending = true;

    private bool IsBlended(EaglDraw d) => _blending && _shading is 0 or 1 && d.State.Blend != EaglBlend.Opaque;

    private Material MaterialFor(EaglDraw d)
    {
        var textured = _shading is 0 or 1;
        var state = _blending && textured ? d.State : EaglPrimState.Default;
        Brush brush = _shading switch
        {
            3 => new SolidColorBrush(PartColors[d.Part % PartColors.Length]),
            _ when textured && !state.Textured => new SolidColorBrush(AverageColor(d)),
            _ when textured && d.Texture is not null && _textures.TryGetValue(d.Texture, out var img) =>
                new ImageBrush(img) { ViewportUnits = BrushMappingMode.Absolute, Viewport = new Rect(0, 0, 1, 1), TileMode = TileMode.Tile },
            _ => new SolidColorBrush(Color.FromRgb(0xB8, 0xB4, 0xAC)),
        };
        // Vertex alpha drives most of the game's overlay passes; WPF has no per-vertex alpha, so use the draw's mean.
        if (state.Blend is EaglBlend.Alpha or EaglBlend.Additive && state.Textured) brush.Opacity = AverageColor(d).A / 255.0;
        brush.Freeze();
        return state.Blend switch
        {
            // WPF draws emissive-only materials additively, which matches ABM_ADD / (Cs − 0)·FIX + Cd.
            EaglBlend.Additive => new EmissiveMaterial(brush),
            // (Cd − Cs)·FIX: darkens what is underneath — approximate with a translucent black layer.
            EaglBlend.Subtractive => new DiffuseMaterial(new SolidColorBrush(Color.FromArgb(0x60, 0, 0, 0))),
            _ => new DiffuseMaterial(brush),
        };
    }

    /// <summary>Mean vertex colour of a draw (PS2 0x80 = 1.0 = white / opaque).</summary>
    private Color AverageColor(EaglDraw d)
    {
        var mesh = _model.Mesh;
        if (mesh.Colors.Count == 0) return Colors.White;
        var sum = System.Numerics.Vector4.Zero; var n = 0;
        for (var t = d.FirstTriangle * 3; t < (d.FirstTriangle + d.TriangleCount) * 3; t++)
        {
            var i = mesh.Indices[t];
            if (i < mesh.Colors.Count) { sum += mesh.Colors[i]; n++; }
        }
        if (n == 0) return Colors.White;
        var c = System.Numerics.Vector4.Clamp(sum / n, System.Numerics.Vector4.Zero, System.Numerics.Vector4.One);
        return Color.FromArgb((byte)(c.W * 255), (byte)(c.X * 255), (byte)(c.Y * 255), (byte)(c.Z * 255));
    }

    // ------------------------------------------------------------------ camera

    private void ResetCamera()
    {
        _distance = _radius * 2.8;
        _yaw = 0.5; _pitch = 0.2;
        _camera.NearPlaneDistance = Math.Max(0.01, _radius * 0.01);
        _camera.FarPlaneDistance = _radius * 200;
        UpdateCamera();
    }

    private void UpdateCamera()
    {
        var dir = new Vector3D(Math.Sin(_yaw) * Math.Cos(_pitch), Math.Sin(_pitch), Math.Cos(_yaw) * Math.Cos(_pitch));
        _camera.Position = _target + dir * _distance;
        _camera.LookDirection = -dir * _distance;
        _camera.UpDirection = new Vector3D(0, 1, 0);
        _headlight.Direction = -dir + new Vector3D(0, -0.3, 0);
        if (_uniformPreview) UniformCameraChanged?.Invoke(CaptureUniformCamera());
    }

    private void OnDrag(MouseEventArgs e, IInputElement host)
    {
        var pos = e.GetPosition(host);
        var dx = pos.X - _lastMouse.X; var dy = pos.Y - _lastMouse.Y;
        _lastMouse = pos;
        if (e.LeftButton == MouseButtonState.Pressed)
        {
            _yaw -= dx * 0.01;
            _pitch = Math.Clamp(_pitch + dy * 0.01, -1.55, 1.55);
            UpdateCamera();
        }
        else if (e.RightButton == MouseButtonState.Pressed || e.MiddleButton == MouseButtonState.Pressed)
        {
            var look = _camera.LookDirection; look.Normalize();
            var right = Vector3D.CrossProduct(look, new Vector3D(0, 1, 0)); right.Normalize();
            var up = Vector3D.CrossProduct(right, look);
            var k = _distance * 0.0015;
            _target += -right * dx * k + up * dy * k;
            UpdateCamera();
        }
    }

    // ------------------------------------------------------------------ textures

    private sealed record TexChoice(AssetNode Pack, int Hits, int Needed)
    {
        public override string ToString() => $"{Pack.Name}  ({Hits}/{Needed})  — {Pack.Parent?.Name}";
    }

    private IEnumerable<string> NeededTextures() => _file.Models.SelectMany(m => m.Mesh.Draws).Select(d => d.Texture).OfType<string>().Distinct(StringComparer.OrdinalIgnoreCase);

    private async Task ResolveTexturesAsync(bool force = false)
    {
        var needed = NeededTextures().ToList();
        if (needed.Count == 0) { _texStatus.Text = "Model has no textures."; return; }

        // 1. SSH packs in the same container / folder
        var local = new List<TexChoice>();
        foreach (var sib in _node.Parent?.Children ?? [])
        {
            if (sib.IsFolder || !sib.IsTexturePack || sib.Format != FormatId.SshTexture) continue;
            try
            {
                var tags = SshFile.Read(sib.ReadAll()).Images.Select(i => i.Tag).ToHashSet(StringComparer.OrdinalIgnoreCase);
                var hits = needed.Count(tags.Contains);
                if (hits > 0) local.Add(new TexChoice(sib, hits, needed.Count));
            }
            catch { }
        }

        var choices = local.OrderByDescending(c => c.Hits).ToList();
        var stadium = _node.Path.Contains("/STADIUM/", StringComparison.OrdinalIgnoreCase);
        if (force || (!stadium && (choices.Count == 0 || choices[0].Hits < needed.Count)))
        {
            if (MainWindow.Instance?.GameRoot is { } root)
            {
                if (_indexRoot != root) { _index = null; _indexTask = null; _indexRoot = root; }
                if (_index is null)
                {
                    _texStatus.Text = "Indexing SSH packs across the game…";
                    _indexTask ??= Task.Run(() => TextureIndex.Build(root));
                    _index = await _indexTask;
                    Log.Info($"Texture index built: {_index.PackCount:N0} SSH packs.");
                }
                foreach (var (pack, hits) in _index.Rank(needed).Take(60))
                    if (choices.All(c => c.Pack != pack)) choices.Add(new TexChoice(pack, hits, needed.Count));
                choices = [.. choices.OrderByDescending(c => c.Pack.Parent == _node.Parent).ThenByDescending(c => c.Hits)];
            }
        }
        _texSource.ItemsSource = choices;
        if (choices.Count > 0) _texSource.SelectedIndex = 0;
        else _texStatus.Text = $"No SSH pack contains: {string.Join(", ", needed)}";
    }

    private static Dictionary<string, BitmapSource> DecodeFrom(AssetNode pack, ISet<string> wanted)
    {
        var res = new Dictionary<string, BitmapSource>(StringComparer.OrdinalIgnoreCase);
        var ssh = SshFile.Read(pack.ReadAll());
        foreach (var img in ssh.Images)
        {
            if (!wanted.Contains(img.Tag) || res.ContainsKey(img.Tag)) continue;
            try
            {
                var d = ssh.Decode(img);
                var bmp = BitmapSource.Create(d.Width, d.Height, 96, 96, PixelFormats.Bgra32, null, d.Bgra, d.Width * 4);
                bmp.Freeze();
                res[img.Tag] = bmp;
            }
            catch { }
        }
        return res;
    }

    private async Task LoadTexturePackAsync(AssetNode pack)
    {
        var needed = NeededTextures().ToHashSet(StringComparer.OrdinalIgnoreCase);
        _texStatus.Text = $"Decoding {pack.Name}…";
        _textures.Clear();
        var index = _index;
        var localPacks = _node.Parent?.Children.Where(n => n.IsTexturePack && n != pack).ToList() ?? [];
        var stadium = _node.Path.Contains("/STADIUM/", StringComparison.OrdinalIgnoreCase);
        var (primary, fallback) = await Task.Run(() =>
        {
            var main = DecodeFrom(pack, needed);
            // Fill anything the chosen pack lacks from the best other pack that has it (e.g. eye black on heads).
            var extra = new Dictionary<string, (BitmapSource Img, string From)>(StringComparer.OrdinalIgnoreCase);
            // Stadium texture tags are reused by every ballpark. Resolve the complete local texture set first.
            foreach (var other in localPacks)
            {
                var missingLocal = needed.Where(t => !main.ContainsKey(t) && !extra.ContainsKey(t)).ToHashSet(StringComparer.OrdinalIgnoreCase);
                if (missingLocal.Count == 0) break;
                try { foreach (var (tag, image) in DecodeFrom(other, missingLocal)) extra.TryAdd(tag, (image, other.Path)); } catch { }
            }
            if (index is not null && !stadium)
                foreach (var tag in needed.Where(t => !main.ContainsKey(t)))
                {
                    if (extra.ContainsKey(tag)) continue;
                    foreach (var other in index.Find(tag).Take(3))
                    {
                        var got = DecodeFrom(other, new HashSet<string>([tag], StringComparer.OrdinalIgnoreCase));
                        if (got.TryGetValue(tag, out var img)) { extra[tag] = (img, other.Path); break; }
                    }
                }
            return (main, extra);
        });
        foreach (var (k, v) in primary) _textures[k] = v;
        foreach (var (k, v) in fallback) _textures[k] = v.Img;
        var missing = needed.Where(t => !_textures.ContainsKey(t)).ToList();
        _texStatus.Text = $"{primary.Count} from {pack.Name}"
            + (fallback.Count > 0 ? $" · {fallback.Count} from other packs ({string.Join(", ", fallback.Select(f => $"{f.Key}←{f.Value.From}").Take(4))})" : "")
            + (missing.Count > 0 ? $" · missing: {string.Join(", ", missing.Take(12))}{(missing.Count > 12 ? "…" : "")}" : "");
        _texStatus.TextWrapping = TextWrapping.Wrap;
        _texStatus.ToolTip = $"Primary: {pack.Path}\n" + string.Join("\n", fallback.Select(f => $"{f.Key} ← {f.Value.From}")) + (stadium ? "\nStadium fallback is restricted to this stadium archive." : "");
        Rebuild();
    }

    // ------------------------------------------------------------------ export

    private void ExportObj()
    {
        var dlg = new Microsoft.Win32.OpenFolderDialog { Title = $"Export {_model.Name} as OBJ into…" };
        if (dlg.ShowDialog(Window.GetWindow(this)) != true) return;
        var name = string.Concat(_model.Name.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c));
        var obj = $"mtllib {name}.mtl\n" + _model.Mesh.ToObj(_model.Name);
        File.WriteAllText(Path.Combine(dlg.FolderName, name + ".obj"), obj);
        var mtl = new System.Text.StringBuilder();
        foreach (var t in _model.Mesh.Draws.Select(d => d.Texture).OfType<string>().Distinct())
        {
            mtl.AppendLine($"newmtl {t}").AppendLine("Kd 1 1 1");
            if (_textures.TryGetValue(t, out var img) && img is BitmapSource bs)
            {
                var enc = new PngBitmapEncoder();
                enc.Frames.Add(BitmapFrame.Create(bs));
                using (var fs = File.Create(Path.Combine(dlg.FolderName, t + ".png"))) enc.Save(fs);
                mtl.AppendLine($"map_Kd {t}.png");
            }
            mtl.AppendLine();
        }
        File.WriteAllText(Path.Combine(dlg.FolderName, name + ".mtl"), mtl.ToString());
        Log.Info($"Exported {_model.Name} ({_model.Mesh.Indices.Count / 3:N0} tris) → {dlg.FolderName}");
    }
}
