using System.Data;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using MVPWorkshop.Core.Formats;
using MVPWorkshop.Core.IO;
using MVPWorkshop.Core.Vfs;

namespace MVPWorkshop.App.Viewers;

public static class SimpleViews
{
    public static FrameworkElement Text(string text)
    {
        var lines = text.Count(c => c == '\n') + 1;
        var wrap = B.Toggle("Wrap", false, _ => { });
        var tb = new TextBox
        {
            Text = text, IsReadOnly = true, FontFamily = (FontFamily)Application.Current.Resources["Mono"], FontSize = 12.5,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto, VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            BorderThickness = new Thickness(0), Background = Ui.Res("WindowBg"), Padding = new Thickness(14, 10, 14, 10),
            VerticalContentAlignment = VerticalAlignment.Top, AcceptsReturn = true,
        };
        wrap.Click += (_, _) => tb.TextWrapping = wrap.IsChecked == true ? TextWrapping.Wrap : TextWrapping.NoWrap;
        var bar = B.Toolbar(B.Text($"{lines:N0} lines · {text.Length:N0} chars · Windows-1252", "Muted"), B.Separator(), wrap);
        return B.Rows((bar, B.Auto), (tb, B.Star()));
    }

    public static FrameworkElement Table(IByteSource src)
    {
        var rows = FormatInspector.ParseCsv(FormatInspector.ReadText(src));
        var grid = B.Grid();
        grid.AutoGenerateColumns = false;
        grid.FrozenColumnCount = 1;
        var headerRow = true;

        void Fill()
        {
            var dt = new DataTable();
            var cols = rows.Count == 0 ? 0 : rows.Max(r => r.Length);
            var names = new HashSet<string>();
            grid.Columns.Clear();
            dt.Columns.Add("#", typeof(int));
            grid.Columns.Add(B.Col("#", "[#]", 48, true));
            for (var c = 0; c < cols; c++)
            {
                var name = headerRow && rows.Count > 0 && c < rows[0].Length && rows[0][c].Trim().Length > 0 ? rows[0][c].Trim() : $"Col{c + 1}";
                var unique = name; var k = 2;
                while (!names.Add(unique)) unique = $"{name} ({k++})";
                dt.Columns.Add($"c{c}");
                grid.Columns.Add(new DataGridTextColumn { Header = unique.Replace("_", "__"), Binding = new System.Windows.Data.Binding($"[c{c}]") });
            }
            for (var r = headerRow ? 1 : 0; r < rows.Count; r++)
            {
                if (rows[r].Length == 1 && rows[r][0].Length == 0) continue;
                var dr = dt.NewRow(); dr[0] = r + 1;
                for (var c = 0; c < rows[r].Length; c++) dr[c + 1] = rows[r][c];
                dt.Rows.Add(dr);
            }
            grid.ItemsSource = dt.DefaultView;
        }
        Fill();

        var filter = B.SearchBox("Filter rows", q =>
        {
            if (grid.ItemsSource is DataView v)
            {
                if (q.Length == 0) { v.RowFilter = ""; return; }
                var esc = q.Replace("'", "''").Replace("[", "[[]").Replace("*", "[*]").Replace("%", "[%]");
                v.RowFilter = string.Join(" OR ", v.Table!.Columns.Cast<DataColumn>().Skip(1).Select(c => $"[{c.ColumnName}] LIKE '%{esc}%'"));
            }
        });
        var bar = B.Toolbar(B.Text($"{rows.Count:N0} rows", "Muted"), B.Separator(),
            B.Toggle("First row is header", true, v => { headerRow = v; Fill(); }), B.Separator(), B.Glyph("", "Muted", 12), filter);
        return B.Rows((bar, B.Auto), (grid, B.Star()));
    }

    public static FrameworkElement Chunks(List<ChunkInfo> chunks)
    {
        var g = B.Grid();
        g.Columns.Add(B.Col("Offset", nameof(ChunkRow.Offset), 110, true));
        g.Columns.Add(B.Col("Id", nameof(ChunkRow.Id), 90, true));
        g.Columns.Add(B.Col("Size", nameof(ChunkRow.Size), 110, true));
        g.Columns.Add(B.Col("Size (dec)", nameof(ChunkRow.SizeDec), 110));
        g.ItemsSource = chunks.Select(c => new ChunkRow($"0x{c.Offset:X8}", c.Id, $"0x{c.Size:X}", c.Size.ToString("N0"))).ToList();
        return g;
    }

    private sealed record ChunkRow(string Offset, string Id, string Size, string SizeDec);

    public static FrameworkElement Font(byte[] ttf, string name)
    {
        // WPF loads fonts from a URI, so copy to a temp file (the game file itself is never touched).
        var dir = Path.Combine(Path.GetTempPath(), "MVP-Workshop", "fonts", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, name);
        File.WriteAllBytes(path, ttf);
        var families = System.Windows.Media.Fonts.GetFontFamilies(new Uri(dir + Path.DirectorySeparatorChar));
        var fam = families.FirstOrDefault();
        var sp = new StackPanel { Margin = new Thickness(28) };
        if (fam is null) { sp.Children.Add(B.Text("Could not load font.", "Bad")); return sp; }
        sp.Children.Add(B.Text(fam.Source.Split('#').Last(), "Muted", 13));
        foreach (var size in new[] { 48.0, 32, 22, 15 })
            sp.Children.Add(new TextBlock { Text = "The quick brown fox jumps over the lazy dog 0123456789", FontFamily = fam, FontSize = size, Foreground = Ui.Res("Text"), Margin = new Thickness(0, 14, 0, 0), TextWrapping = TextWrapping.Wrap });
        sp.Children.Add(new TextBlock { Text = "ABCDEFGHIJKLMNOPQRSTUVWXYZ\nabcdefghijklmnopqrstuvwxyz\n!\"#$%&'()*+,-./:;<=>?@[\\]^_`{|}~ ©®™é", FontFamily = fam, FontSize = 24, Foreground = Ui.Res("Muted"), Margin = new Thickness(0, 22, 0, 0) });
        return new ScrollViewer { Content = sp, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
    }
}

public static class InfoCard
{
    private static readonly Dictionary<FormatId, string> Notes = new()
    {
        [FormatId.SonyHd] = "SCEI HD sound bank header (Vers/Head/Vagi/Smpl/Sset/Prog chunks). Samples live in the paired .BD.",
        [FormatId.SonyBd] = "SCEI BD body: raw PS-ADPCM sample data addressed by the paired .HD.",
        [FormatId.BilgSkeleton] = "BILG skeleton/collision body: bone names are listed in Properties.",
        [FormatId.Ps2IconSys] = "PS2 memory-card icon.sys — title and icon file names are in Properties.",
        [FormatId.Ps2Icon] = "PS2 memory-card 3D icon model.",
        [FormatId.Ps2Overlay] = "Raw MIPS code overlay loaded at runtime (no ELF header).",
        [FormatId.AsfLut] = "Lookup table mapping sound IDs to entries in the matching .asf bank.",
        [FormatId.AdachiFont] = "Network GUI bitmap font (Adachi font format).",
        [FormatId.PfrFont] = "Bitstream PFR font used by the EA online module.",
        [FormatId.XboxExecutable] = "Xbox executable (XBE). Title, ID, regions, sections and linked SDK libraries are listed in Properties; the code is x86 and is not disassembled here.",
        [FormatId.Empty] = "This entry is zero bytes long.",
    };

    public static FrameworkElement For(AssetNode n)
    {
        var sp = new StackPanel();
        var head = new StackPanel { Orientation = Orientation.Horizontal };
        head.Children.Add(B.Glyph(n.FormatInfo.Glyph, TreeItem.CategoryBrush(n.FormatInfo.Category), 28));
        head.Children.Add(new TextBlock { Text = n.FormatInfo.Name, FontSize = 20, FontWeight = FontWeights.SemiBold, Margin = new Thickness(14, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center, Foreground = Ui.Res("Text") });
        sp.Children.Add(head);
        sp.Children.Add(new TextBlock
        {
            Text = Notes.GetValueOrDefault(n.Format, n.FormatInfo.Description + ". No dedicated viewer yet — use the Hex, Strings and Chunks tabs to study the layout."),
            TextWrapping = TextWrapping.Wrap, Foreground = Ui.Res("Muted"), Margin = new Thickness(0, 14, 0, 0), LineHeight = 20
        });
        var head16 = n.Open().ReadBytes(0, 16);
        sp.Children.Add(new TextBlock { Text = "First bytes", Foreground = Ui.Res("Faint"), Margin = new Thickness(0, 18, 0, 4) });
        sp.Children.Add(new TextBox
        {
            Text = string.Join(" ", head16.Select(b => b.ToString("X2"))) + "    " + new string(head16.Select(b => b is >= 32 and < 127 ? (char)b : '.').ToArray()),
            IsReadOnly = true, FontFamily = (FontFamily)Application.Current.Resources["Mono"], Background = Ui.Res("WindowBg"), BorderThickness = new Thickness(0), Padding = new Thickness(8)
        });
        return new ScrollViewer { Content = B.Card(sp), VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
    }

    public static FrameworkElement Error(AssetNode n, Exception ex)
    {
        var sp = new StackPanel();
        sp.Children.Add(new TextBlock { Text = "Viewer failed", FontSize = 18, FontWeight = FontWeights.SemiBold, Foreground = Ui.Res("Bad") });
        sp.Children.Add(new TextBlock { Text = ex.Message, TextWrapping = TextWrapping.Wrap, Foreground = Ui.Res("Muted"), Margin = new Thickness(0, 10, 0, 0) });
        sp.Children.Add(new TextBlock { Text = "The Hex and Strings tabs still work.", Foreground = Ui.Res("Faint"), Margin = new Thickness(0, 10, 0, 0) });
        return B.Card(sp);
    }
}

/// <summary>Extracted ASCII strings with offsets and a filter.</summary>
public sealed class StringsView : Grid
{
    public StringsView(IByteSource src)
    {
        var data = src.Length > 64 * 1024 * 1024 ? src.ReadBytes(0, 64 * 1024 * 1024) : src.ReadAll();
        var minLen = 4;
        var grid = B.Grid();
        grid.Columns.Add(B.Col("Offset", nameof(Row.Offset), 110, true));
        grid.Columns.Add(B.Col("Len", nameof(Row.Len), 60));
        grid.Columns.Add(new DataGridTextColumn { Header = "Text", Binding = new System.Windows.Data.Binding(nameof(Row.Text)), Width = new DataGridLength(1, DataGridLengthUnitType.Star) });
        List<Row> all = [];
        var count = B.Text("", "Muted");
        var filter = "";
        void Apply()
        {
            var list = filter.Length == 0 ? all : all.Where(r => r.Text.Contains(filter, StringComparison.OrdinalIgnoreCase)).ToList();
            grid.ItemsSource = list;
            count.Text = $"{list.Count:N0} strings";
        }
        void Scan() { all = FormatInspector.ExtractStrings(data, minLen).Select(s => new Row($"0x{s.Offset:X8}", s.Text.Length, s.Text)).ToList(); Apply(); }
        Scan();
        var bar = B.Toolbar(count, B.Separator(), B.Segments("Min length", ["3", "4", "6", "8"], 1, i => { minLen = new[] { 3, 4, 6, 8 }[i]; Scan(); }),
            B.Separator(), B.Glyph("", "Muted", 12), B.SearchBox("Filter", q => { filter = q; Apply(); }));
        var g = B.Rows((bar, B.Auto), (grid, B.Star()));
        Children.Add(g);
    }

    private sealed record Row(string Offset, int Len, string Text);
}

/// <summary>Listing of a folder/archive's direct children.</summary>
public sealed class ArchiveView : Grid
{
    public ArchiveView(AssetNode node)
    {
        var rows = new List<Row>();
        void Walk(AssetNode n, string prefix)
        {
            foreach (var c in n.Children)
            {
                if (c.IsVirtualFolder) { Walk(c, prefix + c.Name + "/"); continue; }
                rows.Add(new Row(c, prefix + c.Name, c.IsFolder ? "Folder" : c.FormatInfo.Name, c.StoredSize, c.Location));
            }
        }
        Walk(node, "");
        var grid = B.Grid();
        grid.Columns.Add(new DataGridTextColumn { Header = "Name", Binding = new System.Windows.Data.Binding(nameof(Row.Name)), Width = new DataGridLength(1, DataGridLengthUnitType.Star) });
        grid.Columns.Add(B.Col("Format", nameof(Row.Format), 150));
        grid.Columns.Add(B.Col("Size", nameof(Row.SizeText), 100));
        grid.Columns.Add(B.Col("Location", nameof(Row.Location), 220, true));
        grid.ItemsSource = rows;
        grid.MouseDoubleClick += (_, e) =>
        {
            if ((e.OriginalSource as DependencyObject).FindAncestor<DataGridRow>()?.Item is Row r && !r.Node.IsFolder) MainWindow.Instance?.OpenAsset(r.Node);
        };
        grid.KeyDown += (_, e) => { if (e.Key == Key.Enter && grid.SelectedItem is Row r) { MainWindow.Instance?.OpenAsset(r.Node); e.Handled = true; } };

        var count = B.Text($"{rows.Count:N0} entries · {Bin.Size(rows.Sum(r => r.Size))}", "Muted");
        var filter = B.SearchBox("Filter entries", q => grid.ItemsSource = q.Length == 0 ? rows : rows.Where(r => r.Name.Contains(q, StringComparison.OrdinalIgnoreCase) || r.Format.Contains(q, StringComparison.OrdinalIgnoreCase)).ToList());
        var stats = rows.GroupBy(r => r.Format).OrderByDescending(g => g.Count()).Take(6).Select(g => $"{g.Key} ×{g.Count()}");
        var bar = B.Toolbar(count, B.Separator(), B.Glyph("", "Muted", 12), filter, B.Separator(), B.Text(string.Join("   ", stats), "Faint", 11));
        Children.Add(B.Rows((bar, B.Auto), (grid, B.Star())));
    }

    private sealed record Row(AssetNode Node, string Name, string Format, long Size, string Location)
    {
        public string SizeText => Bin.Size(Size);
    }
}
