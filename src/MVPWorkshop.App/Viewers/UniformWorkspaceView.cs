using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using MVPWorkshop.Core.Formats;
using MVPWorkshop.Core.Vfs;
using MVPWorkshop.Core.Editing;

namespace MVPWorkshop.App.Viewers;

public sealed class UniformWorkspaceState
{
    public Dictionary<string, DecodedImage> Previews { get; } = [];
    public char? Variant { get; set; }
    public bool CompareOriginal { get; set; }
    public string? SelectedTextureTag { get; set; }
    public bool SelectedTextureIsFont { get; set; }
    public bool PlayerOptionsExpanded { get; set; }
    public bool DirtOptionsExpanded { get; set; }
    public uint? Player { get; set; }
    public string? Name { get; set; }
    public string? Number { get; set; }
    public ModelView.UniformCamera? OriginalCamera { get; set; }
    public ModelView.UniformCamera? EditedCamera { get; set; }
    public HashSet<string> DirtRegions { get; } = [];
    public Dictionary<string, char> Roles { get; } = [];
    public string? Role { get; set; }
}

public sealed class UniformWorkspaceView : Grid
{
    private readonly ComboBox _variantPicker;
    public void SelectVariant(char suffix) => _variantPicker.SelectedItem = _variantPicker.Items.OfType<UniformVariant>().FirstOrDefault(v => v.Suffix == suffix);
    public UniformWorkspaceView(TeamProfile profile, IReadOnlyList<AssetNode> nodes, Dictionary<string,string> bindings, UniformWorkspaceState state)
    {
        var previews = state.Previews;
        var initialName = state.Name; var initialNumber = state.Number;
        var initialPlayer = state.Player; var initialVariant = state.Variant; var initialRole = state.Role;
        var body = nodes.First(n => n.Name == "mebody.ord");
        var head = nodes.First(n => n.Name == "hihead.ord");
        var original = ModelView.CreateUniform(body, head);
        var edited = ModelView.CreateUniform(body, head);
        if(state.OriginalCamera is { } originalCamera)original.RestoreUniformCamera(originalCamera);
        if(state.EditedCamera is { } editedCamera)edited.RestoreUniformCamera(editedCamera);
        original.UniformCameraChanged += camera => state.OriginalCamera=camera;
        edited.UniformCameraChanged += camera => state.EditedCamera=camera;
        edited.UniformCameraChanged += camera => { if (state.CompareOriginal) original.RestoreUniformCamera(camera); };
        var variants = UniformPreview.Variants(profile);
        if (variants.Count == 0) throw new InvalidOperationException("No uniform variants.");
        var pick = new ComboBox { Tag="UniformVariant", ItemsSource = variants, MinWidth = 200, Margin = new Thickness(4) };
        _variantPicker = pick;
        var defaults = UniformPreview.PreviewRoles(variants);
        foreach (var (roleName, defaultSuffix) in defaults) state.Roles.TryAdd(roleName, defaultSuffix);
        var role = new ComboBox { Tag = "UniformRole", ItemsSource = new[] { "Home", "Away", "Alternate" }, MinWidth = 100, Margin = new Thickness(4), ToolTip = "Preview roles map to disc suffixes. Use Map variant to role to change the mapping; this does not edit game assignments." };
        bool selectingRole = false;
        var colourSwatches = new WrapPanel { Tag = "UniformColours", Margin = new Thickness(4) };
        var player = new ComboBox { Tag="UniformPlayer", ItemsSource = profile.Players, DisplayMemberPath = "Name", MinWidth = 180, Margin = new Thickness(4) };
        var name = new TextBox { Tag="UniformName", Width = 150, Margin = new Thickness(4), ToolTip = "Preview surname; native renderer uses the first ten characters" };
        var number = new TextBox { Tag="UniformNumber", Width = 45, Margin = new Thickness(4), Text = "0", ToolTip = "Preview jersey number 0–99" };
        var sources = new StackPanel();
        var status = new TextBlock { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(6), Foreground = Ui.Res("Muted") };
        var glyphs = new StackPanel();
        string baselineName = "", baselineNumber = "0";
        AssetNode? playerSource = null; uint playerId = 0; byte[]? playerBaseline = null;
        var tags = new ComboBox { MinWidth = 230, Margin = new Thickness(4), ToolTip = "Choose the uniform texture to replace" };
        List<(AssetNode Pack, SshImage Image)> textures = [];
        bool loading = false, initializing = true;
        void Refresh()
        {
            if (initializing || loading || pick.SelectedItem is not UniformVariant variant) return;
            try
            {
                var normalized = UniformLettering.NumberText(number.Text);
                if (state.CompareOriginal) original.DressUniform(variant, nodes, bindings, new StackPanel(), baselineNumber, baselineName, true);
                edited.DressUniform(variant, nodes, bindings, sources, normalized, name.Text, false, previews,state.DirtRegions);
                state.Name = name.Text; state.Number = number.Text; state.Variant = variant.Suffix;
                colourSwatches.Children.Clear();
                foreach (uint colour in UniformPreview.FabricColours(variant))
                {
                    var color = Color.FromRgb((byte)(colour >> 16), (byte)(colour >> 8), (byte)colour);
                    colourSwatches.Children.Add(new Border { Width = 26, Height = 20, Margin = new Thickness(2), Background = new SolidColorBrush(color), ToolTip = $"Selected fabric #{colour & 0xffffff:X6}" });
                }
                glyphs.Children.Clear();
                if (variant.Font is { } fontNode)
                {
                    var font = SshFile.Read(fontNode.ReadAll());
                    foreach (var image in new[] { UniformLettering.Name(font, name.Text), UniformLettering.Number(font, normalized) })
                    {
                        var bitmap = BitmapSource.Create(image.Width,image.Height,96,96,PixelFormats.Bgra32,null,image.Bgra,image.Width*4);
                        bitmap.Freeze(); glyphs.Children.Add(new Image { Source = bitmap, Width = image.Width, Height = image.Height, Stretch = Stretch.None });
                    }
                }
                // Preserve the selected texture capability or replacement result.
            }
            catch (Exception ex) { status.Text = ex.Message; }
        }
        void SelectPlayer()
        {
            if (player.SelectedItem is not TeamPlayer p) return;
            state.Player = p.Id;
            loading = true;
            var source = p.Sources.FirstOrDefault(s => s.Asset.Name == "attrib.dat");
            playerSource = source?.Asset; playerId = p.Id;
            playerBaseline = playerSource?.ReadAll();
            var record = playerBaseline is null ? null : PlayerAttributes.Read(playerBaseline).Records[p.Id];
            var discRecord = playerSource is null ? null : PlayerAttributes.Read(playerSource.ReadOriginal()).Records[p.Id];
            baselineName = discRecord?.Field(1) ?? ""; baselineNumber = discRecord?.Field(2) ?? "0";
            name.Text = record?.Field(1) ?? "";
            number.Text = record?.Field(2) ?? "0";
            loading = false; Refresh();
        }
        player.SelectionChanged += (_,_) => SelectPlayer();
        pick.SelectionChanged += (_,_) =>
        {
            if (pick.SelectedItem is not UniformVariant v) return;
            if (!selectingRole) { state.Role = null; role.SelectedIndex = -1; }
            textures = new[] { v.Texture, v.Font }.OfType<AssetNode>().SelectMany(pack => SshFile.Read(pack.ReadAll()).Images.Select(i => (pack,i))).ToList();
            tags.ItemsSource = textures.Select(t =>
            {
                var tag = t.Image.Tag.Trim();
                var label = tag switch { "hat" => "Cap", "tors" or "torf" => "Jersey", _ when tag.StartsWith("leg") => "Pants", _ when t.Pack == v.Font => "Lettering", _ => "Uniform" };
                return $"{label} · {tag} ({t.Image.Header.Width}×{t.Image.Header.Height})";
            }).ToList();
            var selectedTexture = textures.FindIndex(t => t.Image.Tag.Trim() == (state.SelectedTextureTag ?? "tors") && (t.Pack == v.Font) == state.SelectedTextureIsFont);
            tags.SelectedIndex = Math.Max(0, selectedTexture);
            Refresh();
        };
        name.TextChanged += (_,_) => Refresh(); number.TextChanged += (_,_) => Refresh();
        var controls = new WrapPanel();
        var previewOptions = new WrapPanel();
        role.SelectionChanged += (_, _) =>
        {
            if (role.SelectedItem is not string selected) return;
            state.Role = selected;
            if (!state.Roles.TryGetValue(selected, out var suffix) || !variants.Any(v => v.Suffix == suffix))
            {
                status.Text = selected + " has no mapped disc variant. Select a native variant and map it to this role."; return;
            }
            selectingRole = true;
            try { pick.SelectedIndex = variants.FindIndex(v => v.Suffix == suffix); }
            finally { selectingRole = false; }
        };
        controls.Children.Add(role); controls.Children.Add(pick);
        previewOptions.Children.Add(B.Button("Map variant to role…", null, (_, _) =>
        {
            if (pick.SelectedItem is not UniformVariant variant) return;
            var dialog = new Window { Owner = Window.GetWindow(this), Title = "Uniform preview role", Width = 360, SizeToContent = SizeToContent.Height, WindowStartupLocation = WindowStartupLocation.CenterOwner, Background = Ui.Res("PanelBg"), Foreground = Ui.Res("Text") };
            var roles = new ComboBox { ItemsSource = new[] { "Home", "Away", "Alternate" }, SelectedIndex = 0, Margin = new Thickness(10) };
            dialog.Content = B.Rows((B.Text("Map disc variant " + variant.Suffix + " to preview role", "Muted"), B.Auto), (roles, B.Auto), (B.Button("Use mapping", null, (_, _) => { state.Roles[(string)roles.SelectedItem] = variant.Suffix; role.SelectedItem = roles.SelectedItem; dialog.DialogResult = true; }, "Primary"), B.Auto));
            dialog.ShowDialog();
        }, "Outline"));
        previewOptions.Children.Add(B.Text("Player", "Muted")); previewOptions.Children.Add(player);
        previewOptions.Children.Add(B.Text("Surname", "Muted")); previewOptions.Children.Add(name);
        previewOptions.Children.Add(B.Text("Number", "Muted")); previewOptions.Children.Add(number);
        var applyRoster=B.Button("Apply name / number to roster", null, (_,_) =>
        {
            try
            {
                if (playerSource is null || playerBaseline is null || Editor.Session is not { } session) throw new InvalidOperationException("Select a player with an attrib.dat record.");
                if (!session.GetBytes(playerSource).AsSpan().SequenceEqual(playerBaseline)) throw new InvalidOperationException("Player source changed; reselect the player before applying.");
                var values = new Dictionary<int,string> { [1] = name.Text, [2] = UniformLettering.NumberText(number.Text) };
                var bytes = PlayerAttributeEditor.Patch(playerBaseline, playerId, values);
                if (bytes.AsSpan().SequenceEqual(playerBaseline)) { status.Text = "No roster changes."; return; }
                session.Commit(playerSource, bytes, $"{profile.Team.Name} uniform workspace: player {playerId:X8} surname / jersey");
                playerBaseline = bytes;
                status.Text = "Roster change applied. Save Project / Export Mod includes it; undo/redo is available in project history.";
            }
            catch(Exception ex) { status.Text = ex.Message; }
        }, "Primary");
        applyRoster.Tag="ApplyUniformRoster"; previewOptions.Children.Add(applyRoster);
        previewOptions.Children.Add(B.Button("Match original camera",null,(_,_)=>original.MatchUniformCamera(edited),"Outline"));
        var replace = new WrapPanel(); replace.Children.Add(tags);
        replace.Children.Add(B.Button("Replace selected texture…",null,async (_,_)=>
        {
            if(tags.SelectedIndex<0||Editor.Session is not { } session)return;
            var (pack,img)=textures[tags.SelectedIndex];
            var dialog=new Microsoft.Win32.OpenFileDialog { Filter="Images|*.png;*.dds;*.tga;*.bmp;*.jpg;*.jpeg;*.gif;*.tif;*.tiff", Title=$"Replace {pack.Name}/{img.Tag} ({img.Header.Width}×{img.Header.Height})" };
            if(dialog.ShowDialog(Window.GetWindow(this))!=true)return;
            try
            {
                var (pixels, width, height) = TextureView.LoadPicture(dialog.FileName);
                var source = SshFile.Read(pack.ReadAll());
                var review = new TextureImportReview(source, source.Images[img.Index], pixels, width, height, PaletteMode.Rebuild, session.SaveBlocker(pack)) { Owner = Window.GetWindow(this) };
                if (review.ShowDialog() != true) return;
                pixels = review.ResultPixels; width = review.TargetWidth; height = review.TargetHeight;
                status.Text = "Checking replacement usage…";
                var decision = MainWindow.Instance is { } main ? await main.PrepareReplacement(pack, profile, SshWriter.Unsupported(SshFile.Read(pack.ReadAll()), img) is null && session.SaveBlocker(pack) is null) : null;
                if (MainWindow.Instance is not null && decision is null) return;
                var ssh=SshFile.Read(pack.ReadAll());
                var reason=SshWriter.Unsupported(ssh,ssh.Images[img.Index])??session.SaveBlocker(pack);
                var description=$"Uniform texture {pack.Name}/{img.Tag} ← {Path.GetFileName(dialog.FileName)}";
                if(reason is null)
                    {
                    var bytes = SshWriter.ReplaceImage(ssh,ssh.Images[img.Index],pixels,width,height, allowResize: width != img.Header.Width || height != img.Header.Height).File;
                    if (decision is null) session.Commit(pack, bytes, description); else decision.Apply(bytes, description);
                }
                else
                {
                    string key=pack.Key+"/"+img.Index; var before=previews.GetValueOrDefault(key);
                    var after=new DecodedImage { Width=width,Height=height,Bgra=pixels };
                    session.CommitPreviewOnly(pack,description,reason,apply=> { if(apply)previews[key]=after; else if(before is null)previews.Remove(key); else previews[key]=before; });
                }
                Refresh(); status.Text=reason is null ? "Saved texture edit applied to history. Save Project / Export Mod includes it." : "PREVIEW ONLY — "+reason;
            }
            catch(Exception ex) { status.Text=ex.Message; Log.Error("Uniform texture replacement: " + ex.Message); MessageBox.Show(Window.GetWindow(this), ex.Message, "Texture replacement failed"); }
        },"Primary"));
        replace.Children.Add(B.Button("Open texture editor",null,(_,_)=> { if(tags.SelectedIndex>=0)MainWindow.Instance?.OpenAsset(textures[tags.SelectedIndex].Pack); },"Outline"));
        replace.Children.Add(B.Button("Export uniform set as PNGs",null,(_,_)=>TexturePackExport.ExportSchool(profile),"Outline"));
        var sidebar = new StackPanel { Margin = new Thickness(12) }; sidebar.Children.Add(B.Text("Selected texture", "Text", 15)); sidebar.Children.Add(status);
        var texturePreview = new Image { Height = 150, Margin = new Thickness(0, 8, 0, 12), Stretch = Stretch.Uniform }; sidebar.Children.Add(texturePreview);
        var playerOptions = new Expander { Header = "Player name & number", Content = B.Rows((previewOptions, B.Auto), (glyphs, B.Auto)), IsExpanded = state.PlayerOptionsExpanded };
        playerOptions.Expanded += (_, _) => state.PlayerOptionsExpanded = true;
        playerOptions.Collapsed += (_, _) => state.PlayerOptionsExpanded = false;
        sidebar.Children.Add(playerOptions);
        sidebar.Children.Add(B.Text("Selected uniform colours", "Muted")); sidebar.Children.Add(colourSwatches);
        var dirt=new WrapPanel();
        foreach(var region in new[]{"dcht","dbtl","dbtr","dlll","dllr","dlul","dlur"})
        {
            var regionLabel = region switch { "dcht" => "Chest", "dbtl" => "Back left", "dbtr" => "Back right", "dlll" => "Lower leg left", "dllr" => "Lower leg right", "dlul" => "Upper leg left", _ => "Upper leg right" };
            var check=new CheckBox { Content=regionLabel,Tag="UniformDirt:"+region,IsChecked=state.DirtRegions.Contains(region),Margin=new Thickness(5),ToolTip="Preview event state: use the game's dirt texture for this native material region" };
            check.Checked+=(_,_)=> { state.DirtRegions.Add(region); Refresh(); };
            check.Unchecked+=(_,_)=> { state.DirtRegions.Remove(region); Refresh(); };
            dirt.Children.Add(check);
        }
        var dirtOptions = new Expander { Header="Dirt regions · preview only",Content=dirt,IsExpanded=state.DirtOptionsExpanded };
        dirtOptions.Expanded += (_, _) => state.DirtOptionsExpanded = true;
        dirtOptions.Collapsed += (_, _) => state.DirtOptionsExpanded = false;
        sidebar.Children.Add(dirtOptions);
        sidebar.Children.Add(new Expander { Header="Texture sources",Content=sources });
        sidebar.Children.Add(new Expander { Header="Runtime evidence / differences", Content=new TextBlock { Margin=new Thickness(6),TextWrapping=TextWrapping.Wrap,Text="Native MVPIrradTexture2 programs provide the fabric TAR, decoration TAR and separate V2-32 UVs indexed by VU output slot. The uniform compositor bakes these two passes with GS alpha in 0–128 units, TAR repeat/clamp and bilinear sampling. Dirt source/material tables: SLUS 0x85C998 / 0x85CAB8; conditional binder 0x43EE28. Region checkboxes select native preview states; gameplay event accumulation is not simulated. WPF supplies scene lighting; sample head/skin/equipment are not roster likenesses." } });
        tags.SelectionChanged+=(_,_)=>
        {
            if(tags.SelectedIndex<0)return;
            var (pack,image)=textures[tags.SelectedIndex];
            state.SelectedTextureTag = image.Tag.Trim();
            state.SelectedTextureIsFont = pack.Name.StartsWith('f');
            var ssh=SshFile.Read(pack.ReadAll());
            var decoded = ssh.Decode(ssh.Images[image.Index]);
            var bitmap = BitmapSource.Create(decoded.Width, decoded.Height, 96, 96, PixelFormats.Bgra32, null, decoded.Bgra, decoded.Width * 4); bitmap.Freeze(); texturePreview.Source = bitmap;
            var reason=SshWriter.Unsupported(ssh,ssh.Images[image.Index])??Editor.Session?.SaveBlocker(pack);
            status.Text=$"{pack.Name}/{image.Tag} · {image.Header.Width}×{image.Header.Height} · "+(reason is null?"Replacement saves to project and export.":"PREVIEW ONLY — "+reason);
        };
        var originalPanel = B.Rows((B.Text("Original", "Muted"), B.Auto), (original, B.Star()));
        originalPanel.Tag = "UniformOriginal";
        var views=B.Cols((originalPanel,B.Px(0)),(B.Rows((B.Text("Edited uniform", "Muted"),B.Auto),(edited,B.Star())),B.Star()),(new ScrollViewer { Content=sidebar,VerticalScrollBarVisibility=ScrollBarVisibility.Auto },B.Px(260)));
        var compare = new CheckBox { Content = "Compare with original", Tag = "UniformCompare", IsChecked = state.CompareOriginal, Margin = new Thickness(14, 6, 4, 6), VerticalAlignment = VerticalAlignment.Center };
        void Compare() { state.CompareOriginal = compare.IsChecked == true; originalPanel.Visibility = state.CompareOriginal ? Visibility.Visible : Visibility.Collapsed; views.ColumnDefinitions[0].Width = state.CompareOriginal ? new GridLength(1, GridUnitType.Star) : new GridLength(0); if (state.CompareOriginal) original.MatchUniformCamera(edited); Refresh(); }
        compare.Checked += (_, _) => Compare(); compare.Unchecked += (_, _) => Compare(); controls.Children.Add(compare); Compare();
        Children.Add(B.Rows((controls,B.Auto),(replace,B.Auto),(views,B.Star())));
        void Changed()=>Refresh();
        Loaded+=(_,_)=> { Editor.Changed-=Changed; Editor.Changed+=Changed; Refresh(); };
        Unloaded+=(_,_)=>
        {
            Editor.Changed-=Changed;
            state.OriginalCamera=original.CaptureUniformCamera(); state.EditedCamera=edited.CaptureUniformCamera();
        };
        var args=Environment.GetCommandLineArgs(); int vi=Array.IndexOf(args,"--uniform-variant");
        pick.SelectedIndex=initialVariant is { } suffix ? Math.Max(0,variants.FindIndex(v=>v.Suffix==suffix)) : vi>=0&&vi+1<args.Length?Math.Max(0,variants.FindIndex(v=>v.Suffix.ToString()==args[vi+1])):0;
        player.SelectedIndex=Math.Max(0,profile.Players.FindIndex(p=>p.Id==initialPlayer));
        if(initialName is not null)name.Text=initialName;
        if(initialNumber is not null)number.Text=initialNumber;
        int ni=Array.IndexOf(args,"--uniform-number"), ti=Array.IndexOf(args,"--uniform-name");
        if(initialNumber is null&&ni>=0&&ni+1<args.Length)number.Text=args[ni+1];
        if(initialName is null&&ti>=0&&ti+1<args.Length)name.Text=args[ti+1];
        role.SelectedItem = initialRole ?? (initialVariant is null ? "Home" : null);
        initializing = false;
        Refresh();
    }
}
