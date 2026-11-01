using System.Windows;
using System.Windows.Controls;
using MVPWorkshop.Core.Formats;
using MVPWorkshop.Core.IO;
using MVPWorkshop.Core.Vfs;

namespace MVPWorkshop.App.Viewers;

public sealed class StructureView : Grid
{
    public StructureView(AssetNode node, IByteSource source, Action<StructureRegion>? navigate = null)
    {
        var fields = B.Grid(); fields.Columns.Add(B.Col("Group", "Group", 120)); fields.Columns.Add(B.Col("Field", "Key", 220)); fields.Columns.Add(B.Col("Value", "Value", 520));
        var decoded = FormatInspector.Describe(node.Format, source); fields.ItemsSource = decoded;
        var regions = B.Grid(); regions.Columns.Add(B.Col("Offset", "Offset", 110, format: "X8")); regions.Columns.Add(B.Col("Length", "Length", 110, format: "N0")); regions.Columns.Add(B.Col("Region", "Name", 500));
        regions.ItemsSource = AssetStructure.Read(node.Format, source);
        regions.SelectionChanged += (_, _) => { if (regions.SelectedItem is StructureRegion region) navigate?.Invoke(region); };
        var words = B.Grid(); words.Columns.Add(B.Col("Offset", "Offset", 110, format: "X8")); words.Columns.Add(B.Col("LE u32", "Little", 140)); words.Columns.Add(B.Col("BE u32", "Big", 140)); words.Columns.Add(B.Col("FourCC / raw", "Text", 250));
        var data = source.ReadBytes(0, (int)Math.Min(source.Length, 4096));
        words.ItemsSource = Enumerable.Range(0, data.Length / 4).Select(i => new { Offset = i * 4, Little = Bin.U32LE(data, i * 4), Big = Bin.U32BE(data, i * 4), Text = string.Concat(data.Skip(i * 4).Take(4).Select(b => b is >= 32 and < 127 ? (char)b : '·')) }).ToList();
        var tabs = new TabControl(); tabs.Items.Add(new TabItem { Header = "Decoded fields", Content = fields }); tabs.Items.Add(new TabItem { Header = "Byte regions → Hex", Content = regions }); tabs.Items.Add(new TabItem { Header = "Raw header words (first 4 KiB)", Content = words });
        if (decoded.Count == 0) tabs.SelectedIndex = 2;
        Children.Add(B.Rows((new TextBlock { Text = "Known fields and bounded byte regions. Raw words are uninterpreted; unknown structures remain visible in Hex and Strings.", TextWrapping = TextWrapping.Wrap, Margin = new Thickness(10), Foreground = Ui.Res("Muted") }, B.Auto), (tabs, B.Star())));
    }
}
