using System.Windows;
using System.Windows.Controls;
using MVPWorkshop.App.Controls;
using MVPWorkshop.App.Viewers;
using MVPWorkshop.Core.Formats;
using MVPWorkshop.Core.IO;
using MVPWorkshop.Core.Vfs;

namespace MVPWorkshop.App;

public partial class MainWindow
{
    private void AddExplorerActions(ContextMenu menu, AssetNode node)
    {
        if (node.IsFolder && !node.IsLooseTexturePack) return;
        void Add(string label, Action action) { var item = new MenuItem { Header = label }; item.Click += (_, _) => action(); menu.Items.Add(item); }
        Add("Open original / sources (read-only)", () => OpenSources(node));
        var formats = new MenuItem { Header = "Format import / export" };
        menu.Items.Add(formats);
        void Command(string label, Action action)
        {
            var item = new MenuItem { Header = label };
            item.Click += (_, _) => { try { action(); } catch (Exception ex) { MessageBox.Show(this, ex.Message, "Format operation unavailable"); } };
            formats.Items.Add(item);
        }
        void Viewer<T>(Action<T> action) where T : DependencyObject
        {
            OpenAsset(node);
            if (Docs.SelectedItem is DocumentTab { Content: { } content } tab && tab.Node == node && FindViewer<T>(content) is { } viewer) action(viewer);
            else MessageBox.Show(this, "This asset has no supported viewer for this operation. See Preview for the parser or missing-partner explanation.", "Format operation unavailable");
        }
        if (node.IsTexturePack || node.IsLooseTexturePack || node.Parent?.IsLooseTexturePack == true || node.Format is FormatId.SshTexture or FormatId.RawRgb or FormatId.XboxResource)
        {
            Command("Import image…", () => ReplaceImage(node));
            Command("Export selected image as PNG…", () => Viewer<TextureView>(v => v.ExportCurrent()));
            if (node.Format == FormatId.SshTexture || node.IsLooseTexturePack || node.Parent?.IsLooseTexturePack == true)
            {
                Command("Import PNG folder…", () => Viewer<TextureView>(v => v.ImportFolder()));
                Command("Export pack as PNGs…", () => TexturePackExport.ExportPack(node.Parent?.IsLooseTexturePack == true ? node.Parent : node));
            }
            else formats.Items.Add(new MenuItem { Header = "Folder interchange requires an SSH / XSH texture pack", IsEnabled = false });
        }
        else if (node.Format is FormatId.SchlContainer or FormatId.SchlStream or FormatId.EaBank or FormatId.AudioBankAbk or FormatId.SonyHd or FormatId.SonyBd)
        {
            Command("Import audio…", () => Viewer<AudioView>(async v => await v.ReplaceAudio()));
            Command("Import audio folder…", () => Viewer<AudioView>(async v => await v.ImportFolder()));
            Command("Export selected sound as WAV…", () => Viewer<AudioView>(v => v.ExportWav()));
            Command("Export all sounds as WAV…", () => Viewer<AudioView>(v => v.ExportAllSounds()));
        }
        else if (node.Format is FormatId.Elf or FormatId.ElfFragment)
        {
            Command("Import glTF positions…", () => Viewer<ModelView>(v => v.ImportGltf()));
            Command("Export glTF…", () => Viewer<ModelView>(v => v.ExportGltf()));
            Command("Export OBJ…", () => Viewer<ModelView>(v => v.ExportObj()));
        }
        else if (node.Format is FormatId.Csv or FormatId.Text or FormatId.LocText)
        {
            Command("Import text / CSV file…", () => ImportRaw(node));
            Command("Export text / CSV file…", () => Exporter.SaveRaw(node));
        }
        else formats.Items.Add(new MenuItem { Header = "No format-specific interchange; use raw import / export", IsEnabled = false });
    }

    private static T? FindViewer<T>(DependencyObject root) where T : DependencyObject
    {
        if (root is T match) return match;
        foreach (var child in LogicalTreeHelper.GetChildren(root).OfType<DependencyObject>())
            if (FindViewer<T>(child) is { } found) return found;
        return null;
    }

    // Snapshots deliberately use inspection controls only: no editable viewers or live overlay nodes.
    internal static List<(string Label, byte[] Bytes)> CaptureSources(AssetNode node)
    {
        var result = new List<(string Label, byte[] Bytes)>();
        var session = Editor.Session is { } active && node.Root == active.GameRoot ? active : null;
        var vanilla = session is null ? node : session.GameRoot.ResolveOriginal(node.Key);
        if (vanilla is not null) result.Add(("Vanilla / game source", vanilla.ReadOriginal().ToArray()));
        if (session?.BaseLayer?.GetBytes(node) is { } baseBytes)
            result.Add(("Selected base mods · " + session.BaseLayer.Origin(node), baseBytes.ToArray()));
        result.Add(("Current project / preview", node.ReadAll().ToArray()));
        return result;
    }

    internal void OpenSources(AssetNode node)
    {
        try
        {
            var layers = new TabControl();
            void Layer(string label, byte[] bytes)
            {
                var snapshot = bytes;
                var src = new MemoryByteSource(snapshot);
                var isolated = new AssetNode(node.Name, null, false, snapshot.Length, () => new MemoryByteSource(snapshot), label);
                var hex = new HexPanel(src, AssetStructure.Read(isolated.Format, src));
                var views = new TabControl();
                var hexTab = new TabItem { Header = "Hex", Content = hex };
                views.Items.Add(hexTab);
                var stringsTab = new TabItem { Header = "Strings" };
                var structureTab = new TabItem { Header = "Structure" };
                views.Items.Add(stringsTab);
                views.Items.Add(structureTab);
                views.SelectionChanged += (_, e) =>
                {
                    if (e.OriginalSource != views) return;
                    if (views.SelectedItem == stringsTab && stringsTab.Content is null) stringsTab.Content = new StringsView(src);
                    if (views.SelectedItem == structureTab && structureTab.Content is null) structureTab.Content = new StructureView(isolated, src, region => { views.SelectedItem = hexTab; hex.SelectRegion(region); });
                };
                layers.Items.Add(new TabItem { Header = label, Content = views });
            }
            foreach (var (label, bytes) in CaptureSources(node)) Layer(label, bytes);
            AddTab(new DocumentTab { Title = node.Name + " · sources (read-only)", ToolTip = node.Path,
                Content = B.Rows((new TextBlock { Text = "Read-only byte snapshots captured when opened. Added assets have no vanilla source. Current includes preview-only changes.", TextWrapping = TextWrapping.Wrap, Margin = new Thickness(12), Foreground = Ui.Res("Muted") }, B.Auto), (layers, B.Star())) });
        }
        catch (Exception ex) { MessageBox.Show(this, ex.Message, "Source inspection unavailable"); }
    }
}
