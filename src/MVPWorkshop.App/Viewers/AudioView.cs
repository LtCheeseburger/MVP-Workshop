using System.IO;
using System.Media;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using MVPWorkshop.Core.Formats;
using MVPWorkshop.Core.IO;
using MVPWorkshop.Core.Vfs;
using MVPWorkshop.Core.Editing;
using System.Diagnostics;

namespace MVPWorkshop.App.Viewers;

/// <summary>Plays and inspects EA SCHl streams and BNKl bank sounds.</summary>
public sealed class AudioView : Grid, IDisposable
{
    public sealed class Item
    {
        public required string Title { get; init; }
        public required string Subtitle { get; init; }
        public required EaPatchHeader Header { get; init; }
        public required Func<DecodedAudio> Decode { get; init; }
        public List<Prop> Extra { get; init; } = [];
        public Func<DecodedAudio, (byte[] Bytes, DecodedAudio Preview)>? Replace { get; init; }
        public Action? ValidateSource { get; init; }
    }

    private readonly AssetNode _node;
    private readonly Action<List<Prop>> _setExtra;
    private readonly ListBox _list = new();
    private readonly WaveformView _wave = new();
    private readonly TextBlock _title = B.Text("", "Text", 16, FontWeights.SemiBold);
    private readonly TextBlock _sub = B.Text("", "Muted");
    private readonly TextBlock _usage = B.Text("Resolving confirmed usage…", "Muted");
    private readonly TextBlock _msg = B.Text("", "Muted");
    private SoundPlayer? _player;
    private DecodedAudio? _audio;
    private readonly byte[] _sourceBaseline;

    public static FrameworkElement ForSony(AssetNode node, Action<List<Prop>> setExtra)
    {
        var header = node.Extension == ".hd" ? node : node.Parent?.Children.FirstOrDefault(n => n.Name.Equals(Path.ChangeExtension(node.Name, ".hd"), StringComparison.OrdinalIgnoreCase));
        var body = node.Extension == ".bd" ? node : node.Parent?.Children.FirstOrDefault(n => n.Name.Equals(Path.ChangeExtension(node.Name, ".bd"), StringComparison.OrdinalIgnoreCase));
        if (header is null || body is null) return new TextBlock { Text = "Open the game folder containing both Sony HD and BD partners.", Margin = new Thickness(16) };
        var bytes = body.ReadAll(); var headerBytes = header.ReadAll(); var sounds = SonySoundBank.Read(headerBytes, bytes.Length);
        var items = sounds.Select(sound =>
        {
            var patch = new EaPatchHeader { Platform = 5 }; patch.Tags.AddRange([(0x82, 1u, 0), (0x84, (uint)sound.SampleRate, 0), (0x85, (uint)(sound.Length / 16 * 28), 0), (0xA0, 5u, 0)]);
            return new Item { Title = "Sony sample " + sound.Index, Subtitle = $"PS-ADPCM · {sound.SampleRate} Hz · HD Vagi @ BD 0x{sound.Offset:X}", Header = patch, Decode = () => SonySoundBank.Decode(bytes, sound), ValidateSource = () => { if (!header.ReadAll().AsSpan().SequenceEqual(headerBytes)) throw new InvalidOperationException("Sony header changed; reopen the sound bank."); }, Replace = audio => { if (sounds.Count(s => s.Offset == sound.Offset) != 1) throw new InvalidOperationException("This Sony payload is shared; isolated replacement is unavailable."); var replacement = AudioReplacement.Sony(bytes, sound, audio); return (replacement, SonySoundBank.Decode(replacement, sound)); } };
        }).ToList();
        return new AudioView(items, body, setExtra);
    }

    public static List<Item> FromStreams(IByteSource src) =>
        SchlStream.ScanAll(src).Select((s, i) => new Item
        {
            Title = $"Stream {i}",
            Subtitle = s.Header is { } h ? $"{EaPatchHeader.CodecName(h.Codec2)} · {h.SampleRate} Hz · {h.Channels}ch" : "no header",
            Header = s.Header ?? new EaPatchHeader(),
            Decode = () => s.Decode(src),
            Replace = audio => { var bytes = AudioReplacement.Stream(src.ReadAll(), i, audio); var memory = new MemoryByteSource(bytes); return (bytes, SchlStream.ScanAll(memory)[i].Decode(memory)); },
            Extra = [new("Stream", "Offset", $"0x{s.Offset:X}"), new("Stream", "Length", $"{s.Length:N0}"), new("Stream", "Blocks", string.Join(" ", s.Blocks.GroupBy(b => b.Id).Select(g => $"{g.Key}×{g.Count()}")))],
        }).ToList();

    public static List<Item> FromBank(byte[] data, Func<byte[], byte[]>? wrap = null)
    {
        var bank = EaBank.Read(data);
        return bank.Sounds.Select(s => new Item
        {
            Title = $"Sound {s.Index}",
            Subtitle = $"{EaPatchHeader.CodecName(s.Header.Codec2)} · {s.Header.SampleRate} Hz · {(s.Header.SampleRate > 0 ? s.Header.Samples / (double)s.Header.SampleRate : 0):0.00}s",
            Header = s.Header,
            Decode = () => EaBank.Decode(data, s),
            Replace = audio => { var bytes = AudioReplacement.Bank(data, s.Index, audio); return (wrap?.Invoke(bytes) ?? bytes, EaBank.Decode(bytes, EaBank.Read(bytes).Sounds.Single(sound => sound.Index == s.Index))); },
            Extra = [new("Sound", "Header offset", $"0x{s.HeaderOffset:X}"), new("Sound", "Bank version", bank.Version.ToString())],
        }).ToList();
    }

    public AudioView(List<Item> items, AssetNode node, Action<List<Prop>> setExtra)
    {
        _node = node; _setExtra = setExtra; _sourceBaseline = node.ReadAll();
        _list.ItemsSource = items;
        _list.Background = Ui.Res("PanelBg");
        _list.ItemTemplate = Template();
        _list.SelectionChanged += (_, _) => Select();

        var toolbar = B.Toolbar(
            B.Button("Play", "", (_, _) => Play(), "Primary"),
            B.Button("Stop", "", (_, _) => _player?.Stop()),
            B.Button("Replace audio…", null, async (_, _) => await ReplaceAudio(), "Outline"),
            B.Button("Used by / moments", null, (_, _) => MainWindow.Instance?.ShowDependencies(_node), "Outline"),
            B.Separator(),
            B.Button("Export WAV", "", (_, _) => ExportWav(), "Outline"),
            B.Button("Export All", "", (_, _) => ExportAll(items), "Outline"));

        var info = new StackPanel { Margin = new Thickness(24, 20, 24, 8) };
        info.Children.Add(_title);
        info.Children.Add(_sub);
        _usage.TextWrapping = TextWrapping.Wrap; info.Children.Add(_usage);
        var waveHost = new Border { Child = _wave, Margin = new Thickness(24, 8, 24, 8), Background = Ui.Res("PanelBg"), BorderBrush = Ui.Res("Line"), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(6), MinHeight = 160 };
        var msgHost = new Border { Child = _msg, Margin = new Thickness(24, 4, 24, 16) };
        var right = B.Rows((toolbar, B.Auto), (info, B.Auto), (waveHost, B.Star()), (msgHost, B.Auto));
        right.Background = Ui.Res("WindowBg");
        var listHost = B.Rows((new Border { Child = B.Text($"  {(items.Count == 1 ? "STREAM" : $"SOUNDS ({items.Count})")}", "Muted", 11, FontWeights.SemiBold), Height = 30, Background = Ui.Res("PanelBg") }, B.Auto), (_list, B.Star()));
        Children.Add(B.Cols((listHost, B.Px(250)), (B.VSplit(), B.Px(4)), (right, B.Star())));
        if (items.Count > 0) _list.SelectedIndex = 0;
        else _msg.Text = "No playable sounds found.";
        void Changed() { _player?.Stop(); Select(); }
        Loaded += (_, _) => { Editor.Changed -= Changed; Editor.Changed += Changed; };
        Unloaded += (_, _) => { Editor.Changed -= Changed; _player?.Stop(); };
        Loaded += async (_, _) =>
        {
            if (MainWindow.Instance is not { } main) return;
            try { var index = await main.GetDependencies(); var consumers = index.Impact("asset:" + node.Key); _usage.Text = "Confirmed usage: " + (consumers.Count == 0 ? "school/event binding unresolved" : string.Join("; ", consumers.Take(12).Select(c => c.Label))); }
            catch (OperationCanceledException) { }
            catch (Exception ex) { Log.Warn("Audio usage: " + ex.Message); }
        };
    }

    private static DataTemplate Template()
    {
        var sp = new FrameworkElementFactory(typeof(StackPanel));
        sp.SetValue(MarginProperty, new Thickness(4, 3, 4, 3));
        var t1 = new FrameworkElementFactory(typeof(TextBlock)); t1.SetBinding(TextBlock.TextProperty, new System.Windows.Data.Binding(nameof(Item.Title)));
        var t2 = new FrameworkElementFactory(typeof(TextBlock)); t2.SetBinding(TextBlock.TextProperty, new System.Windows.Data.Binding(nameof(Item.Subtitle)));
        t2.SetValue(TextBlock.ForegroundProperty, Ui.Res("Faint")); t2.SetValue(TextBlock.FontSizeProperty, 10.5);
        sp.AppendChild(t1); sp.AppendChild(t2);
        return new DataTemplate { VisualTree = sp };
    }

    private void Select()
    {
        if (!_node.ReadAll().AsSpan().SequenceEqual(_sourceBaseline)) { _audio = null; _player?.Stop(); _wave.SetAudio(null); _msg.Text = "Audio changed. Reopen this asset to refresh its sound entries."; return; }
        _player?.Stop();
        if (_list.SelectedItem is not Item it) return;
        _title.Text = $"{_node.Name} — {it.Title}";
        _sub.Text = it.Subtitle;
        var props = new List<Prop>();
        FormatInspector.AddPatch(props, "Header", it.Header);
        props.AddRange(it.Extra);
        try
        {
            it.ValidateSource?.Invoke();
            _audio = it.Decode();
            _wave.SetAudio(_audio);
            _msg.Text = $"{_audio.Duration:0.00}s · {_audio.Pcm.Length / Math.Max(1, _audio.Channels):N0} samples/ch · {_audio.Notes}";
            _msg.Foreground = Ui.Res("Muted");
            props.Add(new("Decoded", "Duration", $"{_audio.Duration:0.000}s"));
            props.Add(new("Decoded", "Codec", _audio.Notes));
        }
        catch (Exception ex)
        {
            _audio = null;
            _wave.SetAudio(null);
            _msg.Text = ex.Message;
            _msg.Foreground = Ui.Res("Accent");
        }
        _setExtra(props);
    }

    private void Play()
    {
        if (_audio is null) return;
        _player?.Stop();
        _player = new SoundPlayer(new MemoryStream(AudioCodecs.ToWav(_audio)));
        _player.Play();
    }

    internal async Task ReplaceAudio(string? inputPath = null)
    {
        if (!_node.ReadAll().AsSpan().SequenceEqual(_sourceBaseline)) { _msg.Text = "Reopen this asset before another replacement."; return; }
        if (_list.SelectedItem is not Item item || _audio is null || item.Replace is null) return;
        if (AudioReplacement.Unsupported(item.Header) is { } why) { MessageBox.Show(Window.GetWindow(this), why, "Replacement unavailable"); return; }
        if (inputPath is null) { var pick = new Microsoft.Win32.OpenFileDialog { Filter = "Audio|*.wav;*.mp3;*.ogg", Title = "Replace sound · native sample count is preserved" }; if (pick.ShowDialog(Window.GetWindow(this)) != true) return; inputPath = pick.FileName; }
        var session = Editor.Session; var baseline = _node.ReadAll();
        try
        {
            item.ValidateSource?.Invoke();
            _msg.Text = "Converting and encoding replacement…";
            double duration = _audio!.Duration;
            var imported = await Task.Run(() => ReadInput(inputPath, item.Header.SampleRate, item.Header.Channels, duration));
            var proposed = await Task.Run(() => item.Replace(imported));
            var review = new Window { Owner = Window.GetWindow(this), Title = "Audio replacement preview", Width = 520, SizeToContent = SizeToContent.Height, WindowStartupLocation = WindowStartupLocation.CenterOwner, Background = Ui.Res("PanelBg"), Foreground = Ui.Res("Text") };
            var panel = new StackPanel { Margin = new Thickness(16) };
            panel.Children.Add(new TextBlock { Text = $"{item.Title} · {EaPatchHeader.CodecName(item.Header.Codec2)}\n{proposed.Preview.SampleRate} Hz · {proposed.Preview.Channels} channel(s) · {proposed.Preview.Duration:0.000}s\nInput is resampled, channel-converted and trimmed/silence-padded to the original sample count. Bank offsets, stream block sizes and loop metadata stay fixed.", TextWrapping = TextWrapping.Wrap });
            panel.Children.Add(B.Button("Play encoded preview", null, (_, _) => { _player?.Stop(); _player = new SoundPlayer(new MemoryStream(AudioCodecs.ToWav(proposed.Preview))); _player.Play(); }, "Outline"));
            panel.Children.Add(B.Button("Continue to impact review", null, (_, _) => review.DialogResult = true, "Primary"));
            panel.Children.Add(B.Button("Cancel", null, (_, _) => review.DialogResult = false, "Outline")); review.Content = panel;
            bool accepted; try { accepted = review.ShowDialog() == true; } finally { _player?.Stop(); }
            if (!accepted || MainWindow.Instance is not { } main) return;
            var decision = await main.PrepareReplacement(_node);
            if (decision is null) return;
            if (!ReferenceEquals(session, Editor.Session) || !_node.ReadAll().AsSpan().SequenceEqual(baseline)) throw new InvalidOperationException("Audio changed; repeat the import.");
            item.ValidateSource?.Invoke();
            decision.Apply(proposed.Bytes, "Replace " + item.Title + " from " + Path.GetFileName(inputPath));
            _audio = proposed.Preview; _wave.SetAudio(_audio); _msg.Text = "Saved audio replacement. Reopen the asset after undo or to replace another sound.";
        }
        catch (Exception ex) { _msg.Text = ex.Message; Log.Warn("Audio replace: " + ex.Message); }
    }

    internal static DecodedAudio ReadInput(string path, int rate, int channels, double duration = 600)
    {
        if (new FileInfo(path).Length > 256 * 1024 * 1024) throw new InvalidDataException("Audio input exceeds 256 MiB.");
        if (Path.GetExtension(path).Equals(".wav", StringComparison.OrdinalIgnoreCase))
            try { return AudioReplacement.ReadPcmWav(File.ReadAllBytes(path)); } catch (NotSupportedException) { }
        string? executable = null;
        foreach (var directory in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator))
        { var candidate = Path.Combine(directory.Trim('"'), "ffmpeg.exe"); if (File.Exists(candidate)) { executable = candidate; break; } }
        if (executable is null) throw new InvalidOperationException("ffmpeg.exe is required on PATH for MP3/OGG and non-PCM WAV inputs.");
        var output = Path.Combine(Path.GetTempPath(), "mvp-audio-" + Guid.NewGuid().ToString("N") + ".wav");
        try
        {
            var start = new ProcessStartInfo(executable) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true };
            foreach (var argument in new[] { "-v", "error", "-nostdin", "-y", "-i", path, "-vn", "-ar", rate.ToString(), "-ac", channels.ToString(), "-t", duration.ToString(System.Globalization.CultureInfo.InvariantCulture), "-c:a", "pcm_s16le", output }) start.ArgumentList.Add(argument);
            using var process = Process.Start(start)!; var errors = process.StandardError.ReadToEndAsync();
            if (!process.WaitForExit(60000)) { process.Kill(true); throw new TimeoutException("Audio conversion exceeded one minute."); }
            string error = errors.GetAwaiter().GetResult(); if (process.ExitCode != 0) throw new InvalidDataException("ffmpeg: " + error);
            if (new FileInfo(output).Length > 256 * 1024 * 1024) throw new InvalidDataException("Converted audio exceeds sample limit.");
            return AudioReplacement.ReadPcmWav(File.ReadAllBytes(output));
        }
        finally { if (File.Exists(output)) File.Delete(output); }
    }

    private void ExportWav()
    {
        if (_audio is null || _list.SelectedItem is not Item it) return;
        Exporter.SaveBytes(AudioCodecs.ToWav(_audio), $"{Path.GetFileNameWithoutExtension(_node.Name)}_{it.Title.Replace(' ', '_')}.wav", "WAV audio|*.wav");
    }

    private void ExportAll(List<Item> items)
    {
        var dlg = new Microsoft.Win32.OpenFolderDialog { Title = "Export all sounds to…" };
        if (dlg.ShowDialog(Window.GetWindow(this)) != true) return;
        try { Exporter.CheckOutput(dlg.FolderName, _node.Root); } catch (Exception ex) { MessageBox.Show(Window.GetWindow(this), ex.Message, "Export audio"); return; }
        int ok = 0, fail = 0;
        foreach (var it in items)
        {
            try { File.WriteAllBytes(Path.Combine(dlg.FolderName, $"{Path.GetFileNameWithoutExtension(_node.Name)}_{it.Title.Replace(' ', '_')}.wav"), AudioCodecs.ToWav(it.Decode())); ok++; }
            catch { fail++; }
        }
        Log.Info($"Exported {ok} WAVs from {_node.Name}{(fail > 0 ? $" ({fail} not decodable)" : "")} → {dlg.FolderName}");
    }

    public void Dispose() { _player?.Stop(); _player?.Dispose(); }
}

/// <summary>Min/max waveform per pixel column, one lane per channel.</summary>
public sealed class WaveformView : FrameworkElement
{
    private DecodedAudio? _a;
    private static readonly Pen Mid = new(new SolidColorBrush(Color.FromRgb(0x2E, 0x30, 0x37)), 1);
    private static readonly Brush Fill = new SolidColorBrush(Color.FromRgb(0x6A, 0xA8, 0xF0));

    public void SetAudio(DecodedAudio? a) { _a = a; InvalidateVisual(); }

    protected override void OnRender(DrawingContext dc)
    {
        dc.DrawRectangle(Brushes.Transparent, null, new Rect(RenderSize));
        if (_a is null || _a.Pcm.Length == 0 || ActualWidth < 2) return;
        var ch = Math.Max(1, _a.Channels);
        var frames = _a.Pcm.Length / ch;
        var w = (int)ActualWidth;
        var laneH = ActualHeight / ch;
        for (var c = 0; c < ch; c++)
        {
            var mid = laneH * c + laneH / 2;
            dc.DrawLine(Mid, new Point(0, mid), new Point(w, mid));
            var geo = new StreamGeometry();
            using (var g = geo.Open())
            {
                for (var x = 0; x < w; x++)
                {
                    long s0 = (long)x * frames / w, s1 = Math.Max(s0 + 1, (long)(x + 1) * frames / w);
                    int mn = 0, mx = 0;
                    var step = Math.Max(1, (s1 - s0) / 64);
                    for (var s = s0; s < s1 && s < frames; s += step)
                    {
                        var v = _a.Pcm[s * ch + c];
                        if (v < mn) mn = v; if (v > mx) mx = v;
                    }
                    var y0 = mid - mx / 32768.0 * laneH * 0.46;
                    var y1 = mid - mn / 32768.0 * laneH * 0.46;
                    g.BeginFigure(new Point(x, y0), true, true);
                    g.LineTo(new Point(x + 1, y0), false, false);
                    g.LineTo(new Point(x + 1, Math.Max(y1, y0 + 1)), false, false);
                    g.LineTo(new Point(x, Math.Max(y1, y0 + 1)), false, false);
                }
            }
            geo.Freeze();
            dc.DrawGeometry(Fill, null, geo);
        }
    }
}
