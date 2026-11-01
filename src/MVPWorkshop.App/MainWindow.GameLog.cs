using System.Diagnostics;
using System.IO;
using System.Windows.Threading;
using MVPWorkshop.Core.Editing;

namespace MVPWorkshop.App;

public partial class MainWindow
{
    private readonly DispatcherTimer _gameLogTimer = new() { Interval = TimeSpan.FromSeconds(1) };
    private readonly List<ModDiagnostic> _gameLogRows = [];
    private List<GameLogReader> _gameLogReaders = [];
    private EditSession? _gameLogSession;
    private Process? _testGameProcess;
    private bool _readingGameLog;
    private int _gameLogGeneration;
    private int _exitPolls;
    private string? _launchResultPath;
    private bool _launchResultRead;

    private void InitializeGameLog() => _gameLogTimer.Tick += async (_, _) => await ReadGameLogs();

    private void StartGameLogWatch(string executable, string? logDirectory = null)
    {
        StopGameLogWatch(clear: true);
        _gameLogSession = Editor.Session;
        var root = _gameLogSession!.GameRoot;
        var started = DateTime.UtcNow;
        var primary = logDirectory ?? GameLogReader.LogDirectory(executable);
        _launchResultPath = logDirectory is null ? null : Path.Combine(Path.GetDirectoryName(logDirectory)!, "workshop-launch-result.txt"); _launchResultRead = false;
        var fallback = Path.Combine(Path.GetDirectoryName(executable)!, "logs");
        // Runtime falls back beside the executable if its Documents directory cannot be created.
        var directories = logDirectory is null ? new[] { primary, fallback }.Distinct(StringComparer.OrdinalIgnoreCase).ToList() : [primary];
        _gameLogReaders = directories.Select(dir => new GameLogReader(dir, root, started)).ToList();
        _exitPolls = 0;
        _gameLogTimer.Start();
        Log.Info("Watching game logs: " + string.Join(", ", directories));
    }

    private async Task ReadGameLogs()
    {
        if (_readingGameLog || _gameLogSession is null) return;
        _readingGameLog = true;
        var generation = _gameLogGeneration;
        var readers = _gameLogReaders;
        try
        {
            var rows = await Task.Run(() => readers.SelectMany(reader => reader.ReadNew()).ToList());
            if (generation != _gameLogGeneration) return;
            foreach (var row in rows)
            {
                if (_gameLogRows.Contains(row)) continue;
                _gameLogRows.Add(row);
                if (_gameLogRows.Count > 1000) _gameLogRows.RemoveAt(0);
            }
            if (rows.Count > 0) PublishDiagnostics();
            if (!_launchResultRead && _launchResultPath is { } resultPath && File.Exists(resultPath) && new FileInfo(resultPath).Length < 16384)
            {
                var result = await File.ReadAllLinesAsync(resultPath);
                if (result.Length >= 2 && generation == _gameLogGeneration)
                {
                    _launchResultRead = true;
                    SetStatus("Game target " + result[0] + ": " + result[1]);
                    if (result[0] == "reached") Log.Info("Game target reached: " + result[1]);
                }
            }
            // Keep reading briefly after exit so the runtime's final flush is collected.
            if (_testGameProcess?.HasExited == true && ++_exitPolls >= 3) { _gameLogTimer.Stop(); _testGameProcess.Dispose(); _testGameProcess = null; }
        }
        catch (Exception ex) { Log.Warn("Game log: " + ex.Message); }
        finally { _readingGameLog = false; }
    }

    private void StopGameLogWatch(bool clear)
    {
        _gameLogGeneration++;
        _gameLogTimer.Stop();
        // Keep the launched process identity across project/log-watch changes. Never launch
        // a second process or rewrite its mod merely because its log watcher was stopped.
        _gameLogSession = null;
        _launchResultPath = null; _launchResultRead = false;
        if (clear) { _gameLogRows.Clear(); PublishDiagnostics(); }
    }
}
