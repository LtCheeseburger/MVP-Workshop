using System.Windows;
using System.Windows.Controls;
using MVPWorkshop.Core.Formats;
using MVPWorkshop.Core.IO;
using MVPWorkshop.Core.Vfs;

namespace MVPWorkshop.App.Viewers;

/// <summary>Model previews with editable embedded textures, including track artwork.</summary>
public sealed class EmbeddedModelView : TabControl
{
    private readonly AssetNode node;
    private readonly TabItem model = new() { Header = "3D" };
    private readonly TabItem textures = new() { Header = "2D" };
    private TiburonModel.Section? embedded;
    internal void Show2D() => SelectedItem = textures;
    public EmbeddedModelView(AssetNode node)
    {
        Style = (Style)Application.Current.FindResource(typeof(TabControl));
        this.node = node; Items.Add(model); Items.Add(textures);
        SelectionChanged += (_, e) => { if (ReferenceEquals(e.Source, this) && SelectedItem == textures && textures.Content is null) ShowTextures(); };
        Refresh(); SelectedItem = model;
        Loaded += (_, _) => { Editor.Changed -= Refresh; Editor.Changed += Refresh; };
        Unloaded += (_, _) => Editor.Changed -= Refresh;
    }
    private FrameworkElement Error(Exception ex)
    {
        Log.Warn($"{node.Key}: model preview: {ex.Message}");
        return new TextBlock { Text = "Preview unavailable: " + ex.Message, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(16), Foreground = Ui.Res("Muted") };
    }
    private void ShowTextures()
    {
        try { textures.Content = embedded is null ? B.Text("This model has no embedded texture pack.", "Muted") : new MmapView(node, embedded); }
        catch (Exception ex) { textures.Content = Error(ex); }
    }
    private void Refresh()
    {
        int image = (textures.Content as MmapView)?.SelectedImage ?? 0;
        try
        {
            embedded = TiburonModel.Read(new MemoryByteSource(node.ReadAll())).FirstOrDefault(s => s.Tag == "Text" && s.Length > 0);
            var preview = new TiburonModelView(node);
            if (model.Content is TiburonModelView previous) preview.CopyCamera(previous);
            model.Content = preview;
        }
        catch (Exception ex) { model.Content = Error(ex); }
        bool wasVisible = textures.Content is not null || SelectedItem == textures;
        textures.Content = null;
        if (wasVisible) { ShowTextures(); if (textures.Content is MmapView view) view.SelectedImage = image; }
    }
}
