using System.IO;
using System.Windows;
using System.Windows.Controls;
using MVPWorkshop.Core.Editing;
using MVPWorkshop.Core.IO;

namespace MVPWorkshop.App.Viewers;

/// <summary>
/// Every change in the session, newest last, with each one marked as SAVED (written into game assets on export) or PREVIEW ONLY
/// (visible in the viewers, never saved or exported), plus the output folder and the list of files an export would rebuild.
/// </summary>
public sealed class ChangesView : Grid, IDisposable
{
    private sealed record Row(int Step, string Kind, string Asset, string What, string Detail, string Time, Change Change);
    private sealed record FileRow(string Asset, string Size, string Target);

    private readonly DataGrid _history = B.Grid();
    private readonly DataGrid _assets = B.Grid();
    private readonly TextBlock _summary = B.Text("", "Text", 13, FontWeights.SemiBold);
    private readonly TextBlock _detail = B.Text("", "Muted");
    private readonly TextBlock _output = B.Text("", "Muted");
    private readonly TextBlock _legend = new() { TextWrapping = TextWrapping.Wrap, Foreground = Ui.Res("Muted"), Margin = new Thickness(0, 4, 0, 0) };

    public ChangesView()
    {
        _history.IsReadOnly = true; _history.SelectionMode = DataGridSelectionMode.Single;
        _history.Columns.Add(B.Col("#", nameof(Row.Step), 40, true));
        _history.Columns.Add(KindColumn());
        _history.Columns.Add(B.Col("Asset", nameof(Row.Asset), 280));
        _history.Columns.Add(B.Col("Change", nameof(Row.What), 280));
        _history.Columns.Add(new DataGridTextColumn { Header = "Where it goes", Binding = new System.Windows.Data.Binding(nameof(Row.Detail)), Width = new DataGridLength(1, DataGridLengthUnitType.Star) });
        _history.Columns.Add(B.Col("Time", nameof(Row.Time), 70, true));

        _assets.IsReadOnly = true;
        _assets.Columns.Add(B.Col("Edited asset", nameof(FileRow.Asset), 420));
        _assets.Columns.Add(B.Col("Size", nameof(FileRow.Size), 150, true));
        _assets.Columns.Add(new DataGridTextColumn { Header = "Rebuilt into", Binding = new System.Windows.Data.Binding(nameof(FileRow.Target)), Width = new DataGridLength(1, DataGridLengthUnitType.Star) });

        var s = () => Editor.Session;
        var bar = B.Toolbar(
            B.Button("Undo", "", (_, _) => s()?.Undo(), "Outline"),
            B.Button("Redo", "", (_, _) => s()?.Redo(), "Outline"),
            B.Button("Revert selected asset", "", (_, _) =>
            {
                if (_history.SelectedItem is Row r && s() is { } sess && sess.GameRoot.Resolve(r.Change.Key) is { } node) sess.Revert(node);
            }, "Outline", "Drop all saved edits of the asset in the selected row (undoable)"),
            B.Separator(),
            B.Button("Save Project", "", (_, _) => MainWindow.Instance?.SaveProject(false), "Outline"),
            B.Button("Export Mod…", "", (_, _) => MainWindow.Instance?.ExportMod(), "Primary", "Rebuild the edited archives into the output folder and verify them"));

        var outputRow = B.Cols((_output, B.Star()), (B.Button("Change…", "", (_, _) => MainWindow.Instance?.ChooseOutputFolder(), "Outline", "Choose another output folder"), B.Auto),
            (B.Button("Open", "", (_, _) =>
            {
                if (s() is { } sess) { Directory.CreateDirectory(sess.OutputFolder); System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(sess.OutputFolder) { UseShellExecute = true }); }
            }, "Outline"), B.Auto));
        _legend.Inlines.Add(new System.Windows.Documents.Run("SAVED") { FontWeight = FontWeights.Bold, Foreground = Ui.Res("Good") });
        _legend.Inlines.Add(" changes replace the asset's bytes; export rebuilds the BIG archive(s) around it with every other byte kept.   ");
        _legend.Inlines.Add(new System.Windows.Documents.Run("PREVIEW ONLY") { FontWeight = FontWeights.Bold, Foreground = Ui.Res("Accent") });
        _legend.Inlines.Add(" changes only exist in the viewers (the format or its container cannot be written back); they are not saved in the project and not exported.");

        var head = new StackPanel { Margin = new Thickness(18, 14, 18, 8) };
        head.Children.Add(_summary); head.Children.Add(_detail); head.Children.Add(new Border { Height = 8 });
        head.Children.Add(outputRow); head.Children.Add(_legend);

        var lists = B.Rows((Caption("HISTORY — newest last"), B.Auto), (_history, B.Star()), (new GridSplitter { Height = 4, HorizontalAlignment = HorizontalAlignment.Stretch }, B.Px(4)), (Caption("ASSETS THAT EXPORT WILL REBUILD"), B.Auto), (_assets, B.Px(170)));
        Children.Add(B.Rows((bar, B.Auto), (head, B.Auto), (lists, B.Star())));
        Background = Ui.Res("WindowBg");

        Editor.Changed += Refresh;
        Unloaded += (_, _) => Editor.Changed -= Refresh;
        Refresh();
    }

    public void Dispose() => Editor.Changed -= Refresh;

    private static TextBlock Caption(string text) => new() { Text = text, FontSize = 11, FontWeight = FontWeights.SemiBold, Foreground = Ui.Res("Muted"), Margin = new Thickness(18, 8, 0, 4) };

    private static DataGridTemplateColumn KindColumn()
    {
        var text = new FrameworkElementFactory(typeof(TextBlock));
        text.SetBinding(TextBlock.TextProperty, new System.Windows.Data.Binding(nameof(Row.Kind)));
        text.SetBinding(TextBlock.ForegroundProperty, new System.Windows.Data.Binding(nameof(Row.Kind)) { Converter = Conv.KindBrush });
        text.SetValue(TextBlock.FontWeightProperty, FontWeights.Bold);
        text.SetValue(TextBlock.FontSizeProperty, 10.5);
        text.SetValue(FrameworkElement.MarginProperty, new Thickness(6, 0, 6, 0));
        text.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
        return new DataGridTemplateColumn { Header = "Kind", Width = 110, CellTemplate = new DataTemplate { VisualTree = text } };
    }

    private void Refresh()
    {
        if (Editor.Session is not { } s) { _summary.Text = "No game loaded."; return; }
        var rows = s.History.Select((c, i) => new Row(i + 1, c.Kind == ChangeKind.Saved ? "SAVED" : "PREVIEW ONLY", c.Path, c.Description,
            c.Kind == ChangeKind.Saved ? (s.GameRoot.Resolve(c.Key) is { } n ? $"→ {Path.GetRelativePath(s.GameRoot.DiskPath!, ModExporter.DiskFileOf(n).DiskPath!)}" : "") : c.Reason ?? "", c.Time.ToString("HH:mm:ss"), c)).ToList();
        _history.ItemsSource = rows;
        if (rows.Count > 0) _history.ScrollIntoView(rows[^1]);

        var assets = s.Overrides.Select(kv =>
        {
            var node = s.GameRoot.Resolve(kv.Key);
            var orig = node?.StoredSize ?? 0;
            return new FileRow(node?.Path ?? kv.Key, $"{Bin.Size(node?.UnpackedSize ?? orig)} → {Bin.Size(kv.Value.Length)}", node is null ? "(missing)" : ModExporter.RelativePath(s.GameRoot, ModExporter.DiskFileOf(node)));
        }).OrderBy(r => r.Asset).ToList();
        _assets.ItemsSource = assets;

        var files = assets.Select(a => a.Target).Distinct().Count();
        _summary.Text = s.Overrides.Count == 0 && s.PreviewOnlyCount == 0 ? "No changes yet" : $"{s.Overrides.Count} saved asset change(s) in {files} file(s)" + (s.PreviewOnlyCount > 0 ? $"  ·  {s.PreviewOnlyCount} preview-only" : "");
        _detail.Text = s.IsDirty ? "The project has unsaved changes." : s.Overrides.Count > 0 ? "Project is saved." : "";
        _output.Text = $"Output folder:  {s.OutputFolder}";
    }
}
