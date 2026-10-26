using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using MVPWorkshop.Core.Vfs;
using MVPWorkshop.Core.Editing;
using MVPWorkshop.Core.Formats;

namespace MVPWorkshop.App.Viewers;

public static class TeamWorkspaceView
{
    public static UIElement SchoolInfo(TeamProfile profile)
    {
        var team = profile.Team;
        var panel = new StackPanel { Margin = new Thickness(18) };
        panel.Children.Add(B.Text(team.Name + " · school info", "Text", 22, FontWeights.SemiBold));
        void Info(string name, string value)
        {
            panel.Children.Add(B.Cols((Wrap(name, "Muted"), B.Px(170)), (Wrap(value, "Text"), B.Star())));
        }
        Info("Abbreviation", team.Abbreviation); Info("Short name", team.ShortName);
        Info("Nickname", team.Nickname); Info("City", team.City);
        Info("Roster slots", team.PlayerCount.ToString()); Info("Uniform / logo IDs", $"{team.UniformId} / {team.LogoId}");
        Info("Stadium", profile.StadiumEvidence);
        Info("Source", $"{profile.Roster.Path} · record {team.RosterIndex} @0x{team.Offset:X}");
        panel.Children.Add(Wrap("School identity and lookup rows are shown for this school only. Edit decoded asset assignments in the school's Edit assignments section. Lookup IDs retain their source meaning.", "Muted"));
        foreach (var link in profile.Assets.Where(a => a.Asset.Name is "teaminfo.csv" or "schoolnameaudio.csv" or "nicknameaudio.csv"))
        {
            string wanted = link.Asset.Name == "teaminfo.csv" ? team.Abbreviation : link.Asset.Name == "nicknameaudio.csv" ? team.Nickname : team.Name;
            var lines = System.Text.Encoding.Latin1.GetString(link.Asset.ReadAll()).Split('\n');
            var matches = lines.Select((line, index) => (line, index)).Where(r => link.Asset.Name == "teaminfo.csv" ? r.line.Split(',')[0].Trim().Equals(wanted, StringComparison.OrdinalIgnoreCase) : r.line.Split(',').Any(c => c.Trim().Equals(wanted, StringComparison.OrdinalIgnoreCase))).ToList();
            panel.Children.Add(B.Text(link.Asset.Name, "Text", 14));
            if (matches.Count == 0) panel.Children.Add(Wrap("No confirmed matching row for this school.", "Muted"));
            foreach (var match in matches)
            {
                panel.Children.Add(Wrap($"Source line {match.index + 1}", "Muted"));
                var values = match.line.TrimEnd('\r').Split(',');
                var labels = link.Asset.Name == "teaminfo.csv" ? lines[0].TrimEnd('\r').Split(',') : [];
                for (int i = 0; i < values.Length; i++) Info(i < labels.Length ? labels[i] : $"Source column {i + 1} (raw)", values[i]);
            }
        }
        return new ScrollViewer { Content = panel, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
    }
    public sealed class EditableField : Observable
    {
        public int Id { get; init; }
        public string Name { get; init; } = "";
        private string _value = "";
        public string Value { get => _value; set { if (Set(ref _value, value)) { Raise(nameof(IsChanged)); Raise(nameof(Change)); } } }
        private string _original = "";
        public string Original { get => _original; set { if (Set(ref _original, value)) { Raise(nameof(IsChanged)); Raise(nameof(Change)); } } }
        public bool IsChanged => Value != Original;
        public string Change => IsChanged ? "Edited" : "";
        public string Label => Name switch
        {
            "playerattrib_jerseynum" => "Jersey number", "playerattrib_primaryposition" => "Primary position ID",
            "playerattrib_secondaryposition" => "Secondary position ID", "playerattrib_facemorphindex" => "Face morph index",
            "playerattrib_boneprofile" => "Bone profile", "playerattrib_skintone" => "Skin tone",
            "playerattrib_eyecolour" => "Eye colour", "playerattrib_haircolour" => "Hair colour",
            "playerattrib_facialhair" => "Facial hair", "playerattrib_bodytype" => "Body type",
            "playerattrib_captype" => "Cap type", "playerattrib_capposition" => "Cap position",
            "playerattrib_eyeprotection" => "Eye protection", "playerattrib_battinghelmet" => "Batting helmet",
            "playerattrib_elbowguard" => "Elbow guard", "playerattrib_shinguard" => "Shin guard",
            "playerattrib_wristbandcolour" => "Wristband colour", "playerattrib_wristbandleftarm" => "Left wristband",
            "playerattrib_wristbandrightarm" => "Right wristband", "playerattrib_catchermask" => "Catcher mask",
            "playerattrib_audioid" => "Audio ID", "playerattrib_homelocation" => "Home location ID",
            _ => System.Globalization.CultureInfo.InvariantCulture.TextInfo.ToTitleCase(Name.Replace("playerattrib_", "").Replace("pitchattrib_", "").Replace("lrattrib_", "").Replace('_', ' '))
        };
        public bool Unknown { get; init; }
    }
    public static UIElement Overview(TeamProfile profile, Action<string> navigate)
    {
        var panel = new StackPanel { Margin = new Thickness(18) };
        panel.Children.Add(B.Text("School workspace", "Text", 22, FontWeights.SemiBold));
        panel.Children.Add(Wrap($"{profile.Players.Count} roster slots · {profile.Players.Count(p => p.Sources.Any(s => s.Asset.Name == "attrib.dat"))} player attribute records resolved · {profile.Assets.Count(a => a.Status.StartsWith("Confirmed"))} confirmed source links", "Muted"));
        void Section(string title, string text, string tab)
        {
            var card = new StackPanel { Margin = new Thickness(0, 12, 0, 4) };
            card.Children.Add(B.Button(title, null, (_, _) => navigate(tab), "Outline"));
            card.Children.Add(Wrap(text, "Muted"));
            panel.Children.Add(card);
        }
        Section("Roster & players", "Confirmed: each 8-byte roster slot supplies a little-endian player key joined to hexadecimal IDs in attrib.dat, lhattrib.dat, rhattrib.dat and pitcher.dat. Select a player, edit source-labeled fields and Apply to project. Packed slot flags remain unresolved.", "Roster");
        Section("Portraits", "Unresolved: the school asset ID is not a portrait ID. Browse the shared portrait library; no player portrait is asserted until the appearance-to-filename mapping is decoded.", "Portraits");
        Section("Equipment & appearance", "Player fields name cap, helmet, eye protection, guards, wristbands, socks, catcher mask and face settings. Their raw values are confirmed by the DAT header. Shared geometry and texture sources open in the existing viewers; exact selection rules remain unresolved.", "Equipment");
        Section("Uniforms & logos", $"Confirmed: roster uniform slot {profile.Team.UniformId:D3} and logo slot {profile.Team.LogoId:D3}. Open the dressed uniform preview, texture editors or logo sources.", "Uniforms");
        Section("Stadium assets", profile.StadiumEvidence, "Stadium");
        Section("Associated audio", "Player audioid and ditty values are available in player details. School/nickname lookup tables and audio sources can be opened below. The runtime join to speech events, music and stream indices remains unresolved, including schools without a named music file.", "Audio");
        foreach (var problem in profile.WorkspaceProblems) panel.Children.Add(Wrap(problem, "Muted"));
        return new ScrollViewer { Content = panel, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
    }

    public static UIElement Players(TeamProfile profile, out DataGrid playerGrid, int initialGroup = 0, PlayerWorkspaceState? state = null)
    {
        state ??= new PlayerWorkspaceState();
        var grid = B.Grid();
        playerGrid = grid;
        grid.Columns.Add(B.Col("Slot", nameof(TeamPlayer.Slot), 35));
        grid.Columns.Add(B.Col("Player key", nameof(TeamPlayer.Key), 80, true));
        grid.Columns.Add(B.Col("Name", nameof(TeamPlayer.Name), 105));
        grid.Columns.Add(B.Col("Jersey", nameof(TeamPlayer.Jersey), 45));
        grid.Columns.Add(B.Col("Pos. ID", nameof(TeamPlayer.Position), 60));
        grid.ToolTip = "Only players joined to this school's roster. Position values retain source IDs.";
        grid.ItemsSource = profile.Players;
        var details = new TabControl();
        void Select()
        {
            details.Items.Clear();
            if (grid.SelectedItem is not TeamPlayer player) return;
            foreach (var source in player.Sources.OrderBy(s => s.Asset.Name == "attrib.dat" ? 0 : 1))
            {
                var fields = B.Grid();
                fields.IsReadOnly = false;
                fields.CanUserAddRows = false;
                fields.CanUserDeleteRows = false;
                fields.BeginningEdit += (_, e) => { if (e.Row.Item is EditableField f && f.Unknown) e.Cancel = true; };
                fields.Columns.Add(B.Col("ID", "Id", 40));
                fields.Columns.Add(B.Col("Field", "Label", 145));
                fields.Columns[0].IsReadOnly = true;
                fields.Columns[1].IsReadOnly = true;
                var valueColumn = B.Col("Value (editable)", "Value", 100);
                valueColumn.Binding = new System.Windows.Data.Binding("Value") { Mode = System.Windows.Data.BindingMode.TwoWay, UpdateSourceTrigger = System.Windows.Data.UpdateSourceTrigger.PropertyChanged };
                fields.Columns.Add(valueColumn);
                fields.Columns.Add(B.Col("Original", "Original", 85)); fields.Columns[3].IsReadOnly = true;
                fields.Columns.Add(B.Col("Change", "Change", 65)); fields.Columns[4].IsReadOnly = true;
                fields.Columns.Add(B.Col("Source label", "Name", 230)); fields.Columns[5].IsReadOnly = true;
                var draftState = state.Get(source, player.Id);
                var baseline = draftState.Baseline;
                var current = PlayerAttributes.Read(baseline).Records[player.Id];
                var rows = draftState.Fields;
                string fieldQuery = "";
                var groups = new ComboBox { ItemsSource = new[] { "All fields", "Appearance", "Equipment", "Audio", "Ratings" }, SelectedIndex = initialGroup, MinWidth = 160, Margin = new Thickness(8) };
                var appearance = new HashSet<string>(new[] { "facemorphindex", "boneprofile", "skintone", "eyecolour", "haircolour", "sideburns", "facialhair", "bodytype" }.Select(n => "playerattrib_" + n));
                var equipment = new HashSet<string>(new[] { "captype", "capposition", "eyeprotection", "battinghelmet", "elbowguard", "wristbandcolour", "wristbandleftarm", "wristbandrightarm", "shinguard", "socks", "catchermask" }.Select(n => "playerattrib_" + n));
                void Filter()
                {
                    bool Include(int id)
                    {
                        var name = source.Labels.GetValueOrDefault(id, "");
                        return groups.SelectedIndex switch
                        {
                            1 => appearance.Contains(name) || name.StartsWith("playerattrib_facemorph", StringComparison.Ordinal),
                            2 => equipment.Contains(name),
                            3 => name is "playerattrib_audioid" or "playerattrib_ditty",
                            4 => source.Asset.Name == "attrib.dat" ? id is >= 31 and <= 40 || id == 44 : name.StartsWith("pitchattrib_", StringComparison.Ordinal) || name.StartsWith("lrattrib_", StringComparison.Ordinal),
                            _ => true
                        };
                    }
                    fields.ItemsSource = rows.Where(f => Include(f.Id) && ($"{f.Id} {f.Label} {f.Name}").Contains(fieldQuery, StringComparison.OrdinalIgnoreCase)).ToList();
                }
                groups.SelectionChanged += (_, _) => Filter();
                Filter();
                var title = new StackPanel { Margin = new Thickness(4) };
                title.Children.Add(Wrap($"{player.Name} · #{player.Jersey} · {source.Asset.Name} · key {player.Key}", "Text"));
                var actions = new WrapPanel(); actions.Children.Add(groups); title.Children.Add(actions);
                var status = Wrap("", "Muted");
                void UpdateStatus() => status.Text = $"{rows.Count(f => f.IsChanged)} edited fields here · {state.PendingFields} pending across this school. Apply before Save/Export. Enums and units retain source values.";
                UpdateStatus();
                // Attach while this grid is visible; old views must not retain UI event handlers.
                System.ComponentModel.PropertyChangedEventHandler changed = (_, _) => UpdateStatus();
                fields.Loaded += (_, _) => { foreach (var row in rows) row.PropertyChanged += changed; UpdateStatus(); };
                fields.Unloaded += (_, _) => { foreach (var row in rows) row.PropertyChanged -= changed; };
                var apply = B.Button("Apply player fields to project", null, (_, _) =>
                {
                    try
                    {
                        fields.CommitEdit(DataGridEditingUnit.Cell, true);
                        fields.CommitEdit(DataGridEditingUnit.Row, true);
                        var session = Editor.Session ?? throw new InvalidOperationException("No project session.");
                        var edits = rows.Where(f => !f.Unknown && f.Value != current.Field(f.Id)).ToDictionary(f => f.Id, f => f.Value);
                        if (edits.Count == 0) { status.Text = "No changed values."; return; }
                        var draft = PlayerAttributeEditor.ApplyDraft(baseline, session.GetBytes(source.Asset), player.Id, edits);
                        state.Remove(source, player.Id);
                        try { session.Commit(source.Asset, draft, $"{profile.Team.Name} / {player.Key} / {source.Asset.Name}: " + string.Join(", ", rows.Where(f => edits.ContainsKey(f.Id)).Select(f => $"{f.Name}: {current.Field(f.Id)} → {f.Value}"))); }
                        catch { state.Restore(source, player.Id, draftState); throw; }
                    }
                    catch (Exception ex) { status.Text = ex.Message; }
                }, "Primary");
                apply.Tag = "ApplyPlayerFields:" + source.Asset.Name;
                apply.IsEnabled = Editor.Session?.SaveBlocker(source.Asset) is null && Editor.Session is not null;
                actions.Children.Add(apply); title.Children.Add(status);
                var reset = B.Button("Reset draft", null, (_, _) => { state.Remove(source, player.Id); Select(); }, "Outline"); reset.Tag = "ResetPlayerDraft:" + source.Asset.Name;
                actions.Children.Add(reset);
                var fieldSearch = B.SearchBox("Search field names or IDs", q => { fieldQuery = q; Filter(); }, 190); fieldSearch.Tag = "PlayerFieldSearch";
                fieldSearch.Height = 30;
                title.Children.Insert(2, B.Toolbar(B.Text("Find fields", "Muted", 12), fieldSearch));
                var libraries = B.Button("Portrait sources (mapping unresolved)", null, (_, _) =>
                {
                    var tab = new DocumentTab { Title = $"{player.Key} portraits", Content = (FrameworkElement)Sources(profile.SharedAssets.Where(a => a.Category.StartsWith("Portraits")), $"Selected player {player.Key} · {player.Name}. Manual source browsing; automatic portrait selection unresolved.") };
                    MainWindow.Instance?.OpenWorkspaceDocument(tab);
                    if (MainWindow.Instance is { } window) window.Docs.SelectedItem = tab;
                }, "Outline");
                var previews = new StackPanel { Orientation = Orientation.Horizontal };
                previews.Children.Add(libraries);
                title.Children.Add(previews);
                previews.Children.Add(B.Button("3D sources (mapping unresolved)", null, (_, _) =>
                {
                    var tab = new DocumentTab { Title = $"{player.Key} 3D sources", Content = (FrameworkElement)Sources(profile.SharedAssets.Where(a => a.Category.StartsWith("Equipment") && a.Asset.Extension == ".ord"), $"Selected player {player.Key} · {player.Name}. Open a source in the existing 3D viewer; exact appearance/equipment selection unresolved.") };
                    MainWindow.Instance?.OpenWorkspaceDocument(tab);
                    if (MainWindow.Instance is { } window) window.Docs.SelectedItem = tab;
                }, "Outline"));
                details.Items.Add(new TabItem { Header = source.Asset.Name, Content = B.Rows((title, B.Auto), (fields, B.Star())) });
            }
            if (player.Sources.Count == 0) details.Items.Add(new TabItem { Header = "Unresolved", Content = Wrap(player.Evidence, "Muted") });
            details.SelectedIndex = 0;
        }
        grid.SelectionChanged += (_, _) => Select();
        grid.SelectedIndex = 0;
        var search = new StackPanel { Orientation = Orientation.Horizontal };
        search.Children.Add(Wrap("Filter players", "Muted"));
        search.Children.Add(B.SearchBox("Filter roster by name, jersey, position ID or player key", q => { grid.ItemsSource = profile.Players.Where(p => ($"{p.Name} {p.Key} {p.Jersey} {p.Position}").Contains(q, StringComparison.OrdinalIgnoreCase)).ToList(); if (grid.SelectedItem is null) grid.SelectedIndex = 0; }));
        var rosterPanel = B.Rows((search, B.Auto), (grid, B.Star()));
        return B.Rows((Wrap("Select a school player, edit values and Apply to project. Drafts stay while switching players and sections; apply them before closing the school or saving/exporting. Unknown fields are read-only.", "Muted"), B.Auto), (B.Cols((rosterPanel, B.Px(330)), (new GridSplitter { Width = 5, HorizontalAlignment = HorizontalAlignment.Stretch }, B.Px(5)), (details, B.Star())), B.Star()));
    }

    public static UIElement Sources(IEnumerable<TeamAssetLink> sources, string explanation, TeamProfile? school = null)
    {
        var links = sources.ToList();
        var grid = B.Grid();
        grid.Columns.Add(B.Col("Relationship", nameof(TeamAssetLink.Status), 180));
        grid.Columns.Add(new DataGridTextColumn { Header = "Asset", Binding = new System.Windows.Data.Binding("Asset.Name"), Width = 180 });
        grid.Columns.Add(B.Col("Evidence / limitation", nameof(TeamAssetLink.Evidence), 480));
        grid.Columns.Add(new DataGridTextColumn { Header = "Source path", Binding = new System.Windows.Data.Binding("Asset.Path"), Width = 550 });
        grid.ItemsSource = links;
        void Open() { if (grid.SelectedItem is TeamAssetLink link) { if (school is not null) MainWindow.Instance?.OpenSchoolAsset(school, link.Asset); else MainWindow.Instance?.OpenAsset(link.Asset); } }
        grid.MouseDoubleClick += (_, _) => Open();
        grid.KeyDown += (_, e) => { if (e.Key == Key.Enter) { Open(); e.Handled = true; } };
        var search = B.SearchBox("Filter source paths", q => grid.ItemsSource = links.Where(a => a.Asset.Path.Contains(q, StringComparison.OrdinalIgnoreCase)).ToList(), 280);
        var toolbar = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(8) };
        toolbar.Children.Add(Wrap("Filter paths", "Muted")); toolbar.Children.Add(search);
        var open = B.Button("Open selected", null, (_, _) => Open(), "Outline"); open.Tag = "OpenWorkspaceSource"; toolbar.Children.Add(open);
        return B.Rows((Wrap(explanation + $" · {links.Count:N0} source entries. Double-click or Enter opens the existing viewer/editor.", "Muted"), B.Auto), (toolbar, B.Auto), (grid, B.Star()));
    }

    private static TextBlock Wrap(string text, string color) => new() { Text = text, TextWrapping = TextWrapping.Wrap, Foreground = Ui.Res(color), Margin = new Thickness(8), FontSize = 12 };
}

