using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;

namespace MVPWorkshop.App.Viewers;

/// <summary>Small code-behind UI builders so viewers stay compact and consistent with the theme.</summary>
public static class B
{
    public static TextBlock Text(string s, string brush = "Text", double size = 12, FontWeight? weight = null) =>
        new() { Text = s, Foreground = Ui.Res(brush), FontSize = size, FontWeight = weight ?? FontWeights.Normal, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };

    public static TextBlock Glyph(string g, string brush = "Text", double size = 13) =>
        new() { Text = g, Style = Ui.Style("Glyph"), Foreground = Ui.Res(brush), FontSize = size };

    public static Button Button(string label, string? glyph, RoutedEventHandler click, string style = "Tool", string? tip = null)
    {
        var sp = new StackPanel { Orientation = Orientation.Horizontal };
        if (glyph is not null) sp.Children.Add(Glyph(glyph, style == "Primary" ? "WindowBg" : "Text", 12));
        if (label.Length > 0) sp.Children.Add(new TextBlock { Text = label, Margin = new Thickness(glyph is null ? 0 : 6, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center });
        var b = new Button { Content = sp, Style = Ui.Style(style), ToolTip = tip, Margin = new Thickness(1, 0, 1, 0) };
        b.Click += click;
        return b;
    }

    public static Border Separator() => new() { Width = 1, Background = Ui.Res("Line"), Margin = new Thickness(8, 6, 8, 6) };

    public static Border Toolbar(params UIElement[] items)
    {
        var sp = new WrapPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(8, 4, 8, 4) };
        foreach (var i in items)
        {
            if (i is FrameworkElement fe) { fe.VerticalAlignment = VerticalAlignment.Center; if (fe.Height is double.NaN && fe is not Border) fe.MinHeight = 28; }
            sp.Children.Add(i);
        }
        return new Border { Background = Ui.Res("PanelAlt"), BorderBrush = Ui.Res("Line"), BorderThickness = new Thickness(0, 0, 0, 1), Child = sp, MinHeight = 38 };
    }

    /// <summary>Segmented choice; returns the panel. <paramref name="changed"/> receives the selected index.</summary>
    public static StackPanel Segments(string caption, string[] options, int selected, Action<int> changed)
    {
        var sp = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(4, 0, 4, 0) };
        if (caption.Length > 0) sp.Children.Add(new TextBlock { Text = caption, Foreground = Ui.Res("Muted"), Margin = new Thickness(0, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center });
        var group = Guid.NewGuid().ToString();
        for (var i = 0; i < options.Length; i++)
        {
            var idx = i;
            var rb = new RadioButton { Content = options[i], GroupName = group, IsChecked = i == selected };
            rb.Checked += (_, _) => changed(idx);
            sp.Children.Add(rb);
        }
        return sp;
    }

    public static ToggleButton Toggle(string label, bool on, Action<bool> changed)
    {
        var t = new ToggleButton { Content = label, IsChecked = on, Style = Ui.Style("Segment"), Margin = new Thickness(4, 0, 0, 0) };
        t.Click += (_, _) => changed(t.IsChecked == true);
        return t;
    }

    public static Border Card(UIElement content, double maxWidth = 760) => new()
    {
        Background = Ui.Res("PanelBg"), BorderBrush = Ui.Res("Line"), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(6),
        Padding = new Thickness(22), Margin = new Thickness(28), MaxWidth = maxWidth, HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top, Child = content
    };

    public static DataGrid Grid() => new() { AutoGenerateColumns = false };

    public static DataGridTextColumn Col(string header, string path, double width = double.NaN, bool mono = false, string? format = null)
    {
        var binding = new System.Windows.Data.Binding(path) { Mode = System.Windows.Data.BindingMode.OneWay, StringFormat = format };
        var c = new DataGridTextColumn { Header = header, Binding = binding };
        if (!double.IsNaN(width)) c.Width = width;
        if (mono) c.ElementStyle = new Style(typeof(TextBlock)) { Setters = { new Setter(TextBlock.FontFamilyProperty, Application.Current.Resources["Mono"]) } };
        return c;
    }

    public static TextBox SearchBox(string hint, Action<string> changed, double width = 220)
    {
        var tb = new TextBox { Width = width, Margin = new Thickness(4, 0, 4, 0), ToolTip = hint };
        tb.TextChanged += (_, _) => changed(tb.Text);
        return tb;
    }

    public static Grid Rows(params (UIElement el, GridLength h)[] rows)
    {
        var g = new Grid();
        for (var i = 0; i < rows.Length; i++)
        {
            g.RowDefinitions.Add(new RowDefinition { Height = rows[i].h });
            System.Windows.Controls.Grid.SetRow(rows[i].el, i);
            g.Children.Add(rows[i].el);
        }
        return g;
    }

    public static Grid Cols(params (UIElement el, GridLength w)[] cols)
    {
        var g = new Grid();
        for (var i = 0; i < cols.Length; i++)
        {
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = cols[i].w });
            System.Windows.Controls.Grid.SetColumn(cols[i].el, i);
            g.Children.Add(cols[i].el);
        }
        return g;
    }

    public static GridSplitter VSplit() => new() { Width = 4, HorizontalAlignment = HorizontalAlignment.Stretch };

    public static GridLength Star(double v = 1) => new(v, GridUnitType.Star);
    public static GridLength Px(double v) => new(v);
    public static readonly GridLength Auto = GridLength.Auto;
}
