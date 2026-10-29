using System.IO;
using System.Windows;
using System.Windows.Controls;
using MVPWorkshop.App.Controls;
using MVPWorkshop.Core.Formats;
using MVPWorkshop.Core.IO;
using MVPWorkshop.Core.Vfs;

namespace MVPWorkshop.App.Viewers;

/// <summary>Builds a document tab for an asset: header strip + Preview / Hex / Strings / Structure views.</summary>
public static class AssetDocument
{
    public static List<Prop> GeneralProps(AssetNode n, bool open)
    {
        var p = new List<Prop>
        {
            new("Asset", "Name", n.Name),
            new("Asset", "Path", n.Path),
        };
        if (!n.IsFolder)
        {
            p.Add(new("Asset", "Format", n.FormatInfo.Name));
            p.Add(new("Asset", "Description", n.FormatInfo.Description));
            p.Add(new("Asset", "Stored size", $"{Bin.Size(n.StoredSize)} ({n.StoredSize:N0} bytes)"));
            if (open && n.UnpackedSize is { } u && n.IsCompressed) p.Add(new("Asset", "Unpacked size", $"{Bin.Size(u)} ({u:N0} bytes)"));
            if (open) p.Add(new("Asset", "Compression", n.IsCompressed ? "RefPack (10 FB)" : "None"));
            p.Add(new("Asset", "Location", n.Location));
            if (n.Container.Length > 0) p.Add(new("Asset", "Container", n.Container));
            if (n.DiskPath is not null) p.Add(new("Asset", "Disk path", n.DiskPath));
            if (n.LoadError is not null) p.Add(new("Errors", "Load", n.LoadError));
        }
        else if (n.DiskPath is not null) p.Add(new("Asset", "Disk path", n.DiskPath));
        return p;
    }

    public static DocumentTab Create(AssetNode node)
    {
        var src = node.Open();
        var fmt = node.Format;
        var baseProps = GeneralProps(node, true);
        baseProps.AddRange(FormatInspector.Describe(fmt, src));

        DocumentTab? tab = null;
        var extra = new List<Prop>();
        void SetExtra(List<Prop> props)
        {
            extra = props;
            if (tab is null) return;
            tab.Props = [.. baseProps, .. extra];
            MainWindow.Instance?.RefreshPropsFor(tab);
        }

        var tabs = new TabControl { Tag = Ui.Res("PanelBg") };
        FrameworkElement preview;
        try { preview = ViewerFactory.Build(node, src, SetExtra); }
        catch (Exception ex)
        {
            preview = InfoCard.Error(node, ex);
            Log.Error($"{node.Path}: viewer failed — {ex.Message}");
        }
        tabs.Items.Add(new TabItem { Header = Header("", "Preview"), Content = preview });

        var hexItem = new TabItem { Header = Header("", "Hex") };
        var strItem = new TabItem { Header = Header("", "Strings") };
        tabs.Items.Add(hexItem);
        tabs.Items.Add(strItem);
        var chunks = FormatInspector.WalkChunks(src);
        TabItem? chunkItem = null;
        if (chunks.Count > 1)
        {
            chunkItem = new TabItem { Header = Header("", $"Chunks ({chunks.Count})") };
            tabs.Items.Add(chunkItem);
        }
        tabs.SelectionChanged += (_, e) =>
        {
            if (e.OriginalSource != tabs) return;
            if (tabs.SelectedItem == hexItem && hexItem.Content is null) hexItem.Content = new HexPanel(node.Open());
            if (tabs.SelectedItem == strItem && strItem.Content is null) strItem.Content = new StringsView(node.Open());
            if (chunkItem is not null && tabs.SelectedItem == chunkItem && chunkItem.Content is null) chunkItem.Content = SimpleViews.Chunks(chunks);
        };
        tabs.SelectedIndex = 0;

        var header = BuildHeader(node);
        var root = B.Rows((header, B.Auto), (tabs, B.Star()));
        root.Background = Ui.Res("WindowBg");

        tab = new DocumentTab
        {
            Title = node.Name, ToolTip = node.Path, Glyph = node.FormatInfo.Glyph, Node = node, Content = root,
            Props = [.. baseProps, .. extra],
        };
        return tab;
    }

    private static StackPanel Header(string glyph, string text)
    {
        var sp = new StackPanel { Orientation = Orientation.Horizontal };
        sp.Children.Add(B.Glyph(glyph, "Muted", 12));
        sp.Children.Add(new TextBlock { Text = text, Margin = new Thickness(6, 0, 0, 0) });
        return sp;
    }

    private static Border BuildHeader(AssetNode n)
    {
        var info = n.FormatInfo;
        var badge = new Border
        {
            Background = Ui.Res("Raised"), CornerRadius = new CornerRadius(3), Padding = new Thickness(7, 2, 7, 2), Margin = new Thickness(10, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center,
            Child = new TextBlock { Text = info.Name.ToUpperInvariant(), FontSize = 10, FontWeight = FontWeights.Bold, Foreground = Ui.Res(TreeItem.CategoryBrush(info.Category)) }
        };
        var left = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        left.Children.Add(B.Glyph(info.Glyph, TreeItem.CategoryBrush(info.Category), 20));
        var names = new StackPanel { Margin = new Thickness(12, 0, 0, 0) };
        var title = new StackPanel { Orientation = Orientation.Horizontal };
        title.Children.Add(B.Text(n.Name, "Text", 15, FontWeights.SemiBold));
        title.Children.Add(badge);
        if (n.IsCompressed)
            title.Children.Add(new Border
            {
                Background = Ui.Res("AccentSoft"), CornerRadius = new CornerRadius(3), Padding = new Thickness(7, 2, 7, 2), Margin = new Thickness(6, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center,
                Child = new TextBlock { Text = "REFPACK", FontSize = 10, FontWeight = FontWeights.Bold, Foreground = Ui.Res("Accent") }
            });
        var cap = Editor.CapabilityOf(n);
        if (cap.Level != EditLevel.None)
            title.Children.Add(new Border
            {
                Background = Ui.Res(cap.Level == EditLevel.Savable ? "Raised" : "AccentSoft"), CornerRadius = new CornerRadius(3), Padding = new Thickness(7, 2, 7, 2), Margin = new Thickness(6, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center,
                ToolTip = cap.Reason,
                Child = new TextBlock { Text = cap.Level == EditLevel.Savable ? "EDITABLE" : "PREVIEW ONLY", FontSize = 10, FontWeight = FontWeights.Bold, Foreground = Ui.Res(cap.Level == EditLevel.Savable ? "Good" : "Accent") }
            });
        names.Children.Add(title);
        names.Children.Add(B.Text(n.Path, "Faint", 11));
        left.Children.Add(names);

        var right = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center };
        right.Children.Add(B.Text($"{Bin.Size(n.UnpackedSize ?? n.StoredSize)}", "Muted"));
        right.Children.Add(B.Separator());
        right.Children.Add(B.Button("Export Raw", "", (_, _) => Exporter.SaveRaw(n), "Outline", "Save the (decompressed) bytes"));
        if (n.IsContainer) right.Children.Add(B.Button("Extract All", "", (_, _) => Exporter.ExtractTree(n), "Outline"));

        var g = B.Cols((left, B.Star()), (right, B.Auto));
        return new Border { Child = g, Padding = new Thickness(18, 10, 14, 10), Background = Ui.Res("PanelBg"), BorderBrush = Ui.Res("Line"), BorderThickness = new Thickness(0, 0, 0, 1) };
    }
}

public static class ViewerFactory
{
    public static FrameworkElement Build(AssetNode node, IByteSource src, Action<List<Prop>> setExtra) => node.Parent is { IsLooseTexturePack: true } pack
        ? LooseImage(node, pack, setExtra)
        : node.Format switch
    {
        FormatId.BigArchive or FormatId.SchlContainer => new ArchiveView(node),
        FormatId.SshTexture => new TextureView(SshFile.Read(src.ReadAll()), node, setExtra),
        FormatId.RawRgb => TextureView.ForRaw(node, src, setExtra),
        FormatId.SchlStream => new AudioView(AudioView.FromStreams(src), node, setExtra),
        FormatId.EaBank => new AudioView(AudioView.FromBank(src.ReadAll()), node, setExtra),
        FormatId.Elf or FormatId.ElfFragment => ElfJoined(node, setExtra),
        FormatId.Csv => CsvEditHost(node, src),
        FormatId.Text when node.Extension == ".fel" => new FelEditHost(node, setExtra),
        FormatId.Text or FormatId.LocText => new TextEditView(node),
        FormatId.TrueType => SimpleViews.Font(src.ReadAll(), node.Name),
        FormatId.LocBinary => new LocEditView(node),
        FormatId.EaFontSfn => AemsViews.Font(node, src),
        FormatId.AudioBankAbk => AemsViews.Abk(node, src, setExtra),
        FormatId.MoirCsi => AemsViews.Csi(src),
        FormatId.SpeechHeader => AemsViews.SpeechHeader(node, src, setExtra),
        FormatId.AptMovie or FormatId.AptConst => AptView.Create(node, setExtra),
        FormatId.MpcMovie or FormatId.Vp6Movie => new MovieView(node, setExtra),
        FormatId.XboxResource => new TextureView(SshFile.Read(XprTexture.ToShpx(src.ReadAll())), node, setExtra),
        FormatId.OffsetPack => new ArchiveView(node),
        _ => InfoCard.For(node),
    };

    /// <summary>CSV tables keep their grid view, with the raw text editable on a second tab.</summary>
    private static FrameworkElement CsvEditHost(AssetNode node, IByteSource src)
    {
        var tabs = new TabControl();
        var table = new TabItem { Header = "Table", Content = SimpleViews.Table(src) };
        tabs.Items.Add(table);
        tabs.Items.Add(new TabItem { Header = "Text (editable)", Content = new TextEditView(node) });
        var seen = node.ReadAll();
        void Changed()
        {
            var now = node.ReadAll();
            if (now.AsSpan().SequenceEqual(seen)) return;
            seen = now;
            table.Content = SimpleViews.Table(node.Open());
        }
        tabs.Loaded += (_, _) => { Editor.Changed -= Changed; Editor.Changed += Changed; Changed(); };
        tabs.Unloaded += (_, _) => Editor.Changed -= Changed;
        return tabs;
    }

    /// <summary>One DDS (or raw) file of a loose texture folder: its whole .xsh, with this image selected, so edits go through the pack.</summary>
    private static FrameworkElement LooseImage(AssetNode image, AssetNode pack, Action<List<Prop>> setExtra)
    {
        var view = new TextureView(SshFile.Read(pack.ReadAll()), pack, setExtra);
        var meta = File.ReadAllBytes(Path.Combine(pack.DiskPath!, XshFolder.MetaName));
        // the viewer lists images in file order, the order xsh.meta numbers the files in (not the directory order)
        if (XshFolder.ReadMeta(meta, out _, out var images) && images.FindIndex(i => i.File.Equals(image.Name, StringComparison.OrdinalIgnoreCase)) is >= 0 and var at)
            view.SelectedImageIndex = at;
        return view;
    }

    private static FrameworkElement ElfJoined(AssetNode node, Action<List<Prop>> setExtra)
    {
        try { if (node.Name.Equals("mstadium.ord", StringComparison.OrdinalIgnoreCase) && node.Path.Contains("/STADIUM/", StringComparison.OrdinalIgnoreCase)) return new StadiumView(node.Parent!); if (ModelView.TryCreate(node, setExtra, out var mv)) return mv!; }
        catch (Exception ex) { Log.Warn($"{node.Name}: model decode failed, showing ELF tables — {ex.Message}"); }
        var (data, partner) = node.ReadElfJoined();
        if (partner is null && node.Format == FormatId.ElfFragment)
        {
            setExtra([new("Split object", "Partner", "not in this folder (e.g. initpurge .orl ↔ initstay .ord live in different archives)")]);
            return SimpleViews.Text(string.Join(Environment.NewLine, FormatInspector.ExtractStrings(data, 2).Select(s => $"0x{s.Offset:X6}  {s.Text}")));
        }
        if (partner is not null)
            setExtra([
                new("Split object", "Partner", partner.Name),
                new("Split object", "Joined size", $"{data.Length:N0} bytes"),
                new("Split object", "Note", ".ord = ELF header + section data; .orl = section names, headers, relocations and symbols. Shown joined."),
            ]);
        return new ElfView(ElfFile.Read(data));
    }
}

