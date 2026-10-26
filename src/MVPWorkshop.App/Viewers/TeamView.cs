using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using MVPWorkshop.Core.Formats;
using MVPWorkshop.Core.Vfs;

namespace MVPWorkshop.App.Viewers;

public sealed class TeamView : Grid
{
    private readonly TabControl _tabs;
    private readonly DataGrid? _players;
    public UniformWorkspaceState UniformState { get; }
    public PlayerWorkspaceState PlayerState { get; }
    public int SelectedSection { get => _tabs.SelectedIndex; set => _tabs.SelectedIndex = value; }
    public int SelectedPlayerSlot
    {
        get => (_players?.SelectedItem as TeamPlayer)?.Slot ?? 1;
        set { if (_players is not null) _players.SelectedItem = _players.Items.OfType<TeamPlayer>().FirstOrDefault(p => p.Slot == value); }
    }
    public TeamView(TeamProfile profile, bool assignmentPreview = false, UniformWorkspaceState? uniformState = null, PlayerWorkspaceState? playerState = null)
    {
        UniformState = uniformState ?? new UniformWorkspaceState();
        PlayerState = playerState ?? new PlayerWorkspaceState();
        var team = profile.Team;
        var header = new StackPanel { Margin = new Thickness(22) };
        header.Children.Add(B.Text(team.Name, "Text", 26, FontWeights.SemiBold));
        header.Children.Add(B.Text($"{team.Abbreviation} · {team.Nickname} · {team.City} · uniform {team.UniformId:D3} · logo {team.LogoId:D3} · stadium enum {team.StadiumId}", "Muted", 13));
        var evidence = new TextBlock { Text = profile.StadiumEvidence, TextWrapping = TextWrapping.Wrap, Foreground = Ui.Res("Muted"), Margin = new Thickness(0, 10, 0, 0) };
        header.Children.Add(evidence);
        var stadiumModel = profile.Assets.FirstOrDefault(a => a.Category == "Stadium/Assigned models" && a.Asset.Name.Equals("mstadium.ord", StringComparison.OrdinalIgnoreCase));
        var actions = new StackPanel { Orientation = Orientation.Horizontal };
        if (stadiumModel is not null) actions.Children.Add(B.Button("Open stadium in 3D", null, (_, _) => MainWindow.Instance?.OpenAsset(stadiumModel.Asset), "Primary"));
        actions.Children.Add(B.Button("School info", null, (_, _) => MainWindow.Instance?.OpenWorkspaceDocument(new DocumentTab { Title = profile.Label + " · school info", Content = (FrameworkElement)TeamWorkspaceView.SchoolInfo(profile) }), "Outline"));
        var draftStatus = new TextBlock { Foreground = Ui.Res("Muted"), Margin = new Thickness(8), TextWrapping = TextWrapping.Wrap, Visibility = Visibility.Collapsed };
        var applyDrafts = B.Button("Apply all player drafts", null, (_, _) =>
        {
            draftStatus.Visibility = Visibility.Visible;
            try { var count = PlayerState.ApplyAll(Editor.Session ?? throw new InvalidOperationException("No project session."), team.Name); draftStatus.Text = count == 0 ? "No pending player edits." : $"Applied {count} player source drafts. Save Project or Export Mod to write output."; }
            catch (Exception ex) { draftStatus.Text = ex.Message; }
        }, "Primary");
        applyDrafts.Tag = "ApplyAllPlayerDrafts";
        actions.Children.Add(applyDrafts);
        actions.Children.Add(B.Button("Reset all drafts", null, (_, _) => { PlayerState.ResetAll(); draftStatus.Visibility = Visibility.Visible; draftStatus.Text = "Player drafts reset. Applied project edits are unchanged; use Undo to revert them."; }, "Outline"));
        header.Children.Add(actions);
        header.Children.Add(draftStatus);
        var tabs = new TabControl();
        _tabs = tabs;
        tabs.Tag = "TeamWorkspaceTabs";
        if (!assignmentPreview)
        {
            void Navigate(string name) { tabs.SelectedItem = tabs.Items.OfType<TabItem>().FirstOrDefault(t => t.Header.ToString()!.StartsWith(name, StringComparison.Ordinal)); }
            tabs.Items.Add(new TabItem { Header = "Workspace", Content = TeamWorkspaceView.Overview(profile, Navigate) });
            tabs.Items.Add(new TabItem { Header = $"Roster ({profile.Players.Count})", Content = TeamWorkspaceView.Players(profile, out var playerGrid, state: PlayerState) });
            _players = playerGrid;
            foreach (var category in new[] { "Portraits", "Equipment", "Audio" })
            {
                var section = new TabControl();
                section.Items.Add(new TabItem { Header = "Player fields", Content = TeamWorkspaceView.Players(profile, out _, category == "Portraits" ? 1 : category == "Equipment" ? 2 : 3, PlayerState) });
                section.Items.Add(new TabItem { Header = "Source library", Content = TeamWorkspaceView.Sources(profile.Assets.Concat(profile.SharedAssets).Where(a => a.Category.StartsWith(category)), $"{category}: inspect evidence below; shared library entries do not establish school/player ownership", profile) });
                tabs.Items.Add(new TabItem { Header = category, Content = section });
            }
        }
        if (!assignmentPreview)
        {
            var editor = new Grid();
            editor.Loaded += (_, _) => { if (editor.Children.Count == 0) editor.Children.Add(new TeamAssignmentView(profile)); };
            tabs.Items.Add(new TabItem { Header = "Edit assignments", Content = editor });
        }
        foreach (var category in new[] { "Uniforms", "Stadium", "Logos" })
        {
            var links = profile.Assets.Where(a => a.Category.StartsWith(category, StringComparison.Ordinal)).ToList();
            var gallery = new WrapPanel { Margin = new Thickness(14) };
            foreach (var link in links.Where(a => a.Asset.IsTexturePack && a.Asset.StoredSize > 200).OrderByDescending(a => a.Asset.Name.StartsWith('u')).Take(category == "Stadium" ? 8 : 12))
            {
                var image = new Image { Width = 140, Height = 115, Stretch = Stretch.Uniform };
                var card = new StackPanel { Width = 172, Margin = new Thickness(6) };
                card.Children.Add(image);
                card.Children.Add(B.Button(link.Asset.Name, null, (_, _) => MainWindow.Instance?.OpenAsset(link.Asset), "Outline", link.Asset.Path));
                card.Children.Add(B.Text(link.Category, "Faint", 10));
                gallery.Children.Add(card);
                image.Loaded += async (_, _) =>
                {
                    if (image.Source is not null) return;
                    try
                    {
                        image.Source = await Task.Run(() =>
                        {
                            var ssh = SshFile.Read(link.Asset.ReadAll());
                            var candidate = ssh.Images.OrderByDescending(i => i.Header.Width * i.Header.Height).FirstOrDefault();
                            if (candidate is null) return null;
                            var decoded = ssh.Decode(candidate);
                            var bitmap = BitmapSource.Create(decoded.Width, decoded.Height, 96, 96, PixelFormats.Bgra32, null, decoded.Bgra, decoded.Width * 4); bitmap.Freeze(); return bitmap;
                        });
                    }
                    catch (Exception ex) { image.ToolTip = ex.Message; }
                };
            }
            var grid = B.Grid();
            grid.Columns.Add(B.Col("Group", nameof(TeamAssetLink.Category), 160));
            grid.Columns.Add(new DataGridTextColumn { Header = "Asset", Binding = new System.Windows.Data.Binding("Asset.Name"), Width = new DataGridLength(1, DataGridLengthUnitType.Star) });
            grid.Columns.Add(B.Col("Assignment evidence", nameof(TeamAssetLink.Evidence), 380));
            grid.Columns.Add(new DataGridTextColumn { Header = "Resolved source path", Binding = new System.Windows.Data.Binding("Asset.Path"), Width = 440 });
            grid.ItemsSource = links;
            grid.MouseDoubleClick += (_, _) => { if (grid.SelectedItem is TeamAssetLink link) MainWindow.Instance?.OpenAsset(link.Asset); };
            var content = links.Count == 0 ? (UIElement)B.Text("No verified asset links available for this category.", "Muted") :
                B.Rows((new ScrollViewer { Content = gallery, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, MaxHeight = 320 }, B.Auto), (grid, B.Star()));
            if (category == "Uniforms")
            {
                var uniformTabs = new TabControl();
                var preview = new Grid();
                bool initialized = false;
                preview.Loaded += (_, _) =>
                {
                    if (initialized) return;
                    initialized = true;
                    try
                    {
                        var nodes = MainWindow.Instance!.GameRoot!.EnumerateDeep().ToList();
                        var body = nodes.First(n => n.Name == "mebody.ord");
                        var head = nodes.First(n => n.Name == "hihead.ord");
                        var executable = GameExecutable.Find(nodes)!;
                        var bindings = UniformPreview.ReadBindings(executable.ReadAll());
                        preview.Children.Add(new UniformWorkspaceView(profile, nodes, bindings, UniformState));
                    }
                    catch (Exception ex) { preview.Children.Add(B.Text("Uniform preview unavailable: " + ex.Message, "Muted")); }
                };
                uniformTabs.Items.Add(new TabItem { Header = "Dressed player", Content = preview });
                uniformTabs.Items.Add(new TabItem { Header = "Texture sheets & menu artwork", Content = content });
                content = uniformTabs;
            }
            if (assignmentPreview && category == "Stadium" && stadiumModel is not null)
                content = B.Rows((B.Button("Open proposed stadium in 3D", null, (_, _) => MainWindow.Instance?.OpenAsset(stadiumModel.Asset), "Primary"), B.Auto), (content, B.Star()));
            tabs.Items.Add(new TabItem { Header = $"{category} ({links.Count})", Content = content });
        }
        Children.Add(assignmentPreview ? tabs : B.Rows((header, B.Auto), (tabs, B.Star())));
    }
}





