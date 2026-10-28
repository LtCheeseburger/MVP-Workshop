using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using MVPWorkshop.Core.Formats.Eagl;
using MVPWorkshop.Core.Vfs;
using NQuaternion = System.Numerics.Quaternion;
using NVector3 = System.Numerics.Vector3;
using NMatrix = System.Numerics.Matrix4x4;

namespace MVPWorkshop.App.Viewers;

/// <summary>Animation playback from ANIMS.BIG banks (EaglAnimBank) on the skinned model.</summary>
public sealed partial class ModelView
{
    private static readonly Dictionary<AssetNode, EaglAnimBank> BankCache = [];

    private EaglAnim? _anim, _animRoot;
    private float _frame;
    private bool _playing, _loop = true, _inPlace, _withRoot = true;
    private float _speed = 1;
    private DispatcherTimer? _playTimer;
    private readonly Stopwatch _clock = new();
    private readonly Slider _frameSlider = new() { Minimum = 0, Maximum = 1 };
    private readonly TextBlock _frameText = B.Text("—", "Muted", 11);
    private readonly TextBlock _animStatus = B.Text("", "Faint", 10.5);
    private Button? _playButton;
    private bool _syncingFrame;
    private ListBox? _animList;

    private sealed record AnimRow(EaglAnim Anim)
    {
        public string Name => Anim.Name;
        public override string ToString() => Name;
    }

    /// <summary>One list entry per clip: the body half (_e) paired with its root half (_r).</summary>
    private sealed record ClipRow(EaglAnim Anim, string Clip, string Category, string Title)
    {
        public string Detail => $"{Clip} · {Anim.Frames} frames · {Anim.Frames / 30.0:0.0}s";
        public override string ToString() => Title;
    }

    private sealed record BankChoice(AssetNode Node, string Bank, string Group, string Title)
    {
        public override string ToString() => Title == Bank ? $"{Group} · {Bank}" : $"{Group} · {Title}  ({Bank})";
    }

    /// <summary>World matrices for the current animation frame (plus manual pose on top), or null when not animating.</summary>
    private NMatrix[]? CurrentWorld()
    {
        if (_anim is null || Skeleton is not { } sk) return null;
        var n = sk.Bones.Count;
        var rot = new NQuaternion?[n];
        var trans = new NVector3?[n];
        if (_withRoot && _animRoot is not null) _animRoot.Sample(Math.Min(_frame, _animRoot.Frames - 1), rot, trans);
        _anim.Sample(_frame, rot, trans);
        if (_inPlace)
            foreach (var root in new[] { 1, n - 1 })
                if (root < n && trans[root] is { } t) trans[root] = new NVector3(float.NaN, t.Y, float.NaN);
        var extra = _boneEuler.Count == 0 ? null : _boneEuler.ToDictionary(kv => kv.Key, kv => Euler(kv.Value));
        return sk.Pose(rot, trans, extra);
    }

    private FrameworkElement BuildAnimPanel()
    {
        if (Skeleton is null)
            return new TextBlock { Text = "Animations need a skinned model with a __Skeleton.", Foreground = Ui.Res("Muted"), Margin = new Thickness(12), TextWrapping = TextWrapping.Wrap };

        var banksNode = MainWindow.Instance?.FindNode("DATA/ANIMS/ANIMS.BIG");
        var groupOrder = new[] { "Gameplay", "Signature batting stances", "Signature pitching motions", "Face & hands", "Cutscenes", "Crowd", "Other" };
        var banks = (banksNode?.Children.Where(c => c.Extension == ".ord") ?? [])
            .Select(c => (Node: c, Name: System.IO.Path.GetFileNameWithoutExtension(c.Name)))
            .Select(x => new BankChoice(x.Node, x.Name, EaglAnimNames.BankGroup(x.Name), EaglAnimNames.BankTitle(x.Name)))
            .OrderBy(b => Array.IndexOf(groupOrder, b.Group)).ThenBy(b => b.Bank == "DEF_BANK" ? "" : b.Bank).ToList();
        var bankPick = new ComboBox { ItemsSource = banks, Margin = new Thickness(0, 4, 0, 0) };
        var catPick = new ComboBox { Margin = new Thickness(0, 6, 0, 0) };
        var filter = new TextBox { Margin = new Thickness(0, 6, 0, 0), ToolTip = "Search by readable title or raw clip name (e.g. \"catch high\" or \"f_runthrow\")" };
        _animList = new ListBox { Background = Ui.Res("PanelBg"), ItemTemplate = AnimTemplate(), IsSynchronizedWithCurrentItem = false };
        VirtualizingPanel.SetIsVirtualizingWhenGrouping(_animList, true);
        _animList.GroupStyle.Add(new GroupStyle { HeaderTemplate = GroupHeaderTemplate() });
        List<AnimRow> rows = [];
        List<ClipRow> clips = [];
        const string allCats = "All categories";

        void ApplyFilter()
        {
            var cat = catPick.SelectedItem as string;
            var q = filter.Text.Trim();
            var shown = clips.Where(c => (cat is null || cat.StartsWith(allCats, StringComparison.Ordinal) || cat.StartsWith(c.Category + "  (", StringComparison.Ordinal))
                && (q.Length == 0 || q.Split(' ', StringSplitOptions.RemoveEmptyEntries).All(w => c.Title.Contains(w, StringComparison.OrdinalIgnoreCase) || c.Clip.Contains(w, StringComparison.OrdinalIgnoreCase)))).ToList();
            var view = new System.Windows.Data.ListCollectionView(shown);
            view.GroupDescriptions.Add(new System.Windows.Data.PropertyGroupDescription(nameof(ClipRow.Category)));
            _animList.ItemsSource = view;
        }
        filter.TextChanged += (_, _) => ApplyFilter();
        catPick.SelectionChanged += (_, _) => ApplyFilter();

        bankPick.SelectionChanged += async (_, _) =>
        {
            if (bankPick.SelectedItem is not BankChoice choice) return;
            _animStatus.Text = $"Decoding {choice.Bank}…";
            try
            {
                var bank = await Task.Run(() => LoadBank(choice.Node));
                rows = bank.Anims.Select(a => new AnimRow(a)).ToList();
                clips = ToClips(bank.Anims);
                var counts = clips.GroupBy(c => c.Category).OrderBy(g => Array.IndexOf(EaglAnimNames.CategoryOrder, g.Key)).Select(g => $"{g.Key}  ({g.Count()})").ToList();
                catPick.ItemsSource = new[] { $"{allCats}  ({clips.Count})" }.Concat(counts).ToList();
                catPick.SelectedIndex = 0;
                ApplyFilter();
                var mismatch = clips.Count(c => c.Anim.SkeletonHash16 != (Skeleton!.Hash & 0xFFFF));
                _animStatus.Text = $"{clips.Count} clips" + (mismatch > 0 ? $" · {mismatch} target another skeleton" : "") + (bank.Warnings.Count > 0 ? $" · {bank.Warnings.Count} warnings" : "");
            }
            catch (Exception ex) { _animStatus.Text = "Failed: " + ex.Message; }
        };
        _animList.SelectionChanged += (_, _) => { if (_animList.SelectedItem is ClipRow r) SelectAnim(r.Anim, rows); };

        _playButton = B.Button("Play", "", (_, _) => TogglePlay(), "Primary");
        var stop = B.Button("Bind pose", null, (_, _) => { StopAnim(); });
        _frameSlider.ValueChanged += (_, e) =>
        {
            if (_syncingFrame) return;
            _frame = (float)e.NewValue;
            UpdateFrameText();
            ScheduleDeform();
        };
        var transport = new WrapPanel { Margin = new Thickness(0, 8, 0, 4) };
        transport.Children.Add(_playButton);
        transport.Children.Add(stop);
        var opts = new WrapPanel { Margin = new Thickness(0, 4, 0, 4) };
        opts.Children.Add(B.Segments("", ["¼×", "½×", "1×", "2×"], 2, i => _speed = new[] { 0.25f, 0.5f, 1f, 2f }[i]));
        opts.Children.Add(B.Toggle("Loop", _loop, v => _loop = v));
        opts.Children.Add(B.Toggle("In place", _inPlace, v => { _inPlace = v; ScheduleDeform(); }));
        opts.Children.Add(B.Toggle("+ root (_r)", _withRoot, v => { _withRoot = v; ScheduleDeform(); }));

        var top = new StackPanel { Margin = new Thickness(10, 8, 10, 4) };
        top.Children.Add(B.Text("BANK (ANIMS.BIG)", "Muted", 11, FontWeights.SemiBold));
        top.Children.Add(bankPick);
        top.Children.Add(transport);
        top.Children.Add(B.Cols((_frameSlider, B.Star()), (_frameText, B.Px(70))));
        top.Children.Add(opts);
        top.Children.Add(_animStatus);
        top.Children.Add(catPick);
        top.Children.Add(filter);
        if (banksNode is null) top.Children.Add(B.Text("Open the game folder to browse animation banks.", "Bad", 11));
        else bankPick.SelectedItem = banks.FirstOrDefault(b => b.Bank == "DEF_BANK");
        return B.Rows((top, B.Auto), (_animList, B.Star()));
    }

    private static EaglAnimBank LoadBank(AssetNode node)
    {
        lock (BankCache)
            if (BankCache.TryGetValue(node, out var cached)) return cached;
        var bank = EaglAnimBank.Read(RelocatedObject.Load(node.ReadElfJoined().Data));
        lock (BankCache) BankCache[node] = bank;
        return bank;
    }

    /// <summary>One row per clip: prefer the _e (body) half; halves without a partner still get a row.</summary>
    private static List<ClipRow> ToClips(IEnumerable<EaglAnim> anims) =>
        anims.GroupBy(a => EaglAnimNames.ClipName(a.Name))
            .Select(g => new ClipRow(g.FirstOrDefault(a => a.Name.EndsWith("_e", StringComparison.Ordinal)) ?? g.First(),
                g.Key, EaglAnimNames.Category(g.Key), EaglAnimNames.Title(g.Key)))
            .OrderBy(c => Array.IndexOf(EaglAnimNames.CategoryOrder, c.Category)).ThenBy(c => c.Title, StringComparer.OrdinalIgnoreCase).ToList();

    private static DataTemplate AnimTemplate()
    {
        var sp = new FrameworkElementFactory(typeof(StackPanel));
        sp.SetValue(FrameworkElement.MarginProperty, new Thickness(4, 1, 0, 1));
        var t1 = new FrameworkElementFactory(typeof(TextBlock)); t1.SetBinding(TextBlock.TextProperty, new System.Windows.Data.Binding(nameof(ClipRow.Title)));
        var t2 = new FrameworkElementFactory(typeof(TextBlock)); t2.SetBinding(TextBlock.TextProperty, new System.Windows.Data.Binding(nameof(ClipRow.Detail)));
        t2.SetValue(TextBlock.ForegroundProperty, Ui.Res("Faint")); t2.SetValue(TextBlock.FontSizeProperty, 10.5);
        sp.AppendChild(t1); sp.AppendChild(t2);
        return new DataTemplate { VisualTree = sp };
    }

    /// <summary>Banks split each clip into "name_e" (body) and "name_r" (root/hips); play both together.</summary>
    private void SelectAnim(EaglAnim anim, List<AnimRow> all)
    {
        _anim = anim;
        _animRoot = null;
        if (anim.Name.EndsWith("_e", StringComparison.Ordinal))
            _animRoot = all.FirstOrDefault(r => r.Name == anim.Name[..^2] + "_r")?.Anim;
        _frame = 0;
        _syncingFrame = true;
        _frameSlider.Maximum = Math.Max(1, anim.Frames - 1);
        _frameSlider.Value = 0;
        _syncingFrame = false;
        UpdateFrameText();
        if (anim.SkeletonHash16 != (Skeleton!.Hash & 0xFFFF))
            Log.Warn($"{anim.Name} targets skeleton hash 0x{anim.SkeletonHash16:X4}, model uses 0x{Skeleton.Hash & 0xFFFF:X4}.");
        foreach (var w in anim.Warnings.Take(3)) Log.Warn($"{anim.Name}: {w}");
        UpdateDeformation();
        if (!_playing) TogglePlay();
    }

    private void TogglePlay()
    {
        if (_anim is null) return;
        _playing = !_playing;
        if (_playButton is not null) _playButton.Content = _playing ? B.Text("Pause") : B.Text("Play");
        if (_playing)
        {
            _playTimer ??= new DispatcherTimer(TimeSpan.FromMilliseconds(33), DispatcherPriority.Render, (_, _) => Tick(), Dispatcher);
            _clock.Restart();
            _playTimer.Start();
        }
        else _playTimer?.Stop();
    }

    private void Tick()
    {
        if (_anim is null) { _playTimer?.Stop(); return; }
        var dt = (float)_clock.Elapsed.TotalSeconds;
        _clock.Restart();
        _frame += dt * 30f * _speed; // banks are authored at 30 fps
        var last = Math.Max(1, _anim.Frames - 1);
        if (_frame > last)
        {
            if (_loop) _frame %= last;
            else { _frame = last; TogglePlay(); }
        }
        _syncingFrame = true;
        _frameSlider.Value = _frame;
        _syncingFrame = false;
        UpdateFrameText();
        UpdateDeformation();
    }

    private void UpdateFrameText() => _frameText.Text = _anim is null ? "—" : $"{_frame,5:0.0} / {_anim.Frames - 1}";

    private void StopAnim()
    {
        _playTimer?.Stop();
        _playing = false;
        if (_playButton is not null) _playButton.Content = B.Text("Play");
        _anim = _animRoot = null;
        if (_animList is not null) _animList.SelectedItem = null;
        UpdateFrameText();
        UpdateDeformation();
    }

    /// <summary>Dev aid for --snapshot: --anim BANK/name [--frame f].</summary>
    private void ApplyAnimCommandLine()
    {
        var args = Environment.GetCommandLineArgs();
        if (Array.IndexOf(args, "--anim") is not (var ai and > 0)) return;
        var parts = args[ai + 1].Split('/');
        var node = MainWindow.Instance?.FindNode($"DATA/ANIMS/ANIMS.BIG/{parts[0]}.ord");
        if (node is null) return;
        var bank = LoadBank(node);
        var rows = bank.Anims.Select(a => new AnimRow(a)).ToList();
        var pick = rows.FirstOrDefault(r => parts.Length < 2 || r.Name.Equals(parts[1], StringComparison.OrdinalIgnoreCase)) ?? rows.FirstOrDefault();
        if (pick is null) return;
        SelectAnim(pick.Anim, rows);
        TogglePlay();
        if (Array.IndexOf(args, "--frame") is var fi and > 0 && float.TryParse(args[fi + 1], System.Globalization.CultureInfo.InvariantCulture, out var f)) _frame = f;
        UpdateDeformation();
    }
}
