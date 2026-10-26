using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using MVPWorkshop.Core.Formats;
using MVPWorkshop.Core.IO;
using MVPWorkshop.Core.Vfs;

namespace MVPWorkshop.App.Viewers;

/// <summary>Viewers for the text, font and AEMS audio-system tables (SFN, LOCH, ABKC, MOIR, PROJx headers).</summary>
public static class AemsViews
{
    public static BitmapSource Bitmap(DecodedImage d)
    {
        var bmp = BitmapSource.Create(d.Width, d.Height, 96, 96, PixelFormats.Bgra32, null, d.Bgra, d.Width * 4);
        bmp.Freeze();
        return bmp;
    }

    private static Border Checker(UIElement child) => new() { Background = Ui.Res("Checker"), Child = child, HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(16) };

    private static FrameworkElement Filtered<T>(List<T> rows, DataGrid grid, Func<T, string, bool> match, string caption, params UIElement[] extra)
    {
        grid.ItemsSource = rows;
        var count = B.Text($"{rows.Count:N0} {caption}", "Muted");
        var filter = B.SearchBox("Filter", q =>
        {
            var list = q.Length == 0 ? rows : rows.Where(r => match(r, q)).ToList();
            grid.ItemsSource = list;
            count.Text = $"{list.Count:N0} {caption}";
        });
        var bar = B.Toolbar([count, B.Separator(), B.Glyph("", "Muted", 12), filter, .. extra]);
        return B.Rows((bar, B.Auto), (grid, B.Star()));
    }

    // ------------------------------------------------------------------ SFN

    public static FrameworkElement Font(AssetNode node, IByteSource src)
    {
        var font = SfnFont.Read(src.ReadAll());
        var atlas = font.Atlas.Decode(font.Atlas.Images[0]);
        var scale = 2;
        var sample = "MVP 07 NCAA BASEBALL\nThe quick brown fox jumps over the lazy dog\n0123456789 !?&%$#@";

        var preview = new Image { Stretch = Stretch.None, SnapsToDevicePixels = true };
        RenderOptions.SetBitmapScalingMode(preview, BitmapScalingMode.NearestNeighbor);
        void Draw() => preview.Source = Bitmap(font.Render(atlas, sample, scale));
        Draw();
        var input = new TextBox { Text = sample, AcceptsReturn = true, Width = 420, Height = 60, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Margin = new Thickness(4, 0, 4, 0) };
        input.TextChanged += (_, _) => { sample = input.Text; Draw(); };
        var bar = B.Toolbar(B.Text("Sample", "Muted"), input, B.Separator(),
            B.Segments("Zoom", ["1×", "2×", "3×", "4×"], 1, i => { scale = i + 1; Draw(); }), B.Separator(),
            B.Button("Export Atlas PNG", "", (_, _) => Exporter.SaveBytes(PngWriter.Encode(atlas.Width, atlas.Height, atlas.Bgra), System.IO.Path.ChangeExtension(node.Name, ".png"), "PNG image|*.png"), "Outline"));
        var previewHost = new ScrollViewer { Content = Checker(preview), HorizontalScrollBarVisibility = ScrollBarVisibility.Auto, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Background = Ui.Res("WindowBg") };

        // Atlas with glyph rectangles; hovering a row highlights its cell.
        var canvas = new Canvas { Width = atlas.Width * 3, Height = atlas.Height * 3 };
        var atlasImg = new Image { Source = Bitmap(atlas), Width = atlas.Width * 3, Height = atlas.Height * 3 };
        RenderOptions.SetBitmapScalingMode(atlasImg, BitmapScalingMode.NearestNeighbor);
        canvas.Children.Add(atlasImg);
        foreach (var g in font.Glyphs.Where(g => g.Width > 0))
        {
            var r = new Rectangle { Width = g.Width * 3, Height = g.Height * 3, Stroke = Ui.Res("Accent"), StrokeThickness = 1, Opacity = 0.45, ToolTip = $"U+{g.Code:X4} '{(char)g.Code}'  {g.Width}×{g.Height} @({g.X},{g.Y})" };
            Canvas.SetLeft(r, g.X * 3); Canvas.SetTop(r, g.Y * 3);
            canvas.Children.Add(r);
        }
        var atlasHost = new ScrollViewer { Content = Checker(canvas), HorizontalScrollBarVisibility = ScrollBarVisibility.Auto, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Background = Ui.Res("WindowBg") };

        var glyphGrid = B.Grid();
        glyphGrid.Columns.Add(B.Col("Char", nameof(GlyphRow.Char), 50));
        glyphGrid.Columns.Add(B.Col("Code", nameof(GlyphRow.Code), 70, true));
        glyphGrid.Columns.Add(B.Col("Size", nameof(GlyphRow.Size), 70));
        glyphGrid.Columns.Add(B.Col("Atlas pos", nameof(GlyphRow.Pos), 90));
        glyphGrid.Columns.Add(B.Col("Advance", nameof(GlyphRow.Advance), 70));
        glyphGrid.Columns.Add(B.Col("Offset", nameof(GlyphRow.Offset), 80));
        glyphGrid.Columns.Add(B.Col("Extra", nameof(GlyphRow.Extra), 70, true));
        var glyphRows = font.Glyphs.Select(g => new GlyphRow(g.Code >= 0x20 ? ((char)g.Code).ToString() : "", $"U+{g.Code:X4}", $"{g.Width}×{g.Height}", $"{g.X}, {g.Y}", g.Advance, $"{g.OffsetX:+0;-0;0}, {g.OffsetY:+0;-0;0}", $"0x{g.Extra:X2}")).ToList();

        var kernGrid = B.Grid();
        kernGrid.Columns.Add(B.Col("Pair", nameof(KernRow.Pair), 70));
        kernGrid.Columns.Add(B.Col("Left", nameof(KernRow.Left), 80, true));
        kernGrid.Columns.Add(B.Col("Right", nameof(KernRow.Right), 80, true));
        kernGrid.Columns.Add(B.Col("Adjust", nameof(KernRow.Amount), 70));
        var kernRows = font.Kerning.Select(k => new KernRow($"{(char)k.Left}{(char)k.Right}", $"U+{k.Left:X4}", $"U+{k.Right:X4}", k.Amount)).ToList();

        var tabs = new TabControl();
        tabs.Items.Add(new TabItem { Header = "Glyph Atlas", Content = atlasHost });
        tabs.Items.Add(new TabItem { Header = $"Glyphs ({font.Glyphs.Count})", Content = Filtered(glyphRows, glyphGrid, (r, q) => r.Char.Contains(q) || r.Code.Contains(q, StringComparison.OrdinalIgnoreCase), "glyphs") });
        tabs.Items.Add(new TabItem { Header = $"Kerning ({font.Kerning.Count})", Content = Filtered(kernRows, kernGrid, (r, q) => r.Pair.Contains(q), "pairs") });

        var root = B.Rows((bar, B.Auto), (previewHost, B.Px(210)), (new GridSplitter { Height = 4, HorizontalAlignment = HorizontalAlignment.Stretch }, B.Px(4)), (tabs, B.Star()));
        return root;
    }

    private sealed record GlyphRow(string Char, string Code, string Size, string Pos, int Advance, string Offset, string Extra);
    private sealed record KernRow(string Pair, string Left, string Right, int Amount);

    // ------------------------------------------------------------------ LOCH

    public static FrameworkElement Loc(IByteSource src)
    {
        var t = LocTable.Read(src.ReadAll());
        var grid = B.Grid();
        grid.Columns.Add(B.Col("ID", nameof(LocRow.Id), 70, true));
        grid.Columns.Add(B.Col("Index", nameof(LocRow.Index), 60, true));
        grid.Columns.Add(new DataGridTextColumn { Header = "Text", Binding = new System.Windows.Data.Binding(nameof(LocRow.Text)), Width = new DataGridLength(1, DataGridLengthUnitType.Star) });
        var rows = t.Strings.Select(s => new LocRow(s.Id, s.Index, s.Text.Replace("\n", "⏎").Replace(" ", "·"))).ToList();
        return Filtered(rows, grid, (r, q) => r.Text.Contains(q, StringComparison.OrdinalIgnoreCase) || r.Id.ToString() == q, "strings",
            B.Separator(), B.Text("UTF-16LE · ⏎ = newline, · = non-breaking space", "Faint", 11));
    }

    private sealed record LocRow(int Id, int Index, string Text);

    // ------------------------------------------------------------------ ABKC

    public static FrameworkElement Abk(AssetNode node, IByteSource src, Action<List<Prop>> setExtra)
    {
        var all = src.ReadAll();
        var abk = AbkBank.Read(all);
        if (abk.ExtractBank(all) is { } bnk) return new AudioView(AudioView.FromBank(bnk), node, setExtra);

        var ast = node.Parent?.Children.FirstOrDefault(c => c.Name.Equals(System.IO.Path.ChangeExtension(node.Name, ".AST"), StringComparison.OrdinalIgnoreCase));
        var sp = new StackPanel();
        sp.Children.Add(new TextBlock { Text = "Stream-only module", FontSize = 20, FontWeight = FontWeights.SemiBold, Foreground = Ui.Res("Text") });
        sp.Children.Add(new TextBlock
        {
            Text = $"This ABKC bank has no embedded BNKl: its {abk.Modules} module(s) are compiled MIPS behaviour code that picks streams from the matching .AST pack at run time.",
            TextWrapping = TextWrapping.Wrap, Foreground = Ui.Res("Muted"), Margin = new Thickness(0, 12, 0, 12), LineHeight = 20,
        });
        if (ast is not null) sp.Children.Add(B.Button($"Open {ast.Name}", "", (_, _) => MainWindow.Instance?.OpenAsset(ast), "Primary"));
        return new ScrollViewer { Content = B.Card(sp), VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
    }

    // ------------------------------------------------------------------ MOIR

    public static FrameworkElement Csi(IByteSource src)
    {
        var s = MoirScript.Read(src.ReadAll());
        var grid = B.Grid();
        grid.Columns.Add(new DataGridTextColumn { Header = "Label", Binding = new System.Windows.Data.Binding(nameof(CsiLabel.Name)), Width = new DataGridLength(1, DataGridLengthUnitType.Star) });
        grid.Columns.Add(B.Col("Script offset", nameof(CsiLabel.Target), 110, true, "0x{0:X}"));
        return Filtered(s.Labels, grid, (r, q) => r.Name.Contains(q, StringComparison.OrdinalIgnoreCase), "labels");
    }

    // ------------------------------------------------------------------ PROJx HDR record

    public static FrameworkElement SpeechHeader(AssetNode node, IByteSource src, Action<List<Prop>> setExtra)
    {
        var data = src.ReadAll();
        var h = Core.Formats.SpeechHeader.Parse(data);
        var grid = B.Grid();
        grid.Columns.Add(B.Col("Offset", nameof(FieldRow.Offset), 70, true));
        grid.Columns.Add(B.Col("Field", nameof(FieldRow.Field), 160));
        grid.Columns.Add(new DataGridTextColumn { Header = "Value", Binding = new System.Windows.Data.Binding(nameof(FieldRow.Value)), Width = new DataGridLength(1, DataGridLengthUnitType.Star) });
        var rows = new List<FieldRow>();
        if (h is not null)
        {
            rows.Add(new("0x00", "Record id (u16)", $"{h.Id} (0x{h.Id:X})"));
            rows.Add(new("0x02", "Kind (u8)", h.Kind.ToString()));
            rows.Add(new("0x03", "Segments (u8)", h.Segments.ToString()));
            rows.Add(new("0x04", "Link (s32)", h.Link.ToString()));
            rows.Add(new("0x08", "u16", h.Field8.ToString()));
            rows.Add(new("0x0A", "u16 (span?)", h.Length.ToString()));
            if (h.SplitPoints.Length > 0) rows.Add(new("0x12", "Phrase split points? (BE u16)", string.Join(", ", h.SplitPoints)));
            for (var o = 0; o < h.Payload.Length; o += 16)
                rows.Add(new($"0x{Core.Formats.SpeechHeader.HeadSize + o:X2}", o == 0 ? "Payload" : "", string.Join(" ", h.Payload.Skip(o).Take(16).Select(b => b.ToString("X2")))));
        }
        grid.ItemsSource = rows;
        var partner = SpeechPairs.StreamFor(node);
        if (partner is null) return grid;
        var audio = new AudioView(AudioView.FromStreams(partner.Open()), partner, setExtra);
        var title = B.Toolbar(B.Glyph("", "Muted", 12), B.Text($"Paired stream: {partner.Path}", "Muted"));
        return B.Rows((grid, B.Star()), (new GridSplitter { Height = 4, HorizontalAlignment = HorizontalAlignment.Stretch }, B.Px(4)), (title, B.Auto), (audio, B.Px(300)));
    }

    private sealed record FieldRow(string Offset, string Field, string Value);
}
