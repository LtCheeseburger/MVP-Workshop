using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using MVPWorkshop.Core.IO;
using MVPWorkshop.Core.Vfs;

namespace MVPWorkshop.App.Viewers;

/// <summary>Runs <see cref="FormatSurvey"/> over the whole game and shows per-format coverage.</summary>
public sealed class SurveyView : Grid
{
    private readonly AssetNode _root;
    private readonly DataGrid _grid = B.Grid();
    private readonly TextBlock _status = B.Text("Not run yet. Quick = identify formats; Deep = also parse/decode every asset.", "Muted");
    private List<SurveyRow> _rows = [];
    private CancellationTokenSource? _cts;

    public SurveyView(AssetNode root)
    {
        _root = root;
        _grid.Columns.Add(B.Col("Category", nameof(SurveyRow.Category), 90));
        _grid.Columns.Add(B.Col("Format", nameof(SurveyRow.Format), 150));
        _grid.Columns.Add(B.Col("Ext", nameof(SurveyRow.Extension), 70, true));
        _grid.Columns.Add(B.Col("Count", nameof(SurveyRow.Count), 70, format: "N0"));
        _grid.Columns.Add(B.Col("Size", "SizeText", 90));
        _grid.Columns.Add(B.Col("RefPack", nameof(SurveyRow.Compressed), 70, format: "N0"));
        _grid.Columns.Add(B.Col("Parsed", nameof(SurveyRow.Parsed), 70, format: "N0"));
        _grid.Columns.Add(B.Col("Failed", nameof(SurveyRow.Failed), 60, format: "N0"));
        _grid.Columns.Add(B.Col("Details", nameof(SurveyRow.DetailText), 320));
        _grid.Columns.Add(B.Col("Sample", nameof(SurveyRow.Sample), 380, true));
        _grid.Columns.Add(B.Col("First error", nameof(SurveyRow.FirstError), 400));
        _grid.MouseDoubleClick += (_, _) =>
        {
            if (_grid.SelectedItem is RowView r && Find(r.Row.Sample) is { } n) MainWindow.Instance?.OpenAsset(n);
        };

        var bar = B.Toolbar(
            B.Button("Quick Survey", "", (_, _) => Run(false), "Outline", "Identify formats of every file (reads headers)"),
            B.Button("Deep Survey", "", (_, _) => Run(true), "Primary", "Also parse every asset and decode every texture"),
            B.Button("Cancel", "", (_, _) => _cts?.Cancel()),
            B.Separator(),
            B.Button("Export Markdown", "", (_, _) => Export(), "Tool"),
            B.Separator(), _status);
        Children.Add(B.Rows((bar, B.Auto), (_grid, B.Star())));
    }

    private AssetNode? Find(string path)
    {
        var parts = path.Split('/');
        var n = _root;
        foreach (var p in parts.Skip(1))
        {
            var next = n.Children.FirstOrDefault(c => c.Name == p);
            if (next is null) return null;
            n = next;
        }
        return n;
    }

    private async void Run(bool deep)
    {
        _cts?.Cancel();
        var cts = _cts = new CancellationTokenSource();
        var sw = Stopwatch.StartNew();
        _status.Text = "Running…";
        var progress = new Progress<SurveyProgress>(p => _status.Text = $"{p.Files:N0} files…  {p.Current}");
        try
        {
            _rows = await Task.Run(() => FormatSurvey.Run(_root, deep, progress, cts.Token));
            _grid.ItemsSource = _rows.Select(r => new RowView(r)).ToList();
            var files = _rows.Sum(r => r.Count); var failed = _rows.Sum(r => r.Failed);
            _status.Text = $"{files:N0} files · {_rows.Count} format/extension groups · {failed:N0} failures · {sw.Elapsed.TotalSeconds:0.0}s{(deep ? " (deep)" : "")}";
            Log.Info($"Format survey finished: {_status.Text}");
        }
        catch (OperationCanceledException) { _status.Text = "Cancelled."; }
        catch (Exception ex) { _status.Text = "Failed: " + ex.Message; Log.Error(_status.Text); }
    }

    private void Export()
    {
        if (_rows.Count == 0) { Log.Warn("Run a survey first."); return; }
        var md = FormatSurvey.ToMarkdown(_rows, AppInfo.Name + " — format survey");
        Exporter.SaveBytes(System.Text.Encoding.UTF8.GetBytes(md), "FORMAT_SURVEY.md", "Markdown|*.md");
    }

    // Wraps SurveyRow so the grid can bind a formatted size without touching the core type.
    private sealed class RowView(SurveyRow r)
    {
        public SurveyRow Row { get; } = r;
        public string Category => Row.Category;
        public string Format => Row.Format;
        public string Extension => Row.Extension;
        public int Count => Row.Count;
        public string SizeText => Bin.Size(Row.Bytes);
        public int Compressed => Row.Compressed;
        public int Parsed => Row.Parsed;
        public int Failed => Row.Failed;
        public string DetailText => Row.DetailText;
        public string Sample => Row.Sample;
        public string FirstError => Row.FirstError;
    }
}
