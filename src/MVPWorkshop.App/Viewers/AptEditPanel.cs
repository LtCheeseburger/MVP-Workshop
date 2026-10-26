using System.Globalization;
using System.Numerics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using MVPWorkshop.Core.Formats;
using MVPWorkshop.Core.Vfs;

namespace MVPWorkshop.App.Viewers;

/// <summary>
/// Editor for the parts of an APT movie that define a screen: text fields, display-list placements, string constants and the stage.
/// Every committed field becomes an undoable edit of the .apt / .const bytes (saved, or preview-only when the file cannot be rebuilt);
/// the stage preview next to it re-renders from the edited bytes straight away.
/// </summary>
public sealed class AptEditPanel : Grid
{
    private sealed record Item(string Key, string Kind, string Title, string Subtitle, object Tag);

    private readonly AssetNode _apt;
    private readonly AssetNode? _const;
    private readonly AptFile _file;
    private readonly AptConstants? _consts;
    private readonly byte[] _aptData, _constData;
    private readonly Action<string> _remember;
    private readonly List<Item> _all = [];
    private readonly ListBox _list = new() { BorderThickness = new Thickness(0), Background = Ui.Res("PanelBg") };
    private readonly StackPanel _fields = new() { Margin = new Thickness(14, 10, 14, 14) };
    private readonly TextBox _search = new() { Margin = new Thickness(8, 6, 8, 6), ToolTip = "Filter by text, variable, name or number" };
    private readonly TextBlock _banner = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(12, 7, 12, 7) };
    private string _kind = "All";

    public AptEditPanel(AssetNode aptNode, AssetNode? constNode, AptFile file, AptConstants? consts, byte[] aptData, byte[]? constData, string? select, Action<string> remember, Action? editTextures = null)
    {
        _apt = aptNode; _const = constNode; _file = file; _consts = consts; _aptData = aptData; _constData = constData ?? []; _remember = remember;
        Background = Ui.Res("PanelBg");
        Collect();

        var kinds = B.Segments("", ["All", "Text", "Placements", "Constants", "Stage"], 0, i => { _kind = new[] { "All", "Text", "Placement", "Constant", "Stage" }[i]; Filter(); });
        _search.TextChanged += (_, _) => Filter();
        _list.SelectionChanged += (_, _) => { if (_list.SelectedItem is Item it) { _remember(it.Key); ShowFields(it); } };
        _list.DisplayMemberPath = null;
        _list.ItemTemplate = ItemTemplate();

        var head = new Border { Child = new TextBlock { Text = "EDIT UI", Style = Ui.Style("PanelHeader"), Margin = new Thickness(12, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center }, Height = 32, Background = Ui.Res("PanelBg") };
        var banner = new Border { Child = _banner, Background = Ui.Res("PanelAlt"), BorderBrush = Ui.Res("Line"), BorderThickness = new Thickness(0, 1, 0, 1) };
        var textureLink = B.Toolbar(B.Button("Edit textures…", null, (_, _) => editTextures?.Invoke(), "Outline"));
        textureLink.Visibility = editTextures is null ? Visibility.Collapsed : Visibility.Visible;
        var top = B.Rows((head, B.Auto), (textureLink, B.Auto), (kinds, B.Auto), (_search, B.Auto), (banner, B.Auto), (_list, B.Star()));
        var editor = new ScrollViewer { Content = _fields, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Background = Ui.Res("WindowBg") };
        Children.Add(B.Rows((top, B.Star(2)), (new GridSplitter { Height = 4, HorizontalAlignment = HorizontalAlignment.Stretch }, B.Px(4)), (editor, B.Star(3))));

        var cap = Editor.CapabilityOf(_apt);
        _banner.Text = cap.Level == EditLevel.Savable ? $"EDITABLE — {cap.Reason}" : $"PREVIEW ONLY — {cap.Reason}";
        _banner.Foreground = Ui.Res(cap.Level == EditLevel.Savable ? "Good" : "Accent");

        Filter();
        var pick = _all.FirstOrDefault(i => i.Key == select) ?? _all.FirstOrDefault(i => i.Kind == "Text") ?? _all.FirstOrDefault();
        if (pick is not null) { _list.SelectedItem = pick; _list.ScrollIntoView(pick); }
    }

    private static DataTemplate ItemTemplate()
    {
        var root = new FrameworkElementFactory(typeof(StackPanel));
        root.SetValue(MarginProperty, new Thickness(2, 2, 2, 3));
        var t1 = new FrameworkElementFactory(typeof(TextBlock));
        t1.SetBinding(TextBlock.TextProperty, new System.Windows.Data.Binding(nameof(Item.Title)));
        t1.SetValue(TextBlock.TextTrimmingProperty, TextTrimming.CharacterEllipsis);
        var t2 = new FrameworkElementFactory(typeof(TextBlock));
        t2.SetBinding(TextBlock.TextProperty, new System.Windows.Data.Binding(nameof(Item.Subtitle)));
        t2.SetValue(TextBlock.ForegroundProperty, Ui.Res("Faint"));
        t2.SetValue(TextBlock.FontSizeProperty, 10.5);
        t2.SetValue(TextBlock.TextTrimmingProperty, TextTrimming.CharacterEllipsis);
        root.AppendChild(t1); root.AppendChild(t2);
        return new DataTemplate { VisualTree = root };
    }

    private static string Short(string? s, int n = 60) { s ??= ""; s = s.Replace("\r", "").Replace("\n", " ⏎ "); return s.Length > n ? s[..n] + "…" : s; }

    private void Collect()
    {
        _all.Add(new Item("stage", "Stage", "Stage / movie", $"{_file.Width}×{_file.Height} · {_file.FrameTime} ms per frame", "stage"));
        foreach (var c in _file.Characters.OfType<AptCharacter>())
        {
            if (c.Type == AptCharacterType.EditText)
                _all.Add(new Item($"text:{c.Id}", "Text", $"[{c.Id}] “{Short(c.Text)}”", string.IsNullOrEmpty(c.Variable) ? "text field" : $"text field · variable {c.Variable}", c));
            foreach (var f in c.Frames)
                foreach (var it in f.Items.Where(i => i.Type == 3))
                    _all.Add(new Item($"place:{it.Offset}", "Placement", $"[{c.Id}] f{f.Index} · depth {it.Depth} · {(it.Character is { } ch ? $"char {ch}{CharLabel(ch)}" : "move")}{(it.Name is { Length: > 0 } nm ? $" “{nm}”" : "")}", $"{c.Type} {c.Id} · placement @0x{it.Offset:X}", (c, f, it)));
        }
        if (_consts is not null)
            foreach (var k in _consts.Items.Where(k => k.Type == 1))
                _all.Add(new Item($"const:{k.Index}", "Constant", $"#{k.Index} “{Short(k.Text)}”", "string constant (used by scripts)", k));
    }

    private string CharLabel(int id) => id >= 0 && id < _file.Characters.Count && _file.Characters[id] is { } c ? $" {c.Type}" : "";

    private void Filter()
    {
        var q = _search.Text.Trim();
        _list.ItemsSource = _all.Where(i => (_kind == "All" || i.Kind == _kind) && (q.Length == 0 || i.Title.Contains(q, StringComparison.OrdinalIgnoreCase) || i.Subtitle.Contains(q, StringComparison.OrdinalIgnoreCase))).ToList();
    }

    // ---------------------------------------------------------------- property rows

    private void ShowFields(Item item)
    {
        _fields.Children.Clear();
        _fields.Children.Add(Heading(item.Title));
        switch (item.Tag)
        {
            case AptCharacter c: TextFields(c); break;
            case ValueTuple<AptCharacter, AptFrame, AptFrameItem> p: PlacementFields(p.Item1, p.Item2, p.Item3); break;
            case AptConstant k: ConstantFields(k); break;
            default: StageFields(); break;
        }
    }

    private static TextBlock Heading(string text) => new() { Text = text, FontSize = 14, FontWeight = FontWeights.SemiBold, Foreground = Ui.Res("Text"), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 8) };

    private void Info(string key, string value)
    {
        var row = B.Cols((B.Text(key, "Muted"), B.Px(120)), (new TextBlock { Text = value, Foreground = Ui.Res("Faint"), TextWrapping = TextWrapping.Wrap }, B.Star()));
        row.Margin = new Thickness(0, 2, 0, 2);
        _fields.Children.Add(row);
    }

    /// <summary>A labelled text box; <paramref name="commit"/> runs when the value changed and the box lost focus (or Enter was pressed).</summary>
    private void Field(string label, string value, Action<string> commit, bool multiline = false, string? tip = null)
    {
        var box = new TextBox { Text = value, ToolTip = tip, AcceptsReturn = multiline, TextWrapping = multiline ? TextWrapping.Wrap : TextWrapping.NoWrap, MinLines = multiline ? 3 : 1, MaxLines = multiline ? 10 : 1, VerticalScrollBarVisibility = multiline ? ScrollBarVisibility.Auto : ScrollBarVisibility.Disabled };
        var original = value;
        void Commit()
        {
            if (box.Text == original) return;
            try { commit(box.Text); }
            catch (Exception ex)
            {
                Log.Warn($"{label}: {ex.Message}");
                box.Text = original;
                MessageBox.Show(Window.GetWindow(this), ex.Message, label, MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }
        box.LostFocus += (_, _) => Commit();
        if (!multiline) box.KeyDown += (_, e) => { if (e.Key == Key.Enter) { Commit(); e.Handled = true; } else if (e.Key == Key.Escape) { box.Text = original; e.Handled = true; } };
        var row = B.Cols((B.Text(label, "Muted"), B.Px(120)), (box, B.Star()));
        row.Margin = new Thickness(0, 3, 0, 3);
        ((TextBlock)row.Children[0]).VerticalAlignment = multiline ? VerticalAlignment.Top : VerticalAlignment.Center;
        _fields.Children.Add(row);
    }

    private void Check(string label, bool value, Action<bool> commit)
    {
        var box = new CheckBox { IsChecked = value, Content = label, Margin = new Thickness(120, 3, 0, 3) };
        box.Click += (_, _) => commit(box.IsChecked == true);
        _fields.Children.Add(box);
    }

    private void Choice(string label, string[] options, int selected, Action<int> commit)
    {
        var box = new ComboBox { ItemsSource = options, SelectedIndex = Math.Clamp(selected, 0, options.Length - 1), Width = 160, HorizontalAlignment = HorizontalAlignment.Left };
        var row = B.Cols((B.Text(label, "Muted"), B.Px(120)), (box, B.Star()));
        row.Margin = new Thickness(0, 3, 0, 3);
        var armed = false;
        box.Loaded += (_, _) => armed = true;
        box.SelectionChanged += (_, _) => { if (armed && box.SelectedIndex != selected) commit(box.SelectedIndex); };
        _fields.Children.Add(row);
    }

    private static float F(string s) => float.Parse(s.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture);
    private static int I(string s) => int.Parse(s.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture);
    private static uint X(string s) => uint.Parse(s.Trim().TrimStart('#').Replace("0x", "", StringComparison.OrdinalIgnoreCase), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
    private static string N(float v) => v.ToString("0.###", CultureInfo.InvariantCulture);

    private void ApplyApt(byte[] bytes, string what) => Editor.Apply(_apt, bytes, what);

    private void TextFields(AptCharacter c)
    {
        string Desc(string what) => $"{_apt.Name}: text field [{c.Id}] {what}";
        void Do(EditTextChange change, string what)
        {
            var bytes = AptEditor.EditText(_aptData, c, change, out var note);
            ApplyApt(bytes, Desc(what));
            if (note.Length > 0) Log.Info($"{_apt.Name} [{c.Id}]: {note}");
        }
        Field("Text", c.Text ?? "", v => Do(new EditTextChange { Text = v }, $"text → “{Short(v, 30)}”"), multiline: true, tip: "Initial text of the field (Latin-1). Click elsewhere to apply.");
        Field("Variable", c.Variable ?? "", v => Do(new EditTextChange { Variable = v }, $"variable → {v}"), tip: "ActionScript variable bound to the field");
        var b = c.Bounds ?? default;
        Field("Left", N(b.X), v => Do(new EditTextChange { Bounds = b with { X = F(v) } }, "left"));
        Field("Top", N(b.Y), v => Do(new EditTextChange { Bounds = b with { Y = F(v) } }, "top"));
        Field("Right", N(b.Z), v => Do(new EditTextChange { Bounds = b with { Z = F(v) } }, "right"));
        Field("Bottom", N(b.W), v => Do(new EditTextChange { Bounds = b with { W = F(v) } }, "bottom"));
        Field("Font height", N(c.FontHeight), v => Do(new EditTextChange { FontHeight = F(v) }, "font height"));
        Field("Font character", c.FontId.ToString(), v => Do(new EditTextChange { FontId = I(v) }, "font"), tip: $"Character id of the font definition ({c.FontName})");
        Field("Colour (AABBGGRR)", c.TextColor.ToString("X8"), v => Do(new EditTextChange { Color = X(v) }, "colour"), tip: "Hex; the red channel is the lowest byte");
        Choice("Alignment", ["Left", "Right", "Centre", "Justify"], c.TextAlign, i => Do(new EditTextChange { Align = i }, "alignment"));
        Check("Read-only", c.ReadOnly, v => Do(new EditTextChange { ReadOnly = v }, "read-only"));
        Check("Multiline", c.Multiline, v => Do(new EditTextChange { Multiline = v }, "multiline"));
        Check("Word wrap", c.WordWrap, v => Do(new EditTextChange { WordWrap = v }, "word wrap"));
        Info("Font", string.IsNullOrEmpty(c.FontName) ? "-" : c.FontName!);
        Info("Offset", $"0x{c.Offset:X}");
    }

    private void PlacementFields(AptCharacter owner, AptFrame frame, AptFrameItem it)
    {
        void Do(PlacementChange change, string what) => ApplyApt(AptEditor.Placement(_aptData, it, change), $"{_apt.Name}: [{owner.Id}] frame {frame.Index} depth {it.Depth} {what}");
        var m = it.Matrix;
        var hasMatrix = (it.Flags & 4) != 0;
        Info("Where", $"{owner.Type} {owner.Id}, frame {frame.Index}" + (it.Name is { Length: > 0 } n ? $", instance “{n}”" : ""));
        Info("Flags", $"0x{it.Flags:X} ({(hasMatrix ? "matrix " : "")}{((it.Flags & 8) != 0 ? "colour " : "")}{((it.Flags & 2) != 0 ? "character " : "")}{((it.Flags & 1) != 0 ? "move" : "")})");
        Field("Character", (it.Character ?? -1).ToString(), v => Do(new PlacementChange { Character = I(v) }, "character"), tip: "Character id placed at this depth");
        Field("Depth", (it.Depth ?? 0).ToString(), v => Do(new PlacementChange { Depth = I(v) }, "depth"));
        Field("X", N(m.M31), v => Do(new PlacementChange { X = F(v) }, "x"), tip: hasMatrix ? null : "This placement has no matrix; setting a value enables it");
        Field("Y", N(m.M32), v => Do(new PlacementChange { Y = F(v) }, "y"));
        Field("Scale X", N(m.M11), v => Do(new PlacementChange { ScaleX = F(v) }, "scale x"));
        Field("Scale Y", N(m.M22), v => Do(new PlacementChange { ScaleY = F(v) }, "scale y"));
        Field("Skew X", N(m.M12), v => Do(new PlacementChange { SkewX = F(v) }, "skew x"));
        Field("Skew Y", N(m.M21), v => Do(new PlacementChange { SkewY = F(v) }, "skew y"));
        Field("Tint (AABBGGRR)", it.Tint.ToString("X8"), v => Do(new PlacementChange { Tint = X(v) }, "tint"), tip: "Colour multiplier; FFFFFFFF leaves the picture unchanged");
        Field("Clip depth", it.ClipDepth.ToString(), v => Do(new PlacementChange { ClipDepth = I(v) }, "clip depth"));
        Info("Offset", $"0x{it.Offset:X}");
    }

    private void ConstantFields(AptConstant k)
    {
        if (_const is null || _consts is null) { Info("Constants", "No .const file next to this movie."); return; }
        Field("Text", k.Text ?? "", v =>
        {
            var bytes = AptEditor.ConstantString(_constData, k, v, out var how);
            Editor.Apply(_const, bytes, $"{_const.Name}: constant #{k.Index} → “{Short(v, 30)}”");
            Log.Info($"{_const.Name} #{k.Index}: {how}");
        }, multiline: true, tip: "String constant used by this movie's scripts (Latin-1). Click elsewhere to apply.");
        Info("Index", k.Index.ToString());
        Info("Used by", "ActionScript in this movie — changing it changes every use of the string");
        Info("File", _const.Path);
    }

    private void StageFields()
    {
        void Do(int? w, int? h, int? t, string what) => ApplyApt(AptEditor.Stage(_aptData, _file, w, h, t), $"{_apt.Name}: stage {what}");
        Field("Width", _file.Width.ToString(), v => Do(I(v), null, null, "width"));
        Field("Height", _file.Height.ToString(), v => Do(null, I(v), null, "height"));
        Field("Frame time (ms)", _file.FrameTime.ToString(), v => Do(null, null, I(v), "frame time"));
        Info("Characters", _file.Characters.Count.ToString());
        Info("Note", "Changing the stage size changes the movie's coordinate space; positions are not rescaled.");
    }
}
