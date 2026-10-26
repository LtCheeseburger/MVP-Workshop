using System.IO;
using System.Media;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using MVPWorkshop.Core.Formats;
using MVPWorkshop.Core.IO;
using MVPWorkshop.Core.Vfs;

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
    }

    private readonly AssetNode _node;
    private readonly Action<List<Prop>> _setExtra;
    private readonly ListBox _list = new();
    private readonly WaveformView _wave = new();
    private readonly TextBlock _title = B.Text("", "Text", 16, FontWeights.SemiBold);
    private readonly TextBlock _sub = B.Text("", "Muted");
    private readonly TextBlock _msg = B.Text("", "Muted");
    private SoundPlayer? _player;
    private DecodedAudio? _audio;

    public static List<Item> FromStreams(IByteSource src) =>
        SchlStream.ScanAll(src).Select((s, i) => new Item
        {
            Title = $"Stream {i}",
            Subtitle = s.Header is { } h ? $"{EaPatchHeader.CodecName(h.Codec2)} · {h.SampleRate} Hz · {h.Channels}ch" : "no header",
            Header = s.Header ?? new EaPatchHeader(),
            Decode = () => s.Decode(src),
            Extra = [new("Stream", "Offset", $"0x{s.Offset:X}"), new("Stream", "Length", $"{s.Length:N0}"), new("Stream", "Blocks", string.Join(" ", s.Blocks.GroupBy(b => b.Id).Select(g => $"{g.Key}×{g.Count()}")))],
        }).ToList();

    public static List<Item> FromBank(byte[] data)
    {
        var bank = EaBank.Read(data);
        return bank.Sounds.Select(s => new Item
        {
            Title = $"Sound {s.Index}",
            Subtitle = $"{EaPatchHeader.CodecName(s.Header.Codec2)} · {s.Header.SampleRate} Hz · {(s.Header.SampleRate > 0 ? s.Header.Samples / (double)s.Header.SampleRate : 0):0.00}s",
            Header = s.Header,
            Decode = () => EaBank.Decode(data, s),
            Extra = [new("Sound", "Header offset", $"0x{s.HeaderOffset:X}"), new("Sound", "Bank version", bank.Version.ToString())],
        }).ToList();
    }

    public AudioView(List<Item> items, AssetNode node, Action<List<Prop>> setExtra)
    {
        _node = node; _setExtra = setExtra;
        _list.ItemsSource = items;
        _list.Background = Ui.Res("PanelBg");
        _list.ItemTemplate = Template();
        _list.SelectionChanged += (_, _) => Select();

        var toolbar = B.Toolbar(
            B.Button("Play", "", (_, _) => Play(), "Primary"),
            B.Button("Stop", "", (_, _) => _player?.Stop()),
            B.Separator(),
            B.Button("Export WAV", "", (_, _) => ExportWav(), "Outline"),
            B.Button("Export All", "", (_, _) => ExportAll(items), "Outline"));

        var info = new StackPanel { Margin = new Thickness(24, 20, 24, 8) };
        info.Children.Add(_title);
        info.Children.Add(_sub);
        var waveHost = new Border { Child = _wave, Margin = new Thickness(24, 8, 24, 8), Background = Ui.Res("PanelBg"), BorderBrush = Ui.Res("Line"), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(6), MinHeight = 160 };
        var msgHost = new Border { Child = _msg, Margin = new Thickness(24, 4, 24, 16) };
        var right = B.Rows((toolbar, B.Auto), (info, B.Auto), (waveHost, B.Star()), (msgHost, B.Auto));
        right.Background = Ui.Res("WindowBg");
        var listHost = B.Rows((new Border { Child = B.Text($"  {(items.Count == 1 ? "STREAM" : $"SOUNDS ({items.Count})")}", "Muted", 11, FontWeights.SemiBold), Height = 30, Background = Ui.Res("PanelBg") }, B.Auto), (_list, B.Star()));
        Children.Add(B.Cols((listHost, B.Px(250)), (B.VSplit(), B.Px(4)), (right, B.Star())));
        if (items.Count > 0) _list.SelectedIndex = 0;
        else _msg.Text = "No playable sounds found.";
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
        _player?.Stop();
        if (_list.SelectedItem is not Item it) return;
        _title.Text = $"{_node.Name} — {it.Title}";
        _sub.Text = it.Subtitle;
        var props = new List<Prop>();
        FormatInspector.AddPatch(props, "Header", it.Header);
        props.AddRange(it.Extra);
        try
        {
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

    private void ExportWav()
    {
        if (_audio is null || _list.SelectedItem is not Item it) return;
        Exporter.SaveBytes(AudioCodecs.ToWav(_audio), $"{Path.GetFileNameWithoutExtension(_node.Name)}_{it.Title.Replace(' ', '_')}.wav", "WAV audio|*.wav");
    }

    private void ExportAll(List<Item> items)
    {
        var dlg = new Microsoft.Win32.OpenFolderDialog { Title = "Export all sounds to…" };
        if (dlg.ShowDialog(Window.GetWindow(this)) != true) return;
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
