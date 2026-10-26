using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using MVPWorkshop.Core.Formats;
using MVPWorkshop.Core.Vfs;

namespace MVPWorkshop.App.Viewers;

/// <summary>One-line status strip telling whether edits to an asset are saved into game files or only previewed.</summary>
public static class EditBanner
{
    public static Border Build(AssetNode node, out Action refresh)
    {
        var text = new TextBlock { TextWrapping = TextWrapping.Wrap, FontSize = 12 };
        var bar = new Border { Child = text, Padding = new Thickness(12, 6, 12, 6), Background = Ui.Res("PanelAlt"), BorderBrush = Ui.Res("Line"), BorderThickness = new Thickness(0, 0, 0, 1) };
        void Refresh()
        {
            var cap = Editor.CapabilityOf(node);
            var s = Editor.Session;
            var saved = s?.IsModified(node) == true;
            var previewed = s?.GetPreview(node) is not null;
            (text.Text, text.Foreground) = (cap.Level, saved, previewed) switch
            {
                (_, _, true) => ("PREVIEW ONLY — this edit is shown here but cannot be saved or exported. " + cap.Reason, Ui.Res("Accent")),
                (EditLevel.Savable, true, _) => ($"EDITED — saved changes; export rebuilds {ModExporter_Disk(node)} in the mod folder.", Ui.Res("Good")),
                (EditLevel.Savable, false, _) => ("Editable. Applied edits show at once and are written into the mod on export.", Ui.Res("Muted")),
                (EditLevel.PreviewOnly, _, _) => ("Edits here are preview-only: " + cap.Reason, Ui.Res("Accent")),
                _ => (cap.Reason ?? cap.Summary, Ui.Res("Muted")),
            };
        }
        refresh = Refresh;
        Refresh();
        return bar;
    }

    private static string ModExporter_Disk(AssetNode n) => MVPWorkshop.Core.Editing.ModExporter.DiskFileOf(n).Name;
}

/// <summary>Editable text for plain-text assets (KEY=Value tables, CSV, scripts). The file's encoding and line endings are kept.</summary>
public sealed class TextEditView : Grid
{
    private readonly AssetNode _node;
    private readonly TextBox _box;
    private readonly TextBlock _info = B.Text("", "Muted");
    private readonly Action _refreshBanner;
    private TextCodec _codec;
    private byte[] _loaded;
    private string _loadedText;
    private bool _crlf;

    public TextEditView(AssetNode node)
    {
        _node = node;
        _loaded = node.ReadAll();
        (_loadedText, _codec) = TextCodec.Decode(_loaded);
        _crlf = _loadedText.Contains("\r\n");
        _box = new TextBox
        {
            Text = _loadedText, FontFamily = (System.Windows.Media.FontFamily)Application.Current.Resources["Mono"], FontSize = 12.5, AcceptsReturn = true, AcceptsTab = true,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, BorderThickness = new Thickness(0),
            Background = Ui.Res("WindowBg"), Padding = new Thickness(14, 10, 14, 10), VerticalContentAlignment = VerticalAlignment.Top,
        };
        _box.TextChanged += (_, _) => UpdateInfo();
        _box.PreviewKeyDown += (_, e) => { if (e.Key == Key.Enter && Keyboard.Modifiers == ModifierKeys.Control) { Apply(); e.Handled = true; } };
        var wrap = B.Toggle("Wrap", false, v => _box.TextWrapping = v ? TextWrapping.Wrap : TextWrapping.NoWrap);
        var bar = B.Toolbar(
            B.Button("Apply", "", (_, _) => Apply(), "Primary", "Make this text the asset's new contents (Ctrl+Enter)"),
            B.Button("Discard typing", "", (_, _) => { _box.Text = _loadedText; }, "Outline", "Go back to the text as last applied"),
            B.Separator(), wrap, B.Separator(), _info);
        var banner = EditBanner.Build(node, out _refreshBanner);
        Children.Add(B.Rows((bar, B.Auto), (banner, B.Auto), (_box, B.Star())));
        UpdateInfo();
        Editor.Changed += OnEditorChanged;
        Unloaded += (_, _) => Editor.Changed -= OnEditorChanged;
        Loaded += (_, _) => { Editor.Changed -= OnEditorChanged; Editor.Changed += OnEditorChanged; OnEditorChanged(); };
    }

    private bool Typing => _box.Text != _loadedText;

    private void UpdateInfo() => _info.Text = $"{_box.Text.Count(c => c == '\n') + 1:N0} lines · {_box.Text.Length:N0} chars · {_codec.Name}{(_crlf ? " · CRLF" : "")}{(Typing ? "  ·  not applied yet" : "")}";

    private void OnEditorChanged()
    {
        _refreshBanner();
        var now = _node.ReadAll();
        if (now.AsSpan().SequenceEqual(_loaded)) return;
        _loaded = now;
        (_loadedText, _codec) = TextCodec.Decode(now);
        _crlf = _loadedText.Contains("\r\n");
        _box.Text = _loadedText;
    }

    private void Apply()
    {
        var text = _box.Text;
        if (text.Replace("\r\n", "\n") == _loadedText.Replace("\r\n", "\n")) return;
        try
        {
            var normalized = text.Replace("\r\n", "\n");
            var output = _crlf ? normalized.Replace("\n", "\r\n") : normalized;
            var bytes = _codec.Encode(output);
            Editor.Apply(_node, bytes, $"Edit text of {_node.Name}");
        }
        catch (Exception ex)
        {
            Log.Warn($"{_node.Name}: {ex.Message}");
            MessageBox.Show(Window.GetWindow(this), ex.Message, "Apply text", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }
}

/// <summary>Frontend layout (.fel) viewer with its source text editable; the layout re-renders from the edited text.</summary>
public sealed class FelEditHost : Grid
{
    private readonly AssetNode _node;
    private readonly Action<List<Prop>> _setExtra;
    private readonly TabItem _layout;
    private byte[] _seen;

    public FelEditHost(AssetNode node, Action<List<Prop>> setExtra)
    {
        _node = node; _setExtra = setExtra; _seen = node.ReadAll();
        var tabs = new TabControl();
        _layout = new TabItem { Header = "Layout", Content = SafeFel() };
        tabs.Items.Add(_layout);
        tabs.Items.Add(new TabItem { Header = "Source (editable)", Content = new TextEditView(node) });
        Children.Add(tabs);
        Editor.Changed += OnEditorChanged;
        Unloaded += (_, _) => Editor.Changed -= OnEditorChanged;
        Loaded += (_, _) => { Editor.Changed -= OnEditorChanged; Editor.Changed += OnEditorChanged; OnEditorChanged(); };
    }

    private FrameworkElement SafeFel()
    {
        try { return new FelView(_node, _setExtra); }
        catch (Exception ex) { return new TextBlock { Text = "Layout preview unavailable for the current text: " + ex.Message, Foreground = Ui.Res("Bad"), Margin = new Thickness(16), TextWrapping = TextWrapping.Wrap }; }
    }

    private void OnEditorChanged()
    {
        var now = _node.ReadAll();
        if (now.AsSpan().SequenceEqual(_seen)) return;
        _seen = now;
        _layout.Content = SafeFel();
    }
}

/// <summary>LOCH string table with editable text: double-click a cell, type, Enter. ⏎ stands for a line break and · for a non-breaking space.</summary>
public sealed class LocEditView : Grid
{
    private sealed class Row
    {
        public int Id { get; init; }
        public int Index { get; init; }
        public string Text { get; set; } = "";
    }

    private readonly AssetNode _node;
    private readonly DataGrid _grid = B.Grid();
    private readonly TextBlock _count = B.Text("", "Muted");
    private readonly TextBox _filter;
    private readonly Action _refreshBanner;
    private List<Row> _rows = [];
    private byte[] _seen = [];

    private static string Show(string s) => s.Replace("\r", "").Replace("\n", "⏎").Replace(" ", "·");
    private static string Store(string s) => s.Replace("⏎", "\n").Replace("·", " ");

    public LocEditView(AssetNode node)
    {
        _node = node;
        _grid.IsReadOnly = false;
        _grid.CanUserAddRows = false; _grid.CanUserDeleteRows = false;
        _grid.Columns.Add(B.Col("ID", nameof(Row.Id), 70, true));
        _grid.Columns.Add(B.Col("Index", nameof(Row.Index), 60, true));
        _grid.Columns.Add(new DataGridTextColumn { Header = "Text (double-click to edit)", Binding = new System.Windows.Data.Binding(nameof(Row.Text)) { Mode = System.Windows.Data.BindingMode.TwoWay }, Width = new DataGridLength(1, DataGridLengthUnitType.Star) });
        _grid.Columns[0].IsReadOnly = true; _grid.Columns[1].IsReadOnly = true;
        _grid.CellEditEnding += OnCellEditEnding;
        _filter = B.SearchBox("Filter by id or text", _ => ApplyFilter());
        var bar = B.Toolbar(_count, B.Separator(), B.Glyph("", "Muted", 12), _filter, B.Separator(), B.Text("UTF-16LE · ⏎ = line break, · = non-breaking space", "Faint", 11));
        var banner = EditBanner.Build(node, out _refreshBanner);
        Children.Add(B.Rows((bar, B.Auto), (banner, B.Auto), (_grid, B.Star())));
        Reload();
        Editor.Changed += OnEditorChanged;
        Unloaded += (_, _) => Editor.Changed -= OnEditorChanged;
        Loaded += (_, _) => { Editor.Changed -= OnEditorChanged; Editor.Changed += OnEditorChanged; OnEditorChanged(); };
    }

    private void Reload()
    {
        _seen = _node.ReadAll();
        var table = LocTable.Read(_seen);
        _rows = table.Strings.Select(s => new Row { Id = s.Id, Index = s.Index, Text = Show(s.Text) }).ToList();
        ApplyFilter();
    }

    private void ApplyFilter()
    {
        var q = _filter?.Text.Trim() ?? "";
        var list = q.Length == 0 ? _rows : _rows.Where(r => r.Text.Contains(q, StringComparison.OrdinalIgnoreCase) || r.Id.ToString() == q).ToList();
        _grid.ItemsSource = list;
        _count.Text = $"{list.Count:N0} strings";
    }

    private void OnEditorChanged()
    {
        _refreshBanner();
        if (!_node.ReadAll().AsSpan().SequenceEqual(_seen)) Reload();
    }

    private void OnCellEditEnding(object? sender, DataGridCellEditEndingEventArgs e)
    {
        if (e.EditAction != DataGridEditAction.Commit || e.Row.Item is not Row row || e.EditingElement is not TextBox box) return;
        var text = box.Text;
        if (text == row.Text) return;
        // the binding has not updated the row yet; defer so Reload() sees consistent data
        Dispatcher.BeginInvoke(() =>
        {
            try
            {
                var bytes = LocEditor.Write(_node.ReadAll(), new Dictionary<int, string> { [row.Index] = Store(text) });
                Editor.Apply(_node, bytes, $"{_node.Name}: string {row.Id} → “{(text.Length > 40 ? text[..40] + "…" : text)}”");
            }
            catch (Exception ex)
            {
                Log.Warn($"{_node.Name}: {ex.Message}");
                row.Text = Show(LocTable.Read(_node.ReadAll()).Strings[row.Index].Text);
                _grid.Items.Refresh();
            }
        }, System.Windows.Threading.DispatcherPriority.Background);
    }
}
