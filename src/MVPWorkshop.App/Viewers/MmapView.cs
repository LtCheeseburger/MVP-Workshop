using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.Win32;
using MVPWorkshop.Core.Formats;
using MVPWorkshop.Core.IO;
using MVPWorkshop.Core.Vfs;

namespace MVPWorkshop.App.Viewers;

public sealed class MmapView : DockPanel
{
    private readonly AssetNode node;
    private MmapTexture texture;
    private readonly TiburonModel.Section? embedded;
    private readonly ComboBox choices = new() { MinWidth = 330, MaxWidth = 680 };
    private readonly Image image = new() { Stretch = Stretch.None, Margin = new Thickness(24) };
    private readonly TextBlock info = B.Text("", "Muted");
    private readonly CheckBox opaque = new() { Content = "Opaque preview", VerticalAlignment = VerticalAlignment.Center };
    internal int SelectedImage { get => choices.SelectedIndex; set => choices.SelectedIndex = Math.Clamp(value, 0, Math.Max(0, texture.Images.Count - 1)); }

    public MmapView(AssetNode node, TiburonModel.Section? embedded = null)
    {
        this.node = node; this.embedded = embedded; texture = ReadTexture();
        var toolbar = B.Toolbar(choices, B.Button("Export PNG", "", (_, _) => Export()), B.Button("Replace texture…", "", (_, _) => Import()), opaque);
        SetDock(toolbar, Dock.Top); Children.Add(toolbar);
        var footer = B.Toolbar(info); SetDock(footer, Dock.Bottom); Children.Add(footer);
        var canvas = new Border { Background = (Brush)Application.Current.Resources["Checker"], Child = image, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        RenderOptions.SetBitmapScalingMode(image, BitmapScalingMode.NearestNeighbor);
        Children.Add(new ScrollViewer { Content = canvas, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
        choices.SelectionChanged += (_, _) => Show(); opaque.Checked += (_, _) => Show(); opaque.Unchecked += (_, _) => Show();
        choices.ItemsSource = texture.Images.Select(i => i.Label).ToArray(); choices.SelectedIndex = 0;
        Loaded += (_, _) => Editor.Changed += Refresh;
        Unloaded += (_, _) => Editor.Changed -= Refresh;
    }

    private MmapTexture ReadTexture()
    {
        var bytes = node.ReadAll();
        return MmapTexture.Read(embedded is null ? bytes : bytes.AsSpan((int)embedded.Offset, (int)embedded.Length).ToArray());
    }

    private void Refresh()
    {
        try
        {
        int selected = choices.SelectedIndex;
        texture = ReadTexture(); choices.ItemsSource = texture.Images.Select(i => i.Label).ToArray();
        choices.IsEnabled = true; choices.SelectedIndex = Math.Clamp(selected, 0, Math.Max(0, texture.Images.Count - 1)); Show();
        }
        catch (Exception ex) { choices.IsEnabled = false; image.Source = null; info.Text = "Texture unavailable: " + ex.Message; Log.Warn($"{node.Key}: MMAP refresh: {ex.Message}"); }
    }

    private void Show()
    {
        try
        {
        if (texture.Images.ElementAtOrDefault(choices.SelectedIndex) is not { } selected) return;
        var decoded = texture.Decode(selected); var pixels = decoded.Bgra;
        if (opaque.IsChecked == true) { pixels = pixels.ToArray(); for (int i = 3; i < pixels.Length; i += 4) pixels[i] = 255; }
        image.Source = BitmapSource.Create(decoded.Width, decoded.Height, 96, 96, PixelFormats.Bgra32, null, pixels, decoded.Width * 4);
        info.Text = $"{selected.Name} · {selected.Width}×{selected.Height} · {MmapTexture.FormatName(selected.Format)} · mip {selected.Mip} · pixels @0x{selected.Offset:X}";
        var shared = texture.SharedPixelFiles(selected);
        if (shared.Count > 0) info.Text += $" · Replacing this texture also changes {shared.Count} shared texture names";
        info.ToolTip = shared.Count == 0 ? null : "Shared pixels: " + string.Join(", ", shared);
        }
        catch (Exception ex) { image.Source = null; info.Text = "Texture unavailable: " + ex.Message; Log.Warn($"{node.Key}: MMAP preview: {ex.Message}"); }
    }

    private void Export()
    {
        if (texture.Images.ElementAtOrDefault(choices.SelectedIndex) is not { } selected) return;
        var picker = new SaveFileDialog { Title = "Export texture PNG", Filter = "PNG image|*.png", FileName = $"texture-{selected.Index:D3}.png" };
        if (picker.ShowDialog(Window.GetWindow(this)) != true) return;
        try { var decoded = texture.Decode(selected); File.WriteAllBytes(picker.FileName, PngWriter.Encode(decoded.Width, decoded.Height, decoded.Bgra)); }
        catch (Exception ex) { MessageBox.Show(Window.GetWindow(this), ex.Message, "Export texture", MessageBoxButton.OK, MessageBoxImage.Warning); }
    }

    private void Import()
    {
        if (texture.Images.ElementAtOrDefault(choices.SelectedIndex) is not { } selected) return;
        var top = texture.Images.First(i => i.FileIndex == selected.FileIndex && i.Mip == 0);
        var picker = new OpenFileDialog { Title = $"Replace {selected.Name} ({top.Width}×{top.Height}); regenerate mipmaps", Filter = "Images|*.png;*.bmp;*.jpg;*.jpeg;*.tif;*.tiff" };
        if (picker.ShowDialog(Window.GetWindow(this)) != true) return;
        try
        {
            var decoder = BitmapDecoder.Create(new Uri(picker.FileName), BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
            var bitmap = new FormatConvertedBitmap(decoder.Frames[0], PixelFormats.Bgra32, null, 0);
            var pixels = new byte[checked(bitmap.PixelWidth * bitmap.PixelHeight * 4)]; bitmap.CopyPixels(pixels, bitmap.PixelWidth * 4, 0);
            var result = texture.ReplaceFile(selected.Index, pixels, bitmap.PixelWidth, bitmap.PixelHeight);
            if (!result.AsSpan().SequenceEqual(texture.Data))
            {
                if (embedded is not null)
                {
                    if (result.Length != embedded.Length) throw new InvalidDataException("Embedded model texture replacement must retain its section size.");
                    var model = node.ReadAll().ToArray(); result.CopyTo(model, (int)embedded.Offset); result = model;
                }
                Editor.Apply(node, result, $"Replace MMAP {selected.Name} and regenerate mipmaps");
            }
        }
        catch (Exception ex) { MessageBox.Show(Window.GetWindow(this), ex.Message, "Replace texture", MessageBoxButton.OK, MessageBoxImage.Warning); }
    }
}
