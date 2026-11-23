using System.Globalization;
using System.Numerics;
using System.Windows;
using System.Windows.Controls;
using MVPWorkshop.Core.Formats;
using MVPWorkshop.Core.Vfs;

namespace MVPWorkshop.App.Viewers;

public sealed class NascarAptArtworkView : DockPanel
{
    private sealed class VertexRow { public int Index { get; init; } public float X { get; set; } public float Y { get; set; } }
    private readonly ComboBox shapes = new() { MinWidth = 220, MaxWidth = 650 };
    private readonly ComboBox draws = new() { MinWidth = 180 };
    private readonly StackPanel fields = new() { Margin = new Thickness(12) };
    private readonly DataGrid points = B.Grid();
    private readonly TextBlock status = B.Text("", "Muted");
    private readonly IReadOnlyList<NascarAptResources.Resource> resources;
    private readonly Dictionary<string, TextBox> values = [];
    private VertexRow[] rows = [];
    public NascarAptArtworkView(AssetNode movie)
    {
        resources = NascarAptResources.Read(movie).Where(r => r.Kind == 0).ToArray();
        shapes.ItemsSource = resources.Select(r => $"Shape [{r.Character}] · {r.Node.Key}").ToArray();
        shapes.SelectionChanged += (_, _) => { if (resources.ElementAtOrDefault(shapes.SelectedIndex) is { } r) { draws.ItemsSource = NascarAptShape.Read(r.Node.ReadAll()).Select((d, i) => $"Batch {i} · fill {d.Kind} · {d.Vertices.Length} vertices").ToArray(); draws.SelectedIndex = 0; } };
        draws.SelectionChanged += (_, _) => Show();
        points.IsReadOnly = false; points.CanUserAddRows = false; points.CanUserDeleteRows = false;
        points.Columns.Add(B.Col("Vertex", nameof(VertexRow.Index), 70, true));
        points.Columns.Add(B.Col("X", nameof(VertexRow.X), 130)); points.Columns.Add(B.Col("Y", nameof(VertexRow.Y), 130));
        var bar = B.Toolbar(shapes, draws, B.Button("Apply artwork", null, (_, _) => Apply())); SetDock(bar, Dock.Top); Children.Add(bar);
        var note = new TextBlock { Text = "Edit native artwork vertices, fill colour and UV mapping. Changes affect every use of this shape and use project undo/export.", TextWrapping = TextWrapping.Wrap, Foreground = Ui.Res("Muted"), Margin = new Thickness(10) }; SetDock(note, Dock.Top); Children.Add(note);
        var footer = B.Toolbar(status); SetDock(footer, Dock.Bottom); Children.Add(footer);
        var propertyScroll = new ScrollViewer { Content = fields, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Width = 300 }; SetDock(propertyScroll, Dock.Left); Children.Add(propertyScroll); Children.Add(points);
        if (resources.Count > 0) shapes.SelectedIndex = 0; else status.Text = "No native Xbox artwork resources in this movie.";
    }
    private void Show()
    {
        fields.Children.Clear(); values.Clear();
        if (resources.ElementAtOrDefault(shapes.SelectedIndex) is not { } r || draws.SelectedIndex < 0) return;
        try
        {
            var draw = NascarAptShape.Read(r.Node.ReadAll())[draws.SelectedIndex];
            void Field(string name, string value) { fields.Children.Add(B.Text(name, "Muted")); var box = new TextBox { Text = value, Margin = new Thickness(0, 3, 0, 9) }; values[name] = box; fields.Children.Add(box); }
            Field("Colour AABBGGRR", draw.Color.ToString("X8")); Field("Texture character ID", draw.Texture.ToString());
            var m = draw.TextureMatrix;
            var names = new[] { "UV scale X", "UV skew X", "UV skew Y", "UV scale Y", "UV offset X", "UV offset Y" };
            var v = new[] { m.M11, m.M12, m.M21, m.M22, m.M31, m.M32 };
            for (int i = 0; i < names.Length; i++) Field(names[i], v[i].ToString("R", CultureInfo.InvariantCulture));
            rows = draw.Vertices.Select((p, i) => new VertexRow { Index = i, X = p.X, Y = p.Y }).ToArray(); points.ItemsSource = rows;
            status.Text = r.Node.Key;
        }
        catch (Exception ex) { status.Text = ex.Message; }
    }
    private void Apply()
    {
        if (resources.ElementAtOrDefault(shapes.SelectedIndex) is not { } r || draws.SelectedIndex < 0) return;
        try
        {
            if (!points.CommitEdit(DataGridEditingUnit.Cell, true) || !points.CommitEdit(DataGridEditingUnit.Row, true)) throw new InvalidOperationException("Correct the vertex values before applying.");
            float F(string key) => float.Parse(values[key].Text, CultureInfo.InvariantCulture);
            var matrix = new Matrix3x2(F("UV scale X"), F("UV skew X"), F("UV skew Y"), F("UV scale Y"), F("UV offset X"), F("UV offset Y"));
            var color = uint.Parse(values["Colour AABBGGRR"].Text.Trim().TrimStart('#'), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
            int texture = int.Parse(values["Texture character ID"].Text, CultureInfo.InvariantCulture);
            var bytes = NascarAptShape.Edit(r.Node.ReadAll(), draws.SelectedIndex, color, texture, matrix, rows.Select(p => new Vector2(p.X, p.Y)).ToArray());
            Editor.Apply(r.Node, bytes, $"Edit UI shape [{r.Character}] batch {draws.SelectedIndex}");
            status.Text = "Artwork applied to project history.";
        }
        catch (Exception ex) { status.Text = ex.Message; }
    }
}
