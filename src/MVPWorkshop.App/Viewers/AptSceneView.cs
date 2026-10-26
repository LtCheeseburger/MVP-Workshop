using System.Globalization;
using System.Numerics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using System.Windows.Threading;
using MVPWorkshop.Core.Formats;
using MVPWorkshop.Core.Formats.Eagl;
using MVPWorkshop.Core.Vfs;

namespace MVPWorkshop.App.Viewers;

/// <summary>
/// APT screen reconstruction. Shapes are drawn from their paired native EAGL geometry (vertex colours, texel-unit UVs from the draw's
/// matrix, TAR clamp/repeat, GS blend state) by a GS-style software rasteriser; text uses the game's own TrueType faces; the placement's
/// colour transform (multiply + add) is applied per fragment. Anything not reproduced is listed in the Limitations tab.
/// </summary>
public sealed partial class AptSceneView : Grid
{
    private sealed class Movie
    {
        public required AssetNode Node;
        public required AptFile Apt;
        public AptConstants? Constants;
        public Dictionary<int, EaglModel> Shapes = [];
        public Dictionary<string, AptTexture> Images = new(StringComparer.OrdinalIgnoreCase);
        public List<string> Notes = [];
    }
    private sealed record Element(string Path, string Detail, Vector2[] Quad);
    private readonly Dictionary<AssetNode, Movie> _movies = [];
    private readonly Movie _root;
    private readonly Canvas _stage;
    private readonly Image _picture = new() { Stretch = Stretch.Fill, SnapsToDevicePixels = true };
    private readonly Canvas _overlay = new() { IsHitTestVisible = false };
    private readonly Slider _frame = new() { Minimum = 0, IsSnapToTickEnabled = true, TickFrequency = 1, Width = 170 };
    private readonly ComboBox _target = new() { Width = 210, DisplayMemberPath = "Label" };
    private sealed record Target(AptCharacter Character, string Label) { public override string ToString() => Label; }
    private readonly ComboBox _button = new() { ItemsSource = new[] { "Up", "Over", "Down", "Hit" }, SelectedIndex = 0, Width = 85 };
    private readonly ComboBox _action = new() { Width = 170 };
    private readonly ComboBox _backdrop = new() { ItemsSource = new[] { "None", "bgPS2" }, SelectedIndex = 0, Width = 75 };
    private readonly ComboBox _scripts = new() { ItemsSource = new[] { "Run", "Off" }, SelectedIndex = 0, Width = 60 };
    private readonly ComboBox _quality = new() { ItemsSource = new[] { "Native 1×", "Smooth 2×" }, SelectedIndex = 0, Width = 100 };
    private readonly TextBlock _status = B.Text("Loading…", "Muted");
    private readonly TextBox _detail = new() { IsReadOnly = true, TextWrapping = TextWrapping.Wrap, Padding = new Thickness(10), VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
    private readonly ListBox _instances = new();
    private readonly TextBox _limitations = new() { IsReadOnly = true, TextWrapping = TextWrapping.Wrap, Padding = new Thickness(8), VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
    private readonly HashSet<string> _warnings = [];
    private readonly HashSet<string> _substitutions = [];
    private readonly CheckBox _bounds = new() { Content = "Bounds", Margin = new Thickness(8, 0, 8, 0) };
    private readonly DispatcherTimer _timer = new();
    private List<AssetNode> _index = [];
    private int _draws, _triangles;
    private bool _loading;
    private readonly Dictionary<string, AptNativeFont> _fonts = new(StringComparer.OrdinalIgnoreCase);
    private AptNativeFont? _defaultFont;
    private AptLocale? _locale;
    private List<Element> _elements = [];
    private BitmapSource? _bitmap;
    private Polygon? _selection;
    private uint? _background;
    private const string DefaultFontFile = "EASSanCB.ttf";   // the single entry of EASOAPT.BIG/fonts.txt

    private static Vector4 Color4(uint value) => new((value & 255) / 255f, ((value >> 8) & 255) / 255f, ((value >> 16) & 255) / 255f, (value >> 24) / 255f);
    private int Scale => _quality.SelectedIndex == 1 ? 2 : 1;

    private void LoadFonts()
    {
        foreach (var node in _index.Where(n => n.Extension == ".ttf" && n.Path.Contains("EASOAPT", StringComparison.OrdinalIgnoreCase)))
        {
            try
            {
                var face = new AptNativeFont(node.ReadAll());
                foreach (var name in face.Names) _fonts.TryAdd(name, face);
                if (node.Name.Equals(DefaultFontFile, StringComparison.OrdinalIgnoreCase)) { _fonts["EAS SansCondensed"] = face; _defaultFont = face; }
            }
            catch (Exception ex) { _root.Notes.Add($"Font {node.Name}: {ex.Message}"); }
        }
        if (_index.FirstOrDefault(n => n.Name.Equals("10_easo.loc", StringComparison.OrdinalIgnoreCase)) is { } loc)
            try { _locale = AptLocale.Parse(loc.ReadAll()); } catch (Exception ex) { _root.Notes.Add($"Localisation: {ex.Message}"); }
    }

    /// <summary>Greedy word wrap in font units, honouring explicit line breaks.</summary>
    private static List<string> Lines(string value, AptNativeFont face, double width, double scale, bool wrap)
    {
        var lines = new List<string>();
        foreach (var paragraph in value.Replace("\r", "").Split('\n').Take(256))
        {
            if (!wrap) { lines.Add(paragraph); continue; }
            var line = "";
            foreach (var word in paragraph.Split(' '))
            {
                var candidate = line.Length == 0 ? word : line + " " + word;
                if (line.Length > 0 && Measure(candidate, face) * scale > width) { lines.Add(line); line = word; } else line = candidate;
            }
            lines.Add(line);
        }
        return lines;
    }

    private static double Measure(string text, AptNativeFont face)
    {
        double width = 0; int previous = -1;
        foreach (var rune in text.EnumerateRunes().Take(4096))
        {
            var glyph = face.Glyph(rune.Value);
            if (previous >= 0) width += face.Kerning(previous, glyph);
            width += face.Advance(glyph); previous = glyph;
        }
        return width;
    }

    /// <summary>Render an EditText into a straight-alpha white-on-transparent image at its device-space position.</summary>
    private (AptTexture Image, int X, int Y)? RenderText(Movie movie, AptCharacter character, Rect bounds, Matrix3x2 device, string value)
    {
        var font = character.FontId >= 0 && character.FontId < movie.Apt.Characters.Count ? movie.Apt.Characters[character.FontId] : null;
        var name = font?.FontName ?? "";
        if (!_fonts.TryGetValue(name, out var face))
        {
            if (_defaultFont is null) { _warnings.Add($"No native APT font available for '{name}'"); return null; }
            face = _defaultFont; _substitutions.Add($"Font '{name}' is not shipped; drawn with {DefaultFontFile} (the only face in EASOAPT.BIG/fonts.txt)");
        }
        if (font?.Fields.Any(f => f.Key == "glyphs" && f.Value != "0") == true) _warnings.Add("Embedded APT font glyph outlines are unsupported");
        double size = Math.Clamp(character.FontHeight, 1, 256), scale = size / face.Units;
        var lines = Lines(value, face, bounds.Width - 4, scale, character.WordWrap && character.Multiline);
        if (!character.Multiline && lines.Count > 1) lines = [string.Join(" ", lines)];

        var corners = new[] { new Vector2((float)bounds.Left, (float)bounds.Top), new Vector2((float)bounds.Right, (float)bounds.Top), new Vector2((float)bounds.Right, (float)bounds.Bottom), new Vector2((float)bounds.Left, (float)bounds.Bottom) }
            .Select(c => Vector2.Transform(c, device)).ToArray();
        int x0 = (int)Math.Floor(corners.Min(c => c.X)) - 1, y0 = (int)Math.Floor(corners.Min(c => c.Y)) - 1;
        int w = (int)Math.Ceiling(corners.Max(c => c.X)) + 1 - x0, h = (int)Math.Ceiling(corners.Max(c => c.Y)) + 1 - y0;
        if (w <= 0 || h <= 0 || w > 4096 || h > 4096 || !corners.All(c => float.IsFinite(c.X + c.Y))) return null;

        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            dc.PushTransform(new MatrixTransform(device.M11, device.M12, device.M21, device.M22, device.M31 - x0, device.M32 - y0));
            dc.PushClip(new RectangleGeometry(bounds));
            double y = bounds.Y + 2 + face.Ascent * scale;
            foreach (var line in lines)
            {
                var glyphs = line.EnumerateRunes().Take(4096).Select(r => { if (!face.Has(r.Value)) _warnings.Add($"Missing glyph U+{r.Value:X} in {name}"); return face.Glyph(r.Value); }).ToList();
                double width = Measure(line, face) * scale;
                double x = bounds.X + 2 + character.TextAlign switch { 1 => Math.Max(0, bounds.Width - 4 - width), 2 => Math.Max(0, (bounds.Width - 4 - width) / 2), _ => 0 };
                int previous = -1;
                foreach (var glyph in glyphs)
                {
                    if (previous >= 0) x += face.Kerning(previous, glyph) * scale;
                    dc.PushTransform(new MatrixTransform(scale, 0, 0, scale, x, y));
                    dc.DrawGeometry(Brushes.White, null, face.Outline(glyph)); dc.Pop();
                    x += face.Advance(glyph) * scale; previous = glyph;
                }
                y += face.LineHeight * scale;
            }
            dc.Pop(); dc.Pop();
        }
        var target = new RenderTargetBitmap(w, h, 96, 96, PixelFormats.Pbgra32); target.Render(visual);
        var pixels = new byte[w * h * 4]; target.CopyPixels(pixels, w * 4, 0);
        for (int i = 0; i < pixels.Length; i += 4)      // Pbgra32 → straight alpha
            if (pixels[i + 3] is > 0 and < 255) { var a = pixels[i + 3]; pixels[i] = (byte)Math.Min(255, pixels[i] * 255 / a); pixels[i + 1] = (byte)Math.Min(255, pixels[i + 1] * 255 / a); pixels[i + 2] = (byte)Math.Min(255, pixels[i + 2] * 255 / a); }
        return (new AptTexture(w, h, pixels), x0, y0);
    }

    public AptSceneView(AssetNode node, AptFile apt)
    {
        _root = Load(node, apt);
        _stage = new Canvas { Width = Math.Clamp(apt.Width, 1, 4096), Height = Math.Clamp(apt.Height, 1, 4096), ClipToBounds = true, Background = Brushes.Black };
        _picture.Width = _stage.Width; _picture.Height = _stage.Height;
        _stage.Children.Add(_picture); _stage.Children.Add(_overlay);
        _target.ItemsSource = apt.Characters.OfType<AptCharacter>().Where(c => c.Type is AptCharacterType.Movie or AptCharacterType.Sprite or AptCharacterType.Button)
            .Select(c => new Target(c, c == apt.Movie ? "Movie" : $"[{c.Id}] {c.Type} · {apt.Exports.FirstOrDefault(e => e.Character == c.Id)?.Name}")).Prepend(new Target(apt.Movie, "Movie")).DistinctBy(t => t.Character).ToList();
        _target.SelectedIndex = 0;
        _backdrop.SelectedIndex = node.Path.Contains("/screens/", StringComparison.OrdinalIgnoreCase) && !node.Path.Contains("/screens/bg/", StringComparison.OrdinalIgnoreCase) && !node.Path.Contains("/screens/foreground/", StringComparison.OrdinalIgnoreCase) ? 1 : 0;
        var toolbar = B.Toolbar(B.Text("Timeline"), _target, B.Button("Play / pause", null, (_, _) => { if (_timer.IsEnabled) _timer.Stop(); else _timer.Start(); }, "Outline"), _frame,
            B.Text("Button"), _button, B.Text("Action"), _action, B.Text("Backdrop"), _backdrop, B.Text("Scripts"), _scripts, B.Text("Raster"), _quality, _bounds, B.Button("Export PNG", null, (_, _) => Export(), "Outline"));
        var viewport = new Viewbox { Child = _stage, Margin = new Thickness(12), Stretch = Stretch.Uniform };
        var inspector = new TabControl(); inspector.Items.Add(new TabItem { Header = "Selection", Content = _detail }); inspector.Items.Add(new TabItem { Header = "Limitations", Content = _limitations });
        Children.Add(B.Rows((toolbar, B.Auto), (B.Cols((B.Rows((_instances, B.Star()), (inspector, B.Px(220))), B.Px(260)), (B.VSplit(), B.Px(4)), (viewport, B.Star())), B.Star()), (_status, B.Auto)));
        _timer.Interval = TimeSpan.FromMilliseconds(Math.Clamp(apt.FrameTime, 10, 1000));
        _timer.Tick += (_, _) => _frame.Value = _frame.Value >= _frame.Maximum ? 0 : _frame.Value + 1;
        _target.SelectionChanged += (_, _) => { ResetTimeline(); Render(); };
        ResetTimeline();
        _frame.ValueChanged += (_, _) => Render();
        _button.SelectionChanged += (_, _) => Render(); _bounds.Click += (_, _) => Render();
        _action.SelectionChanged += (_, _) => Render(); _quality.SelectionChanged += (_, _) => Render(); _scripts.SelectionChanged += (_, _) => Render(); _backdrop.SelectionChanged += (_, _) => Render();
        _instances.SelectionChanged += (_, _) => { if (_instances.SelectedItem is string item) _detail.Text = item; };
        _picture.MouseLeftButtonDown += (_, e) => Pick(e.GetPosition(_picture));
        Unloaded += (_, _) => _timer.Stop();
        Loaded += async (_, _) =>
        {
            if (_loading) return; _loading = true;
            try
            {
                if (MainWindow.Instance is { } window) _index = await window.GetAssetIndex();
                LoadFonts();
                ResetTimeline();
                Render();
            }
            catch (Exception ex) { _status.Text = ex.Message; }
        };
        Render();
    }

    private AptCharacter Selected => (_target.SelectedItem as Target)?.Character ?? _root.Apt.Movie;

    /// <summary>Script path → clip, found by following named instances down from the movie being previewed.</summary>
    private AptPreviewScript.SpriteInfo? Locate(Movie root, AptCharacter timeline, string path)
    {
        var movie = root; var character = timeline; string[]? linkage = null;
        foreach (var segment in path.Split('/'))
        {
            AptInstance? found = null;
            for (int f = 0; f < character.Frames.Count && found is null; f++)
                found = AptTimeline.Sample(character, f).FirstOrDefault(i => (i.Name.Length > 0 ? i.Name : $"depth {i.Depth}") == segment);
            if (found is null) return null;
            var resolved = Resolve(movie, found.Character);
            if (resolved.Character is not { } child) return null;
            linkage = [.. movie.Apt.Imports.Where(i => i.Character == found.Character).Select(i => i.Name).Concat(resolved.Movie.Apt.Exports.Where(e => e.Character == child.Id).Select(e => e.Name)).Distinct()];
            (movie, character) = (resolved.Movie, child);
        }
        return character.Type is AptCharacterType.Sprite or AptCharacterType.Movie && linkage is not null ? new AptPreviewScript.SpriteInfo(movie.Apt, movie.Constants, character, linkage) : null;
    }

    private AptPreviewScript NewScript()
    {
        var script = new AptPreviewScript { Locale = _locale, Enabled = _scripts.SelectedIndex == 0, Interactive = Simulation is not null };
        script.Execution += message => Simulation?.Log("native script", message);
        script.Locator = path => Locate(_root, Selected, path);
        LoadShared(script);
        script.Initialize(_root.Apt, _root.Constants, Selected, Selected.Frames.Count == 0 ? 0 : (int)_frame.Value % Selected.Frames.Count);
        return script;
    }

    /// <summary>The OSDK screens reach shared services through <c>_root.gEaso</c> (palette, helper classes); load that module's scripts first.</summary>
    private void LoadShared(AptPreviewScript script)
    {
        if (_root.Node.Name.Equals("gEaso.apt", StringComparison.OrdinalIgnoreCase)) return;
        if (_index.FirstOrDefault(n => n.Name.Equals("gEaso.apt", StringComparison.OrdinalIgnoreCase) && n.Path.Contains("OSDKInterface", StringComparison.OrdinalIgnoreCase)) is not { } node) return;
        try { var shared = Load(node); script.LoadEnvironment(shared.Apt, shared.Constants, "gEaso"); }
        catch (Exception ex) { _warnings.Add($"gEaso module: {ex.Message}"); }
    }

    private void ResetTimeline()
    {
        _frame.Maximum = Selected.Frames.Count > 1 ? Selected.Frames.Count - 1 : Math.Ceiling(5000.0 / Math.Max(1, _root.Apt.FrameTime));
        _frame.Value = 0;
        var script = new AptPreviewScript { Locale = _locale }; LoadShared(script); script.Initialize(_root.Apt, _root.Constants, Selected, 0);
        _action.ItemsSource = new[] { "Startup only" }.Concat(script.Functions.Order()).ToList(); _action.SelectedIndex = 0;
    }

    private Movie Load(AssetNode node, AptFile? apt = null)
    {
        if (_movies.TryGetValue(node, out var cached)) return cached;
        apt ??= AptFile.Read(node.ReadAll(), AptView.Sibling(node, ".const") is { } cn ? AptConstants.Read(cn.ReadAll()).MovieOffset : 0);
        var movie = new Movie { Node = node, Apt = apt, Constants = AptView.Sibling(node, ".const") is { } constantNode ? AptConstants.Read(constantNode.ReadAll()) : null }; _movies[node] = movie;
        if (AptView.Sibling(node, ".o") is { } geometry)
        {
            try
            {
                var file = EaglReader.Read(geometry.ReadAll());
                foreach (var model in file.Models)
                    if (int.TryParse(model.Name, out var id)) movie.Shapes[id] = model;
                movie.Notes.AddRange(file.Models.SelectMany(m => m.Mesh.Warnings));
            }
            catch (Exception ex) { movie.Notes.Add($"Geometry: {ex.Message}"); }
        }
        if (AptView.Sibling(node, ".ssh") is { } textures)
        {
            try
            {
                var ssh = SshFile.Read(textures.ReadAll());
                var originalTextures = SshFile.Read(textures.ReadOriginal());
                foreach (var resized in ssh.Images.Where(i => i.Index < originalTextures.Images.Count && (i.Header.Width != originalTextures.Images[i.Index].Header.Width || i.Header.Height != originalTextures.Images[i.Index].Header.Height)))
                    movie.Notes.Add($"Native upscale {resized.Tag}: {resized.Header.Width}×{resized.Header.Height}; authored APT texel matrices and game GS allocation are unchanged. Runtime compatibility is experimental.");
                foreach (var image in ssh.Images)
                {
                    var decoded = ssh.Decode(image);
                    movie.Images[image.Tag.Trim('\0', ' ')] = new AptTexture(decoded.Width, decoded.Height, decoded.Bgra);
                }
            }
            catch (Exception ex) { movie.Notes.Add($"Textures: {ex.Message}"); }
        }
        return movie;
    }

    private (Movie Movie, AptCharacter? Character) Resolve(Movie movie, int id)
    {
        var import = movie.Apt.Imports.FirstOrDefault(i => i.Character == id);
        if (import is null) return (movie, id >= 0 && id < movie.Apt.Characters.Count ? movie.Apt.Characters[id] : null);
        var name = System.IO.Path.ChangeExtension(import.Movie.Replace('\\', '/').Split('/').Last(), ".apt");
        var candidates = _index.Where(n => n.Name.Equals(name, StringComparison.OrdinalIgnoreCase)).OrderByDescending(n => n.Parent == movie.Node.Parent).ToList();
        if (candidates.Count > 1 && candidates[0].Parent != movie.Node.Parent) { _warnings.Add($"Ambiguous import {import.Movie}"); return (movie, null); }
        if (candidates.FirstOrDefault() is not { } node) { _warnings.Add($"Missing import {import.Movie}"); return (movie, null); }
        var loaded = Load(node);
        var export = loaded.Apt.Exports.FirstOrDefault(e => e.Name == import.Name);
        return (loaded, export is not null && export.Character >= 0 && export.Character < loaded.Apt.Characters.Count ? loaded.Apt.Characters[export.Character] : null);
    }

    private static Vector2[] QuadOf(Rect r, Matrix3x2 m) => [Vector2.Transform(new((float)r.Left, (float)r.Top), m), Vector2.Transform(new((float)r.Right, (float)r.Top), m), Vector2.Transform(new((float)r.Right, (float)r.Bottom), m), Vector2.Transform(new((float)r.Left, (float)r.Bottom), m)];

    /// <summary>Pixels covered by the geometry of a clip-depth mask character.</summary>
    private void FillMask(AptSurface surface, bool[] mask, Movie movie, AptCharacter character, int frame, Matrix3x2 matrix, int level = 0)
    {
        if (level > 16) { _warnings.Add("Mask recursion limit reached"); return; }
        if (character.Type is AptCharacterType.Sprite or AptCharacterType.Movie)
        {
            if (character.Frames.Count == 0) return;
            foreach (var instance in AptTimeline.Sample(character, frame % character.Frames.Count))
            {
                var child = Resolve(movie, instance.Character);
                if (child.Character is not null) FillMask(surface, mask, child.Movie, child.Character, Math.Max(0, frame - instance.Born), instance.Matrix * matrix, level + 1);
            }
            return;
        }
        if (character.GeometryId is { } id && movie.Shapes.TryGetValue(id, out var model))
        {
            var mesh = model.Mesh;
            for (int i = 0; i + 2 < mesh.Indices.Count; i += 3)
            {
                Vector2 P(int k) => Vector2.Transform(new(mesh.Positions[mesh.Indices[i + k]].X, mesh.Positions[mesh.Indices[i + k]].Y), matrix);
                surface.MaskTriangle(mask, P(0), P(1), P(2));
            }
        }
        else if (character.Bounds is { } b)
        {
            var q = QuadOf(new Rect(b.X, b.Y, Math.Max(0, b.Z - b.X), Math.Max(0, b.W - b.Y)), matrix);
            surface.MaskTriangle(mask, q[0], q[1], q[2]); surface.MaskTriangle(mask, q[0], q[2], q[3]);
            _warnings.Add("Mask uses character bounds where native geometry is unavailable");
        }
    }

    private void Render()
    {
        if (_stage is null || _picture is null) return;
        var clock = System.Diagnostics.Stopwatch.StartNew();
        _warnings.Clear(); _substitutions.Clear(); _draws = 0; _triangles = 0;
        var script = Simulation is null ? NewScript() : (_interactiveScript ??= NewScript());
        if (Simulation is not null) { script.Interactive = true; _simulationControls.Clear(); }
        var mainScript = script;
        if (Simulation is null && _action.SelectedIndex > 0 && _action.SelectedItem is string action) script.Invoke(action);
        script.Advance(Simulation is null ? _frame.Value * Math.Max(1, _root.Apt.FrameTime) : _simulationTime);
        foreach (var note in _movies.Values.SelectMany(m => m.Notes)) _warnings.Add(note);

        int scale = Scale; int width = (int)_stage.Width * scale, height = (int)_stage.Height * scale;
        var surface = new AptSurface(width, height);
        var background = _root.Apt.Movie.Frames.SelectMany(f => f.Items).FirstOrDefault(i => i.Type == 5)?.Background;
        _background = background;
        surface.Clear(0, 0, 0);
        var rows = new List<string>(); var elements = new List<Element>();
        bool collecting = true;
        var toDevice = Matrix3x2.CreateScale(scale);

        void Walk(Movie movie, AptCharacter character, int frame, Matrix3x2 transform, Vector4 mult, Vector4 add, string path, int level, bool[]? clip)
        {
            if (level > 32 || _draws > 20000) { _warnings.Add("Display-list recursion/draw limit reached"); return; }
            if (!float.IsFinite(transform.M11 + transform.M12 + transform.M21 + transform.M22 + transform.M31 + transform.M32)) { _warnings.Add("Invalid transform"); return; }
            if (character.Type is AptCharacterType.Sprite or AptCharacterType.Movie)
            {
                if (character.Frames.Count == 0) return;
                var masks = new List<(int End, bool[] Pixels)>();
                if (Simulation is not null && path == "root") frame = script.StepTimeline("", movie.Apt, movie.Constants, character, frame % character.Frames.Count);
                foreach (var instance in AptTimeline.Sample(character, frame % character.Frames.Count))
                {
                    masks.RemoveAll(m => m.End < instance.Depth);
                    var resolved = Resolve(movie, instance.Character);
                    var instancePath = path + "/" + (instance.Name.Length > 0 ? instance.Name : $"depth {instance.Depth}");
                    var scriptPath = instancePath.StartsWith("root/", StringComparison.Ordinal) ? instancePath[5..] : instancePath;
                    // run the clip's own frame actions / registered class constructor first: they set _visible, _x, _alpha and colour transforms
                    int localFrame = Math.Max(0, frame - instance.Born);
                    if (resolved.Character is { Type: AptCharacterType.Sprite or AptCharacterType.Movie } sprite && instance.ClipDepth <= instance.Depth)
                    {
                        // linkage names: this movie's import of the symbol, and the exports of the movie that defines it
                        var linkage = movie.Apt.Imports.Where(i => i.Character == instance.Character).Select(i => i.Name).Concat(resolved.Movie.Apt.Exports.Where(e => e.Character == sprite.Id).Select(e => e.Name)).Distinct().ToList();
                        localFrame = script.EnterSprite(scriptPath, resolved.Movie.Apt, resolved.Movie.Constants, sprite, localFrame, linkage);
                    }
                    if (!script.Visible(scriptPath)) continue;
                    if (collecting) rows.Add($"{instancePath} → [{instance.Character}] {resolved.Character?.Type} · {resolved.Movie.Node.Name}");
                    var local = instance.Matrix;
                    var adjusted = Matrix3x2.CreateScale((float)script.Number(scriptPath, "_xscale", 100) / 100, (float)script.Number(scriptPath, "_yscale", 100) / 100)
                        * Matrix3x2.CreateRotation((float)script.Number(scriptPath, "_rotation", 0) * MathF.PI / 180) * local;
                    adjusted.M31 = (float)script.Number(scriptPath, "_x", local.M31); adjusted.M32 = (float)script.Number(scriptPath, "_y", local.M32);
                    var world = adjusted * transform;
                    if (Simulation is not null && resolved.Character?.Type == AptCharacterType.Sprite && script.HasHandler(scriptPath, "onRelease"))
                    { var point = Vector2.Transform(Vector2.Zero, world); _simulationControls.Add(new(scriptPath, point.X, point.Y, script.Number(scriptPath, "enabled", 1) != 0)); }
                    if (instance.ClipDepth > instance.Depth && resolved.Character is { } mask)
                    {
                        var pixels = surface.NewMask();
                        FillMask(surface, pixels, resolved.Movie, mask, Math.Max(0, frame - instance.Born), world * toDevice);
                        masks.Add((instance.ClipDepth, pixels)); continue;
                    }
                    if (resolved.Character is not { } child) { _warnings.Add($"Unresolved character [{instance.Character}] in {movie.Node.Name}"); continue; }

                    bool[]? active = clip; foreach (var m in masks) active = AptSurface.And(active, m.Pixels);
                    // colour transform concatenation: child ∘ parent  →  mult = mc·mp, add = ac·mp + ap
                    var tint = Color4(instance.Tint) * new Vector4(1, 1, 1, (float)(script.Number(scriptPath, "_alpha", 100) / 100));
                    var offset = Color4(instance.Additive);
                    if (script.ColorTransform(scriptPath) is { } scripted)   // Color.setTransform replaces the clip's own RGB transform
                    {
                        tint = new Vector4(scripted.Multiply.X, scripted.Multiply.Y, scripted.Multiply.Z, tint.W * scripted.Multiply.W);
                        offset = new Vector4(scripted.Add.X, scripted.Add.Y, scripted.Add.Z, offset.W + scripted.Add.W);
                    }
                    Walk(resolved.Movie, child, localFrame, world, mult * tint, add + offset * mult, instancePath, level + 1, active);
                }
                return;
            }
            if (character.Type == AptCharacterType.Button)
            {
                var id = path.StartsWith("root/") ? path[5..] : path;
                if (Simulation is not null) { var point = Vector2.Transform(Vector2.Zero, transform); _simulationControls.Add(new(id, point.X, point.Y, script.Visible(id) && script.Number(id, "enabled", 1) != 0)); }
                var state = Simulation is null ? 1 << Math.Max(0, _button.SelectedIndex) : Simulation.Focus == id ? (_pressing ? 4 : 2) : 1;
                foreach (var record in character.ButtonRecords.Where(r => (r.Flags & state) != 0).OrderBy(r => r.Depth))
                {
                    var resolved = Resolve(movie, record.Character);
                    if (resolved.Character is { } child) Walk(resolved.Movie, child, frame, record.Matrix * transform, mult * record.Tint, add, path + "/button", level + 1, clip);
                }
                return;
            }
            DrawLeaf(movie, character, transform, mult, add, path, clip);
        }

        void DrawLeaf(Movie movie, AptCharacter character, Matrix3x2 transform, Vector4 mult, Vector4 add, string path, bool[]? clip)
        {
            var bounds = character.Bounds is { } b ? new Rect(b.X, b.Y, Math.Max(0, b.Z - b.X), Math.Max(0, b.W - b.Y)) : new Rect(0, 0, 32, 32);
            var device = transform * toDevice;
            var detail = $"{path}\n{movie.Node.Path}\nCharacter {character.Id} {character.Type}\nGeometry {character.GeometryId}\n{character.Summary}";
            if (collecting) elements.Add(new Element(path, detail, QuadOf(bounds, transform)));
            _draws++;
            if (character.GeometryId is { } geometryId && movie.Shapes.TryGetValue(geometryId, out var model) && model.Mesh.Indices.Count > 0)
            {
                var mesh = model.Mesh;
                foreach (var draw in mesh.Draws)
                {
                    AptTexture? texture = null;
                    if (draw.Texture is { } tag && draw.State.Textured)
                    {
                        texture = movie.Images.GetValueOrDefault(tag) ?? movie.Images.GetValueOrDefault(tag.PadLeft(4, '0'));
                        if (texture is null) { _warnings.Add($"Missing texture {tag} in {movie.Node.Name}"); continue; }
                        if (draw.TextureMatrix is null) { _warnings.Add($"Missing native texture matrix: {model.Name}/{draw.Index}"); continue; }
                    }
                    var clamp = draw.Clamp;
                    var pass = new AptSurface.Pass(texture, new AptSampler(clamp.U, clamp.V), AptBlend.From(draw.State), mult, add, clip);
                    for (int i = draw.FirstLine; i < draw.FirstLine + draw.LineCount; i++)
                    {
                        AptVertex V(int v) { var p = mesh.Positions[v]; return new AptVertex(p.X, p.Y, 0, 0, mesh.Colors[v]); }
                        surface.Line(V(mesh.Lines[i].A), V(mesh.Lines[i].B), pass, device);
                    }
                    for (int i = draw.FirstTriangle * 3; i < (draw.FirstTriangle + draw.TriangleCount) * 3; i += 3)
                    {
                        AptVertex V(int k) { var v = mesh.Indices[i + k]; var p = mesh.Positions[v]; return new AptVertex(p.X, p.Y, mesh.Uvs[v].X, mesh.Uvs[v].Y, mesh.Colors[v]); }
                        surface.Triangle(V(0), V(1), V(2), pass, device); _triangles++;
                    }
                }
            }
            else if (character.TextureId is { } textureId && (movie.Images.GetValueOrDefault(textureId.ToString()) ?? movie.Images.GetValueOrDefault(textureId.ToString("D4"))) is { } image)
            {
                var pass = new AptSurface.Pass(image, new AptSampler(true, true, false), AptBlend.SourceOver, mult, add, clip);
                var white = Vector4.One; float w = image.Width, h = image.Height;
                surface.Triangle(new(0, 0, 0, 0, white), new(w, 0, w, 0, white), new(0, h, 0, h, white), pass, device);
                surface.Triangle(new(w, 0, w, 0, white), new(w, h, w, h, white), new(0, h, 0, h, white), pass, device);
            }
            else if (character.Type == AptCharacterType.EditText)
            {
                var scriptPath = path.StartsWith("root/", StringComparison.Ordinal) ? path[5..] : path;
                var value = script.Text(scriptPath, "text", character.Text ?? "");
                if (PreviewText is not null) value = PreviewText(scriptPath, value);
                if (value.Length > 0 && RenderText(movie, character, bounds, device, value) is { } text)
                    surface.Blit(text.Image, text.X, text.Y, new AptSurface.Pass(null, default, AptBlend.SourceOver, mult * Color4(character.TextColor), add, clip));
            }
            else _warnings.Add($"Unsupported/missing {character.Type} [{character.Id}]");
        }

        try
        {
            // the game's screen manager shows a screen over the shared background movie; layer it underneath when asked
            if (_backdrop.SelectedIndex == 1 && _index.FirstOrDefault(n => n.Name.Equals("bgPS2.apt", StringComparison.OrdinalIgnoreCase)) is { } bgNode)
            {
                var bg = Load(bgNode);
                script = new AptPreviewScript { Locale = _locale, Enabled = mainScript.Enabled };
                script.Locator = path => Locate(bg, bg.Apt.Movie, path);
                LoadShared(script);
                script.Initialize(bg.Apt, bg.Constants, bg.Apt.Movie, 0);
                collecting = false;
                Walk(bg, bg.Apt.Movie, bg.Apt.Movie.Frames.Count == 0 ? 0 : (int)_frame.Value % bg.Apt.Movie.Frames.Count, Matrix3x2.Identity, Vector4.One, Vector4.Zero, "bg", 0, null);
                collecting = true;
                foreach (var note in script.Limitations) _warnings.Add("bgPS2: " + note);
                script = mainScript;
            }
            Walk(_root, Selected, (int)_frame.Value, Matrix3x2.Identity, Vector4.One, Vector4.Zero, "root", 0, null);
        }
        catch (Exception ex) { _warnings.Add(ex.Message); }
        if (Simulation is not null) Simulation.SetControls(_simulationControls);
        foreach (var note in script.Limitations) _warnings.Add(note);

        _bitmap = BitmapSource.Create(width / scale, height / scale, 96, 96, PixelFormats.Bgr32, null, ToBgr32(surface.ToBgra(scale)), width / scale * 4); _bitmap.Freeze();
        _picture.Source = _bitmap;
        _elements = elements; _selection = null;
        DrawOverlay();
        _instances.ItemsSource = rows;
        _status.Text = $"{_frame.Value * Math.Max(1, _root.Apt.FrameTime) / 1000:0.00}s · timeline frame {(int)_frame.Value % Math.Max(1, Selected.Frames.Count) + 1}/{Math.Max(1, Selected.Frames.Count)} · {_draws} elements · {_triangles} triangles · {_warnings.Count} limitations · {clock.ElapsedMilliseconds} ms";
        var localized = script.Localized.Count == 0 ? "" : "\n\nLocalised strings read by scripts (10_easo.loc):\n" + string.Join("\n", script.Localized.Select(l => $"  {l.Key} = {l.Value}"));
        _limitations.Text = Limitations() + localized;
        _status.ToolTip = _limitations.Text;
    }

    private static byte[] ToBgr32(byte[] bgra) => bgra;   // alpha is 255 throughout; Bgr32 ignores it

    private string Limitations()
    {
        var text = new System.Text.StringBuilder();
        text.AppendLine("Reproduced from the game data");
        text.AppendLine("  · Shape geometry, vertex colours, strip restarts and per-draw texture matrices (texel-unit UVs) from the paired .o");
        text.AppendLine("  · TAR clamp / repeat addressing, alpha-test and GS source-over blend (the only blend state APT geometry uses)");
        text.AppendLine("  · Placement colour transform: multiply (RGBA) and add (RGB) applied per fragment, concatenated through nested clips");
        text.AppendLine("  · Clip-depth masks as per-pixel stencil; imports resolved across movies; EditText alignment, multiline and word-wrap fields");
        text.AppendLine("  · Text outlines from the game's own TrueType faces (with kern pairs when present), localisation from 10_easo.loc");
        text.AppendLine("  · Scripts: locals, parameters, registers, objects/arrays, this-relative properties, timers, stop()/gotoAndStop(), InitActions");
        text.AppendLine();
        text.AppendLine("Still approximate");
        text.AppendLine("  · Colour pipeline is modulate-then-add, evidenced by the placement data; the VU1 TextureApt/GouraudApt microcode is not disassembled");
        text.AppendLine("  · Text is rasterised with WPF anti-aliasing, +2 px field gutter and no hinting; the console glyph rasteriser is not reproduced");
        text.AppendLine("  · Screens are populated at runtime by the game (screen manager, EASO, network, rosters); those calls are listed below and not executed");
        text.AppendLine("  · Fields without a script-set value show their authored placeholder text; runtime values (undefined here) concatenate as empty");
        text.AppendLine("  · Components the game initialises from its screens (player-info bars, spreadsheets, menus) keep their authored colours and layout");
        text.AppendLine($"  · Stage is cleared to black: the movie's authored BackgroundColor{(_background is { } bc ? $" (#{bc:X8})" : "")} is the Flash editor colour, and the game composites these screens over its own scene");
        text.AppendLine("  · Output is 854×480 stage pixels, not the PS2's 640×448 frame buffer; no depth buffer (APT is painter-ordered)");
        if (_substitutions.Count > 0) { text.AppendLine(); text.AppendLine("Substitutions in this movie"); foreach (var s in _substitutions.Order()) text.AppendLine("  · " + s); }
        if (_warnings.Count > 0) { text.AppendLine(); text.AppendLine("Detected in this movie"); foreach (var w in _warnings.Order()) text.AppendLine("  · " + w); }
        return text.ToString();
    }

    private void DrawOverlay()
    {
        _overlay.Children.Clear();
        if (_bounds.IsChecked == true)
            foreach (var element in _elements)
                _overlay.Children.Add(new Polygon { Points = new PointCollection(element.Quad.Select(q => new Point(q.X, q.Y))), Stroke = Brushes.Cyan, StrokeThickness = 0.5, Opacity = 0.8 });
        if (_selection is not null) _overlay.Children.Add(_selection);
    }

    private static bool Inside(Vector2[] q, Point p)
    {
        bool inside = false;
        for (int i = 0, j = q.Length - 1; i < q.Length; j = i++)
            if ((q[i].Y > p.Y) != (q[j].Y > p.Y) && p.X < (q[j].X - q[i].X) * (p.Y - q[i].Y) / (q[j].Y - q[i].Y) + q[i].X) inside = !inside;
        return inside;
    }

    private void Pick(Point at)
    {
        var hit = _elements.LastOrDefault(e => Inside(e.Quad, at));
        if (hit is null) return;
        _detail.Text = hit.Detail;
        _selection = new Polygon { Points = new PointCollection(hit.Quad.Select(q => new Point(q.X, q.Y))), Stroke = Brushes.Orange, StrokeThickness = 1.5 };
        DrawOverlay();
    }

    internal string ValidateFonts()
    {
        var report = new List<string>();
        foreach (var font in _fonts.Values.Distinct())
        {
            int count = 0;
            foreach (var code in font.Codes) { _ = font.Outline(font.Glyph(code)).Bounds; count++; }
            report.Add($"{string.Join(" / ", font.Names.Distinct())}: {count} mapped glyphs validated, units={font.Units}, ascent={font.Ascent}, line={font.LineHeight}, kern pairs={font.KerningPairs}");
        }
        return string.Join("\n", report);
    }
    internal string PreviewLimitations => _limitations.Text;
    /// <summary>Every element under a stage point, topmost first (developer aid for the snapshot mode).</summary>
    internal string ElementsAt(double x, double y) => string.Join("\n---\n", _elements.AsEnumerable().Reverse().Where(e => Inside(e.Quad, new Point(x, y))).Select(e => e.Detail));
    internal void Configure(int frame, string? action = null, bool smooth = false, bool bounds = false, bool scripts = true, bool backdrop = true)
    {
        _quality.SelectedIndex = smooth ? 1 : 0; _bounds.IsChecked = bounds; _scripts.SelectedIndex = scripts ? 0 : 1; if (!backdrop) _backdrop.SelectedIndex = 0;
        if (action is not null && _action.Items.Contains(action)) _action.SelectedItem = action;
        _frame.Value = Math.Clamp(frame, 0, _frame.Maximum); Render();
    }

    /// <summary>Developer aid for the snapshot mode: the stage image at native size, without the window chrome.</summary>
    internal void SaveStage(string path)
    {
        if (_bitmap is null) return;
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(_bitmap));
        using var stream = System.IO.File.Create(path); encoder.Save(stream);
    }

    private void Export()
    {
        if (_bitmap is null) return;
        var dialog = new Microsoft.Win32.SaveFileDialog { Filter = "PNG|*.png", FileName = "apt-preview.png" };
        if (dialog.ShowDialog() != true) return;
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(_bitmap));
        using var stream = System.IO.File.Create(dialog.FileName); encoder.Save(stream);
    }
}
