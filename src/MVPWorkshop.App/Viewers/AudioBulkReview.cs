using System.IO;
using System.Media;
using System.Windows;
using System.Windows.Controls;
using MVPWorkshop.Core.Editing;
using MVPWorkshop.Core.Formats;

namespace MVPWorkshop.App.Viewers;

/// <summary>Lists what a folder audio import will do to each sound of one container before anything is applied.</summary>
internal sealed class AudioBulkReview : Window
{
    private SoundPlayer? _player;
    internal IReadOnlyList<AudioBulkImport.Row> Rows { get; }

    internal AudioBulkReview(string assetName, string folder, IReadOnlyList<AudioBulkImport.Row> rows, IReadOnlyList<AudioView.Item> items, IReadOnlyDictionary<int, DecodedAudio> previews)
    {
        Rows = rows;
        Background = Ui.Res("WindowBg"); Foreground = Ui.Res("Text");
        Title = "Import audio folder"; Width = 900; MaxHeight = 820; SizeToContent = SizeToContent.Height; WindowStartupLocation = WindowStartupLocation.CenterOwner;
        int ready = rows.Count(r => r.Ready);
        var panel = new StackPanel { Margin = new Thickness(18) };
        panel.Children.Add(new TextBlock { Text = $"{assetName}: {ready} of {items.Count} sound(s) will be replaced from {folder}.", TextWrapping = TextWrapping.Wrap, FontWeight = FontWeights.SemiBold });
        panel.Children.Add(new TextBlock { Text = $"Files are matched by exact name, as Export All writes them ({AudioBulkImport.FileStem(assetName, items[0].Title)}.wav/.mp3/.ogg). Each input is resampled, channel-converted and trimmed or silence-padded to the native sample count, then encoded in the sound's own codec. Offsets, wrappers, block sizes and loop metadata stay fixed; unmatched sounds keep their bytes. All accepted sounds are saved as one undoable edit.", TextWrapping = TextWrapping.Wrap, Foreground = Ui.Res("Muted"), Margin = new Thickness(0, 4, 0, 10) });

        var grid = new Grid();
        foreach (var w in new[] { 110.0, 200, 230, 70, double.NaN })
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = double.IsNaN(w) ? new GridLength(1, GridUnitType.Star) : new GridLength(w) });
        void Cell(int r, int c, UIElement e) { Grid.SetRow(e, r); Grid.SetColumn(e, c); grid.Children.Add(e); }
        TextBlock T(string text, bool bold = false, bool muted = false) => new() { Text = text, Margin = new Thickness(0, 2, 8, 2), TextTrimming = TextTrimming.CharacterEllipsis, ToolTip = text, FontWeight = bold ? FontWeights.SemiBold : FontWeights.Normal, Foreground = muted ? Ui.Res("Muted") : Ui.Res("Text") };
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        string[] head = ["Sound", "Input", "Native target", "Preview", "Result"];
        for (int c = 0; c < head.Length; c++) Cell(0, c, T(head[c], true));
        foreach (var row in rows)
        {
            int r = grid.RowDefinitions.Count; grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var h = row.Target?.Header; bool muted = !row.Ready;
            Cell(r, 0, T(row.Target?.Title ?? "—", muted: muted));
            Cell(r, 1, T(row.File is null ? "—" : Path.GetFileName(row.File) + (row.Input is { } i ? $" ({i.SampleRate} Hz · {i.Channels}ch · {i.Duration:0.00}s)" : ""), muted: muted));
            Cell(r, 2, T(h is null ? "—" : $"{EaPatchHeader.CodecName(h.Codec2)} · {h.SampleRate} Hz · {h.Channels}ch · {row.Target!.Duration:0.00}s", muted: muted));
            if (row.Ready && previews.TryGetValue(row.Target!.Index, out var preview))
            { var play = B.Button("Play", null, (_, _) => { _player?.Stop(); _player = new SoundPlayer(new MemoryStream(AudioCodecs.ToWav(preview))); _player.Play(); }, "Outline"); play.Margin = new Thickness(0, 1, 8, 1); Cell(r, 3, play); }
            else Cell(r, 3, T("—", muted: true));
            Cell(r, 4, T(row.Ready ? "replace (encoded natively)" : row.Error is { } e ? "conversion failed: " + e.Split('\n')[0].Trim() : row.Status switch
            {
                AudioBulkImport.Status.Unsupported => "not replaced: " + row.Problem,
                AudioBulkImport.Status.Duplicate => "not replaced: " + row.Problem,
                AudioBulkImport.Status.Unmatched => "not used: " + row.Problem,
                AudioBulkImport.Status.UnsupportedInput => "not used: " + row.Problem,
                _ => row.Problem ?? "",
            }, muted: muted));
        }
        panel.Children.Add(new ScrollViewer { Content = grid, MaxHeight = 520, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
        var untouched = items.Count - rows.Where(r => r.Ready).Select(r => r.Target!.Index).Distinct().Count();
        panel.Children.Add(new TextBlock { Text = $"{untouched} sound(s) without a usable input keep their current bytes. Next: the replacement impact review confirms which schools use this asset.", TextWrapping = TextWrapping.Wrap, Foreground = Ui.Res("Muted"), Margin = new Thickness(0, 10, 0, 0) });

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 14, 0, 0) };
        var apply = B.Button($"Continue with {ready} sound(s)", null, (_, _) => DialogResult = true, "Primary");
        apply.IsEnabled = ready > 0; apply.IsDefault = true;
        var cancel = B.Button("Cancel", null, (_, _) => DialogResult = false); cancel.IsCancel = true; cancel.Margin = new Thickness(8, 0, 0, 0);
        buttons.Children.Add(apply); buttons.Children.Add(cancel); panel.Children.Add(buttons);
        Content = new Border { Background = Ui.Res("WindowBg"), Child = panel };
        Closed += (_, _) => StopPreview();
    }

    internal void StopPreview() { _player?.Stop(); _player?.Dispose(); _player = null; }
}
