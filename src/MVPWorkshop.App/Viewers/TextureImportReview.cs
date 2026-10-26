using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using MVPWorkshop.Core.Formats;

namespace MVPWorkshop.App.Viewers;

/// <summary>Pick a size (1×–4× of the original), check the encoded result, apply.</summary>
internal sealed class TextureImportReview : Window
{
    private readonly SshFile _file;
    private readonly SshImage _image;
    private readonly byte[] _pixels;
    private readonly int _width, _height;
    private readonly PaletteMode _palette;
    private readonly List<(int Scale, RadioButton Button)> _sizes = new();
    private readonly ComboBox _sampling = new() { ItemsSource = new[] { "Smooth (best for photos / HD art)", "Bilinear", "Sharp (pixel art)" }, SelectedIndex = 0, Margin = new Thickness(0, 4, 0, 10) };
    private readonly Image _encoded = new() { Height = 150, Stretch = Stretch.Uniform };
    private readonly TextBlock _quality = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 8) };
    private readonly Button _apply;
    private int _previewRevision;

    internal int Scale => _sizes.FirstOrDefault(s => s.Button.IsChecked == true).Scale is var s && s > 0 ? s : 1;
    internal int TargetWidth => _image.Header.Width * Scale;
    internal int TargetHeight => _image.Header.Height * Scale;
    internal bool KeepImportedSize => Scale > 1;
    internal ResizeFilter Filter => (ResizeFilter)_sampling.SelectedIndex;
    internal bool PreviewReady { get; private set; }
    internal string PreviewStatus => _quality.Text;
    /// <summary>The imported picture resampled to <see cref="TargetWidth"/>×<see cref="TargetHeight"/>.</summary>
    internal byte[] ResultPixels => Resampled(Scale, Filter);

    internal TextureImportReview(SshFile file, SshImage image, byte[] pixels, int width, int height, PaletteMode palette, string? saveBlocker)
    {
        (_file, _image, _pixels, _width, _height, _palette) = (file, image, pixels, width, height, palette);
        Background = Ui.Res("WindowBg"); Foreground = Ui.Res("Text");
        Title = "Replace texture"; Width = 640; MaxHeight = 850; SizeToContent = SizeToContent.Height; WindowStartupLocation = WindowStartupLocation.CenterOwner;
        var h = image.Header;
        var panel = new StackPanel { Margin = new Thickness(18) };
        panel.Children.Add(new TextBlock { Text = $"Your picture is {width}×{height}. The original texture is {h.Width}×{h.Height}.", TextWrapping = TextWrapping.Wrap });
        panel.Children.Add(new TextBlock { Text = "Choose the size to save at:", Margin = new Thickness(0, 12, 0, 4), FontWeight = FontWeights.SemiBold });

        int best = 1;
        for (int k = 1; k <= SshWriter.MaxUpscale; k++)
        {
            string? why = k == 1 ? null : saveBlocker ?? SshWriter.ResizeUnsupported(file, image, h.Width * k, h.Height * k);
            var rb = new RadioButton { Content = k == 1 ? $"Original size ({h.Width}×{h.Height}) — safest" : $"{k}× upscale ({h.Width * k}×{h.Height * k})", IsEnabled = why is null, ToolTip = why, Margin = new Thickness(0, 2, 0, 2) };
            _sizes.Add((k, rb)); panel.Children.Add(rb);
            if (why is null && (k == 1 || (h.Width * k <= width && h.Height * k <= height))) best = k;
        }
        _sizes.First(s => s.Scale == best).Button.IsChecked = true;
        panel.Children.Add(new TextBlock { Text = "Upscales are capped at 4×. Larger sizes are experimental: the game's UVs and video memory aren't adjusted.", Foreground = Ui.Res("Muted"), FontSize = 11, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 0) });

        panel.Children.Add(new TextBlock { Text = "Resize method (used when your picture isn't exactly the chosen size):", Margin = new Thickness(0, 12, 0, 0) });
        panel.Children.Add(_sampling);

        var imported = new Image { Source = Bitmap(new DecodedImage { Width = width, Height = height, Bgra = pixels }), Height = 150, Stretch = Stretch.Uniform };
        panel.Children.Add(B.Cols((B.Rows((B.Text("Your picture"), B.Auto), (imported, B.Star())), B.Star()), (B.Rows((B.Text("Result in game format"), B.Auto), (_encoded, B.Star())), B.Star())));
        panel.Children.Add(_quality);

        var details = new Expander { Header = "Technical details", Content = new TextBlock { Text = TextureImport.Describe(file, image, width, height), TextWrapping = TextWrapping.Wrap, Foreground = Ui.Res("Muted"), FontSize = 11 } };
        panel.Children.Add(details);
        _apply = B.Button("Apply", null, (_, _) => DialogResult = true, "Primary");
        _apply.Margin = new Thickness(0, 12, 0, 0);
        panel.Children.Add(_apply);
        Content = new ScrollViewer { Content = panel, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        foreach (var (_, rb) in _sizes) rb.Checked += async (_, _) => await PreviewAsync();
        _sampling.SelectionChanged += async (_, _) => await PreviewAsync();
        Loaded += async (_, _) => await PreviewAsync();
    }

    private byte[] Resampled(int scale, ResizeFilter filter)
    {
        int tw = _image.Header.Width * scale, th = _image.Header.Height * scale;
        return tw == _width && th == _height ? _pixels : ImageOps.Resize(_pixels, _width, _height, tw, th, filter);
    }

    internal async Task PreviewAsync()
    {
        int revision = ++_previewRevision; PreviewReady = false; _apply.IsEnabled = false; _encoded.Source = null;
        try
        {
            int scale = Scale; var filter = Filter; _quality.Text = "Encoding…";
            var result = await Task.Run(() =>
                SshWriter.ReplaceImage(_file, _image, Resampled(scale, filter), _image.Header.Width * scale, _image.Header.Height * scale, _palette, allowResize: scale > 1));
            if (revision != _previewRevision) return;
            var file = SshFile.Read(result.File); var decoded = file.Decode(file.Images[_image.Index]);
            _encoded.Source = Bitmap(decoded); _quality.Text = $"{decoded.Width}×{decoded.Height} · {result.Notes}"; PreviewReady = true; _apply.IsEnabled = true;
        }
        catch (Exception ex) { if (revision == _previewRevision) _quality.Text = ex.Message; }
    }
    private static BitmapSource Bitmap(DecodedImage image)
    {
        var result = BitmapSource.Create(image.Width, image.Height, 96, 96, PixelFormats.Bgra32, null, image.Bgra, image.Width * 4); result.Freeze(); return result;
    }
}
