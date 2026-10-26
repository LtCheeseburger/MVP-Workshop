using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using MVPWorkshop.Core.Formats;
using MVPWorkshop.Core.Vfs;

namespace MVPWorkshop.App.Viewers;

/// <summary>Inspectable reconstruction of the native frontend's 640x480 layouts.</summary>
public sealed partial class FelView : Grid
{
    private readonly FelFile _file;
    private sealed record Layer(AssetNode Node, FelFile File) { public bool Enabled { get; set; } = true; }
    private readonly List<Layer> _layers = [];
    private readonly Dictionary<string, (SfnFont Font, DecodedImage Atlas)> _fonts = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, BitmapSource> _textCache = [];
    private readonly ComboBox _animation = new() { Width = 220, DisplayMemberPath = "" };
    private readonly Slider _time = new() { Minimum = 0, Maximum = 1, Width = 160 };
    private readonly TextBlock _clock = B.Text("0.00 s", "Muted");
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMilliseconds(33) };
    private Dictionary<FelElement, string[]> _sample = [];
    private readonly TreeView _tree = new() { Background = Ui.Res("PanelBg") };
    private readonly TextBlock _notes = new() { TextWrapping = TextWrapping.Wrap, Foreground = Ui.Res("Muted"), Margin = new Thickness(8) };
    private int _missingFonts;
    private readonly TextBox[] _geometry = Enumerable.Range(0, 4).Select(_ => new TextBox { Width = 49, Margin = new Thickness(2) }).ToArray();
    private readonly TextBox _label = new() { MinWidth = 100, Margin = new Thickness(2), ToolTip = "Preview text override" };
    private readonly Dictionary<FelElement, Dictionary<int, string>> _edits = [];
    private readonly Dictionary<FelElement, string> _labels = [];
    private readonly Canvas _stage = new() { Width = 640, Height = 480, Background = Brushes.Black, ClipToBounds = true };
    private readonly Dictionary<string, BitmapSource> _images = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<int, string> _strings = [];
    private readonly TextBox _detail = new() { IsReadOnly = true, TextWrapping = TextWrapping.Wrap, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Padding = new Thickness(10) };
    private readonly TextBlock _status = B.Text("Loading referenced artwork…", "Muted");
    private readonly ComboBox _state = new() { Width = 125, ItemsSource = new[] { "NO", "HI", "PR", "DI", "SE" }, SelectedIndex = 0 };
    private readonly CheckBox _hidden = new() { Content = "Show hidden", Margin = new Thickness(10, 0, 10, 0) };
    private readonly CheckBox _bounds = new() { Content = "Bounds", IsChecked = false, Margin = new Thickness(10, 0, 10, 0) };
    private FelElement? _selected;
    private readonly HashSet<FelElement> _excluded = [];
    private readonly Dictionary<FelElement, bool> _visibility = [];
    private int _unresolved;
    private readonly Dictionary<string, (AssetNode Node, int Index)> _sources = new(StringComparer.OrdinalIgnoreCase);
    private readonly TabControl _tabs = new();
    private readonly TabItem _textureTab = new() { Header = "Textures" };
    private readonly ComboBox _packs = new() { MinWidth = 260 };
    private readonly ContentControl _textureHost = new();
    private readonly TextBlock _textureNote = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(10, 6, 10, 6), Foreground = Ui.Res("Muted") };
    private readonly Button _editTexture = B.Button("Edit texture…", null, (_, _) => { }, "Primary", "Open the selected picture in the texture editor");
    private string _sourceSignature = "";
    private bool _switchingPack;

    public FelView(AssetNode node, Action<List<Prop>> setExtra, bool simulator = false)
    {
        var text = FormatInspector.ReadText(node.Open());
        _file = FelFile.Read(text);
        _layers.Add(new Layer(node, _file));
        setExtra([new("Frontend", "Stage", "640 × 480"), new("Frontend", "Elements", _file.Elements.Count.ToString()), new("Frontend", "Preview", "Authored layouts, bitmap fonts, included layers and animation snapshots")]);
        var tree = _tree;
        tree.Resources[typeof(TreeViewItem)] = new Style(typeof(TreeViewItem)) { Setters = { new Setter(TreeViewItem.ForegroundProperty, Ui.Res("Text")) } };
        BuildTree();
        tree.SelectedItemChanged += (_, e) => { if (e.NewValue is TreeViewItem { Tag: FelElement element }) Select(element); };
        var toolbar = B.Toolbar(B.Text("640 × 480"), B.Separator(), B.Text("Button state"), _state, _hidden, _bounds,
            B.Button("Hide selected", null, (_, _) => { if (_selected is not null) { _excluded.Add(_selected); Render(); } }, "Outline"),
            B.Button("Restore", null, (_, _) => { _excluded.Clear(); Render(); }, "Outline"));
        var playback = B.Toolbar(B.Text("Animation"), _animation,
            B.Button("Play / pause", null, (_, _) => { if (_timer.IsEnabled) _timer.Stop(); else _timer.Start(); }, "Outline"), _time, _clock,
            B.Button("Export PNG", null, (_, _) => SavePreview(), "Outline"));
        var preview = B.Rows((toolbar, B.Auto), (playback, B.Auto), (new Viewbox { Child = _stage, Margin = new Thickness(12), Stretch = Stretch.Uniform }, B.Star()), (_status, B.Auto));
        var tabs = _tabs;
        var editBar = B.Toolbar(B.Text("X / Y / W / H"), _geometry[0], _geometry[1], _geometry[2], _geometry[3]);
        var editActions = B.Toolbar(_editTexture, B.Button("Apply preview", null, (_, _) => ApplyEdits(), "Outline"), B.Button("Reset edits", null, (_, _) => { _edits.Clear(); _labels.Clear(); _textCache.Clear(); Render(); }, "Outline"));
        tabs.Items.Add(new TabItem { Header = "Menu preview", Content = B.Cols((B.Rows((tree, B.Star()), (_detail, B.Px(140)), (editBar, B.Auto), (_label, B.Auto), (editActions, B.Auto)), B.Px(270)), (B.VSplit(), B.Px(4)), (preview, B.Star())) });
        if (!simulator) tabs.Items.Add(new TabItem { Header = "Menu simulator", Content = new MenuSimulatorView(node) });
        if (!simulator)
        {
            _textureTab.Content = B.Rows((B.Toolbar(B.Text("Texture pack"), _packs), B.Auto), (_textureNote, B.Auto), (_textureHost, B.Star()));
            tabs.Items.Add(_textureTab);
            _packs.SelectionChanged += (_, _) => { if (!_switchingPack) ShowPack(null); };
            _editTexture.Click += (_, _) => { if (_selected is { Kind: "SH" } sh) EditTexture(sh); };
            _editTexture.IsEnabled = false;
            Editor.Changed += OnEditorChanged;
            Unloaded += (_, _) => Editor.Changed -= OnEditorChanged;
            Loaded += (_, _) => { Editor.Changed -= OnEditorChanged; Editor.Changed += OnEditorChanged; };
        }
        else _editTexture.Visibility = Visibility.Collapsed;
        tabs.Items.Add(new TabItem { Header = "FEL source", Content = SimpleViews.Text(text) });
        tabs.Items.Add(new TabItem { Header = "Resources and fidelity", Content = new ScrollViewer { Content = _notes, VerticalScrollBarVisibility = ScrollBarVisibility.Auto } });
        Children.Add(tabs);
        _state.SelectionChanged += (_, _) => Render();
        _hidden.Click += (_, _) => Render();
        _bounds.Click += (_, _) => Render();
        _animation.SelectionChanged += (_, _) => { _time.Maximum = (_animation.SelectedItem as FelAnimation)?.Duration ?? 1; _time.Value = 0; Render(); };
        _time.ValueChanged += (_, _) => Render();
        _timer.Tick += (_, _) => { _time.Value = _time.Value >= _time.Maximum ? 0 : Math.Min(_time.Maximum, _time.Value + .033); };
        Unloaded += (_, _) => _timer.Stop();
        Render();
        Loaded += async (_, _) =>
        {
            if (_loaded) return;
            _loaded = true;
            try
            {
                var root = node;
                while (root.Parent is not null) root = root.Parent;
                var nodes = MainWindow.Instance?.GameRoot == root ? await MainWindow.Instance.GetAssetIndex() : await Task.Run(() => root.EnumerateDeep().ToList());
                var result = await Task.Run(() => LoadAssets(nodes, node));
                foreach (var (tag, bitmap) in result.Images) _images[tag] = bitmap;
                _sourceSignature = SourceSignature();
                RefreshPackList();
                foreach (var s in result.Strings) _strings[s.Id] = s.Text;
                foreach (var (name, font) in result.Fonts) _fonts[name] = font;
                _layers.Clear(); _layers.AddRange(result.Layers);
                foreach (var element in _file.Elements.Where(e => e.Kind == "GR" && e.Name == "CONSOLEMENU")) _visibility[element] = true;
                BuildTree();
                _animation.ItemsSource = new object[] { "Authored layout" }.Concat(_layers.SelectMany(l => FelAnimation.Read(l.File)).Cast<object>()).ToList();
                _animation.SelectedIndex = 0;
                if (_animation.Items.Cast<object>().OfType<FelAnimation>().FirstOrDefault(a => a.File == _file && a.Definition.Name == "ENTER") is { } entrance)
                { _animation.SelectedItem = entrance; _time.Value = simulator ? 0 : entrance.Duration; }
                _notes.Text = string.Join("\n", result.Notes) + "\n\nPreview supports authored geometry, visibility, alpha, native font glyphs/kerning, button states, includes and sparse animation snapshots. Game-driven data, scripts, special blend modes and interpolation easing are not emulated. Timeline interpolation is linear. Included overlays are available as layer checkboxes; enable the screens you need.\n\n" + string.Join("\n", _layers.Select(l => l.Node.Path));
                Render();
            }
            catch (Exception ex) { _status.Text = $"Artwork load failed: {ex.Message}"; }
        };
    }
    private bool _loaded;

    private void ApplyEdits()
    {
        if (_selected is not { } selected) return;
        var values = new Dictionary<int, string>();
        for (var i = 0; i < 4; i++)
        {
            if (!double.TryParse(_geometry[i].Text, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var value) || !double.IsFinite(value) || (i >= 2 && (value <= 0 || value > 4096)))
            { _detail.Text = "Enter finite X/Y values and widths/heights between 1 and 4096."; return; }
            values[i + 3] = value.ToString("R", System.Globalization.CultureInfo.InvariantCulture);
        }
        _edits[selected] = values;
        if (selected.Kind == "TB") _labels[selected] = _label.Text;
        Render();
    }

    internal void ConfigurePreview(string? animation, double time, IEnumerable<string> show)
    {
        if (animation is not null && _animation.Items.Cast<object>().OfType<FelAnimation>().FirstOrDefault(a => a.Definition.Name.Equals(animation, StringComparison.OrdinalIgnoreCase)) is { } found)
            _animation.SelectedItem = found;
        _time.Value = Math.Clamp(time, 0, _time.Maximum);
        foreach (var name in show)
            foreach (var element in _layers.SelectMany(l => l.File.Elements).Where(e => e.Name.Equals(name, StringComparison.OrdinalIgnoreCase)))
                _visibility[element] = true;
        Render();
    }

    private void BuildTree()
    {
        _tree.Items.Clear();
        TreeViewItem Make(FelElement e)
        {
            var item = new TreeViewItem { Header = e.ToString(), Tag = e };
            if (e.Kind is "SC" or "GR" or "GG" or "SH" or "TB" or "FC" or "BU")
            {
                var check = new CheckBox { Content = e.ToString(), IsChecked = _visibility.GetValueOrDefault(e, e.Number(1, 1) != 0), ToolTip = "Preview visibility; select row to inspect" };
                check.Click += (_, _) => { _visibility[e] = check.IsChecked == true; Select(e); };
                item.Header = check;
            }
            foreach (var child in e.Children) item.Items.Add(Make(child));
            return item;
        }
        foreach (var layer in _layers)
        {
            var check = new CheckBox { Content = layer.Node.Name, IsChecked = layer.Enabled, ToolTip = layer.Node.Path };
            check.Click += (_, _) => { layer.Enabled = check.IsChecked == true; Render(); };
            var group = new TreeViewItem { Header = check, IsExpanded = layer == _layers[0] };
            var snapshots = new TreeViewItem { Header = "Animation snapshots" };
            foreach (var scene in layer.File.Elements.Where(e => e.Parent is null && e.Kind == "SC"))
                if (scene.Number(14) != 0) snapshots.Items.Add(Make(scene)); else group.Items.Add(Make(scene));
            if (snapshots.Items.Count > 0) group.Items.Add(snapshots);
            _tree.Items.Add(group);
        }
    }

    private void SavePreview()
    {
        var dialog = new Microsoft.Win32.SaveFileDialog { Filter = "PNG image|*.png", FileName = "menu-preview.png" };
        if (dialog.ShowDialog() != true) return;
        try
        {
            var bitmap = new RenderTargetBitmap(640, 480, 96, 96, PixelFormats.Pbgra32);
            var drawing = new DrawingVisual();
            using (var dc = drawing.RenderOpen()) dc.DrawRectangle(new VisualBrush(_stage), null, new Rect(0, 0, 640, 480));
            bitmap.Render(drawing);
            var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using var stream = File.Create(dialog.FileName); encoder.Save(stream);
        }
        catch (Exception ex) { Log.Error("Menu PNG export: " + ex.Message); }
    }

    private (Dictionary<string, BitmapSource> Images, List<LocString> Strings, Dictionary<string, (SfnFont, DecodedImage)> Fonts, List<Layer> Layers, List<string> Notes) LoadAssets(List<AssetNode> nodes, AssetNode owner)
    {
        var layers = new List<Layer> { new(owner, _file) };
        var notes = new List<string>();
        var visited = new HashSet<AssetNode> { owner };
        void Includes(Layer layer, int depth)
        {
            foreach (var include in layer.File.Elements.Where(e => e.Kind == "SF"))
            {
                var n = nodes.Where(n => n.Name.Equals(include.Field(1), StringComparison.OrdinalIgnoreCase))
                    .OrderByDescending(n => n.Parent == layer.Node.Parent).ThenBy(n => n.Path, StringComparer.OrdinalIgnoreCase).FirstOrDefault();
                if (n is null) { notes.Add($"Missing include: {include.Field(1)}"); continue; }
                if (!visited.Add(n)) continue;
                if (depth >= 8) { notes.Add($"Include depth limit: {n.Path}"); continue; }
                try
                {
                    var child = new Layer(n, FelFile.Read(FormatInspector.ReadText(n.Open())))
                    { Enabled = layer.Enabled && (include.Name.Contains("BACKGROUND", StringComparison.OrdinalIgnoreCase) || include.Name.Contains("BRAND", StringComparison.OrdinalIgnoreCase) || include.Name == "NAVBAR") };
                    layers.Add(child); Includes(child, depth + 1);
                }
                catch (Exception ex) { notes.Add($"Include {n.Path}: {ex.Message}"); }
            }
        }
        Includes(layers[0], 0);
        var images = new Dictionary<string, BitmapSource>(StringComparer.OrdinalIgnoreCase);
        var all = layers.SelectMany(l => l.File.Elements).ToList();
        var packs = all.Where(e => e.Kind == "LS").Select(e => e.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var tags = all.Where(e => e.Kind == "SH").SelectMany(e => new[] { e.Field(12), e.Field(13) }).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var fontNames = all.Where(e => e.Kind == "TB").Select(e => Path.GetFileNameWithoutExtension(e.Field(13))).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var fonts = new Dictionary<string, (SfnFont, DecodedImage)>(StringComparer.OrdinalIgnoreCase);
        List<LocString> strings = [];
        // Only referenced packs are decoded; identical four-character tags in unrelated packs must not win.
        foreach (var n in nodes.OrderByDescending(n => n.Parent == owner.Parent))
        {
            if (n.Name.Equals("FEENG.LOC", StringComparison.OrdinalIgnoreCase))
            {
                try { strings = LocTable.Read(n.ReadAll()).Strings; } catch { }
            }
            if (n.Extension is ".sfn" or ".xfn" && fontNames.Contains(Path.GetFileNameWithoutExtension(n.Name)))
            {
                try { var font = SfnFont.Read(n.ReadAll()); fonts[Path.GetFileNameWithoutExtension(n.Name)] = (font, font.Atlas.Decode(font.Atlas.Images[0])); }
                catch (Exception ex) { notes.Add($"Font {n.Path}: {ex.Message}"); }
            }
            if (!n.IsTexturePack || !packs.Contains(Path.GetFileNameWithoutExtension(n.Name))) continue;
            try
            {
                var ssh = SshFile.Read(n.ReadAll());
                foreach (var image in ssh.Images.Where(i => tags.Contains(i.Tag)))
                {
                    var key = Path.GetFileNameWithoutExtension(n.Name) + "/" + image.Tag;
                    if (images.ContainsKey(key)) continue;
                    var decoded = ssh.Decode(image);
                    var bitmap = BitmapSource.Create(decoded.Width, decoded.Height, 96, 96, PixelFormats.Bgra32, null, decoded.Bgra, decoded.Width * 4);
                    bitmap.Freeze();
                    images[key] = bitmap;
                    _sources[key] = (n, image.Index);
                }
            }
            catch (Exception ex) { notes.Add($"Artwork {n.Path}: {ex.Message}"); }
        }
        notes.Insert(0, $"{layers.Count} layout files · {images.Count} textures · {fonts.Count} native fonts · {strings.Count} localized strings");
        return (images, strings, fonts, layers, notes);
    }

    private bool Visible(FelElement e)
    {
        for (var p = e; p is not null; p = p.Parent)
        {
            if (_excluded.Contains(p)) return false;
            if (p.Kind == "SC" && p.Number(14) != 0) return false;
            if (p.Kind == "FR" && p.Name != ButtonState(e)) return false;
            if (_hidden.IsChecked != true && p.Fields.Length >= 11 && !_visibility.GetValueOrDefault(p, Number(p, 1, 1) != 0)) return false;
        }
        return true;
    }

    private static string TextureTag(FelElement e) => e.Field(13).Length > 0 ? e.Field(13) : e.Field(12);

    private string Field(FelElement e, int index) => _edits.TryGetValue(e, out var edits) && edits.TryGetValue(index, out var edited) ? edited : _sample.TryGetValue(e, out var fields) ? fields.ElementAtOrDefault(index) ?? "" : e.Field(index);
    private double Number(FelElement e, int index, double fallback = 0) => double.TryParse(Field(e, index), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var value) && double.IsFinite(value) ? value : fallback;
    private Point Position(FelElement e)
    {
        var x = Number(e, 3); var y = Number(e, 4);
        for (var p = e.Parent; p is not null; p = p.Parent)
        {
            x += Number(p, 3) - p.Number(3); y += Number(p, 4) - p.Number(4);
        }
        return new Point(x, y);
    }

    private string? ArtworkKey(FelElement e, FelFile file)
    {
        var tag = Field(e, 13).Length > 0 ? Field(e, 13) : Field(e, 12);
        var scene = e;
        while (scene.Parent is not null) scene = scene.Parent;
        var packs = scene.Children.Where(c => c.Kind == "LS").Concat(file.Elements.Where(c => c.Kind == "LS"));
        foreach (var pack in packs)
            if (_images.ContainsKey(pack.Name + "/" + tag)) return pack.Name + "/" + tag;
        return null;
    }

    private BitmapSource? Artwork(FelElement e, FelFile file) => ArtworkKey(e, file) is { } key ? _images[key] : null;

    // ---------------------------------------------------------------- texture editing

    private string SourceSignature() => string.Join("|", _sources.Values.Select(s => s.Node).Distinct()
        .Select(n => n.Key + Convert.ToHexString(System.Security.Cryptography.SHA1.HashData(n.ReadAll()))));

    private sealed record PackItem(AssetNode Node, string Label) { public override string ToString() => Label; }

    private void RefreshPackList()
    {
        var keep = (_packs.SelectedItem as PackItem)?.Node;
        _switchingPack = true;
        var items = _sources.Values.Select(s => s.Node).Distinct().OrderBy(n => n.Name, StringComparer.OrdinalIgnoreCase)
            .Select(n => new PackItem(n, $"{n.Name}  ({_sources.Count(s => s.Value.Node == n)} used here)")).ToList();
        _packs.ItemsSource = items;
        _packs.SelectedItem = items.FirstOrDefault(i => i.Node == keep);
        _switchingPack = false;
        _textureNote.Text = items.Count == 0 ? "No texture packs resolved for this layout yet."
            : "Select a picture on the stage and press Edit texture…, or double-click it. Replace image… saves into the project/mod like the standalone texture editor, and the stage updates. Packs are shared: every screen using the picture changes.";
    }

    private void EditTexture(FelElement e)
    {
        var file = _layers.FirstOrDefault(l => l.File.Elements.Contains(e))?.File ?? _file;
        if (ArtworkKey(e, file) is not { } key || !_sources.TryGetValue(key, out var source))
        { _textureNote.Text = $"{e.Name}: no decoded texture for tag '{TextureTag(e)}'."; _tabs.SelectedItem = _textureTab; return; }
        _switchingPack = true;
        _packs.SelectedItem = _packs.Items.OfType<PackItem>().FirstOrDefault(i => i.Node == source.Node);
        _switchingPack = false;
        ShowPack(source.Index);
        _tabs.SelectedItem = _textureTab;
    }

    private void ShowPack(int? select)
    {
        if (_packs.SelectedItem is not PackItem pack) { _textureHost.Content = null; return; }
        try
        {
            var view = new TextureView(SshFile.Read(pack.Node.ReadAll()), pack.Node, _ => { });
            if (select is { } index) view.SelectedImageIndex = index;
            _textureHost.Content = view;
        }
        catch (Exception ex) { _textureHost.Content = B.Text("Cannot edit " + pack.Node.Path + ": " + ex.Message, "Muted"); }
    }

    /// <summary>A texture pack used by this layout changed (replace, undo, redo, project load): re-decode its artwork and redraw the stage.</summary>
    private void OnEditorChanged()
    {
        if (_sources.Count == 0 || SourceSignature() == _sourceSignature) return;
        _sourceSignature = SourceSignature();
        foreach (var group in _sources.GroupBy(s => s.Value.Node).ToList())
        {
            try
            {
                var ssh = SshFile.Read(group.Key.ReadAll());
                foreach (var (key, source) in group)
                {
                    var decoded = ssh.Decode(ssh.Images.Single(i => i.Index == source.Index));
                    var bitmap = BitmapSource.Create(decoded.Width, decoded.Height, 96, 96, PixelFormats.Bgra32, null, decoded.Bgra, decoded.Width * 4);
                    bitmap.Freeze();
                    _images[key] = bitmap;
                }
            }
            catch (Exception ex) { Log.Warn($"{group.Key.Name}: stage refresh failed — {ex.Message}"); }
        }
        Render();
    }

    private FrameworkElement TextVisual(FelElement e, double width, double height)
    {
        var label = _labels.GetValueOrDefault(e, _strings.GetValueOrDefault((int)Number(e, 14, -1), $"{e.Name} [text {Field(e, 14)}]"));
        var fontName = Path.GetFileNameWithoutExtension(Field(e, 13));
        if (PreviewText is not null) label = PreviewText(e.Name, label);
        var colorAt = ButtonState(e) switch { "HI" => 19, "PR" => 22, "DI" => 25, "SE" => 28, _ => 16 };
        var red = (byte)Math.Clamp(Number(e, colorAt, 210), 0, 255);
        var green = (byte)Math.Clamp(Number(e, colorAt + 1, 210), 0, 255);
        var blue = (byte)Math.Clamp(Number(e, colorAt + 2, 210), 0, 255);
        if (_fonts.TryGetValue(fontName, out var native))
        {
            var key = $"{fontName}|{red},{green},{blue}|{label}";
            if (!_textCache.TryGetValue(key, out var bitmap))
            {
                var decoded = native.Font.Render(native.Atlas, label.Length > 2048 ? label[..2048] : label);
                for (int i = 0; i < decoded.Bgra.Length; i += 4)
                {
                    decoded.Bgra[i] = (byte)(decoded.Bgra[i] * blue / 255);
                    decoded.Bgra[i + 1] = (byte)(decoded.Bgra[i + 1] * green / 255);
                    decoded.Bgra[i + 2] = (byte)(decoded.Bgra[i + 2] * red / 255);
                }
                bitmap = BitmapSource.Create(decoded.Width, decoded.Height, 96, 96, PixelFormats.Bgra32, null, decoded.Bgra, decoded.Width * 4);
                bitmap.Freeze();
                if (_textCache.Count > 2048) _textCache.Clear();
                _textCache[key] = bitmap;
            }
            var canvas = new Canvas { ClipToBounds = true };
            var image = new Image { Source = bitmap, Width = bitmap.PixelWidth, Height = bitmap.PixelHeight, Stretch = Stretch.None };
            Canvas.SetLeft(image, -2); Canvas.SetTop(image, -2); canvas.Children.Add(image);
            return canvas;
        }
        _missingFonts++;
        return new TextBlock { Text = label, Foreground = new SolidColorBrush(Color.FromRgb(red, green, blue)), FontSize = Math.Clamp(height, 8, 32), ClipToBounds = true };
    }

    private void Select(FelElement e)
    {
        _selected = e;
        var owner = _layers.FirstOrDefault(l => l.File.Elements.Contains(e));
        _detail.Text = $"{e}\n{owner?.Node.Name} · line {e.Line}\n\n{e.Source.Trim()}\n\nGeometry/text edits affect this preview only. Export PNG to save the rendered result.";
        for (var i = 0; i < 4; i++) _geometry[i].Text = Number(e, i + 3).ToString("0.###", System.Globalization.CultureInfo.InvariantCulture);
        _label.IsEnabled = e.Kind == "TB";
        _editTexture.IsEnabled = e.Kind == "SH" && _sources.Count > 0;
        _label.Text = e.Kind == "TB" ? _labels.GetValueOrDefault(e, _strings.GetValueOrDefault((int)Number(e, 14), e.Name)) : "Select a text element to override its label";
        Render();
    }

    private void Render()
    {
        _sample = (_animation.SelectedItem as FelAnimation)?.Sample(_time.Value) ?? [];
        if (Simulation is not null) Simulation.SetControls(_layers.Where(l => l.Enabled).SelectMany(l => l.File.Elements).Where(e => e.Kind == "BU" && (e.Parent is null || Visible(e.Parent)) && !_excluded.Contains(e)).Select(e => { var p = Position(e); return new MenuControl(ControlId(e), p.X, p.Y); }));
        _clock.Text = $"{_time.Value:0.00} s";
        _stage.Children.Clear();
        _unresolved = 0;
        _missingFonts = 0;
        var scenes = _layers.Where(l => l.Enabled).SelectMany(l => l.File.Elements.Where(e => e.Kind == "SC" && e.Number(14) == 0).Select(s => (Layer: l, Scene: s)))
            .OrderBy(s => s.Scene.Number(11));
        foreach (var entry in scenes)
        foreach (var e in FelAnimation.Descendants(entry.Scene).Where(e => (e.Kind is "SH" or "TB") && Visible(e)))
        {
            var w = Math.Clamp(Number(e, 5), 1, 4096);
            var h = Math.Clamp(Number(e, 6), 1, 4096);
            FrameworkElement visual;
            if (e.Kind == "SH" && Artwork(e, entry.Layer.File) is { } bitmap)
                visual = new Image { Source = bitmap, Stretch = Stretch.Fill, Opacity = Math.Clamp(Number(e, 17, 255) / 255, 0, 1) };
            else if (e.Kind == "TB")
            {
                visual = TextVisual(e, w, h);
            }
            else
            {
                _unresolved++;
                visual = new Border { BorderBrush = Brushes.Magenta, BorderThickness = new Thickness(.6), Child = new TextBlock { Text = $"{e.Name} · {TextureTag(e)}", Foreground = Brushes.Magenta, FontSize = 9 } };
            }
            visual.Width = w; visual.Height = h;
            for (var p = e.Parent; p is not null; p = p.Parent)
                if (p.Kind is "GR" or "GG" or "FR") visual.Opacity *= Math.Clamp(Number(p, 11, 255) / 255, 0, 1);
            visual.ToolTip = $"{e} · line {e.Line}";
            visual.MouseLeftButtonDown += (_, args) => { Select(e); if (args.ClickCount == 2 && e.Kind == "SH" && Simulation is null) EditTexture(e); args.Handled = true; };
            var position = Position(e);
            Canvas.SetLeft(visual, position.X); Canvas.SetTop(visual, position.Y);
            _stage.Children.Add(visual);
            if (_bounds.IsChecked == true || e == _selected)
            {
                var outline = new Border { Width = w, Height = h, BorderBrush = e == _selected ? Brushes.Yellow : new SolidColorBrush(Color.FromArgb(65, 80, 210, 255)), BorderThickness = new Thickness(e == _selected ? 2 : .5), IsHitTestVisible = false };
                Canvas.SetLeft(outline, position.X); Canvas.SetTop(outline, position.Y);
                _stage.Children.Add(outline);
            }
        }
        if (_selected is { } selected && selected.Kind is not ("SH" or "TB") && selected.Fields.Length >= 11)
        {
            var outline = new Border { Width = Math.Clamp(selected.Number(5), 1, 4096), Height = Math.Clamp(selected.Number(6), 1, 4096),
                BorderBrush = Brushes.Yellow, BorderThickness = new Thickness(2), IsHitTestVisible = false };
            Canvas.SetLeft(outline, selected.Number(3)); Canvas.SetTop(outline, selected.Number(4));
            _stage.Children.Add(outline);
        }
        _status.Text = $"{_layers.Count} layouts · {_fonts.Count} native fonts · {_unresolved} missing textures · {_missingFonts} text fallbacks";
    }
}
