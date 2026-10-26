using System.Buffers.Binary;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using MVPWorkshop.App.Viewers;
using MVPWorkshop.Core.IO;

namespace MVPWorkshop.App.Controls;

/// <summary>Virtualised hex view (renders only visible rows) with go-to-offset and a data inspector.</summary>
public sealed class HexPanel : Grid
{
    private readonly HexCanvas _hex;
    private readonly ScrollBar _bar = new() { Orientation = Orientation.Vertical };
    private readonly StackPanel _inspector = new() { Margin = new Thickness(12, 10, 12, 10) };
    private readonly TextBox _goto = new() { Width = 120, ToolTip = "Offset (hex, or decimal with #prefix)" };
    private readonly TextBlock _sel = B.Text("", "Muted");

    public HexPanel(IByteSource src)
    {
        _hex = new HexCanvas(src);
        _bar.Maximum = Math.Max(0, _hex.TotalRows - 1);
        _bar.SmallChange = 1;
        _bar.LargeChange = 16;
        _bar.ValueChanged += (_, e) => { _hex.TopRow = (long)e.NewValue; };
        _hex.ViewportRowsChanged += rows => { _bar.ViewportSize = rows; _bar.LargeChange = Math.Max(1, rows - 1); _bar.Maximum = Math.Max(0, _hex.TotalRows - rows); };
        _hex.SelectionChanged += UpdateInspector;
        _hex.ScrollRequest += row => _bar.Value = Math.Clamp(row, 0, _bar.Maximum);
        _hex.MouseWheel += (_, e) => _bar.Value = Math.Clamp(_bar.Value - Math.Sign(e.Delta) * 3, 0, _bar.Maximum);

        _goto.KeyDown += (_, e) => { if (e.Key == Key.Enter) Goto(); };
        var bar = B.Toolbar(B.Text($"{src.Length:N0} bytes (0x{src.Length:X})", "Muted"), B.Separator(),
            B.Text("Go to  ", "Muted"), _goto, B.Button("Go", "", (_, _) => Goto()), B.Separator(), _sel);

        var hexHost = B.Cols((_hex, B.Star()), (_bar, B.Auto));
        hexHost.Background = Ui.Res("WindowBg");
        var insp = new Border
        {
            Background = Ui.Res("PanelBg"), BorderBrush = Ui.Res("Line"), BorderThickness = new Thickness(1, 0, 0, 0),
            Child = new ScrollViewer { Content = _inspector, VerticalScrollBarVisibility = ScrollBarVisibility.Auto }
        };
        Children.Add(B.Rows((bar, B.Auto), (B.Cols((hexHost, B.Star()), (insp, B.Px(290))), B.Star())));
        UpdateInspector(0);
    }

    private void Goto()
    {
        var t = _goto.Text.Trim();
        long off;
        var ok = t.StartsWith('#') ? long.TryParse(t[1..], out off) : long.TryParse(t.Replace("0x", "", StringComparison.OrdinalIgnoreCase), NumberStyles.HexNumber, null, out off);
        if (ok) _hex.Select(off, true);
    }

    private void UpdateInspector(long off)
    {
        _sel.Text = $"Offset 0x{off:X8} ({off:N0})";
        _inspector.Children.Clear();
        _inspector.Children.Add(new TextBlock { Text = "DATA INSPECTOR", Style = Ui.Style("PanelHeader"), Margin = new Thickness(0, 0, 0, 8) });
        var b = _hex.Source.ReadBytes(off, 8);
        var p = new byte[8]; b.CopyTo(p, 0);
        void Row(string k, string v)
        {
            var g = B.Cols((B.Text(k, "Muted", 11.5), B.Px(78)), (new TextBox { Text = v, IsReadOnly = true, BorderThickness = new Thickness(0), Background = Brushes.Transparent, Padding = new Thickness(0), FontFamily = (FontFamily)Application.Current.Resources["Mono"] }, B.Star()));
            g.Margin = new Thickness(0, 2, 0, 2);
            _inspector.Children.Add(g);
        }
        Row("u8 / s8", $"{p[0]} / {(sbyte)p[0]}");
        Row("binary", Convert.ToString(p[0], 2).PadLeft(8, '0'));
        Row("u16 LE", BinaryPrimitives.ReadUInt16LittleEndian(p).ToString());
        Row("u16 BE", BinaryPrimitives.ReadUInt16BigEndian(p).ToString());
        Row("s16 LE", BinaryPrimitives.ReadInt16LittleEndian(p).ToString());
        Row("u24 LE", (p[0] | p[1] << 8 | p[2] << 16).ToString());
        Row("u32 LE", $"{BinaryPrimitives.ReadUInt32LittleEndian(p)} (0x{BinaryPrimitives.ReadUInt32LittleEndian(p):X})");
        Row("u32 BE", $"{BinaryPrimitives.ReadUInt32BigEndian(p)} (0x{BinaryPrimitives.ReadUInt32BigEndian(p):X})");
        Row("s32 LE", BinaryPrimitives.ReadInt32LittleEndian(p).ToString());
        Row("f32 LE", BinaryPrimitives.ReadSingleLittleEndian(p).ToString("G7", CultureInfo.InvariantCulture));
        Row("f32 BE", BinaryPrimitives.ReadSingleBigEndian(p).ToString("G7", CultureInfo.InvariantCulture));
        Row("u64 LE", BinaryPrimitives.ReadUInt64LittleEndian(p).ToString());
        Row("f64 LE", BinaryPrimitives.ReadDoubleLittleEndian(p).ToString("G10", CultureInfo.InvariantCulture));
        Row("4CC", Bin.FourCC(p));
        Row("RGBA", $"{p[0]},{p[1]},{p[2]},{p[3]}");
        var sw = new Border { Height = 18, CornerRadius = new CornerRadius(3), Margin = new Thickness(78, 4, 0, 0), Background = new SolidColorBrush(Color.FromRgb(p[0], p[1], p[2])) };
        _inspector.Children.Add(sw);
    }

    private sealed class HexCanvas : FrameworkElement
    {
        private const int PerRow = 16;
        private readonly Typeface _face = new((FontFamily)Application.Current.Resources["Mono"], FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);
        private readonly double _fontSize = 12.5;
        private readonly double _lineH = 19;
        private double _charW;
        private long _top;
        private long _selected;
        private int _viewRows;
        private static readonly Brush OffBrush = new SolidColorBrush(Color.FromRgb(0x6A, 0x6D, 0x77));
        private static readonly Brush HexBrush = new SolidColorBrush(Color.FromRgb(0xE4, 0xE5, 0xE9));
        private static readonly Brush ZeroBrush = new SolidColorBrush(Color.FromRgb(0x55, 0x58, 0x62));
        private static readonly Brush AsciiBrush = new SolidColorBrush(Color.FromRgb(0x9B, 0xC8, 0x8E));
        private static readonly Brush SelBrush = new SolidColorBrush(Color.FromArgb(0x90, 0xF0, 0x91, 0x2E));
        private static readonly Brush StripeBrush = new SolidColorBrush(Color.FromRgb(0x18, 0x19, 0x1D));

        public HexCanvas(IByteSource src)
        {
            Source = src;
            Focusable = true;
            var ft = new FormattedText("0", CultureInfo.InvariantCulture, FlowDirection.LeftToRight, _face, _fontSize, Brushes.White, 1.0);
            _charW = ft.WidthIncludingTrailingWhitespace;
            ClipToBounds = true;
        }

        public IByteSource Source { get; }
        public long TotalRows => (Source.Length + PerRow - 1) / PerRow;
        public event Action<long>? SelectionChanged;
        public event Action<int>? ViewportRowsChanged;
        public event Action<long>? ScrollRequest;

        public long TopRow { get => _top; set { _top = Math.Max(0, value); InvalidateVisual(); } }

        public void Select(long off, bool scroll)
        {
            _selected = Math.Clamp(off, 0, Math.Max(0, Source.Length - 1));
            SelectionChanged?.Invoke(_selected);
            var row = _selected / PerRow;
            if (scroll && (row < _top || row >= _top + _viewRows)) ScrollRequest?.Invoke(row - _viewRows / 3);
            InvalidateVisual();
        }

        private double HexX(int col) => 12 + 10 * _charW + col * 3 * _charW + (col >= 8 ? _charW : 0);
        private double AsciiX(int col) => HexX(16) + 2 * _charW + col * _charW;

        protected override void OnRenderSizeChanged(SizeChangedInfo info)
        {
            base.OnRenderSizeChanged(info);
            _viewRows = (int)(ActualHeight / _lineH);
            ViewportRowsChanged?.Invoke(_viewRows);
        }

        protected override void OnMouseDown(MouseButtonEventArgs e)
        {
            Focus();
            var pt = e.GetPosition(this);
            var row = _top + (long)((pt.Y - 6) / _lineH);
            int col;
            if (pt.X >= AsciiX(0)) col = (int)((pt.X - AsciiX(0)) / _charW);
            else
            {
                col = -1;
                for (var c = 0; c < PerRow; c++) if (pt.X >= HexX(c) - _charW / 2 && pt.X < HexX(c) + 2.5 * _charW) { col = c; break; }
            }
            if (col is >= 0 and < PerRow) Select(row * PerRow + col, false);
        }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            var d = e.Key switch { Key.Left => -1, Key.Right => 1, Key.Up => -PerRow, Key.Down => PerRow, Key.PageUp => -PerRow * _viewRows, Key.PageDown => PerRow * _viewRows, _ => 0L };
            if (d != 0) { Select(_selected + d, true); e.Handled = true; }
        }

        protected override void OnRender(DrawingContext dc)
        {
            dc.DrawRectangle(Brushes.Transparent, null, new Rect(RenderSize));
            var rows = (int)(ActualHeight / _lineH) + 1;
            var data = Source.ReadBytes(_top * PerRow, rows * PerRow);
            var dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;
            dc.DrawRectangle(StripeBrush, null, new Rect(0, 0, 10 * _charW + 18, ActualHeight));
            for (var r = 0; r < rows; r++)
            {
                var rowOff = (_top + r) * PerRow;
                if (rowOff >= Source.Length) break;
                var y = 6 + r * _lineH;
                Draw(dc, rowOff.ToString("X8"), 8, y, OffBrush, dpi);
                for (var c = 0; c < PerRow; c++)
                {
                    var i = r * PerRow + c;
                    if (i >= data.Length) break;
                    var abs = rowOff + c;
                    if (abs == _selected)
                    {
                        dc.DrawRoundedRectangle(SelBrush, null, new Rect(HexX(c) - 2, y - 1, 2 * _charW + 4, _lineH - 2), 2, 2);
                        dc.DrawRoundedRectangle(SelBrush, null, new Rect(AsciiX(c) - 1, y - 1, _charW + 2, _lineH - 2), 2, 2);
                    }
                    var b = data[i];
                    Draw(dc, b.ToString("X2"), HexX(c), y, b == 0 ? ZeroBrush : HexBrush, dpi);
                    Draw(dc, b is >= 32 and < 127 ? ((char)b).ToString() : "·", AsciiX(c), y, b is >= 32 and < 127 ? AsciiBrush : ZeroBrush, dpi);
                }
            }
        }

        private void Draw(DrawingContext dc, string s, double x, double y, Brush b, double dpi) =>
            dc.DrawText(new FormattedText(s, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, _face, _fontSize, b, dpi), new Point(x, y));
    }
}
