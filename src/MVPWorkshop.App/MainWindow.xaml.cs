using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using MVPWorkshop.App.Viewers;
using MVPWorkshop.Core.Editing;
using MVPWorkshop.Core.Formats;
using MVPWorkshop.Core.IO;
using MVPWorkshop.Core.Install;
using MVPWorkshop.Core.Vfs;
using Microsoft.Win32;

namespace MVPWorkshop.App;

public partial class MainWindow : Window
{
    public static readonly RoutedCommand OpenFolderCmd = new(), FocusSearchCmd = new(), CloseTabCmd = new(),
        SaveProjectCmd = new(), SaveProjectAsCmd = new(), UndoCmd = new(), RedoCmd = new(), ExportModCmd = new();

    private readonly ObservableCollection<TreeItem> _roots = [];
    private readonly ObservableCollection<DocumentTab> _docs = [];
    private readonly Settings _settings = Settings.Load();
    private AssetNode? _gameRoot;
    private CancellationTokenSource? _searchCts;
    private List<AssetNode>? _searchIndex;
    private List<TreeItem>? _categoryRoots;
    private int _catalogGeneration;
    private readonly Dictionary<TeamProfile, DocumentTab> _teamTabs = [];
    private Task<List<AssetNode>>? _assetIndexTask;

    public Task<List<AssetNode>> GetAssetIndex()
    {
        var game = _gameRoot;
        return _assetIndexTask ??= Task.Run(() => game?.EnumerateDeep().ToList() ?? []);
    }

    public static MainWindow? Instance { get; private set; }

    public MainWindow()
    {
        InitializeComponent();
        Instance = this;
        Tree.ItemsSource = _roots;
        Docs.ItemsSource = _docs;
        LogList.ItemsSource = Log.Lines;
        Log.Lines.CollectionChanged += (_, _) => { if (Log.Lines.Count > 0) LogList.ScrollIntoView(Log.Lines[^1]); };

        CommandBindings.Add(new CommandBinding(OpenFolderCmd, (_, _) => OpenFolder_Click(this, new RoutedEventArgs())));
        CommandBindings.Add(new CommandBinding(FocusSearchCmd, (_, _) => { SearchBox.Focus(); SearchBox.SelectAll(); }));
        CommandBindings.Add(new CommandBinding(CloseTabCmd, (_, _) => { if (Docs.SelectedItem is DocumentTab { CanClose: true } d) CloseDocument(d); }));
        CommandBindings.Add(new CommandBinding(SaveProjectCmd, (_, _) => SaveProject(false)));
        CommandBindings.Add(new CommandBinding(SaveProjectAsCmd, (_, _) => SaveProject(true)));
        CommandBindings.Add(new CommandBinding(UndoCmd, (_, _) => Editor.Session?.Undo(), (_, e) => e.CanExecute = Editor.Session?.CanUndo == true));
        CommandBindings.Add(new CommandBinding(RedoCmd, (_, _) => Editor.Session?.Redo(), (_, e) => e.CanExecute = Editor.Session?.CanRedo == true));
        CommandBindings.Add(new CommandBinding(ExportModCmd, (_, _) => ExportMod()));
        Editor.Changed += OnEditorChanged;
        Closing += OnClosing;
        StateChanged += (_, _) =>
        {
            MaxGlyph.Text = WindowState == WindowState.Maximized ? "" : "";
            RootBorder.BorderThickness = WindowState == WindowState.Maximized ? new Thickness(7) : new Thickness(1);
        };

        _docs.Add(new DocumentTab { Title = "Start", Glyph = "", CanClose = false, Content = StartPage.Build(this, _settings) });
        Docs.SelectedIndex = 0;
        VersionLabel.Text = "v" + AppInfo.Version;
        Log.Info($"{AppInfo.Name} {AppInfo.Version} started. Edits stay in memory/projects; exports go to a separate mod folder and never touch the game files.");

        Loaded += (_, _) =>
        {
            var startupArgs = Environment.GetCommandLineArgs();
            var gameArgument = Array.IndexOf(startupArgs, "--game");
            var requestedGame = gameArgument >= 0 && gameArgument + 1 < startupArgs.Length ? startupArgs[gameArgument + 1] : null;
            // the tool starts empty and asks for the installed game; --game (tests, snapshots) skips the question
            if (requestedGame is not null) LoadGame(requestedGame);
            Snapshot.RunIfRequested(this);
            SelfTest.RunIfRequested(this);
            if (startupArgs.Length == 1) Dispatcher.BeginInvoke(PromptOpenGame, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
        };
    }

    public AssetNode? GameRoot => _gameRoot;

    /// <summary>Find a node by virtual path relative to the game root (e.g. "DATA/GHEAD.BIG/gaaaaa.ssh").</summary>
    public AssetNode? FindNode(string vpath)
    {
        var n = _gameRoot;
        foreach (var part in vpath.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            n = n?.Children.FirstOrDefault(c => c.Name.Equals(part, StringComparison.OrdinalIgnoreCase));
            if (n is null) return null;
        }
        return n;
    }

    /// <summary>Expand the tree down to <paramref name="node"/> and select it.</summary>
    public void Reveal(AssetNode node)
    {
        if (BrowseMode.SelectedIndex == 0)
        {
            TreeItem? FindCategory(IEnumerable<TreeItem> items)
            {
                foreach (var entry in items)
                {
                    if (entry.Node == node) return entry;
                    if (entry.Node is null && FindCategory(entry.Children) is { } found) { entry.IsExpanded = true; return found; }
                }
                return null;
            }
            if (FindCategory(_roots) is { } match) { match.IsSelected = true; return; }
            BrowseMode.SelectedIndex = 1;
        }
        var chain = new List<AssetNode>();
        for (var p = node; p is not null; p = p.Parent) chain.Insert(0, p);
        IEnumerable<TreeItem> level = _roots;
        TreeItem? item = null;
        foreach (var n in chain)
        {
            item = level.FirstOrDefault(t => t.Node == n);
            if (item is null)
            {
                // virtual BIG sub-folders are their own nodes; fall through when the chain skips them
                break;
            }
            if (n != node) { item.IsExpanded = true; level = item.Children; }
        }
        if (item is not null) item.IsSelected = true;
    }

    // ---------------------------------------------------------------- game loading

    public void LoadGame(string folder, bool confirm = true)
    {
        if (confirm && !ConfirmDiscard("Open another game source")) return;
        try
        {
            var openedGame = GameFileSystem.OpenGame(folder);
            foreach (var document in _docs.Where(d => d.CanClose).ToList()) _docs.Remove(document);
            _gameRoot = openedGame;
            var output = _settings.OutputFolders.TryGetValue(folder, out var chosen) ? chosen : ModExporter.DefaultOutputFolder(folder);
            Editor.Attach(new EditSession(_gameRoot, output));
            _searchCts?.Cancel();
            SearchPanel.Visibility = Visibility.Collapsed;
            SearchResults.ItemsSource = null;
            _roots.Clear();
            var root = new TreeItem(_gameRoot, 0);
            _roots.Add(root);
            root.IsExpanded = true;
            _searchIndex = null;
            _assetIndexTask = null;
            _categoryRoots = null; _teamRefreshSession = null; _refreshTeamGrid = null;
            _catalogGeneration++;
            UpdateBrowser();
            var exe = _gameRoot.Children.FirstOrDefault(n => n.Name.StartsWith("SLUS_", StringComparison.OrdinalIgnoreCase) || n.Name.StartsWith("SLES_", StringComparison.OrdinalIgnoreCase) || n.Name.Equals("default.xbe", StringComparison.OrdinalIgnoreCase))?.Name;
            if (exe is not null && exe.EndsWith(".xbe", StringComparison.OrdinalIgnoreCase))
                try { exe = $"{exe} ({XbeFile.Read(_gameRoot.Children.First(n => n.Name == exe).ReadAll()).Title}, Xbox)"; } catch { }
            GameLabel.Text = $"{folder}{(exe is null ? "" : $"  ·  {exe}")}";
            _settings.Remember(folder);
            Log.Info($"Loaded game source {folder}");
            SetStatus($"Loaded {folder}");
            if (_docs.FirstOrDefault(d => !d.CanClose) is { } start)
            {
                var i = _docs.IndexOf(start);
                _docs[i] = new DocumentTab { Title = "Start", Glyph = "", CanClose = false, Content = StartPage.Build(this, _settings) };
                Docs.SelectedIndex = i;
            }
        }
        catch (Exception ex) { Log.Error($"Could not open {folder}: {ex.Message}"); }
    }

    public void SetStatus(string text) => Status.Text = text;

    private void BrowseMode_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (e.OriginalSource == BrowseMode && IsLoaded) UpdateBrowser();
    }

    private async void UpdateBrowser()
    {
        if (_gameRoot is not { } game || BrowseMode is null) return;
        var generation = ++_catalogGeneration;
        if (BrowseMode.SelectedIndex == 1)
        {
            _roots.Clear();
            _roots.Add(new TreeItem(game, 0) { IsExpanded = true });
            return;
        }
        if (_categoryRoots is null)
        {
            SetStatus("Categorizing assets inside archives…");
            try
            {
                var nodes = await GetAssetIndex();
                var groups = await Task.Run(() => AssetCatalog.Build(nodes));
                var teams = await Task.Run(() => TeamAssetCatalog.Build(nodes));
                if (_gameRoot != game || generation != _catalogGeneration) return;
                _searchIndex = nodes;
                var roots = new List<TreeItem>();
                void AddGroups(List<TreeItem> target, IEnumerable<KeyValuePair<string, List<AssetNode>>> groups, int depth)
                {
                    foreach (var group in groups.GroupBy(g => g.Key.Split('/')[depth]))
                    {
                        var children = new List<TreeItem>();
                        var terminal = group.Where(g => g.Key.Split('/').Length == depth + 1).ToList();
                        foreach (var leaf in terminal)
                            foreach (var pack in leaf.Value.GroupBy(AssetCatalog.PackName))
                            {
                                // DDS files of a loose install stay together under their texture folder
                                IEnumerable<TreeItem> Leaves(IEnumerable<AssetNode> nodes, int d) => nodes.GroupBy(n => n.Parent is { IsLooseTexturePack: true } t ? t : n)
                                    .Select(g => g.Key.IsLooseTexturePack ? new TreeItem(g.Key.Name, d, g.Select(n => new TreeItem(n, d + 1))) : new TreeItem(g.Key, d));
                                // mixed archives (stadiums) get Models / Textures / Data folders so the models are easy to find
                                var kinds = pack.GroupBy(AssetCatalog.Kind).OrderBy(k => k.Key == "Models" ? 0 : 1).ThenBy(k => k.Key).ToList();
                                children.Add(kinds.Count > 1 && pack.Count() > 12
                                    ? new TreeItem(pack.Key, depth + 1, kinds.Select(k => new TreeItem(k.Key, depth + 2, Leaves(k, depth + 3))))
                                    : new TreeItem(pack.Key, depth + 1, Leaves(pack, depth + 2)));
                            }
                        AddGroups(children, group.Where(g => g.Key.Split('/').Length > depth + 1), depth + 1);
                        target.Add(new TreeItem(group.Key, depth, children));
                    }
                }
                AddGroups(roots, groups, 0);
                var teamRoot = roots.FirstOrDefault(r => r.Name == "Teams");
                if (teamRoot is not null && teams.Count > 0)
                {
                    var shared = new TreeItem("All team assets by type", 1, teamRoot.Children);
                    teamRoot.Children.Clear();
                    foreach (var profile in teams)
                    {
                        var children = profile.Assets.GroupBy(a => a.Category.Split('/')[0]).Select(g => new TreeItem(g.Key, 2,
                            g.Any(a => a.Category.Contains('/'))
                                ? g.GroupBy(a => a.Category[(a.Category.IndexOf('/') + 1)..]).Select(sub => new TreeItem(sub.Key, 3, sub.Select(a => new TreeItem(a.Asset, 4) { SchoolScope = profile })))
                                : g.Select(a => new TreeItem(a.Asset, 3) { SchoolScope = profile })));
                        teamRoot.Children.Add(new TreeItem(profile.Label, 1, children) { Team = profile });
                    }
                    teamRoot.Children.Add(shared);
                }
                var order = new[] { "Models", "Teams", "Players", "Animations", "UI", "Audio", "Video", "Textures", "Data", "System" };
                _categoryRoots = roots.OrderBy(r => Array.IndexOf(order, r.Name) is var rank && rank >= 0 ? rank : int.MaxValue).ToList();
                foreach (var item in _categoryRoots.Where(r => r.Name is "Models" or "Teams")) item.IsExpanded = true;
            }
            catch (Exception ex) { Log.Error("Asset categories: " + ex.Message); SetStatus("Category indexing failed; use Files and archives."); return; }
        }
        if (generation != _catalogGeneration || BrowseMode.SelectedIndex != 0) return;
        _roots.Clear();
        foreach (var item in _categoryRoots) _roots.Add(item);
        SetStatus($"{_searchIndex?.Count(n => !n.IsFolder && !n.IsContainer && !n.IsLooseTexturePack):N0} assets categorized · original paths retained");
    }

    // ---------------------------------------------------------------- documents

    public void OpenAsset(AssetNode node)
    {
        var existing = _docs.FirstOrDefault(d => d.Node == node);
        if (existing is not null) { Docs.SelectedItem = existing; return; }
        try
        {
            Mouse.OverrideCursor = Cursors.Wait;
            var tab = AssetDocument.Create(node);
            _docs.Add(tab);
            Docs.SelectedItem = tab;
            Log.Info($"Opened {node.Path} ({node.FormatInfo.Name})");
        }
        catch (Exception ex) { Log.Error($"{node.Path}: {ex.Message}"); }
        finally { Mouse.OverrideCursor = null; }
    }

    public void ShowProps(List<Prop> props)
    {
        var view = new ListCollectionView(props);
        view.GroupDescriptions.Add(new PropertyGroupDescription(nameof(Prop.Group)));
        PropsList.ItemsSource = view;
    }

    private void Docs_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (e.OriginalSource != Docs) return;
        if (Docs.SelectedItem is DocumentTab d)
        {
            ShowProps(d.Props);
            SetStatus(d.Node?.Path ?? d.Title);
        }
    }

    public void RefreshPropsFor(DocumentTab tab) { if (Docs.SelectedItem == tab) ShowProps(tab.Props); }

    private void CloseTab_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is DocumentTab d) CloseDocument(d);
    }

    private void CloseDocument(DocumentTab tab)
    {
        if (tab.Content is TeamView view && view.PlayerState.PendingFields > 0)
        {
            MessageBox.Show(this, "This school has unapplied player edits. Use Apply all player drafts or Reset draft before closing it.", "Player drafts", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        DisposeTree(tab.Content); _docs.Remove(tab);
    }

    private bool CheckPlayerDrafts()
    {
        var pending = _docs.Select(d => d.Content).OfType<TeamView>().FirstOrDefault(v => v.PlayerState.PendingFields > 0);
        if (pending is null) return true;
        Docs.SelectedItem = _docs.First(d => ReferenceEquals(d.Content, pending));
        MessageBox.Show(this, $"{pending.PlayerState.PendingFields} player fields have unapplied edits. Use Apply all player drafts or Reset draft before saving, exporting or leaving this project.", "Player drafts", MessageBoxButton.OK, MessageBoxImage.Information);
        return false;
    }

    /// <summary>Viewers that hold players or processes (audio, movies) are nested inside the document layout.</summary>
    private static void DisposeTree(object? o)
    {
        if (o is IDisposable disp) { disp.Dispose(); return; }
        if (o is DependencyObject dep)
            foreach (var child in LogicalTreeHelper.GetChildren(dep)) DisposeTree(child);
    }

    public void AddTab(DocumentTab tab) { _docs.Add(tab); Docs.SelectedItem = tab; }

    // ---------------------------------------------------------------- tree

    private TreeItem? SelectedTreeItem => Tree.SelectedItem as TreeItem;

    private AssetNode? SelectedNode =>
        SearchPanel.Visibility == Visibility.Visible && SearchResults.SelectedItem is AssetNode s ? s
        : Docs.SelectedItem is DocumentTab { Node: { } dn } && !Tree.IsKeyboardFocusWithin ? dn
        : SelectedTreeItem?.Node;

    private void Tree_SelectedItemChanged(object sender, RoutedPropertyChangedEventArgs<object> e)
    {
        if (e.NewValue is TreeItem { Team: { } profile }) { OpenTeam(profile); return; }
        if (e.NewValue is TreeItem { Name: "Teams", Node: null } teams && teams.Children.Any(c => c.Team is not null))
        {
            var profiles = teams.Children.Select(c => c.Team).OfType<TeamProfile>().ToList();
            var search = B.SearchBox("Find a school, abbreviation or city", query => { }, 280);
            var grid = B.Grid(); grid.Columns.Add(B.Col("School", "Team.Name", 210)); grid.Columns.Add(B.Col("Abbreviation", "Team.Abbreviation", 110)); grid.Columns.Add(B.Col("City", "Team.City", 150)); grid.Columns.Add(B.Col("Uniform ID", "Team.UniformId", 85)); grid.Columns.Add(B.Col("Logo ID", "Team.LogoId", 75)); grid.Columns.Add(B.Col("Stadium enum", "Team.StadiumId", 95)); grid.Columns.Add(B.Col("Stadium code", nameof(TeamProfile.StadiumCode), 110));
            grid.ItemsSource = profiles;
            _refreshTeamGrid = () => { grid.ItemsSource = profiles.Where(p => (p.Label + " " + p.Team.City + " " + p.Team.Nickname).Contains(search.Text, StringComparison.OrdinalIgnoreCase)).ToList(); };
            search.TextChanged += (_, _) => grid.ItemsSource = profiles.Where(p => (p.Label + " " + p.Team.City + " " + p.Team.Nickname).Contains(search.Text, StringComparison.OrdinalIgnoreCase)).ToList();
            grid.MouseDoubleClick += (_, _) => { if (grid.SelectedItem is TeamProfile selected) OpenTeam(selected); };
            var existing = _docs.FirstOrDefault(d => d.Title == "Teams");
            if (existing is null) { existing = new DocumentTab { Title = "Teams", Glyph = "", Content = B.Rows((B.Toolbar(B.Text($"{profiles.Count} schools · double-click to open"), search), B.Auto), (grid, B.Star())) }; _docs.Add(existing); }
            Docs.SelectedItem = existing; return;
        }
        if (e.NewValue is TreeItem { Node: null } group)
        {
            ShowProps([new("Category", "Name", group.Name), new("Category", "Assets", group.LeafCount.ToString("N0"))]);
            SetStatus($"{group.Name} · {group.LeafCount:N0} assets");
            return;
        }
        if (e.NewValue is not TreeItem { Node: { } n }) return;
        ShowProps(Docs.SelectedItem is DocumentTab { Node: { } dn } d && dn == n ? d.Props : AssetDocument.GeneralProps(n, false));
        SetStatus(n.Path);
    }

    public void OpenWorkspaceDocument(DocumentTab tab) { _docs.Add(tab); Docs.SelectedItem = tab; }

    public void OpenSchoolAsset(TeamProfile profile, AssetNode asset)
    {
        if (asset.Name is "roster.bin" or "attrib.dat" or "lhattrib.dat" or "rhattrib.dat" or "pitcher.dat")
        {
            OpenTeam(profile);
            if (Docs.SelectedItem is DocumentTab { Content: TeamView view }) view.SelectedSection = 1;
        }
        else if (asset.Name is "teaminfo.csv" or "schoolnameaudio.csv" or "nicknameaudio.csv")
            OpenWorkspaceDocument(new DocumentTab { Title = profile.Label + " · school info", Content = (FrameworkElement)TeamWorkspaceView.SchoolInfo(profile) });
        else OpenAsset(asset);
    }

    public void OpenTeam(TeamProfile profile)
    {
        if (_teamTabs.TryGetValue(profile, out var existing) && _docs.Contains(existing)) { Docs.SelectedItem = existing; return; }
        var team = profile.Team;
        var tab = new DocumentTab { Title = profile.Label, Glyph = "", Content = new TeamView(profile),
            Props = [new("Team", "School", team.Name), new("Team", "Roster index", team.RosterIndex.ToString()), new("Team", "Asset ID", team.UniformId.ToString()), new("Team", "Roster source", $"{profile.Roster.Path} @0x{team.Offset:X}"), new("Stadium", "Evidence", profile.StadiumEvidence)] };
        _teamTabs[profile] = tab; _docs.Add(tab); Docs.SelectedItem = tab;
    }

    private void Tree_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (SelectedTreeItem is { Node: { } n } item && (e.OriginalSource as DependencyObject).FindAncestor<TreeViewItem>()?.DataContext == item)
        {
            if (!n.IsFolder) { if (item.SchoolScope is { } school) OpenSchoolAsset(school, n); else OpenAsset(n); }
            e.Handled = true;
        }
    }

    private void Tree_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && SelectedTreeItem is { Node: null } group)
        { group.IsExpanded = !group.IsExpanded; e.Handled = true; return; }
        if (e.Key == Key.Enter && SelectedTreeItem is { Node: { } n })
        {
            if (n.IsFolder) SelectedTreeItem.IsExpanded = !SelectedTreeItem.IsExpanded;
            else if (SelectedTreeItem.SchoolScope is { } school) OpenSchoolAsset(school, n);
            else OpenAsset(n);
            e.Handled = true;
        }
    }

    private void CollapseAll_Click(object sender, RoutedEventArgs e)
    {
        void C(IEnumerable<TreeItem> items) { foreach (var i in items) { C(i.Children); i.IsExpanded = false; } }
        C(_roots);
    }

    // ---------------------------------------------------------------- search

    private void SearchBox_TextChanged(object sender, TextChangedEventArgs e) =>
        SearchHint.Visibility = SearchBox.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;

    private async void SearchBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape) { SearchBox.Clear(); CloseSearch_Click(this, e); return; }
        if (e.Key != Key.Enter) return;
        var q = SearchBox.Text.Trim();
        if (q.Length == 0 || _gameRoot is null) return;

        _searchCts?.Cancel();
        var cts = _searchCts = new CancellationTokenSource();
        SearchPanel.Visibility = Visibility.Visible;
        SearchResults.ItemsSource = null;
        SearchStatus.Text = _searchIndex is null ? "Indexing all archives…" : "Searching…";
        var root = _gameRoot;
        try
        {
            var index = _searchIndex ?? await GetAssetIndex();
            if (cts.IsCancellationRequested || _gameRoot != root) return;
            _searchIndex = index;
            var results = await Task.Run(() =>
            {
                var terms = q.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                return index.Where(n => !n.IsFolder && terms.All(t => n.Path.Contains(t, StringComparison.OrdinalIgnoreCase))).Take(5000).ToList();
            }, cts.Token);
            if (cts.IsCancellationRequested) return;
            SearchResults.ItemsSource = results;
            SearchStatus.Text = $"{results.Count:N0}{(results.Count == 5000 ? "+" : "")} matches for “{q}” in {index.Count:N0} entries";
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { SearchStatus.Text = "Search failed: " + ex.Message; }
    }

    private void CloseSearch_Click(object sender, RoutedEventArgs e) => SearchPanel.Visibility = Visibility.Collapsed;
    private void SearchResults_DoubleClick(object sender, MouseButtonEventArgs e) { if (SearchResults.SelectedItem is AssetNode n) OpenAsset(n); }
    private void SearchResults_KeyDown(object sender, KeyEventArgs e) { if (e.Key == Key.Enter && SearchResults.SelectedItem is AssetNode n) OpenAsset(n); }
    private void SearchResults_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (SearchResults.SelectedItem is AssetNode n) { ShowProps(AssetDocument.GeneralProps(n, false)); SetStatus(n.Path); }
    }

    // ---------------------------------------------------------------- toolbar

    private void OpenFolder_Click(object sender, RoutedEventArgs e) => PromptOpenGame();

    /// <summary>Asks for the installed game's MVP06.exe and loads its loose <c>game/</c> folder.</summary>
    public void PromptOpenGame()
    {
        var last = _settings.LastGameFolder is { } g && Directory.Exists(g) ? Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(g)) : null;
        var dlg = new OpenFileDialog { Title = "Where is the game? Select MVP06.exe in the install folder", Filter = "Game executable|*.exe;default.xbe|All files|*.*", InitialDirectory = last ?? "" };
        if (dlg.ShowDialog(this) != true) return;
        if (LooseInstall.GameFolderFor(dlg.FileName, out var error) is not { } game)
        {
            MessageBox.Show(this, error, "Open game", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        if (!LooseInstall.IsLoose(game)) Log.Warn($"{game} has no loose archive folders (_big.json); it is read as packed .big files. Run the game's setup to install loose assets.");
        LoadGame(game);
    }

    private void OpenFile_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog { Title = "Open a game file", Filter = "All files|*.*", InitialDirectory = _settings.LastGameFolder ?? "" };
        if (dlg.ShowDialog(this) == true) { if (Path.GetExtension(dlg.FileName).Equals(".iso", StringComparison.OrdinalIgnoreCase)) LoadGame(dlg.FileName); else OpenAsset(GameFileSystem.OpenFile(dlg.FileName)); }
    }

    private void ExportRaw_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedNode is not { IsFolder: false } n) { Log.Warn("Select a file to export."); return; }
        Exporter.SaveRaw(n);
    }

    private void ExportFolder_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedNode is not { IsContainer: true } n) { Log.Warn("Select a folder or archive to extract."); return; }
        Exporter.ExtractTree(n);
    }

    private void Survey_Click(object sender, RoutedEventArgs e)
    {
        if (_gameRoot is null) { Log.Warn("Open a game folder first."); return; }
        var existing = _docs.FirstOrDefault(d => d.Content is SurveyView);
        if (existing is not null) { Docs.SelectedItem = existing; return; }
        AddTab(new DocumentTab { Title = "Format Survey", Glyph = "", Content = new SurveyView(_gameRoot) });
    }

    private void LogToggle_Click(object sender, RoutedEventArgs e)
    {
        var show = LogToggle.IsChecked == true;
        LogRow.Height = show ? new GridLength(170) : new GridLength(0);
        LogSplitRow.Height = show ? new GridLength(4) : new GridLength(0);
    }

    private void PropsToggle_Click(object sender, RoutedEventArgs e)
    {
        var show = PropsToggle.IsChecked == true;
        PropsCol.Width = show ? new GridLength(330) : new GridLength(0);
        PropsSplitCol.Width = show ? new GridLength(4) : new GridLength(0);
    }

    private void ClearLog_Click(object sender, RoutedEventArgs e) => Log.Lines.Clear();

    // ---------------------------------------------------------------- editing: project, undo, export

    private EditSession? _teamRefreshSession;
    private int _teamRefreshChange = -1;
    private Action? _refreshTeamGrid;

    private void RefreshTeamAssignments()
    {
        if (_searchIndex is null || _categoryRoots?.FirstOrDefault(r => r.Name == "Teams") is not { } root) return;
        var profiles = root.Children.Where(c => c.Team is not null).ToList();
        if (profiles.Count == 0) return;
        var session = Editor.Session;
        int change = session?.History.LastOrDefault()?.Id ?? 0;
        if (ReferenceEquals(session, _teamRefreshSession) && change == _teamRefreshChange) return;
        bool sameSession = _teamRefreshSession is null || ReferenceEquals(session, _teamRefreshSession);
        _teamRefreshSession = session; _teamRefreshChange = change;
        var bytes = profiles[0].Team!.Roster.ReadAll();
        var updated = TeamAssetCatalog.Build(_searchIndex, bytes).ToDictionary(p => p.Team.RosterIndex);
        foreach (var item in profiles)
        {
            var profile = item.Team!;
            if (!updated.TryGetValue(profile.Team.RosterIndex, out var fresh)) continue;
            profile.Team = fresh.Team; profile.Assets.Clear(); profile.Assets.AddRange(fresh.Assets);
            profile.Players.Clear(); profile.Players.AddRange(fresh.Players);
            profile.SharedAssets = fresh.SharedAssets; profile.WorkspaceProblems = fresh.WorkspaceProblems;
            profile.StadiumCode = fresh.StadiumCode; profile.StadiumName = fresh.StadiumName;
            profile.RegisteredVenue = fresh.RegisteredVenue; profile.StadiumEvidence = fresh.StadiumEvidence;
            item.Children.Clear();
            foreach (var group in profile.Assets.GroupBy(a => a.Category))
                item.Children.Add(new TreeItem(group.Key, 2, group.Select(a => new TreeItem(a.Asset, 3) { SchoolScope = profile })));
            if (_teamTabs.TryGetValue(profile, out var tab) && _docs.Contains(tab))
            {
                var selected = (tab.Content as TeamView)?.SelectedSection ?? 0;
                var playerSlot = (tab.Content as TeamView)?.SelectedPlayerSlot ?? 1;
                var uniformState = sameSession ? (tab.Content as TeamView)?.UniformState : null;
                var playerState = sameSession ? (tab.Content as TeamView)?.PlayerState : null;
                tab.Content = new TeamView(profile, uniformState: uniformState, playerState: playerState) { SelectedSection = selected, SelectedPlayerSlot = playerSlot };
                tab.Props = [new("Team", "Uniform ID", profile.Team.UniformId.ToString()), new("Team", "Logo ID", profile.Team.LogoId.ToString()), new("Stadium", "Evidence", profile.StadiumEvidence)];
                if (Docs.SelectedItem == tab) ShowProps(tab.Props);
            }
        }
        _refreshTeamGrid?.Invoke();
    }


    private void OnEditorChanged()
    {
        InvalidateDependencies();
        RefreshTeamAssignments();
        var s = Editor.Session;
        var dirty = s?.IsDirty == true;
        DirtyBadge.Visibility = dirty ? Visibility.Visible : Visibility.Collapsed;
        ProjectLabel.Text = s?.ProjectPath is { } pp ? Path.GetFileName(pp) : "Untitled project";
        Title = (dirty ? "● " : "") + AppInfo.Name + " " + AppInfo.Version + (s?.ProjectPath is { } p2 ? " — " + Path.GetFileNameWithoutExtension(p2) : "");
        UndoButton.IsEnabled = s?.CanUndo == true;
        RedoButton.IsEnabled = s?.CanRedo == true;
        UndoButton.ToolTip = s?.UndoLabel is { } u ? $"Undo: {u} (Ctrl+Z)" : "Undo (Ctrl+Z)";
        RedoButton.ToolTip = s?.RedoLabel is { } r ? $"Redo: {r} (Ctrl+Y)" : "Redo (Ctrl+Y)";
        var pending = (s?.ModifiedAssetCount ?? 0) + (s?.PreviewOnlyCount ?? 0);
        ChangesLabel.Text = pending == 0 ? "Changes" : $"Changes ({s!.ModifiedAssetCount}{(s.PreviewOnlyCount > 0 ? $" + {s.PreviewOnlyCount} preview" : "")})";
        CommandManager.InvalidateRequerySuggested();
        foreach (var d in _docs) d.IsEdited = d.Node is { } n && s?.IsAffected(n) == true;
        foreach (var (profile, tab) in _teamTabs) if (_docs.Contains(tab)) tab.IsEdited = s is not null && profile.Assets.Any(a => s.IsAffected(a.Asset));
        foreach (var root in _roots) root.RefreshEdited(s);
        if (_categoryRoots is not null && BrowseMode.SelectedIndex != 0) foreach (var root in _categoryRoots) root.RefreshEdited(s);
    }

    private void OnClosing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        if (!ConfirmDiscard("Close the tool")) e.Cancel = true;
    }

    /// <summary>Ask what to do with unsaved project changes. Returns false when the user cancels.</summary>
    public bool ConfirmDiscard(string action)
    {
        if (!CheckPlayerDrafts()) return false;
        if (Editor.Session is not { IsDirty: true } s) return true;
        var answer = MessageBox.Show(this, $"The project has unsaved changes to {s.ModifiedAssetCount} asset(s).\n\nSave the project before you continue?\n(Yes = save, No = discard the changes, Cancel = stay)", action, MessageBoxButton.YesNoCancel, MessageBoxImage.Warning);
        if (answer == MessageBoxResult.Cancel) return false;
        if (answer == MessageBoxResult.Yes) return SaveProject(false);
        return true;
    }

    public bool SaveProject(bool saveAs)
    {
        if (!CheckPlayerDrafts()) return false;
        if (Editor.Session is not { } s || _gameRoot is null) { Log.Warn("Open a game folder first."); return false; }
        var path = s.ProjectPath;
        if (path is null || saveAs)
        {
            var parent = Directory.GetParent(Path.TrimEndingDirectorySeparator(_gameRoot.DiskPath!))?.FullName ?? _gameRoot.DiskPath!;
            var dlg = new SaveFileDialog { Title = "Save editing project", Filter = "MVP-Workshop project|*" + ProjectFile.Extension, FileName = "mvpw_mod" + ProjectFile.Extension, InitialDirectory = parent, DefaultExt = ProjectFile.Extension };
            if (dlg.ShowDialog(this) != true) return false;
            path = dlg.FileName;
        }
        try
        {
            ProjectFile.Save(s, path);
            _settings.LastProject = path; _settings.Save();
            Log.Info($"Project saved: {path} ({s.ModifiedAssetCount} edited asset(s)). Game files untouched.");
            SetStatus($"Saved {Path.GetFileName(path)}");
            if (s.PreviewOnlyCount > 0) Log.Warn($"{s.PreviewOnlyCount} preview-only change(s) are not part of the project: they cannot be written into game files.");
            return true;
        }
        catch (Exception ex) { Log.Error($"Could not save the project: {ex.Message}"); MessageBox.Show(this, ex.Message, "Save project", MessageBoxButton.OK, MessageBoxImage.Error); return false; }
    }

    /// <summary>Save to an explicit path without asking (self-test and scripting).</summary>
    public bool SaveProjectTo(string path)
    {
        if (_docs.Select(d => d.Content).OfType<TeamView>().Any(v => v.PlayerState.PendingFields > 0)) throw new InvalidOperationException("Apply or reset pending player drafts before saving.");
        if (Editor.Session is not { } s) return false;
        ProjectFile.Save(s, path);
        return true;
    }

    private void OpenProject_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog { Title = "Open editing project", Filter = "MVP-Workshop project|*" + ProjectFile.Extension + ";*" + ProjectFile.LegacyExtension + "|All files|*.*", InitialDirectory = _settings.LastProject is { } lp ? Path.GetDirectoryName(lp) : "" };
        if (dlg.ShowDialog(this) == true) LoadProject(dlg.FileName);
    }

    public void LoadProject(string path)
    {
        if (!ConfirmDiscard("Open another project")) return;
        try
        {
            var game = ProjectFile.PeekGameFolder(path);
            if (_gameRoot is null || game is not null && !string.Equals(Path.TrimEndingDirectorySeparator(game), Path.TrimEndingDirectorySeparator(_gameRoot.DiskPath ?? ""), StringComparison.OrdinalIgnoreCase))
            {
                if (game is null || !(Directory.Exists(game) || File.Exists(game))) { MessageBox.Show(this, $"The project's game folder was not found:\n{game}", "Open project", MessageBoxButton.OK, MessageBoxImage.Warning); return; }
                LoadGame(game, confirm: false);
                if (_gameRoot is null || !string.Equals(_gameRoot.DiskPath, Path.GetFullPath(game), StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("Could not open the project's game source.");
            }
            var session = ProjectFile.Load(path, _gameRoot!, out var warnings);
            foreach (var w in warnings) Log.Warn(w);
            Editor.Attach(session);
            _settings.LastProject = path; _settings.Save();
            Log.Info($"Project opened: {path} — {session.ModifiedAssetCount} edited asset(s)");
            if (warnings.Count > 0) MessageBox.Show(this, string.Join("\n\n", warnings.Take(8)) + (warnings.Count > 8 ? $"\n…and {warnings.Count - 8} more (see the log)" : ""), "Project opened with warnings", MessageBoxButton.OK, MessageBoxImage.Warning);
            ShowChanges_Click(this, new RoutedEventArgs());
        }
        catch (Exception ex) { Log.Error($"Could not open the project: {ex.Message}"); MessageBox.Show(this, ex.Message, "Open project", MessageBoxButton.OK, MessageBoxImage.Error); }
    }

    private void SaveProject_Click(object sender, RoutedEventArgs e) => SaveProject(false);
    private void Undo_Click(object sender, RoutedEventArgs e) => Editor.Session?.Undo();
    private void Redo_Click(object sender, RoutedEventArgs e) => Editor.Session?.Redo();
    private void ExportMod_Click(object sender, RoutedEventArgs e) => ExportMod();

    public void ShowChanges_Click(object sender, RoutedEventArgs e)
    {
        var existing = _docs.FirstOrDefault(d => d.Content is ChangesView);
        if (existing is not null) { Docs.SelectedItem = existing; return; }
        AddTab(new DocumentTab { Title = "Changes", Glyph = "", Content = new ChangesView() });
    }

    public void ChooseOutputFolder()
    {
        if (Editor.Session is not { } s) return;
        var dlg = new OpenFolderDialog { Title = "Choose the mod output folder (must be outside the game folder)", InitialDirectory = Directory.Exists(s.OutputFolder) ? s.OutputFolder : Directory.GetParent(s.OutputFolder)?.FullName ?? "" };
        if (dlg.ShowDialog(this) != true) return;
        if (ModExporter.OutputProblem(s.GameRoot, dlg.FolderName) is { } why) { MessageBox.Show(this, why, "Output folder", MessageBoxButton.OK, MessageBoxImage.Warning); return; }
        s.OutputFolder = dlg.FolderName;
        _settings.OutputFolders[s.GameRoot.DiskPath!] = dlg.FolderName; _settings.Save();
        OnEditorChanged();
    }

    private bool _exporting;

    /// <summary>Rebuild every edited archive into the output folder, re-open the results and compare them with the originals.</summary>
    public async void ExportMod()
    {
        if (!CheckPlayerDrafts()) return;
        if (Editor.Session is not { } s) { Log.Warn("Open a game folder first."); return; }
        if (_exporting) return;
        if (s.Overrides.Count == 0)
        {
            MessageBox.Show(this, s.PreviewOnlyCount > 0 ? "Nothing can be exported: the only changes are preview-only (they cannot be written into game files)." : "There are no edited assets to export.", "Export Mod", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        if (ModExporter.OutputProblem(s.GameRoot, s.OutputFolder) is { } bad) { MessageBox.Show(this, bad, "Export Mod", MessageBoxButton.OK, MessageBoxImage.Warning); return; }

        var files = s.Overrides.Keys.Select(k => s.GameRoot.Resolve(k)).Where(n => n is not null).Select(n => ModExporter.RelativePath(s.GameRoot, ModExporter.DiskFileOf(n!))).Distinct().Order().ToList();
        var preview = s.PreviewOnlyCount > 0 ? $"\n\n{s.PreviewOnlyCount} preview-only change(s) will NOT be exported." : "";
        var ask = MessageBox.Show(this, $"Rebuild {files.Count} file(s) from {s.Overrides.Count} edited asset(s) into:\n{s.OutputFolder}\n\n{string.Join("\n", files.Take(12))}{(files.Count > 12 ? $"\n…and {files.Count - 12} more" : "")}\n\nThe original game files are not modified. Each rebuilt archive is re-opened and compared with the original.{preview}", "Export Mod", MessageBoxButton.OKCancel, MessageBoxImage.Question);
        if (ask != MessageBoxResult.OK) return;

        _exporting = true;
        Mouse.OverrideCursor = Cursors.AppStarting;
        try
        {
            var report = await Task.Run(() => ModExporter.Export(s, s.OutputFolder, verify: true, msg => Dispatcher.BeginInvoke(() => SetStatus(msg))));
            foreach (var f in report.Files) Log.Info($"Exported {f.RelativePath} — {Bin.Size(f.Size)}, {f.EditedAssets} edited asset(s), {f.ComparedAssets:N0} entries verified");
            foreach (var p in report.Problems) Log.Error("Export: " + p);
            SetStatus(report.Ok ? $"Exported {report.Files.Count} file(s) to {report.OutputFolder}" : "Export finished with problems — see the log");
            var text = report.Ok
                ? $"Exported and verified {report.Files.Count} file(s):\n\n{string.Join("\n", report.Files.Select(f => $"  {f.RelativePath}  ({Bin.Size(f.Size)}, {f.ComparedAssets:N0} entries compared)"))}\n\nEvery rebuilt archive re-opened with identical contents except your edits.{(report.PreviewOnlySkipped > 0 ? $"\n\n{report.PreviewOnlySkipped} preview-only change(s) were not exported." : "")}\n\nOpen the output folder?"
                : $"The export completed with {report.Problems.Count} problem(s):\n\n{string.Join("\n", report.Problems.Take(10))}";
            if (report.Ok)
            {
                if (MessageBox.Show(this, text, "Export Mod", MessageBoxButton.YesNo, MessageBoxImage.Information) == MessageBoxResult.Yes)
                    System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(report.OutputFolder) { UseShellExecute = true });
            }
            else MessageBox.Show(this, text, "Export Mod", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        catch (Exception ex) { Log.Error("Export failed: " + ex.Message); MessageBox.Show(this, ex.Message, "Export Mod", MessageBoxButton.OK, MessageBoxImage.Error); }
        finally { _exporting = false; Mouse.OverrideCursor = null; }
    }

    private void Minimize(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;
    private void Maximize(object sender, RoutedEventArgs e) => WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
    private void CloseWindow(object sender, RoutedEventArgs e) => Close();
}

public static class VisualTreeExt
{
    public static T? FindAncestor<T>(this DependencyObject? d) where T : DependencyObject
    {
        while (d is not null && d is not T) d = d is System.Windows.Media.Visual ? System.Windows.Media.VisualTreeHelper.GetParent(d) : LogicalTreeHelper.GetParent(d);
        return d as T;
    }
}




