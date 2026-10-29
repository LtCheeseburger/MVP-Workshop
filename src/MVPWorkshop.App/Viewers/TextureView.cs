using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using MVPWorkshop.Core.Editing;
using MVPWorkshop.Core.Formats;
using MVPWorkshop.Core.IO;
using MVPWorkshop.Core.Vfs;

namespace MVPWorkshop.App.Viewers;

/// <summary>SSH texture pack viewer: image list with thumbnails, zoomable canvas, channel and swizzle controls, palette strip.</summary>
public sealed class TextureView : Grid
{
    public sealed class Entry : Observable
    {
        private ImageSource? _thumb;
        public required int Index { get; init; }
        public required string Title { get; init; }
        public required string Subtitle { get; init; }
        public SshImage? Image { get; init; }
        public DecodedImage? Raw { get; init; }
        /// <summary>A replacement shown in this viewer only (the image cannot be written back).</summary>
        public DecodedImage? PreviewImage { get; set; }
        public ImageSource? Thumb { get => _thumb; set => Set(ref _thumb, value); }
    }

    internal int SelectedImageIndex { get => _list.SelectedIndex; set => _list.SelectedIndex = Math.Clamp(value, -1, _entries.Count - 1); }
    internal Action<int>? ImageSelected { get; set; }
    private SshFile? _ssh;
    private readonly Dictionary<int, (DecodedImage Image, string Reason)> _previews = [];
    private readonly TextBlock _banner = new() { TextWrapping = TextWrapping.Wrap, FontSize = 12 };
    private readonly Border _bannerBar = new() { Padding = new Thickness(12, 6, 12, 6), BorderThickness = new Thickness(0, 0, 0, 1) };
    private PaletteMode _paletteMode = PaletteMode.Rebuild;
    private readonly AssetNode _node;
    private readonly Action<List<Prop>> _setExtra;
    private readonly ObservableCollection<Entry> _entries = [];
    private readonly ListBox _list = new();
    private readonly Image _image = new() { Stretch = Stretch.Fill, SnapsToDevicePixels = true };
    private readonly ScrollViewer _scroll = new() { HorizontalScrollBarVisibility = ScrollBarVisibility.Auto, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
    private readonly Border _canvas = new();
    private readonly TextBlock _info = B.Text("", "Muted");
    private readonly TextBlock _zoomText = B.Text("100%", "Text");
    private readonly WrapPanel _palette = new() { Margin = new Thickness(10, 6, 10, 6) };
    private readonly Border _paletteBar;
    private readonly DecodeOptions _opt = new();
    private DecodedImage? _current;
    private double _zoom = 1;
    private int _channels; // 0 RGBA, 1 RGB, 2 Alpha
    private bool _fit = true;

    private TextureView(AssetNode node, Action<List<Prop>> setExtra, SshFile? ssh)
    {
        _node = node; _setExtra = setExtra; _ssh = ssh;
        _paletteBar = new Border { Background = Ui.Res("PanelAlt"), BorderBrush = Ui.Res("Line"), BorderThickness = new Thickness(0, 1, 0, 0), Child = _palette, Visibility = Visibility.Collapsed };
        RenderOptions.SetBitmapScalingMode(_image, BitmapScalingMode.NearestNeighbor);
        _canvas.Child = _image;
        _canvas.HorizontalAlignment = HorizontalAlignment.Center;
        _canvas.VerticalAlignment = VerticalAlignment.Center;
        _canvas.Margin = new Thickness(24);
        _canvas.BorderBrush = Ui.Res("LineStrong");
        _canvas.BorderThickness = new Thickness(1);
        _canvas.Background = (Brush)Application.Current.Resources["Checker"];
        _scroll.Content = _canvas;
        _scroll.Background = Ui.Res("WindowBg");
        _scroll.PreviewMouseWheel += OnWheel;
        _scroll.SizeChanged += (_, _) => { if (_fit) ApplyZoom(); };

        _list.ItemsSource = _entries;
        _list.ItemTemplate = BuildItemTemplate();
        _list.SelectionChanged += (_, _) => { ShowSelected(); ImageSelected?.Invoke(_list.SelectedIndex); };
        _list.Background = Ui.Res("PanelBg");

        var alphaToggle = B.Toggle("PS2 alpha ×2", true, v => { _opt.ScaleAlpha = v; ShowSelected(); });
        var emulatorButton = B.Button("PCSX2 replacement…", null, (_, _) => ExportEmulatorReplacement(), "Outline", "Export an external image at its imported size using an existing PCSX2 base-level dump filename");
        var paletteSegments = B.Segments("Palette", ["Auto", "CSM", "Linear"], 0, i => { _opt.PaletteSwizzle = i switch { 1 => true, 2 => false, _ => null }; ShowSelected(); });
        var toolbar = B.Toolbar(
            B.Button("", "", (_, _) => SetZoom(_zoom / 1.25), tip: "Zoom out (Ctrl+wheel)"),
            _zoomText,
            B.Button("", "", (_, _) => SetZoom(_zoom * 1.25), tip: "Zoom in (Ctrl+wheel)"),
            B.Button("Fit", null, (_, _) => { _fit = true; ApplyZoom(); }),
            B.Button("1:1", null, (_, _) => SetZoom(1)),
            B.Separator(),
            B.Segments("", ["RGBA", "RGB", "Alpha"], 0, i => { _channels = i; Redraw(); }),
            B.Separator(),
            B.Segments("Swizzle", ["Auto", "On", "Off"], 0, i => { _opt.Swizzle = i switch { 1 => true, 2 => false, _ => null }; ShowSelected(); }),
            paletteSegments,
            alphaToggle,
            B.Separator(),
            B.Button("PNG", "", (_, _) => ExportCurrent(), "Outline", "Export the current image as PNG"),
            B.Button("All", "", (_, _) => ExportAll(), "Outline", "Export every image in this pack as PNG"),
            emulatorButton,
            B.Separator(),
            B.Button("Replace image…", "", (_, _) => ReplaceCurrent(), "Primary", "Pick a picture (or drop one onto this view) to replace the selected texture. Up to 4× upscale."),
            B.Segments("Colours", ["New palette", "Keep palette"], 0, i => _paletteMode = i == 1 ? PaletteMode.KeepExisting : PaletteMode.Rebuild));
        toolbar.ToolTip = null;
        if (ssh?.IsXbox == true) alphaToggle.Visibility = emulatorButton.Visibility = paletteSegments.Visibility = Visibility.Collapsed;   // PS2-only options (GS alpha, PCSX2 dumps, CSM palettes)
        _bannerBar.Child = _banner;

        var status = new Border { Child = _info, Padding = new Thickness(12, 5, 12, 5), Background = Ui.Res("PanelBg"), BorderBrush = Ui.Res("Line"), BorderThickness = new Thickness(0, 1, 0, 0) };
        var center = B.Rows((toolbar, B.Auto), (_bannerBar, B.Auto), (_scroll, B.Star()), (_paletteBar, B.Auto), (status, B.Auto));
        var listHost = B.Rows((new Border { Child = B.Text($"  IMAGES ({(ssh?.Images.Count ?? 1)})", "Muted", 11, FontWeights.SemiBold), Height = 30, Background = Ui.Res("PanelBg") }, B.Auto), (_list, B.Star()));
        Children.Add(B.Cols((listHost, B.Px(240)), (B.VSplit(), B.Px(4)), (center, B.Star())));
        AllowDrop = true; Drop += OnDropImage;
    }

    public TextureView(SshFile ssh, AssetNode node, Action<List<Prop>> setExtra) : this(node, setExtra, ssh)
    {
        FillEntries();
        if (_entries.Count > 0) _list.SelectedIndex = 0;
        _ = LoadThumbsAsync();
        Editor.Changed += OnEditorChanged;
        Unloaded += (_, _) => Editor.Changed -= OnEditorChanged;
        Loaded += (_, _) => { Editor.Changed -= OnEditorChanged; Editor.Changed += OnEditorChanged; OnEditorChanged(); };
    }

    private void FillEntries()
    {
        _entries.Clear();
        foreach (var img in _ssh!.Images)
            _entries.Add(new Entry
            {
                Index = img.Index, Image = img, Title = img.DisplayName,
                Subtitle = $"{img.Header.Width}×{img.Header.Height}  ·  {SshFile.TypeName(img.Header.Type).Split(' ')[0]}",
                PreviewImage = _previews.TryGetValue(img.Index, out var p) ? p.Image : null,
            });
    }

    /// <summary>Re-read the pack after an edit, undo, redo or project load changed it, keeping the selection.</summary>
    private void OnEditorChanged()
    {
        if (_ssh is null) return;
        try
        {
            var current = _node.ReadAll();
            if (!current.AsSpan().SequenceEqual(_ssh.Data))
            {
                var keep = _list.SelectedIndex;
                _ssh = SshFile.Read(current);
                FillEntries();
                _list.SelectedIndex = Math.Min(keep, _entries.Count - 1);
                _ = LoadThumbsAsync();
            }
        }
        catch (Exception ex) { Log.Error($"{_node.Name}: could not refresh after edit — {ex.Message}"); }
        UpdateBanner();
    }

    private void UpdateBanner()
    {
        var accent = Ui.Res("Accent"); var good = Ui.Res("Good"); var muted = Ui.Res("Muted");
        string text; System.Windows.Media.Brush fg;
        if (_list.SelectedItem is Entry { Image: { } img } e)
        {
            var s = Editor.Session;
            var unsupported = _ssh is null ? null : _node.Format != FormatId.SshTexture ? "this texture is a view-only conversion of an Xbox packed resource (XPR0)." : SshWriter.Unsupported(_ssh, img);
            var blocker = s?.SaveBlocker(_node);
            if (_previews.TryGetValue(img.Index, out var pv)) { text = "PREVIEW ONLY — this replacement is shown here but will not be saved or exported. " + pv.Reason; fg = accent; }
            else if (s?.IsModified(_node) == true) { text = $"EDITED — this texture file has saved changes; export writes them into {Path.GetFileName(ModExporter.DiskFileOf(_node).Name)} in the mod folder." + (unsupported is not null ? " (This image's format cannot be replaced: " + unsupported + ")" : ""); fg = good; }
            else if (unsupported is not null) { text = "This image can only be previewed: " + unsupported; fg = muted; }
            else if (blocker is not null) { text = "Replacements here are preview-only: " + blocker; fg = accent; }
            else { text = _ssh?.IsXbox == true ? "Editable at its native size (mip levels are regenerated, DXT is re-encoded lossily). Click Replace image… or drop a picture here; Export Mod saves it." : "Editable. Click Replace image… or drop a picture here (up to 4× upscale); Export Mod saves it."; fg = muted; }
        }
        else { text = ""; fg = muted; }
        _banner.Text = text; _banner.Foreground = fg;
        _bannerBar.Background = Ui.Res("PanelAlt"); _bannerBar.BorderBrush = Ui.Res("Line");
        _bannerBar.Visibility = text.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
    }

    public static TextureView ForRaw(AssetNode node, IByteSource src, Action<List<Prop>> setExtra)
    {
        var (w, h) = FormatDetector.RawRgbDims(src.Length) ?? (0, 0);
        var raw = src.ReadAll();
        var bgra = new byte[w * h * 4];
        for (var i = 0; i < w * h; i++) { bgra[i * 4] = raw[i * 3 + 2]; bgra[i * 4 + 1] = raw[i * 3 + 1]; bgra[i * 4 + 2] = raw[i * 3]; bgra[i * 4 + 3] = 255; }
        var v = new TextureView(node, setExtra, null);
        var d = new DecodedImage { Width = w, Height = h, Bgra = bgra, Notes = "raw RGB888" };
        v._entries.Add(new Entry { Index = 0, Title = node.Name, Subtitle = $"{w}×{h} RGB888", Raw = d, Thumb = ToBitmap(d, 0) });
        v._list.SelectedIndex = 0;
        return v;
    }

    private static DataTemplate BuildItemTemplate()
    {
        var root = new FrameworkElementFactory(typeof(Grid));
        var c0 = new FrameworkElementFactory(typeof(ColumnDefinition)); c0.SetValue(ColumnDefinition.WidthProperty, new GridLength(52));
        var c1 = new FrameworkElementFactory(typeof(ColumnDefinition));
        root.AppendChild(c0); root.AppendChild(c1);
        var thumbBg = new FrameworkElementFactory(typeof(Border));
        thumbBg.SetValue(Border.WidthProperty, 44.0); thumbBg.SetValue(Border.HeightProperty, 44.0);
        thumbBg.SetValue(Border.BackgroundProperty, Application.Current.Resources["Checker"]);
        thumbBg.SetValue(Border.CornerRadiusProperty, new CornerRadius(3));
        var img = new FrameworkElementFactory(typeof(Image));
        img.SetBinding(Image.SourceProperty, new System.Windows.Data.Binding(nameof(Entry.Thumb)));
        img.SetValue(Image.StretchProperty, Stretch.Uniform);
        thumbBg.AppendChild(img);
        root.AppendChild(thumbBg);
        var sp = new FrameworkElementFactory(typeof(StackPanel));
        sp.SetValue(Grid.ColumnProperty, 1);
        sp.SetValue(StackPanel.VerticalAlignmentProperty, VerticalAlignment.Center);
        var t1 = new FrameworkElementFactory(typeof(TextBlock));
        t1.SetBinding(TextBlock.TextProperty, new System.Windows.Data.Binding(nameof(Entry.Title)));
        t1.SetValue(TextBlock.TextTrimmingProperty, TextTrimming.CharacterEllipsis);
        var t2 = new FrameworkElementFactory(typeof(TextBlock));
        t2.SetBinding(TextBlock.TextProperty, new System.Windows.Data.Binding(nameof(Entry.Subtitle)));
        t2.SetValue(TextBlock.ForegroundProperty, Ui.Res("Faint"));
        t2.SetValue(TextBlock.FontSizeProperty, 10.5);
        sp.AppendChild(t1); sp.AppendChild(t2);
        root.AppendChild(sp);
        return new DataTemplate { VisualTree = root };
    }

    private async Task LoadThumbsAsync()
    {
        if (_ssh is null) return;
        foreach (var e in _entries.ToList())
        {
            var thumb = await Task.Run(() =>
            {
                try { return ToBitmap(Downscale(_ssh.Decode(e.Image!), 88), 0); }
                catch { return null; }
            });
            e.Thumb = thumb;
        }
    }

    private static DecodedImage Downscale(DecodedImage d, int max)
    {
        var s = Math.Max(1, (int)Math.Ceiling(Math.Max(d.Width, d.Height) / (double)max));
        if (s == 1) return d;
        int w = Math.Max(1, d.Width / s), h = Math.Max(1, d.Height / s);
        var o = new byte[w * h * 4];
        for (var y = 0; y < h; y++)
            for (var x = 0; x < w; x++)
                Buffer.BlockCopy(d.Bgra, ((y * s) * d.Width + x * s) * 4, o, (y * w + x) * 4, 4);
        return new DecodedImage { Width = w, Height = h, Bgra = o };
    }

    private void ShowSelected()
    {
        if (_list.SelectedItem is not Entry e) return;
        var props = new List<Prop>();
        try
        {
            _current = e.PreviewImage ?? e.Raw ?? _ssh!.Decode(e.Image!, _opt);
            Redraw();
            if (_fit) ApplyZoom();
            _info.Text = $"{e.Title}   {_current.Width}×{_current.Height}   {_current.Notes}";
            _info.Foreground = Ui.Res("Muted");
        }
        catch (Exception ex)
        {
            _current = null; _image.Source = null;
            _info.Text = $"Decode failed: {ex.Message}";
            _info.Foreground = Ui.Res("Bad");
        }

        if (e.Image is { } img)
        {
            var h = img.Header;
            props.Add(new("Image", "Index", $"{img.Index} of {_ssh!.Images.Count}"));
            props.Add(new("Image", "Tag", img.Tag));
            if (img.Name is not null) props.Add(new("Image", "Name", img.Name));
            if (img.Comment is not null) props.Add(new("Image", "Comment", img.Comment));
            props.Add(new("Image", "Type", $"0x{h.Type:X2} — {SshFile.TypeName(h.Type)}"));
            props.Add(new("Image", "Dimensions", $"{h.Width} × {h.Height}"));
            props.Add(new("Image", "Bits per pixel", SshFile.Bpp(h.Type).ToString()));
            props.Add(new("Image", "Mipmaps", h.Mipmaps.ToString()));
            props.Add(new("Image", "Center", $"{h.CenterX}, {h.CenterY}"));
            props.Add(new("Image", "Position", $"{h.PosX}, {h.PosY}"));
            props.Add(new("Image", "Flags", $"swizzled={h.FlagSwizzled} referenced={h.FlagReferenced} transposed={h.FlagTransposed}"));
            props.Add(new("Image", "Header offset", $"0x{h.Offset:X}"));
            props.Add(new("Image", "Block size", $"0x{h.BlockSize:X}"));
            props.Add(new("Image", "Pixel data", $"0x{img.DataOffset:X} (+{img.DataLength:N0} bytes)"));
            if (h.Type is >= 0x08 and <= 0x0F)
                props.Add(new("Image", "GST planes", $"base {Gst.BaseSize(h.Width, h.Height, h.Type):N0} B + detail {Gst.DetailSize(h.Width, h.Height, h.Type):N0} B"));
            foreach (var a in img.Attachments)
            {
                var ah = a.Header;
                var desc = ah.Type is 0x6F or 0x70 ? $"\"{Bin.CString(_ssh.Data, a.DataOffset, a.DataLength)}\"" : $"{ah.Width}×{ah.Height} swz={ah.FlagSwizzled}";
                props.Add(new("Attachments", $"{a.Kind}", $"@0x{ah.Offset:X} len {a.DataLength:N0} · {desc}"));
            }
        }
        _setExtra(props);
        ShowPalette();
        UpdateBanner();
    }

    // ---------------------------------------------------------------- replacing images

    private static (byte[] Bgra, int W, int H) LoadPicture(string path)
    {
        if (PictureFile.Handles(path)) return PictureFile.Decode(path);
        var decoder = BitmapDecoder.Create(new Uri(path), BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
        var frame = new FormatConvertedBitmap(decoder.Frames[0], PixelFormats.Bgra32, null, 0);
        if ((long)frame.PixelWidth * frame.PixelHeight > 16777216) throw new InvalidDataException("Import exceeds the 16-million-pixel limit.");
        var px = new byte[checked(frame.PixelWidth * frame.PixelHeight * 4)];
        frame.CopyPixels(px, frame.PixelWidth * 4, 0);
        return (px, frame.PixelWidth, frame.PixelHeight);
    }

    private void ExportEmulatorReplacement()
    {
        var owner = Window.GetWindow(this);
        var dump = new Microsoft.Win32.OpenFileDialog { Title = "Select matching PCSX2 base-level runtime dump (visually verify the match)", Filter = "PCSX2 PNG dump|*.png" };
        if (dump.ShowDialog(owner) != true) return;
        var name = Path.GetFileName(dump.FileName);
        if (!System.Text.RegularExpressions.Regex.IsMatch(name, @"\A[0-9a-fA-F]{1,16}-(?:[0-9a-fA-F]{1,16}-)?(?:r[1-9][0-9]*x[1-9][0-9]*-)?[0-9a-fA-F]{8}\.png\z"))
        { MessageBox.Show(owner, "Unsupported dump filename. Use a base-level PNG dumped by the target PCSX2 version. Mip-level and legacy region names are not supported here."); return; }
        var input = new Microsoft.Win32.OpenFileDialog { Title = "Choose externally upscaled replacement image", Filter = "Images|*.png;*.bmp;*.jpg;*.jpeg;*.tif;*.tiff" };
        if (input.ShowDialog(owner) != true) return;
        try
        {
            var (px, w, h) = LoadPicture(input.FileName);
            var (original, dw, dh) = LoadPicture(dump.FileName);
            if ((long)w * dh != (long)h * dw) throw new InvalidDataException("Replacement must preserve the runtime dump's aspect ratio.");
            if (MessageBox.Show(owner, $"Emulator-only PNG: {dw}×{dh} → {w}×{h}; BGRA {px.LongLength:N0} bytes. Alpha retained.\n\nThe selected runtime dump supplies the texture/CLUT hash and GS format bits. Confirm that it depicts the intended texture. Save this exact filename under the target emulator's textures/<serial>/replacements folder. No native asset or project edit is made.", "Review PCSX2 export", MessageBoxButton.OKCancel) != MessageBoxResult.OK) return;
            Exporter.SaveBytes(PngWriter.Encode(w, h, px), name, "PNG image|*.png");
        }
        catch (Exception ex) { MessageBox.Show(owner, ex.Message, "PCSX2 export failed"); }
    }

    /// <summary>Replace the selected image from a picture the user picks (the tree's Replace image… command).</summary>
    internal void ReplaceSelectedImage() => ReplaceCurrent();

    private async void ReplaceCurrent(string? path = null)
    {
        if (_list.SelectedItem is not Entry e || e.Image is not { } img || _ssh is null) { Log.Warn("Select a texture in the list first."); return; }
        if (_node.Format != FormatId.SshTexture) { Log.Warn($"{_node.Name} is an Xbox packed resource; it can be viewed and exported but not replaced."); return; }
        if (Editor.Session is not { } session) { Log.Warn("Open a game folder first."); return; }
        var h = img.Header;
        var owner = Window.GetWindow(this);
        if (path is null)
        {
            var dlg = new Microsoft.Win32.OpenFileDialog { Title = $"Replace “{img.DisplayName}” ({h.Width}×{h.Height})", Filter = "Images|*.png;*.tga;*.dds;*.bmp;*.jpg;*.jpeg;*.gif;*.tif;*.tiff|All files|*.*" };
            if (dlg.ShowDialog(owner) != true) return;
            path = dlg.FileName;
        }

        byte[] bgra; int w, ht;
        try { (bgra, w, ht) = LoadPicture(path); }
        catch (Exception ex) { Log.Error($"Could not read {path}: {ex.Message}"); MessageBox.Show(owner, ex.Message, "Replace image", MessageBoxButton.OK, MessageBoxImage.Error); return; }
        int tw, th;
        try
        {
            var review = new TextureImportReview(_ssh, img, bgra, w, ht, _paletteMode, session.SaveBlocker(_node)) { Owner = owner };
            if (review.ShowDialog() != true) return;
            bgra = review.ResultPixels; tw = review.TargetWidth; th = review.TargetHeight;
        }
        catch (Exception ex) { MessageBox.Show(owner, ex.Message, "Import unavailable"); return; }

        if (MainWindow.Instance is { } main && !await main.PreviewReplacementImpact(_node)) return;
        bool upscaled = tw != h.Width || th != h.Height;
        ReplaceWith(img.Index, bgra, Path.GetFileName(path), upscaled ? tw : h.Width, upscaled ? th : h.Height);
    }

    /// <summary>Dropping an image file anywhere on the viewer starts a replace of the selected texture.</summary>
    private void OnDropImage(object sender, DragEventArgs e)
    {
        if (e.Data.GetData(DataFormats.FileDrop) is string[] { Length: > 0 } files) ReplaceCurrent(files[0]);
    }

    /// <summary>Apply a BGRA picture (optional native upscale dimensions) to image <paramref name="imageIndex"/>: saved when the format and container allow it, preview-only otherwise.</summary>
    internal void ReplaceWith(int imageIndex, byte[] bgra, string source, int? width = null, int? height = null)
    {
        if (_ssh is null || Editor.Session is not { } session) return;
        var img = _ssh.Images[imageIndex];
        var h = img.Header;
        var owner = Window.GetWindow(this);
        var what = $"Replace texture {img.DisplayName} with {source}";
        var unsupported = SshWriter.Unsupported(_ssh, img);
        var blocker = session.SaveBlocker(_node);
        if (unsupported is null && blocker is null)
        {
            try
            {
                var result = SshWriter.ReplaceImage(_ssh, img, bgra, width ?? h.Width, height ?? h.Height, _paletteMode, allowResize: width.HasValue || height.HasValue);
                Editor.Apply(_node, result.File, what);
                Log.Info($"{_node.Name}: {what} — {result.Notes}");
            }
            catch (Exception ex) { Log.Error($"{what}: {ex.Message}"); MessageBox.Show(owner, ex.Message, "Replace image", MessageBoxButton.OK, MessageBoxImage.Warning); }
            return;
        }

        var reason = unsupported ?? blocker!;
        var shown = new DecodedImage { Width = h.Width, Height = h.Height, Bgra = bgra, Notes = "PREVIEW ONLY replacement" };
        var index = img.Index;
        session.CommitPreviewOnly(_node, what, reason, apply =>
        {
            if (apply) _previews[index] = (shown, reason); else _previews.Remove(index);
            if (index < _entries.Count)
            {
                _entries[index].PreviewImage = apply ? shown : null;
                _entries[index].Thumb = apply ? ToBitmap(Downscale(shown, 88), 0) : null;
                if (!apply) _ = LoadThumbsAsync();
                if (_list.SelectedIndex == index) ShowSelected();
            }
        });
        Log.Warn($"{_node.Name}: {what} is PREVIEW ONLY — {reason}");
    }

    private void ShowPalette()
    {
        _palette.Children.Clear();
        var pal = _current?.PaletteBgra;
        if (pal is null) { _paletteBar.Visibility = Visibility.Collapsed; return; }
        _paletteBar.Visibility = Visibility.Visible;
        var n = pal.Length / 4;
        var size = n <= 16 ? 18.0 : 11.0;
        for (var i = 0; i < n; i++)
        {
            var c = Color.FromArgb(pal[i * 4 + 3], pal[i * 4 + 2], pal[i * 4 + 1], pal[i * 4]);
            _palette.Children.Add(new Border
            {
                Width = size, Height = size, Margin = new Thickness(0, 0, 1, 1), Background = new SolidColorBrush(Color.FromRgb(c.R, c.G, c.B)),
                ToolTip = $"#{i}  R{c.R} G{c.G} B{c.B} A{c.A}", BorderBrush = c.A < 255 ? Ui.Res("Accent") : null, BorderThickness = new Thickness(c.A < 255 ? 1 : 0)
            });
        }
    }

    private void Redraw()
    {
        if (_current is null) return;
        _image.Source = ToBitmap(_current, _channels);
        _image.Width = _current.Width * _zoom;
        _image.Height = _current.Height * _zoom;
    }

    private static BitmapSource ToBitmap(DecodedImage d, int channels)
    {
        var px = d.Bgra;
        if (channels != 0)
        {
            px = (byte[])px.Clone();
            for (var i = 0; i < px.Length; i += 4)
            {
                if (channels == 1) px[i + 3] = 255;
                else { var a = px[i + 3]; px[i] = px[i + 1] = px[i + 2] = a; px[i + 3] = 255; }
            }
        }
        var bmp = BitmapSource.Create(d.Width, d.Height, 96, 96, PixelFormats.Bgra32, null, px, d.Width * 4);
        bmp.Freeze();
        return bmp;
    }

    private void OnWheel(object sender, MouseWheelEventArgs e)
    {
        if ((Keyboard.Modifiers & ModifierKeys.Control) == 0) return;
        SetZoom(e.Delta > 0 ? _zoom * 1.2 : _zoom / 1.2);
        e.Handled = true;
    }

    private void SetZoom(double z)
    {
        _fit = false;
        _zoom = Math.Clamp(z, 0.05, 32);
        Redraw();
        _zoomText.Text = $"{_zoom * 100:0}%";
    }

    private void ApplyZoom()
    {
        if (_current is null || _scroll.ActualWidth <= 0) return;
        var z = Math.Min((_scroll.ActualWidth - 60) / _current.Width, (_scroll.ActualHeight - 60) / _current.Height);
        _zoom = Math.Clamp(z >= 1 ? Math.Floor(z) : z, 0.05, 16);
        Redraw();
        _zoomText.Text = $"{_zoom * 100:0}%";
    }

    private void ExportCurrent()
    {
        if (_current is null || _list.SelectedItem is not Entry e) return;
        Exporter.SaveBytes(PngWriter.Encode(_current.Width, _current.Height, _current.Bgra), $"{Path.GetFileNameWithoutExtension(_node.Name)}_{Safe(e.Title)}.png", "PNG image|*.png");
    }

    private void ExportAll()
    {
        if (_ssh is null) { ExportCurrent(); return; }
        var dlg = new Microsoft.Win32.OpenFolderDialog { Title = "Export all images to…" };
        if (dlg.ShowDialog(Window.GetWindow(this)) != true) return;
        var ok = 0;
        foreach (var img in _ssh.Images)
        {
            try
            {
                var d = _ssh.Decode(img, _opt);
                File.WriteAllBytes(Path.Combine(dlg.FolderName, $"{Path.GetFileNameWithoutExtension(_node.Name)}_{img.Index:D3}_{Safe(img.DisplayName)}.png"), PngWriter.Encode(d.Width, d.Height, d.Bgra));
                ok++;
            }
            catch (Exception ex) { Log.Warn($"{img.Tag}: {ex.Message}"); }
        }
        Log.Info($"Exported {ok} PNGs from {_node.Name} → {dlg.FolderName}");
    }

    private static string Safe(string s) => string.Concat(s.Select(c => Path.GetInvalidFileNameChars().Contains(c) || c is ' ' or '·' ? '_' : c)).Trim('_');
}
