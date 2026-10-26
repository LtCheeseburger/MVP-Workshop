using System.Diagnostics;
using System.IO;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Threading;
using MVPWorkshop.Core.Formats;
using MVPWorkshop.Core.Vfs;

namespace MVPWorkshop.App.Viewers;

/// <summary>
/// MPC movie player. ffmpeg demuxes EA's MPCh container (MPEG-2 video + EA-XA R3 audio) natively, so the movie is
/// transcoded once to an H.264/AAC MP4 in the temp cache and played with MediaElement. The game file is only read.
/// </summary>
public sealed partial class MovieView : Grid, IDisposable
{
    private readonly AssetNode _node;
    private readonly MediaElement _media = new() { LoadedBehavior = MediaState.Manual, UnloadedBehavior = MediaState.Manual, Stretch = System.Windows.Media.Stretch.Uniform };
    private readonly Slider _seek = new() { Minimum = 0, Width = 300, Margin = new Thickness(8, 0, 8, 0), IsEnabled = false };
    private readonly TextBlock _time = B.Text("--:-- / --:--", "Muted");
    private readonly TextBlock _status = B.Text("", "Muted");
    private readonly ProgressBar _progress = new() { Width = 260, Height = 6, Margin = new Thickness(8, 0, 8, 0), Maximum = 1 };
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMilliseconds(250) };
    private readonly CancellationTokenSource _cts = new();
    private Process? _ffmpeg;
    private string? _mp4;
    private bool _dragging, _playing;

    public static string? FindFfmpeg()
    {
        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator))
        {
            try { var p = Path.Combine(dir.Trim('"'), "ffmpeg.exe"); if (File.Exists(p)) return p; }
            catch (ArgumentException) { }
        }
        return null;
    }

    public MovieView(AssetNode node, Action<List<Prop>> setExtra)
    {
        _node = node;
        var play = B.Button("Play", "", (_, _) => { _media.Play(); _playing = true; }, "Primary");
        var pause = B.Button("Pause", "", (_, _) => { _media.Pause(); _playing = false; });
        var stop = B.Button("Stop", "", (_, _) => { _media.Stop(); _playing = false; });
        var volume = new Slider { Minimum = 0, Maximum = 1, Value = 0.8, Width = 90, Margin = new Thickness(6, 0, 6, 0), ToolTip = "Volume" };
        volume.ValueChanged += (_, e) => _media.Volume = e.NewValue;
        _media.Volume = 0.8;
        var export = B.Button("Export MP4", "", (_, _) => { if (_mp4 is not null) Exporter.SaveBytes(File.ReadAllBytes(_mp4), Path.ChangeExtension(node.Name, ".mp4"), "MP4 video|*.mp4"); }, "Outline");
        var bar = B.Toolbar(play, pause, stop, B.Separator(), _seek, _time, B.Separator(), B.Glyph("", "Muted", 12), volume, B.Separator(), export, B.Separator(), _progress, _status);

        _seek.AddHandler(Thumb.DragStartedEvent, new DragStartedEventHandler((_, _) => _dragging = true));
        _seek.AddHandler(Thumb.DragCompletedEvent, new DragCompletedEventHandler((_, _) => { _dragging = false; _media.Position = TimeSpan.FromSeconds(_seek.Value); }));
        _seek.PreviewMouseLeftButtonUp += (_, _) => { if (!_dragging) _media.Position = TimeSpan.FromSeconds(_seek.Value); };
        _media.MediaOpened += (_, _) =>
        {
            if (_media.NaturalDuration.HasTimeSpan) { _seek.Maximum = _media.NaturalDuration.TimeSpan.TotalSeconds; _seek.IsEnabled = true; }
            _status.Text = $"{_media.NaturalVideoWidth}×{_media.NaturalVideoHeight}";
        };
        _media.MediaEnded += (_, _) => { _media.Stop(); _playing = false; };
        _media.MediaFailed += (_, e) => Fail($"Playback failed: {e.ErrorException.Message}");
        _timer.Tick += (_, _) =>
        {
            if (!_media.NaturalDuration.HasTimeSpan) return;
            if (!_dragging) _seek.Value = _media.Position.TotalSeconds;
            _time.Text = $"{_media.Position:mm\\:ss} / {_media.NaturalDuration.TimeSpan:mm\\:ss}";
        };
        _timer.Start();

        var stage = new Border { Background = System.Windows.Media.Brushes.Black, Child = _media };
        Children.Add(B.Rows((bar, B.Auto), (stage, B.Star())));
        Unloaded += (_, _) => { if (_playing) { _media.Pause(); _playing = false; } };
        _ = PrepareAsync(setExtra);
    }

    private void Fail(string msg)
    {
        _status.Text = msg;
        _status.Foreground = Ui.Res("Accent");
        Log.Warn($"{_node.Name}: {msg}");
    }

    private async Task PrepareAsync(Action<List<Prop>> setExtra)
    {
        var ffmpeg = FindFfmpeg();
        if (ffmpeg is null) { Fail("ffmpeg.exe not found on PATH — install ffmpeg to play MPC movies."); return; }
        try
        {
            var cache = Path.Combine(Path.GetTempPath(), "MVP-Workshop", "movies");
            Directory.CreateDirectory(cache);
            var key = $"{Path.GetFileNameWithoutExtension(_node.Name)}_{_node.StoredSize:X}";
            var mp4 = Path.Combine(cache, key + ".mp4");
            var input = _node.DiskPath;
            if (input is null)
            {
                input = Path.Combine(cache, key + ".mpc");
                if (!File.Exists(input)) await File.WriteAllBytesAsync(input, _node.ReadAll(), _cts.Token);
            }
            var info = await Task.Run(() => Probe(ffmpeg, input), _cts.Token);
            setExtra([.. info.Select(kv => new Prop("ffmpeg", kv.Key, kv.Value))]);
            if (!File.Exists(mp4))
            {
                _status.Text = "Transcoding with ffmpeg…";
                var duration = info.TryGetValue("Duration", out var ds) && TimeSpan.TryParse(ds, out var d) ? d.TotalSeconds : 0;
                var tmp = mp4 + ".part.mp4";
                var ok = await Task.Run(() => Transcode(ffmpeg, input, tmp, duration), _cts.Token);
                if (!ok) { Fail("ffmpeg could not convert this movie (see Log)."); return; }
                File.Move(tmp, mp4, true);
            }
            _mp4 = mp4;
            _progress.Visibility = Visibility.Collapsed;
            _status.Text = "Ready";
            _media.Source = new Uri(mp4);
            _media.Play();
            _media.Pause();
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { Fail(ex.Message); }
    }

    [GeneratedRegex(@"Duration: (\d+:\d+:\d+\.\d+)")] private static partial Regex DurationRx();
    [GeneratedRegex(@"Stream #0:(\d+).*?: (Video|Audio): (.*)")] private static partial Regex StreamRx();
    [GeneratedRegex(@"time=(\d+:\d+:\d+\.\d+)")] private static partial Regex TimeRx();

    private static Dictionary<string, string> Probe(string ffmpeg, string input)
    {
        var psi = new ProcessStartInfo(ffmpeg, ["-hide_banner", "-i", input]) { RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
        using var p = Process.Start(psi)!;
        var err = p.StandardError.ReadToEnd();
        p.WaitForExit();
        var info = new Dictionary<string, string>();
        if (DurationRx().Match(err) is { Success: true } m) info["Duration"] = m.Groups[1].Value;
        foreach (Match s in StreamRx().Matches(err)) info[$"Stream {s.Groups[1].Value} ({s.Groups[2].Value})"] = s.Groups[3].Value.Trim();
        return info;
    }

    private bool Transcode(string ffmpeg, string input, string output, double duration)
    {
        string[] args = ["-hide_banner", "-y", "-i", input, "-c:v", "libx264", "-preset", "veryfast", "-crf", "18", "-pix_fmt", "yuv420p", "-c:a", "aac", "-b:a", "192k", "-movflags", "+faststart", output];
        var psi = new ProcessStartInfo(ffmpeg, args) { RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
        using var p = _ffmpeg = Process.Start(psi)!;
        string? line, last = null;
        while ((line = p.StandardError.ReadLine()) is not null)
        {
            last = line;
            if (_cts.IsCancellationRequested) { try { p.Kill(); } catch { } return false; }
            if (duration > 0 && TimeRx().Match(line) is { Success: true } m && TimeSpan.TryParse(m.Groups[1].Value, out var t))
                Dispatcher.BeginInvoke(() => { _progress.Value = Math.Min(1, t.TotalSeconds / duration); _status.Text = $"Transcoding… {t:mm\\:ss}"; });
        }
        p.WaitForExit();
        _ffmpeg = null;
        if (p.ExitCode != 0) Dispatcher.BeginInvoke(() => Log.Warn($"ffmpeg exited with {p.ExitCode}: {last}"));
        return p.ExitCode == 0 && File.Exists(output);
    }

    public void Dispose()
    {
        _cts.Cancel();
        try { _ffmpeg?.Kill(); } catch { }
        _timer.Stop();
        _media.Close();
    }
}
