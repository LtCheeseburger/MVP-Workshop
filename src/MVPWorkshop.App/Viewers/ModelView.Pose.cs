using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Media3D;
using System.Windows.Threading;
using MVPWorkshop.Core.Formats.Eagl;
using MVPWorkshop.Core.Vfs;
using NVector3 = System.Numerics.Vector3;
using NQuaternion = System.Numerics.Quaternion;

namespace MVPWorkshop.App.Viewers;

/// <summary>Skeleton posing (bone rotations, presets, skeleton overlay) and blend-shape sliders.</summary>
public sealed partial class ModelView
{
    private readonly Dictionary<int, NVector3> _boneEuler = [];   // degrees, applied on top of the bind rotation
    private float[] _morphWeights = [];
    private EaglMorphSet? _morphs;
    private List<(int FirstVertex, short[] Deltas)>[]? _morphBound;
    private bool _showBones;
    private int _selBone = -1;
    private DispatcherTimer? _deformTimer;
    private readonly Slider[] _rot = [new(), new(), new()];
    private readonly TextBlock[] _rotText = [B.Text("0°", "Muted", 11), B.Text("0°", "Muted", 11), B.Text("0°", "Muted", 11)];
    private bool _syncingSliders;
    private StackPanel? _shapePanel;

    private EaglSkeleton? Skeleton => _file.Skeleton;

    private bool HasDeformation => (_boneEuler.Count > 0 && Skeleton is not null) || _morphWeights.Any(w => w != 0) || _anim is not null;

    /// <summary>Morphs live either in the object itself or in a sibling "&lt;name&gt;_morph.ord".</summary>
    private void LoadMorphs()
    {
        _morphs = _file.Morphs;
        if (_morphs is null && _node.Parent is { } parent)
        {
            var stem = System.IO.Path.GetFileNameWithoutExtension(_node.Name);
            var sib = parent.Children.FirstOrDefault(c => c.Name.Equals(stem + "_morph.ord", StringComparison.OrdinalIgnoreCase));
            if (sib is not null)
            {
                try { _morphs = EaglMorphSet.Read(RelocatedObject.Load(sib.ReadElfJoined().Data)); }
                catch (Exception ex) { Log.Warn($"{sib.Name}: {ex.Message}"); }
            }
        }
        _morphWeights = new float[_morphs?.Shapes.Count ?? 0];
    }

    /// <summary>Re-decode the object selecting another entry of the draw jump tables (different head shape).</summary>
    private void SetVariant(int variant)
    {
        if (variant == _variant) return;
        _variant = variant;
        var index = _file.Models.IndexOf(_model);
        _file = EaglReader.Read(_obj, variant);
        var keep = _parts.Where(p => !p.Visible).Select(p => p.Part.Index).ToHashSet();
        LoadModel(_file.Models[Math.Max(0, index)]);
        foreach (var p in _parts) if (keep.Contains(p.Part.Index)) p.Visible = false;
        Log.Info($"{_node.Name}: variation {variant}, {_model.Mesh.Positions.Count:N0} vertices");
    }

    private void BindMorphs()
    {
        _morphBound = _morphs is null ? null : EaglDeformer.Bind(_obj, _model, _morphs);
    }

    private IReadOnlyList<NVector3> CurrentPositions()
    {
        if (!HasDeformation) return _model.Mesh.Positions;
        if (CurrentWorld() is { } world && Skeleton is { } sk) return EaglDeformer.Deform(_model, sk, world, _morphBound, _morphWeights);
        var pose = _boneEuler.ToDictionary(kv => kv.Key, kv => Euler(kv.Value));
        return EaglDeformer.Deform(_model, Skeleton, pose, _morphBound, _morphWeights);
    }

    private static NQuaternion Euler(NVector3 deg)
    {
        const float r = MathF.PI / 180;
        // X (pitch), Y (yaw), Z (roll) in the parent's frame
        return NQuaternion.CreateFromAxisAngle(NVector3.UnitZ, deg.Z * r)
             * NQuaternion.CreateFromAxisAngle(NVector3.UnitY, deg.Y * r)
             * NQuaternion.CreateFromAxisAngle(NVector3.UnitX, deg.X * r);
    }

    private void ScheduleDeform()
    {
        _deformTimer ??= new DispatcherTimer(TimeSpan.FromMilliseconds(40), DispatcherPriority.Background, (_, _) => { _deformTimer!.Stop(); UpdateDeformation(); }, Dispatcher);
        _deformTimer.Stop();
        _deformTimer.Start();
    }

    // ------------------------------------------------------------------ pose tab

    private FrameworkElement BuildPosePanel()
    {
        var sk = Skeleton;
        if (sk is null)
            return new TextBlock { Text = "This object has no __Skeleton (rigid geometry).", Foreground = Ui.Res("Muted"), Margin = new Thickness(12), TextWrapping = TextWrapping.Wrap };

        var depth = new int[sk.Bones.Count];
        foreach (var b in sk.Bones) depth[b.Index] = b.Parent >= 0 && b.Parent < b.Index ? depth[b.Parent] + 1 : 0;
        var used = _file.Models.SelectMany(m => m.Mesh.Skin).SelectMany(s => new[] { s.B0, s.B1, s.B2 }).Where(b => b >= 0).ToHashSet();
        var items = sk.Bones.Select(b => new BoneRow(b.Index, new string(' ', depth[b.Index] * 2) + b.Name, used.Contains(b.Index))).ToList();
        var list = new ListBox { ItemsSource = items, Background = Ui.Res("PanelBg"), FontSize = 11.5 };
        list.ItemTemplate = BoneTemplate();
        list.SelectionChanged += (_, _) => { if (list.SelectedItem is BoneRow r) SelectBone(r.Index); };

        var sliders = new StackPanel { Margin = new Thickness(10, 6, 10, 6) };
        string[] axes = ["X", "Y", "Z"];
        for (var a = 0; a < 3; a++)
        {
            var axis = a;
            var s = _rot[a];
            s.Minimum = -180; s.Maximum = 180; s.Value = 0; s.IsEnabled = false;
            s.ValueChanged += (_, e) =>
            {
                _rotText[axis].Text = $"{e.NewValue:0}°";
                if (_syncingSliders || _selBone < 0) return;
                var v = _boneEuler.GetValueOrDefault(_selBone);
                v = axis switch { 0 => v with { X = (float)e.NewValue }, 1 => v with { Y = (float)e.NewValue }, _ => v with { Z = (float)e.NewValue } };
                if (v == NVector3.Zero) _boneEuler.Remove(_selBone); else _boneEuler[_selBone] = v;
                ScheduleDeform();
            };
            sliders.Children.Add(B.Cols((B.Text(axes[a], "Muted", 11), B.Px(18)), (s, B.Star()), (_rotText[a], B.Px(42))));
        }

        var bones = new CheckBox { Content = "Show skeleton", IsChecked = _showBones, Margin = new Thickness(10, 4, 0, 4) };
        bones.Click += (_, _) => { _showBones = bones.IsChecked == true; Rebuild(); };
        var buttons = new WrapPanel { Margin = new Thickness(8, 0, 8, 6) };
        buttons.Children.Add(B.Button("Reset bone", null, (_, _) => { if (_selBone >= 0) { _boneEuler.Remove(_selBone); SelectBone(_selBone); ScheduleDeform(); } }));
        buttons.Children.Add(B.Button("Reset pose", null, (_, _) => { _boneEuler.Clear(); SelectBone(_selBone); ScheduleDeform(); }));
        buttons.Children.Add(B.Button("Arms down", null, (_, _) => ApplyPreset("RArm:0,0,60;LArm:0,0,-60")));
        buttons.Children.Add(B.Button("Wave", null, (_, _) => ApplyPreset("RArm:0,0,-50;RForearm:0,0,-60;Head:0,-20,0;LArm:0,0,-60")));

        var header = new StackPanel { Margin = new Thickness(10, 8, 10, 2) };
        header.Children.Add(B.Text($"{sk.Name} · {sk.Bones.Count} bones · bind error {sk.BindError:0.0e0}", "Faint", 10.5));
        header.Children.Add(B.Text("Rotations use model axes around the bone's joint; children follow. Dimmed bones aren't weighted in this model.", "Faint", 10.5));
        ((TextBlock)header.Children[1]).TextWrapping = TextWrapping.Wrap;

        return B.Rows((header, B.Auto), (sliders, B.Auto), (buttons, B.Auto), (bones, B.Auto), (list, B.Star()));
    }

    private sealed record BoneRow(int Index, string Label, bool Used);

    private static DataTemplate BoneTemplate()
    {
        var t = new FrameworkElementFactory(typeof(TextBlock));
        t.SetBinding(TextBlock.TextProperty, new System.Windows.Data.Binding(nameof(BoneRow.Label)));
        t.SetValue(TextBlock.FontFamilyProperty, Application.Current.Resources["Mono"]);
        var style = new Style(typeof(TextBlock));
        style.Triggers.Add(new DataTrigger { Binding = new System.Windows.Data.Binding(nameof(BoneRow.Used)), Value = false, Setters = { new Setter(TextBlock.ForegroundProperty, Ui.Res("Faint")) } });
        t.SetValue(TextBlock.StyleProperty, style);
        return new DataTemplate { VisualTree = t };
    }

    private void SelectBone(int index)
    {
        _selBone = index;
        var v = _boneEuler.GetValueOrDefault(index);
        _syncingSliders = true;
        _rot[0].Value = v.X; _rot[1].Value = v.Y; _rot[2].Value = v.Z;
        foreach (var s in _rot) s.IsEnabled = index >= 0;
        _syncingSliders = false;
        if (_showBones) Rebuild();
    }

    /// <summary>"Bone:x,y,z;Bone:x,y,z" (degrees).</summary>
    public void ApplyPreset(string spec)
    {
        if (Skeleton is not { } sk) return;
        _boneEuler.Clear();
        foreach (var part in spec.Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            var kv = part.Split(':');
            var bone = sk.Bones.FirstOrDefault(b => b.Name.Equals(kv[0].Trim(), StringComparison.OrdinalIgnoreCase));
            if (bone is null || kv.Length < 2) continue;
            var n = kv[1].Split(',').Select(x => float.Parse(x, CultureInfo.InvariantCulture)).ToArray();
            if (n.Length == 3) _boneEuler[bone.Index] = new NVector3(n[0], n[1], n[2]);
        }
        SelectBone(_selBone);
        ScheduleDeform();
    }

    // ------------------------------------------------------------------ shapes tab

    private FrameworkElement BuildShapesPanel()
    {
        _shapePanel = new StackPanel { Margin = new Thickness(10, 8, 10, 10) };
        RefreshShapes();
        return new ScrollViewer { Content = _shapePanel, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
    }

    private void RefreshShapes()
    {
        if (_shapePanel is null) return;
        _shapePanel.Children.Clear();
        if (_morphs is null)
        {
            _shapePanel.Children.Add(new TextBlock { Text = "No blend shapes for this model (heads keep them in <name>_morph.ord).", Foreground = Ui.Res("Muted"), TextWrapping = TextWrapping.Wrap });
            return;
        }
        var bound = _morphBound?.Sum(b => b.Count) ?? 0;
        _shapePanel.Children.Add(B.Text($"{_morphs.Shapes.Count} shapes · {bound} records bound to mesh", "Faint", 10.5));
        var reset = B.Button("Reset all", null, (_, _) => { Array.Clear(_morphWeights); RefreshShapes(); ScheduleDeform(); });
        reset.HorizontalAlignment = HorizontalAlignment.Left;
        reset.Margin = new Thickness(0, 6, 0, 6);
        _shapePanel.Children.Add(reset);
        for (var i = 0; i < _morphs.Shapes.Count; i++)
        {
            var idx = i;
            var value = B.Text($"{_morphWeights[i]:0.00}", "Muted", 11);
            var s = new Slider { Minimum = 0, Maximum = 1, Value = _morphWeights[i] };
            s.ValueChanged += (_, e) => { _morphWeights[idx] = (float)e.NewValue; value.Text = $"{e.NewValue:0.00}"; ScheduleDeform(); };
            var verts = _morphBound?[i].Sum(r => r.Deltas.Length / 3) ?? 0;
            var label = B.Text(_morphs.ShapeNames[i].Replace("Shape", ""), "Text", 11.5);
            label.ToolTip = $"{_morphs.ShapeNames[i]} — {_morphs.Shapes[i].Count} batches, {verts} vertices";
            var row = B.Cols((label, B.Px(52)), (s, B.Star()), (value, B.Px(36)));
            row.Margin = new Thickness(0, 2, 0, 2);
            _shapePanel.Children.Add(row);
        }
    }

    // ------------------------------------------------------------------ skeleton overlay

    private void AddSkeletonOverlay()
    {
        _overlay.Children.Clear();
        if (!_showBones || Skeleton is not { } sk) return;
        var world = CurrentWorld() ?? sk.Pose(_boneEuler.ToDictionary(kv => kv.Key, kv => Euler(kv.Value)));
        var g = new MeshGeometry3D();
        var sel = new MeshGeometry3D();
        var r = (float)(_radius * 0.012);
        for (var i = 0; i < sk.Bones.Count; i++)
        {
            var p = world[i].Translation;
            var target = i == _selBone ? sel : g;
            AddOctahedron(target, p, r * (i == _selBone ? 2.2f : 1.4f));
            var parent = sk.Bones[i].Parent;
            // prop / IK helper bones sit away from their parent; a stick to them is misleading
            var helper = IsHelperBone(sk.Bones[i].Name);
            if (parent >= 0 && parent < i && !helper) AddBoneStick(target, world[parent].Translation, p, r * 0.6f);
        }
        void Add(MeshGeometry3D m, Color c)
        {
            if (m.Positions.Count == 0) return;
            var mat = new MaterialGroup();
            mat.Children.Add(new DiffuseMaterial(new SolidColorBrush(Color.FromRgb(0, 0, 0))));
            mat.Children.Add(new EmissiveMaterial(new SolidColorBrush(c)));
            mat.Freeze();
            _overlay.Children.Add(new GeometryModel3D(m, mat) { BackMaterial = mat });
        }
        Add(g, Color.FromRgb(0x6A, 0xA8, 0xF0));
        Add(sel, Color.FromRgb(0xF0, 0x91, 0x2E));
    }

    private static bool IsHelperBone(string name) =>
        name is "Bat" or "Ball" or "Trajectory" or "FBXdummyNode" || name.Contains("Constraint", StringComparison.OrdinalIgnoreCase);

    private static void AddOctahedron(MeshGeometry3D m, NVector3 c, float r)
    {
        var b = m.Positions.Count;
        foreach (var d in new[] { NVector3.UnitX, -NVector3.UnitX, NVector3.UnitY, -NVector3.UnitY, NVector3.UnitZ, -NVector3.UnitZ })
            m.Positions.Add(P(c + d * r));
        int[] f = [0, 2, 4, 2, 1, 4, 1, 3, 4, 3, 0, 4, 2, 0, 5, 1, 2, 5, 3, 1, 5, 0, 3, 5];
        foreach (var i in f) m.TriangleIndices.Add(b + i);
    }

    private static void AddBoneStick(MeshGeometry3D m, NVector3 a, NVector3 b, float r)
    {
        var dir = b - a;
        if (dir.LengthSquared() < 1e-8f) return;
        dir = NVector3.Normalize(dir);
        var side = NVector3.Normalize(NVector3.Cross(dir, MathF.Abs(dir.Y) < 0.9f ? NVector3.UnitY : NVector3.UnitX)) * r;
        var up = NVector3.Normalize(NVector3.Cross(dir, side)) * r;
        var bi = m.Positions.Count;
        foreach (var o in new[] { side, up, -side, -up }) { m.Positions.Add(P(a + o)); m.Positions.Add(P(b + o * 0.35f)); }
        for (var k = 0; k < 4; k++)
        {
            int a0 = bi + k * 2, a1 = a0 + 1, b0 = bi + (k + 1) % 4 * 2, b1 = b0 + 1;
            foreach (var i in new[] { a0, b0, a1, a1, b0, b1 }) m.TriangleIndices.Add(i);
        }
    }

    private static Point3D P(NVector3 v) => new(v.X, v.Y, v.Z);

    /// <summary>Dev aid for --snapshot: --pose "Bone:x,y,z;…" --morph "3:1,7:0.5" --bones.</summary>
    private void ApplyCommandLine()
    {
        var args = Environment.GetCommandLineArgs();
        if (Array.IndexOf(args, "--pose") is var pi and > 0) ApplyPreset(args[pi + 1]);
        if (Array.IndexOf(args, "--morph") is var mi and > 0)
            foreach (var kv in args[mi + 1].Split(',').Select(x => x.Split(':')))
                if (int.TryParse(kv[0], out var s) && s < _morphWeights.Length) _morphWeights[s] = float.Parse(kv[1], CultureInfo.InvariantCulture);
        if (args.Contains("--bones")) _showBones = true;
        if (Array.IndexOf(args, "--variant") is var vi and > 0 && int.TryParse(args[vi + 1], out var v)) SetVariant(v);
        if (args.Contains("--morph") || args.Contains("--bones")) { RefreshShapes(); ScheduleDeform(); }
    }
}
