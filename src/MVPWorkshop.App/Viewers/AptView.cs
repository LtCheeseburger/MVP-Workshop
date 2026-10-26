using System.Text;
using System.Windows;
using System.Windows.Controls;
using MVPWorkshop.Core.Formats;
using MVPWorkshop.Core.Vfs;

namespace MVPWorkshop.App.Viewers;

/// <summary>APT movie explorer: character / frame / display-list tree, action disassembly and the constant pool.</summary>
public sealed class AptView : Grid
{
    internal AptSceneView? StagePreview { get; private set; }
    private MenuSimulatorView? _simulator;
    private AptTextureEditorView? _textureEditor;
    private IReadOnlyList<AssetNode> _textureDependencies = [];
    private byte[] _data = [];
    private AptConstants? _consts;
    private AptFile _apt = null!;
    private readonly AssetNode _node;
    private readonly Action<List<Prop>> _setExtra;
    private string _signature = "";
    private string? _selectedKey;
    private int _tabIndex;
    private bool _reloadQueued;
    private TextBox _detail = NewDetail();
    private static TextBox NewDetail() => new()
    {
        IsReadOnly = true, FontFamily = (System.Windows.Media.FontFamily)Application.Current.Resources["Mono"], FontSize = 12.5, BorderThickness = new Thickness(0),
        HorizontalScrollBarVisibility = ScrollBarVisibility.Auto, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Padding = new Thickness(14, 10, 14, 10),
        VerticalContentAlignment = VerticalAlignment.Top,
    };

    public static AssetNode? Sibling(AssetNode n, string ext) =>
        n.Parent?.Children.FirstOrDefault(c => c.Name.Equals(System.IO.Path.ChangeExtension(n.Name, ext), StringComparison.OrdinalIgnoreCase))
        ?? (ext == ".ssh" ? Sibling(n, ".xsh") : null);

    public static FrameworkElement Create(AssetNode node, Action<List<Prop>> setExtra)
    {
        if (node.Format == FormatId.AptConst)
        {
            var consts = AptConstants.Read(node.ReadAll());
            var movie = Sibling(node, ".apt");
            var grid = B.Grid();
            grid.Columns.Add(B.Col("#", nameof(AptConstant.Index), 60, true));
            grid.Columns.Add(B.Col("Type", nameof(AptConstant.TypeName), 90));
            grid.Columns.Add(new DataGridTextColumn { Header = "Value", Binding = new System.Windows.Data.Binding(nameof(AptConstant.Display)), Width = new DataGridLength(1, DataGridLengthUnitType.Star) });
            grid.ItemsSource = consts.Items;
            var bar = B.Toolbar(B.Text($"{consts.Items.Count} constants · movie character @0x{consts.MovieOffset:X}", "Muted"));
            if (movie is not null) ((WrapPanel)bar.Child).Children.Add(B.Button($"Open {movie.Name}", "", (_, _) => MainWindow.Instance?.OpenAsset(movie), "Outline"));
            return B.Rows((bar, B.Auto), (grid, B.Star()));
        }
        return new AptView(node, setExtra);
    }

    private AptView(AssetNode node, Action<List<Prop>> setExtra)
    {
        _node = node; _setExtra = setExtra;
        Build();
        Editor.Changed += OnEditorChanged;
        Unloaded += (_, _) => Editor.Changed -= OnEditorChanged;
        Loaded += (_, _) => { Editor.Changed -= OnEditorChanged; Editor.Changed += OnEditorChanged; OnEditorChanged(); };
    }

    /// <summary>The movie, its constants or its textures changed (edit, undo, redo, project load): rebuild every tab from the new bytes.</summary>
    private void OnEditorChanged()
    {
        if (_reloadQueued || Signature() == _signature) return;
        _reloadQueued = true;
        Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Background, () =>
        {
            _reloadQueued = false;
            try { Build(); }
            catch (Exception ex) { Log.Error($"{_node.Name}: could not refresh after edit — {ex.Message}"); }
        });
    }

    private string Signature()
    {
        var parts = new[] { _node, Sibling(_node, ".const"), Sibling(_node, ".ssh"), Sibling(_node, ".o") };
        return string.Join("|", parts.OfType<AssetNode>().Concat(_textureDependencies).Distinct().Select(p => p.Key + Convert.ToHexString(System.Security.Cryptography.SHA1.HashData(p.ReadAll()))));
    }

    private MenuSimulatorView Simulator(AssetNode node)
    {
        _simulator ??= new MenuSimulatorView(node);
        if (_simulator.Parent is TabItem tab) tab.Content = null;
        return _simulator;
    }

    private void Build()
    {
        var node = _node; var setExtra = _setExtra;
        _signature = Signature();
        _data = node.ReadAll();
        _consts = Sibling(node, ".const") is { } cn ? AptConstants.Read(cn.ReadAll()) : null;
        _apt = AptFile.Read(_data, _consts?.MovieOffset ?? 0);
        _detail = NewDetail();
        _detail.Background = Ui.Res("WindowBg");

        var actions = _apt.AllItems().Count(i => i.Code is not null);
        setExtra([
            new("APT", "Version", _apt.Version),
            new("APT", "Stage", $"{_apt.Width} × {_apt.Height}"),
            new("APT", "Frame time", $"{_apt.FrameTime} ms"),
            new("APT", "Characters", _apt.Characters.Count.ToString()),
            new("APT", "Movie frames", _apt.Movie.Frames.Count.ToString()),
            new("APT", "Actions", actions.ToString()),
            new("APT", "Imports / exports", $"{_apt.Imports.Count} / {_apt.Exports.Count}"),
            new("APT", "Constant pool", _consts is null ? "missing (.const not found)" : $"{_consts.Items.Count} items"),
            .. _apt.Characters.OfType<AptCharacter>().GroupBy(c => c.Type).Select(g => new Prop("Characters", g.Key.ToString(), g.Count().ToString())),
        ]);

        var tree = new TreeView { Background = Ui.Res("PanelBg"), BorderThickness = new Thickness(0) };
        // The app-wide TreeViewItem style binds IsExpanded/IsSelected to the explorer's view models; these items have none.
        tree.Resources[typeof(TreeViewItem)] = new Style(typeof(TreeViewItem), (Style)Application.Current.Resources[typeof(TreeViewItem)])
        {
            Setters = { new Setter(TreeViewItem.IsExpandedProperty, false), new Setter(TreeViewItem.IsSelectedProperty, false) },
        };
        var movieItem = Node($"Movie · {_apt.Width}×{_apt.Height} · {_apt.Movie.Frames.Count} frames", () => MovieSummary());
        movieItem.IsExpanded = true;
        AddFrames(movieItem, _apt.Movie);
        tree.Items.Add(movieItem);
        var charsItem = Node($"Characters ({_apt.Characters.Count})", () => string.Join(Environment.NewLine, _apt.Characters.Select((c, i) => c is null ? $"[{i}] (none)" : $"[{i}] {c.Type,-10} @0x{c.Offset:X5}  {c.Summary}")));
        charsItem.IsExpanded = true;
        foreach (var c in _apt.Characters.OfType<AptCharacter>().Where(c => c.Offset != _apt.Movie.Offset))
        {
            var ci = Node($"[{c.Id}] {c.Type}  {c.Summary}", () => CharacterText(c));
            AddFrames(ci, c);
            charsItem.Items.Add(ci);
        }
        tree.Items.Add(charsItem);
        if (_apt.Imports.Count > 0) tree.Items.Add(Node($"Imports ({_apt.Imports.Count})", () => string.Join(Environment.NewLine, _apt.Imports.Select(i => $"[{i.Character}] {i.Movie} :: {i.Name}"))));
        if (_apt.Exports.Count > 0) tree.Items.Add(Node($"Exports ({_apt.Exports.Count})", () => string.Join(Environment.NewLine, _apt.Exports.Select(e => $"[{e.Character}] {e.Name}"))));
        tree.SelectedItemChanged += (_, e) => { if (e.NewValue is TreeViewItem { Tag: Func<string> f }) _detail.Text = f(); };
        movieItem.IsSelected = true;

        var explorer = B.Cols((tree, B.Px(380)), (B.VSplit(), B.Px(4)), (_detail, B.Star()));
        var tabs = new TabControl();
        var stage = new AptSceneView(node, _apt); StagePreview = stage;
        _textureEditor ??= new AptTextureEditorView(node, assets => { _textureDependencies = assets; OnEditorChanged(); });
        if (_textureEditor.Parent is TabItem previousTextureTab) previousTextureTab.Content = null;
        var textureTab = new TabItem { Header = "Textures", Content = _textureEditor };
        var constNode = Sibling(node, ".const");
        var editPanel = new AptEditPanel(node, constNode, _apt, _consts, _data, constNode?.ReadAll(), _selectedKey, key => _selectedKey = key, () => tabs.SelectedItem = textureTab);
        tabs.Items.Add(new TabItem { Header = "Stage preview + edit", Content = B.Cols((stage, B.Star()), (B.VSplit(), B.Px(4)), (editPanel, B.Px(360))) });
        tabs.Items.Add(new TabItem { Header = "Menu simulator", Content = Simulator(node) });
        tabs.Items.Add(textureTab);
        tabs.Items.Add(new TabItem { Header = "Display List", Content = explorer });
        var script = new TabItem { Header = $"Script ({actions} actions)" };
        tabs.Items.Add(script);
        if (_consts is not null)
        {
            var grid = B.Grid();
            grid.Columns.Add(B.Col("#", nameof(AptConstant.Index), 60, true));
            grid.Columns.Add(B.Col("Type", nameof(AptConstant.TypeName), 90));
            grid.Columns.Add(new DataGridTextColumn { Header = "Value", Binding = new System.Windows.Data.Binding(nameof(AptConstant.Display)), Width = new DataGridLength(1, DataGridLengthUnitType.Star) });
            grid.ItemsSource = _consts.Items;
            tabs.Items.Add(new TabItem { Header = $"Constants ({_consts.Items.Count})", Content = grid });
        }
        tabs.SelectionChanged += (_, e) =>
        {
            if (e.OriginalSource == tabs && tabs.SelectedItem == script && script.Content is null) script.Content = SimpleViews.Text(FullScript());
        };
        var images = Sibling(node, ".ssh");
        var bar = B.Toolbar(B.Text($"APT {_apt.Version} · stage {_apt.Width}×{_apt.Height} · {_apt.Characters.Count} characters", "Muted"), B.Separator());
        if (images is not null) ((WrapPanel)bar.Child).Children.Add(B.Button($"Open {images.Name}", "", (_, _) => MainWindow.Instance?.OpenAsset(images), "Outline"));
        if (Sibling(node, ".const") is { } cnode) ((WrapPanel)bar.Child).Children.Add(B.Button($"Open {cnode.Name}", "", (_, _) => MainWindow.Instance?.OpenAsset(cnode), "Outline"));
        tabs.SelectedIndex = Math.Min(_tabIndex, tabs.Items.Count - 1);
        tabs.SelectionChanged += (_, e) => { if (e.OriginalSource == tabs && tabs.SelectedIndex >= 0) _tabIndex = tabs.SelectedIndex; };
        Children.Clear();
        Children.Add(B.Rows((bar, B.Auto), (tabs, B.Star())));
    }

    private static TreeViewItem Node(string header, Func<string> detail) => new() { Header = header, Tag = detail, Foreground = Ui.Res("Text") };

    private void AddFrames(TreeViewItem parent, AptCharacter c)
    {
        foreach (var f in c.Frames)
        {
            var label = f.Items.FirstOrDefault(i => i.Type == 2)?.Name;
            var fi = Node($"Frame {f.Index}{(label is null ? "" : $" \"{label}\"")} · {f.Items.Count} items", () => string.Join(Environment.NewLine, f.Items.Select(i => $"{i.TypeName,-16} {i.Detail}")));
            foreach (var it in f.Items)
                fi.Items.Add(Node($"{it.TypeName}  {it.Detail}", () => ItemText(it)));
            parent.Items.Add(fi);
        }
    }

    private string MovieSummary()
    {
        var sb = new StringBuilder();
        sb.AppendLine($"Apt Data version {_apt.Version}");
        sb.AppendLine($"Stage          {_apt.Width} × {_apt.Height}");
        sb.AppendLine($"Frame time     {_apt.FrameTime} ms");
        sb.AppendLine($"Movie frames   {_apt.Movie.Frames.Count}");
        sb.AppendLine($"Characters     {_apt.Characters.Count}");
        foreach (var g in _apt.Characters.OfType<AptCharacter>().GroupBy(c => c.Type)) sb.AppendLine($"  {g.Key,-12} {g.Count()}");
        if (_apt.Imports.Count > 0) { sb.AppendLine(); sb.AppendLine("Imports"); foreach (var i in _apt.Imports) sb.AppendLine($"  [{i.Character}] {i.Movie} :: {i.Name}"); }
        if (_apt.Exports.Count > 0) { sb.AppendLine(); sb.AppendLine("Exports"); foreach (var e in _apt.Exports) sb.AppendLine($"  [{e.Character}] {e.Name}"); }
        return sb.ToString();
    }

    private string CharacterText(AptCharacter c)
    {
        var sb = new StringBuilder($"[{c.Id}] {c.Type} @0x{c.Offset:X}{Environment.NewLine}");
        foreach (var (k, v) in c.Fields) sb.AppendLine($"  {k,-10} {v}");
        if (_apt.Exports.FirstOrDefault(e => e.Character == c.Id) is { } ex) sb.AppendLine($"  exported as \"{ex.Name}\"");
        foreach (var f in c.Frames) { sb.AppendLine($"frame {f.Index}"); foreach (var i in f.Items) sb.AppendLine($"  {i.TypeName,-16} {i.Detail}"); }
        return sb.ToString();
    }

    private string ItemText(AptFrameItem it)
    {
        var sb = new StringBuilder($"{it.TypeName} @0x{it.Offset:X}  {it.Detail}{Environment.NewLine}");
        if (it.Character is { } ch && ch >= 0 && ch < _apt.Characters.Count && _apt.Characters[ch] is { } c)
            sb.AppendLine($"→ character [{ch}] {c.Type} {c.Summary}");
        if (it.Code is { } code) { sb.AppendLine(); AppendCode(sb, code); }
        return sb.ToString();
    }

    private void AppendCode(StringBuilder sb, int code)
    {
        foreach (var ins in AptBytecode.Disassemble(_data, code, _consts))
            sb.AppendLine($"{ins.Offset:X6}  {new string(' ', ins.Depth * 4)}{ins.Mnemonic,-24} {ins.Operands}");
    }

    private string FullScript()
    {
        var sb = new StringBuilder();
        foreach (var c in _apt.Characters.OfType<AptCharacter>())
            foreach (var f in c.Frames)
                foreach (var it in f.Items.Where(i => i.Code is not null))
                {
                    sb.AppendLine($"// [{c.Id}] {c.Type} · frame {f.Index} · {it.TypeName} @0x{it.Code:X}");
                    AppendCode(sb, it.Code!.Value);
                    sb.AppendLine();
                }
        return sb.Length == 0 ? "(no actions)" : sb.ToString();
    }
}
