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
    public uint? Player { get; set; }
    public string? Name { get; set; }
    public string? Number { get; set; }
    public ModelView.UniformCamera? OriginalCamera { get; set; }
    public ModelView.UniformCamera? EditedCamera { get; set; }
    public HashSet<string> DirtRegions { get; } = [];
}

public sealed class UniformWorkspaceView : Grid
{
    public UniformWorkspaceView(TeamProfile profile, IReadOnlyList<AssetNode> nodes, Dictionary<string,string> bindings, UniformWorkspaceState state)
    {
        var previews = state.Previews;
        var initialName = state.Name; var initialNumber = state.Number;
        var initialPlayer = state.Player; var initialVariant = state.Variant;
        var body = nodes.First(n => n.Name == "mebody.ord");
        var head = nodes.First(n => n.Name == "hihead.ord");
        var original = ModelView.CreateUniform(body, head);
        var edited = ModelView.CreateUniform(body, head);
        if(state.OriginalCamera is { } originalCamera)original.RestoreUniformCamera(originalCamera);
        if(state.EditedCamera is { } editedCamera)edited.RestoreUniformCamera(editedCamera);
        original.UniformCameraChanged += camera => state.OriginalCamera=camera;
        edited.UniformCameraChanged += camera => state.EditedCamera=camera;
        var variants = UniformPreview.Variants(profile);
        if (variants.Count == 0) throw new InvalidOperationException("No uniform variants.");
        var pick = new ComboBox { Tag="UniformVariant", ItemsSource = variants, MinWidth = 200, Margin = new Thickness(4) };
        var player = new ComboBox { Tag="UniformPlayer", ItemsSource = profile.Players, DisplayMemberPath = "Name", MinWidth = 180, Margin = new Thickness(4) };
        var name = new TextBox { Tag="UniformName", Width = 150, Margin = new Thickness(4), ToolTip = "Preview surname; native renderer uses the first ten characters" };
        var number = new TextBox { Tag="UniformNumber", Width = 45, Margin = new Thickness(4), Text = "0", ToolTip = "Preview jersey number 0–99" };
        var sources = new StackPanel();
        var status = new TextBlock { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(6), Foreground = Ui.Res("Muted") };
        var glyphs = new StackPanel();
        string baselineName = "", baselineNumber = "0";
        AssetNode? playerSource = null; uint playerId = 0; byte[]? playerBaseline = null;
        var tags = new ComboBox { MinWidth = 170, Margin = new Thickness(4) };
        List<(AssetNode Pack, SshImage Image)> textures = [];
        bool loading = false;
        void Refresh()
        {
            if (loading || pick.SelectedItem is not UniformVariant variant) return;
            try
            {
                var normalized = UniformLettering.NumberText(number.Text);
                original.DressUniform(variant, nodes, bindings, new StackPanel(), baselineNumber, baselineName, true);
                edited.DressUniform(variant, nodes, bindings, sources, normalized, name.Text, false, previews,state.DirtRegions);
                state.Name = name.Text; state.Number = number.Text; state.Variant = variant.Suffix;
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
                status.Text = "Preview only: selected variant, text, camera, dirt event states and sample appearance. Decoration uses native secondary UVs and GS alpha arithmetic. Texture replacements report their save capability individually.";
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
            textures = new[] { v.Texture, v.Font }.OfType<AssetNode>().SelectMany(pack => SshFile.Read(pack.ReadAll()).Images.Select(i => (pack,i))).ToList();
            tags.ItemsSource = textures.Select(t => $"{t.Pack.Name} / {t.Image.Tag}").ToList(); tags.SelectedIndex = 0;
            Refresh();
        };
        name.TextChanged += (_,_) => Refresh(); number.TextChanged += (_,_) => Refresh();
        var controls = new WrapPanel();
        controls.Children.Add(pick); controls.Children.Add(player);
        controls.Children.Add(B.Text("Preview surname", "Muted")); controls.Children.Add(name);
        controls.Children.Add(B.Text("Number", "Muted")); controls.Children.Add(number);
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
        applyRoster.Tag="ApplyUniformRoster"; controls.Children.Add(applyRoster);
        controls.Children.Add(B.Button("Match original camera",null,(_,_)=>original.MatchUniformCamera(edited),"Outline"));
        var replace = new WrapPanel(); replace.Children.Add(tags);
        replace.Children.Add(B.Button("Replace selected texture…",null,async (_,_)=>
        {
            if(tags.SelectedIndex<0||Editor.Session is not { } session)return;
            var (pack,img)=textures[tags.SelectedIndex];
            var dialog=new Microsoft.Win32.OpenFileDialog { Filter="Images|*.png;*.bmp;*.jpg;*.jpeg;*.tif;*.tiff", Title=$"Replace {pack.Name}/{img.Tag} ({img.Header.Width}×{img.Header.Height})" };
            if(dialog.ShowDialog(Window.GetWindow(this))!=true)return;
            try
            {
                var decoder=BitmapDecoder.Create(new Uri(dialog.FileName),BitmapCreateOptions.PreservePixelFormat,BitmapCacheOption.OnLoad);
                var frame=new FormatConvertedBitmap(decoder.Frames[0],PixelFormats.Bgra32,null,0);
                if(frame.PixelWidth!=img.Header.Width||frame.PixelHeight!=img.Header.Height)throw new ArgumentException($"Replacement must be {img.Header.Width}×{img.Header.Height} pixels.");
                var pixels=new byte[frame.PixelWidth*frame.PixelHeight*4]; frame.CopyPixels(pixels,frame.PixelWidth*4,0);
                if(MainWindow.Instance is { } main && !await main.PreviewReplacementImpact(pack))return;
                var ssh=SshFile.Read(pack.ReadAll());
                var reason=SshWriter.Unsupported(ssh,ssh.Images[img.Index])??session.SaveBlocker(pack);
                var description=$"Uniform texture {pack.Name}/{img.Tag} ← {Path.GetFileName(dialog.FileName)}";
                if(reason is null)
                    session.Commit(pack,SshWriter.ReplaceImage(ssh,ssh.Images[img.Index],pixels,frame.PixelWidth,frame.PixelHeight).File,description);
                else
                {
                    string key=pack.Key+"/"+img.Index; var before=previews.GetValueOrDefault(key);
                    var after=new DecodedImage { Width=frame.PixelWidth,Height=frame.PixelHeight,Bgra=pixels };
                    session.CommitPreviewOnly(pack,description,reason,apply=> { if(apply)previews[key]=after; else if(before is null)previews.Remove(key); else previews[key]=before; });
                }
                Refresh(); status.Text=reason is null ? "Saved texture edit applied to history. Save Project / Export Mod includes it." : "PREVIEW ONLY — "+reason;
            }
            catch(Exception ex) { status.Text=ex.Message; }
        },"Primary"));
        replace.Children.Add(B.Button("Open texture editor",null,(_,_)=> { if(tags.SelectedIndex>=0)MainWindow.Instance?.OpenAsset(textures[tags.SelectedIndex].Pack); },"Outline"));
        var sidebar = new StackPanel(); sidebar.Children.Add(status); sidebar.Children.Add(glyphs);
        var dirt=new WrapPanel();
        foreach(var region in new[]{"dcht","dbtl","dbtr","dlll","dllr","dlul","dlur"})
        {
            var check=new CheckBox { Content=region,Tag="UniformDirt:"+region,IsChecked=state.DirtRegions.Contains(region),Margin=new Thickness(5),ToolTip="Preview event state: use the game's dirt texture for this native material region" };
            check.Checked+=(_,_)=> { state.DirtRegions.Add(region); Refresh(); };
            check.Unchecked+=(_,_)=> { state.DirtRegions.Remove(region); Refresh(); };
            dirt.Children.Add(check);
        }
        sidebar.Children.Add(new Expander { Header="Dirt regions · preview only",Content=dirt,IsExpanded=true });
        sidebar.Children.Add(new Expander { Header="Texture sources",Content=sources });
        sidebar.Children.Add(new Expander { Header="Runtime evidence / differences", Content=new TextBlock { Margin=new Thickness(6),TextWrapping=TextWrapping.Wrap,Text="Native MVPIrradTexture2 programs provide the fabric TAR, decoration TAR and separate V2-32 UVs indexed by VU output slot. The uniform compositor bakes these two passes with GS alpha in 0–128 units, TAR repeat/clamp and bilinear sampling. Dirt source/material tables: SLUS 0x85C998 / 0x85CAB8; conditional binder 0x43EE28. Region checkboxes select native preview states; gameplay event accumulation is not simulated. WPF supplies scene lighting; sample head/skin/equipment are not roster likenesses." } });
        tags.SelectionChanged+=(_,_)=>
        {
            if(tags.SelectedIndex<0)return;
            var (pack,image)=textures[tags.SelectedIndex];
            var ssh=SshFile.Read(pack.ReadAll());
            var reason=SshWriter.Unsupported(ssh,ssh.Images[image.Index])??Editor.Session?.SaveBlocker(pack);
            status.Text=$"{pack.Name}/{image.Tag} · {image.Header.Width}×{image.Header.Height} · "+(reason is null?"Replacement saves to project and export.":"PREVIEW ONLY — "+reason);
        };
        var views=B.Cols((B.Rows((B.Text("Original disc · selected variant", "Muted"),B.Auto),(original,B.Star())),B.Star()),(B.Rows((B.Text("Edited project + preview settings", "Muted"),B.Auto),(edited,B.Star())),B.Star()),(new ScrollViewer { Content=sidebar,VerticalScrollBarVisibility=ScrollBarVisibility.Auto },B.Px(260)));
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
    }
}
