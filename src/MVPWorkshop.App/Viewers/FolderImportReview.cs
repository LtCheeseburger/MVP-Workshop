using System.IO;
using System.Windows;
using System.Windows.Controls;
using MVPWorkshop.Core.Formats;

namespace MVPWorkshop.App.Viewers;

/// <summary>Lists what a folder import will do to each slot of a pack before it's applied.</summary>
internal sealed class FolderImportReview : Window
{
    internal FolderImportReview(string packName, string folder, IReadOnlyList<FolderImportRow> rows, IReadOnlyList<string> unused)
    {
        Background = Ui.Res("WindowBg"); Foreground = Ui.Res("Text");
        Title = "Import folder"; Width = 760; MaxHeight = 820; SizeToContent = SizeToContent.Height; WindowStartupLocation = WindowStartupLocation.CenterOwner;
        var replacing = rows.Count(r => r.Replaces);
        var panel = new StackPanel { Margin = new Thickness(18) };
        panel.Children.Add(new TextBlock { Text = $"{packName}: {replacing} of {rows.Count} image(s) will be replaced from {folder}.", TextWrapping = TextWrapping.Wrap, FontWeight = FontWeights.SemiBold });
        panel.Children.Add(new TextBlock { Text = "Pictures are matched to slots by file name (hat.png → hat). In a uniform, torf (the mirrored torso) and llod (the low-detail atlas) are made from your torso, legs and hat unless the folder has its own. Each one is saved at the largest size the slot allows that fits inside the picture, converted to the slot's format with its mips rebuilt.", TextWrapping = TextWrapping.Wrap, Foreground = Ui.Res("Muted"), Margin = new Thickness(0, 4, 0, 10) });

        var grid = new Grid();
        foreach (var w in new[] { 90.0, 110, 190, 110, double.NaN })
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = double.IsNaN(w) ? new GridLength(1, GridUnitType.Star) : new GridLength(w) });
        void Row(int r, bool header, params string[] cells)
        {
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            for (var c = 0; c < cells.Length; c++)
            {
                var t = new TextBlock { Text = cells[c], Margin = new Thickness(0, 2, 8, 2), TextTrimming = TextTrimming.CharacterEllipsis, ToolTip = cells[c], FontWeight = header ? FontWeights.SemiBold : FontWeights.Normal };
                Grid.SetRow(t, r); Grid.SetColumn(t, c); grid.Children.Add(t);
            }
        }
        Row(0, true, "Slot", "Now", "Picture", "Saved at", "Result");
        var i = 1;
        foreach (var r in rows)
        {
            var h = r.Image.Header;
            var row = grid.RowDefinitions.Count;
            Row(row, false, r.Image.Tag, $"{h.Width}×{h.Height}",
                r.File is null ? (r.MadeFrom is { } made ? "made from " + made : "—") : $"{Path.GetFileName(r.File)}" + (r.PictureWidth > 0 ? $" ({r.PictureWidth}×{r.PictureHeight})" : ""),
                r.Replaces ? $"{r.Width}×{r.Height}" : "—",
                r.Replaces ? (r.MadeFrom is not null ? "made" : r.Width != r.PictureWidth || r.Height != r.PictureHeight ? "replace (resampled)" : "replace") : r.Problem!);
            if (!r.Replaces)
                foreach (var t in grid.Children.OfType<TextBlock>().Where(t => Grid.GetRow(t) == row)) t.Foreground = Ui.Res("Muted");
            i++;
        }
        panel.Children.Add(new ScrollViewer { Content = grid, MaxHeight = 520, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
        if (unused.Count > 0)
            panel.Children.Add(new TextBlock { Text = "Not used (no slot with that name): " + string.Join(", ", unused.Select(Path.GetFileName)), TextWrapping = TextWrapping.Wrap, Foreground = Ui.Res("Muted"), Margin = new Thickness(0, 10, 0, 0) });

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 14, 0, 0) };
        var apply = B.Button($"Replace {replacing} image(s)", null, (_, _) => DialogResult = true, "Primary");
        apply.IsEnabled = replacing > 0;
        apply.IsDefault = true;
        var cancel = B.Button("Cancel", null, (_, _) => DialogResult = false);
        cancel.IsCancel = true;
        cancel.Margin = new Thickness(8, 0, 0, 0);
        buttons.Children.Add(apply);
        buttons.Children.Add(cancel);
        panel.Children.Add(buttons);
        Content = panel;
    }
}
