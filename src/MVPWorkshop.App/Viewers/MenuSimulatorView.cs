using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using MVPWorkshop.Core.Formats;
using MVPWorkshop.Core.Vfs;

namespace MVPWorkshop.App.Viewers;

/// <summary>Preview-only host. It does not replace unresolved native calls with guessed game behavior.</summary>
public sealed class MenuSimulatorView : Grid
{
    private readonly AssetNode _initial;
    internal readonly MenuSimulation Session = new();
    private readonly ContentControl _screen = new();
    private readonly ComboBox _destination = new() { Width = 320, DisplayMemberPath = "Path" };
    private readonly ComboBox _focus = new() { Width = 300 };
    private readonly ComboBox _home = new() { Width = 150, DisplayMemberPath = "Label" };
    private readonly ComboBox _away = new() { Width = 150, DisplayMemberPath = "Label" };
    private readonly ComboBox _player = new() { Width = 220, DisplayMemberPath = "Name" };
    private readonly TextBox _homeScore = new() { Text = "3", Width = 35 };
    private readonly TextBox _awayScore = new() { Text = "2", Width = 35 };
    private readonly TextBox _bindings = new() { AcceptsReturn = true, Height = 85, VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
        Text = "# Explicit preview bindings: field path/name=context key\n# e.g. menu/homeTeam=home.name" };
    private readonly TextBox _trace = new() { IsReadOnly = true, AcceptsReturn = true, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
    private readonly TextBlock _data = B.Text("Decoded context loading…", "Muted");
    private readonly CheckBox _popup = new() { Content = "Popup (modal)" };
    private readonly CheckBox _play = new() { Content = "Run timelines", IsChecked = true };
    private readonly CheckBox _controller = new() { Content = "Controller (XInput)" };
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMilliseconds(33) };
    private readonly List<AssetNode> _nodes = [];
    private readonly Dictionary<string, string> _context = [];
    private bool _ready, _sync, _refreshing;
    private ushort _lastButtons;
    private string _shown = "";
    private string _editSignature = "";
    private FrameworkElement? _view;
    private FrameworkElement? _underlay;
    private readonly Dictionary<int, FrameworkElement> _popupBackdrops = [];
    private int _shownDepth;
    private string? _restoreFocus;

    public MenuSimulatorView(AssetNode initial)
    {
        _initial = initial; Focusable = true;
        void TeamTemplate(ComboBox box)
        {
            box.DisplayMemberPath = "";
            var text = new FrameworkElementFactory(typeof(TextBlock));
            text.SetBinding(TextBlock.TextProperty, new System.Windows.Data.Binding("Label"));
            box.ItemTemplate = new DataTemplate { VisualTree = text };
        }
        TeamTemplate(_home); TeamTemplate(_away);
        var navigation = B.Toolbar(B.Button("Capture input", null, (_, _) => Keyboard.Focus(this), "Outline"),
            B.Button("↑", null, (_, _) => Move(0, -1), "Outline"), B.Button("↓", null, (_, _) => Move(0, 1), "Outline"),
            B.Button("←", null, (_, _) => Move(-1, 0), "Outline"), B.Button("→", null, (_, _) => Move(1, 0), "Outline"),
            B.Button("Activate", null, (_, _) => Activate(), "Outline"), B.Button("Back / close", null, (_, _) => Back(), "Outline"),
            B.Button("Reset", null, (_, _) => { Session.Reset(initial.Path); ShowScreen(); }, "Outline"), _play, _controller);
        var routes = B.Toolbar(_destination, _popup,
            B.Button("Open", null, (_, _) => OpenSelected(), "Outline"),
            B.Button("Connect focused → target", null, (_, _) => Connect(), "Outline"),
            B.Button("Clear routes", null, (_, _) => { Session.Routes.Clear(); Session.Log("simulated service", "routes cleared"); }, "Outline"));
        var context = B.Toolbar(B.Text("Home"), _home, B.Text("Away"), _away, _player, B.Text("Score (simulated)"), _homeScore, _awayScore,
            B.Button("Apply context / bindings", null, (_, _) => ApplyContext(), "Outline"));
        var inspector = B.Rows((B.Text("Focus (arrows / Enter / Escape; XInput D-pad / A / B)"), B.Auto), (_focus, B.Auto),
            (_data, B.Auto), (_bindings, B.Auto), (B.Text("Navigation / native event / service trace"), B.Auto), (_trace, B.Star()),
            (B.Toolbar(B.Button("Clear trace", null, (_, _) => { Session.Trace.Clear(); UpdateTrace(); }, "Outline"),
                B.Button("Export trace", null, (_, _) => ExportTrace(), "Outline")), B.Auto));
        Children.Add(B.Rows((navigation, B.Auto), (routes, B.Auto), (context, B.Auto),
            (B.Cols((_screen, B.Star()), (B.VSplit(), B.Px(4)), (inspector, B.Px(340))), B.Star())));
        Session.Changed += UpdateTrace;
        _focus.SelectionChanged += (_, _) => { if (!_sync && _focus.SelectedItem is string id) { _restoreFocus = null; var previous = Session.Focus; Session.Select(id); FocusEvents(previous); Refresh(); } };
        _home.SelectionChanged += (_, _) => { _player.ItemsSource = (_home.SelectedItem as TeamProfile)?.Players; _player.SelectedIndex = 0; };
        PreviewKeyDown += (_, e) =>
        {
            if (e.OriginalSource is TextBox || e.OriginalSource is ComboBox) return;
            if (e.Key == Key.Up) Move(0, -1); else if (e.Key == Key.Down) Move(0, 1);
            else if (e.Key == Key.Left) Move(-1, 0); else if (e.Key == Key.Right) Move(1, 0);
            else if (e.Key is Key.Enter or Key.Space) Activate(); else if (e.Key == Key.Escape) Back(); else return;
            e.Handled = true;
        };
        _timer.Tick += (_, _) =>
        {
            if (!IsVisible || !_ready) return;
            if (_play.IsChecked == true) { if (_view is AptSceneView apt) apt.TickSimulation(); if (_view is FelView fel) fel.TickSimulation(); }
            SyncFocus();
            if (_controller.IsChecked == true && IsKeyboardFocusWithin) PollController();
        };
        Loaded += async (_, _) =>
        {
            Editor.Changed -= OnEdit; Editor.Changed += OnEdit;
            if (!_ready)
            {
                try
                {
                    var root = initial; while (root.Parent is not null) root = root.Parent;
                    _nodes.AddRange(MainWindow.Instance is { } window ? await window.GetAssetIndex() : root.EnumerateDeep().ToList());
                    _destination.ItemsSource = _nodes.Where(n => n.Extension is ".fel" or ".apt").OrderBy(n => n.Path).ToList();
                    await LoadContext();
                    _ready = true; Session.Reset(initial.Path); ShowScreen();
                    _editSignature = EditSignature();
                }
                catch (Exception ex) { Session.Log("diagnostic", ex.Message); }
            }
            OnEdit();
            _timer.Start();
        };
        Unloaded += (_, _) => { _timer.Stop(); Editor.Changed -= OnEdit; };
    }
    private async Task LoadContext()
    {
        var home = (_home.SelectedItem as TeamProfile)?.Label; var away = (_away.SelectedItem as TeamProfile)?.Label; var player = (_player.SelectedItem as TeamPlayer)?.Key;
        var teams = await Task.Run(() => TeamAssetCatalog.Build(_nodes));
        _home.ItemsSource = teams; _away.ItemsSource = teams;
        _home.SelectedItem = teams.FirstOrDefault(t => t.Label == home) ?? teams.FirstOrDefault();
        _away.SelectedItem = teams.FirstOrDefault(t => t.Label == away) ?? teams.Skip(1).FirstOrDefault() ?? teams.FirstOrDefault();
        if ((_home.SelectedItem as TeamProfile)?.Players.FirstOrDefault(p => p.Key == player) is { } selected) _player.SelectedItem = selected;
        ApplyContext();
    }
    private void ApplyContext()
    {
        _context.Clear();
        void Team(string prefix, TeamProfile? team)
        {
            _context[prefix + ".name"] = team?.Team.Name ?? "";
            _context[prefix + ".abbreviation"] = team?.Team.Abbreviation ?? "";
            _context[prefix + ".stadium"] = team?.StadiumName ?? "";
            _context[prefix + ".nickname"] = team?.Team.Nickname ?? "";
        }
        Team("home", _home.SelectedItem as TeamProfile); Team("away", _away.SelectedItem as TeamProfile);
        _context["player.name"] = (_player.SelectedItem as TeamPlayer)?.Name ?? "";
        _context["player.jersey"] = (_player.SelectedItem as TeamPlayer)?.Jersey ?? "";
        _context["player.position"] = (_player.SelectedItem as TeamPlayer)?.Position ?? "";
        _context["home.score"] = int.TryParse(_homeScore.Text, out var h) ? Math.Clamp(h, 0, 999).ToString() : "0";
        _context["away.score"] = int.TryParse(_awayScore.Text, out var a) ? Math.Clamp(a, 0, 999).ToString() : "0";
        _data.Text = "Decoded roster/DAT/stadium context; scores simulated.\n" + string.Join("\n", _context.Select(k => k.Key + " = " + k.Value));
        Session.Log("simulated service", "preview context applied through explicit text bindings"); Refresh();
    }
    private string BindText(string field, string authored)
    {
        foreach (var line in _bindings.Text.Split('\n'))
        {
            var parts = line.Trim().Split('=', 2);
            if (parts.Length == 2 && parts[0].Trim() == field) return _context.GetValueOrDefault(parts[1].Trim(), authored);
        }
        return authored;
    }
    internal void ShowScreen()
    {
        _restoreFocus = Session.Focus;
        var node = _nodes.FirstOrDefault(n => n.Path == Session.Screen) ?? _initial;
        if (_shown != Session.Screen)
        {
            _underlay = Session.IsPopup ? _popupBackdrops.GetValueOrDefault(Session.NavigationDepth) : null;
            if (Session.NavigationDepth == 0) _popupBackdrops.Clear();
            if (Session.IsPopup && Session.NavigationDepth > _shownDepth && _screen.ActualWidth > 0 && _screen.ActualHeight > 0)
            {
                var bitmap = new System.Windows.Media.Imaging.RenderTargetBitmap((int)Math.Ceiling(_screen.ActualWidth), (int)Math.Ceiling(_screen.ActualHeight), 96, 96, System.Windows.Media.PixelFormats.Pbgra32);
                bitmap.Render(_screen); bitmap.Freeze();
                _underlay = new Image { Source = bitmap, IsHitTestVisible = false };
                _popupBackdrops[Session.NavigationDepth] = _underlay;
            }
        }
        _shown = Session.Screen; _shownDepth = Session.NavigationDepth;
        if (_view is not null) _screen.Content = null;
        if (_underlay?.Parent is Panel old) old.Children.Remove(_underlay);
        if (node.Extension == ".apt")
        {
            var constants = AptView.Sibling(node, ".const") is { } c ? AptConstants.Read(c.ReadAll()) : null;
            _view = new AptSceneView(node, AptFile.Read(node.ReadAll(), constants?.MovieOffset ?? 0)) { Simulation = Session, PreviewText = BindText };
        }
        else _view = new FelView(node, _ => { }, simulator: true) { Simulation = Session, PreviewText = BindText };
        if (Session.IsPopup && _underlay is not null)
        {
            var stack = new Grid(); _underlay.IsEnabled = false; stack.Children.Add(_underlay);
            var modal = new Border { Background = System.Windows.Media.Brushes.Black, Margin = new Thickness(30), Child = _view };
            stack.Children.Add(modal); _screen.Content = stack;
        }
        else _screen.Content = _view;
        Session.Log("simulated service", "screen mounted " + node.Path); Refresh();
    }
    private void Refresh()
    {
        if (_view is AptSceneView apt) apt.RefreshSimulation(); if (_view is FelView fel) fel.RefreshSimulation(); SyncFocus();
    }
    private void SyncFocus()
    {
        _sync = true;
        if (_restoreFocus is { } restore && Session.Controls.Any(c => c.Id == restore)) { Session.Select(restore); _restoreFocus = null; }
        var ids = Session.Controls.Select(c => c.Id).ToArray();
        if (_focus.ItemsSource is not string[] prior || !prior.SequenceEqual(ids)) _focus.ItemsSource = ids;
        _focus.SelectedItem = Session.Focus; _sync = false;
    }
    internal void Move(int x, int y)
    {
        _restoreFocus = null;
        var previous = Session.Focus; Session.Move(x, y);
        FocusEvents(previous);
        Refresh();
    }
    private void FocusEvents(string? previous)
    {
        if (_view is AptSceneView apt && previous != Session.Focus)
        {
            if (previous is not null) apt.SimulationEvent(previous, "onRollOut");
            if (Session.Focus is { } next) apt.SimulationEvent(next, "onRollOver");
        }
    }
    internal void Activate()
    {
        if (_view is AptSceneView apt) apt.ActivateSimulation(); if (_view is FelView fel) fel.ActivateSimulation();
        if (Session.Activate()) ShowScreen(); else Session.Log("diagnostic", "No explicit service route for focused control");
        Refresh();
    }
    internal void Back() { if (Session.Back()) ShowScreen(); }
    private void OpenSelected() { if (_destination.SelectedItem is AssetNode node) { Session.Open(node.Path, _popup.IsChecked == true); ShowScreen(); } }
    private void Connect()
    {
        if (Session.Focus is not { } focus || _destination.SelectedItem is not AssetNode node) { Session.Log("diagnostic", "Select a focused control and target first"); return; }
        Session.Routes.RemoveAll(r => r.Screen == Session.Screen && r.Control == focus);
        Session.Routes.Add(new(Session.Screen, focus, node.Path, _popup.IsChecked == true));
        Session.Log("simulated service", $"route {focus} → {node.Path}");
    }
    internal void SetPlayback(bool play) => _play.IsChecked = play;
    internal void SetPreviewBinding(string field, string key) { _bindings.Text = field + "=" + key; ApplyContext(); }
    internal bool Ready => _ready;
    internal FrameworkElement? CurrentPreview => _view;
    internal string ContextSummary => _data.Text;
    private void UpdateTrace() { _trace.Text = string.Join("\n", Session.Trace); _trace.ScrollToEnd(); }
    private void ExportTrace()
    {
        var dialog = new Microsoft.Win32.SaveFileDialog { Filter = "Text|*.txt", FileName = "menu-simulation-trace.txt" };
        if (dialog.ShowDialog() == true) File.WriteAllText(dialog.FileName, string.Join("\n", Session.Trace));
    }
    private string EditSignature() => string.Join("|", (Editor.Session?.History.Select(c => c.Id.ToString()) ?? []).Concat(Editor.Session?.Overrides.Select(c => c.Key + Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(c.Value))) ?? []));
    private async void OnEdit()
    {
        if (!_ready || _refreshing) return;
        // Project saves also raise Changed. Only content changes rebuild the session.
        var signature = EditSignature(); if (signature == _editSignature) return;
        _editSignature = signature; _refreshing = true;
        try { await LoadContext(); Session.Log("simulated service", "live project edit: runtime reset, routes retained"); ShowScreen(); }
        catch (Exception ex) { Session.Log("diagnostic", "Live refresh: " + ex.Message); }
        finally { _refreshing = false; }
    }
    [StructLayout(LayoutKind.Sequential)] private struct Gamepad { public ushort Buttons; public byte LeftTrigger, RightTrigger; public short LX, LY, RX, RY; }
    [StructLayout(LayoutKind.Sequential)] private struct ControllerState { public uint Packet; public Gamepad Pad; }
    [DllImport("xinput1_4.dll")] private static extern uint XInputGetState(uint index, out ControllerState state);
    private void PollController()
    {
        try
        {
            if (XInputGetState(0, out var state) != 0) { _lastButtons = 0; return; }
            var pressed = (ushort)(state.Pad.Buttons & ~_lastButtons); _lastButtons = state.Pad.Buttons;
            if ((pressed & 1) != 0) Move(0, -1); if ((pressed & 2) != 0) Move(0, 1);
            if ((pressed & 4) != 0) Move(-1, 0); if ((pressed & 8) != 0) Move(1, 0);
            if ((pressed & 0x1000) != 0) Activate(); if ((pressed & 0x2000) != 0) Back();
        }
        catch (DllNotFoundException) { _controller.IsChecked = false; Session.Log("diagnostic", "XInput unavailable"); }
    }
}
