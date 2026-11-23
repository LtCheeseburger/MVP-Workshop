using System.Windows;
using System.Windows.Controls;
using System.IO;
using MVPWorkshop.Core.Vfs;
using MVPWorkshop.Core.Formats;
using MVPWorkshop.Core.IO;

namespace MVPWorkshop.App.Viewers;

/// <summary>Shared 3D/2D workflow for separate NASCAR character and car resource packs.</summary>
public sealed class NascarAssetView : DockPanel
{
    private readonly AssetNode asset;
    private readonly ComboBox models = new() { MinWidth = 240, MaxWidth = 550 };
    private readonly ComboBox skins = new() { MinWidth = 240, MaxWidth = 550 };
    private readonly TabItem modelTab = new() { Header = "3D" };
    private readonly TabItem textureTab = new() { Header = "2D" };
    private readonly TabControl tabs = new();
    private IReadOnlyList<NascarModelResources.Model> modelList;
    private List<AssetNode?> textureList = [];
    private bool updating;
    private ContentControl? textureContent;
    private AssetNode? shownTexture;
    internal void Show2D() => tabs.SelectedItem = textureTab;
    internal void SelectModel(string name)
    {
        int index = modelList.ToList().FindIndex(m => m.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
        if (index >= 0) { models.SelectedIndex = index; tabs.SelectedItem = modelTab; }
    }

    public NascarAssetView(AssetNode asset)
    {
        this.asset = asset;
        var catalog = NascarModelResources.Get(asset);
        modelList = asset.Format == MVPWorkshop.Core.Formats.FormatId.TiburonModel
            ? catalog.Models.Where(m => ReferenceEquals(m.Node, asset)).ToArray()
            : NascarModelResources.ModelsFor(asset);
        var controls = new WrapPanel { Margin = new Thickness(8), Orientation = Orientation.Horizontal };
        controls.Children.Add(B.Text("Model  ", "Muted")); controls.Children.Add(models);
        controls.Children.Add(B.Text("  Textures  ", "Muted")); controls.Children.Add(skins);
        SetDock(controls, Dock.Top); Children.Add(controls);
        tabs.Items.Add(modelTab); tabs.Items.Add(textureTab); Children.Add(tabs);
        models.ItemsSource = modelList.Select(m => m.Label).ToArray();
        models.SelectionChanged += (_, _) =>
        {
            if (!updating)
            {
                if (asset.Format == FormatId.MmapTexture && modelList.ElementAtOrDefault(models.SelectedIndex) is { } model) NascarModelResources.RememberModel(asset, model);
                try { ConfigureTextures(); }
                catch (Exception ex) { Log.Warn($"{asset.Key}: model selection: {ex.Message}"); modelTab.Content = Error(ex); }
                tabs.SelectedItem = modelTab;
            }
        };
        skins.SelectionChanged += (_, _) => { if (!updating) Rebuild(); };
        var preferred = NascarModelResources.PreferredModel(asset);
        bool unmappedPaint = asset.Format == FormatId.MmapTexture && catalog.Textures.Any(t => ReferenceEquals(t.Node, asset) && t.Names.Contains("DECAL")) && preferred is null;
        int preferredIndex = modelList.ToList().FindIndex(m => m.Name == preferred);
        models.SelectedIndex = modelList.Count > 0 && !unmappedPaint ? Math.Max(0, preferredIndex) : -1;
        if (unmappedPaint && modelList.Count > 0)
        {
            modelTab.Content = B.Text("This paint atlas has no vehicle link in NASCAR.DB. Choose its body from Model to preview it in 3D.", "Muted");
            textureTab.Content = new MmapView(asset); tabs.SelectedItem = textureTab; skins.IsEnabled = false;
        }
        if (modelList.Count == 0)
        {
            modelTab.Content = B.Text("No compatible model found in the car or character resources. The texture remains available in 2D.", "Muted");
            textureTab.Content = new MmapView(asset); tabs.SelectedItem = textureTab;
            models.IsEnabled = skins.IsEnabled = false;
        }
        Loaded += (_, _) => { Editor.Changed -= Refresh; Editor.Changed += Refresh; };
        Unloaded += (_, _) => Editor.Changed -= Refresh;
    }

    private void ConfigureTextures()
    {
        if (modelList.ElementAtOrDefault(models.SelectedIndex) is not { } model) return;
        updating = true;
        try
        {
            var packs = NascarModelResources.TexturesFor(model).ToList();
            var originalPack = NascarModelResources.Get(asset).Textures.FirstOrDefault(t => ReferenceEquals(t.Node, asset));
            if (originalPack is not null && !packs.Any(p => ReferenceEquals(p.Node, asset))) packs.Insert(0, originalPack);
            textureList = new List<AssetNode?> { null }; textureList.AddRange(packs.Select(t => t.Node));
            skins.ItemsSource = new[] { "Associated model textures" }.Concat(packs.Select(t => NascarModelResources.VehicleFor(t.Node) is { } vehicle ? $"{t.Node.Key} · {vehicle.Name}" : t.Label)).ToArray();
            int original = textureList.FindIndex(n => ReferenceEquals(n, asset));
            skins.SelectedIndex = Math.Max(0, original);
            skins.IsEnabled = true;
        }
        finally { updating = false; }
        Rebuild();
    }

    private void Refresh() => Rebuild();
    private void Rebuild()
    {
        try { RebuildContent(); }
        catch (Exception ex)
        {
            Log.Warn($"{asset.Key}: NASCAR preview refresh: {ex.Message}");
            modelTab.Content = Error(ex);
            // A broken 3D resource should still leave its original texture editable.
            if (asset.Format == FormatId.MmapTexture)
            {
                try { textureTab.Content = new MmapView(asset); }
                catch (Exception textureError) { textureTab.Content = Error(textureError); }
            }
            else textureTab.Content = Error(ex);
        }
    }
    private static TextBlock Error(Exception ex) => new() { Text = "Preview unavailable: " + ex.Message + "\nChoose another resource or use the Hex and Structure tabs to inspect this asset.", TextWrapping = TextWrapping.Wrap, Margin = new Thickness(16), Foreground = Ui.Res("Muted") };
    private void RebuildContent()
    {
        if (modelList.ElementAtOrDefault(models.SelectedIndex) is not { } model) return;
        int imageIndex = (textureContent?.Content as MmapView)?.SelectedImage ?? 0;
        var oldTexture = shownTexture;
        var selected = textureList.ElementAtOrDefault(skins.SelectedIndex);
        var preview = new TiburonModelView(model.Node, selected);
        if (modelTab.Content is TiburonModelView previous && ReferenceEquals(previous.ModelNode, model.Node)) preview.CopyCamera(previous);
        modelTab.Content = preview;
        var bindings = NascarModelResources.Bind(model, selected);
        var sources = bindings.Values.Where(b => b.Source is not null).Select(b => b.Source!).Distinct().ToList();
        if (selected is not null) { sources.Remove(selected); sources.Insert(0, selected); }
        var embedded = TiburonModel.Read(new MemoryByteSource(model.Node.ReadAll())).FirstOrDefault(s => s.Tag == "Text" && s.Length > 0);
        if (sources.Count == 0 && embedded is null) { textureTab.Content = B.Text("No texture pack is bound to this model.", "Muted"); return; }
        var panel = new DockPanel();
        var labels = sources.Select(n => n.Key).ToList();
        if (embedded is not null) labels.Add("Embedded model textures");
        var chooser = new ComboBox { Margin = new Thickness(8), ItemsSource = labels };
        var content = new ContentControl();
        textureContent = content;
        chooser.SelectionChanged += (_, _) =>
        {
            try
            {
            if (sources.ElementAtOrDefault(chooser.SelectedIndex) is { } source)
            {
                var view = new MmapView(source); if (ReferenceEquals(source, oldTexture)) view.SelectedImage = imageIndex;
                shownTexture = source; content.Content = view;
            }
            else if (embedded is not null && chooser.SelectedIndex == sources.Count)
            {
                var view = new MmapView(model.Node, embedded); if (ReferenceEquals(model.Node, oldTexture)) view.SelectedImage = imageIndex;
                shownTexture = model.Node; content.Content = view;
            }
            }
            catch (Exception ex) { Log.Warn($"{asset.Key}: texture selection: {ex.Message}"); content.Content = Error(ex); }
        };
        SetDock(chooser, Dock.Top); panel.Children.Add(chooser); panel.Children.Add(content);
        int remembered = oldTexture is null ? -1 : sources.IndexOf(oldTexture);
        chooser.SelectedIndex = remembered >= 0 ? remembered : 0; textureTab.Content = panel;
    }
}
