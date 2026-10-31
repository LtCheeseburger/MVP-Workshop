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

    private void InitializeGameLog() => _gameLogTimer.Tick += async (_, _) => await ReadGameLogs();

    private void StartGameLogWatch(string executable)
    {
        StopGameLogWatch(clear: true);
        _gameLogSession = Editor.Session;
        var root = _gameLogSession!.GameRoot;
        var started = DateTime.UtcNow;
        var primary = GameLogReader.LogDirectory(executable);
        var fallback = Path.Combine(Path.GetDirectoryName(executable)!, "logs");
        // Runtime falls back beside the executable if its Documents directory cannot be created.
        _gameLogReaders = new[] { primary, fallback }.Distinct(StringComparer.OrdinalIgnoreCase).Select(dir => new GameLogReader(dir, root, started)).ToList();
        _exitPolls = 0;
        _gameLogTimer.Start();
        Log.Info("Watching game logs: " + string.Join(", ", new[] { primary, fallback }.Distinct()));
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
        if (clear) { _gameLogRows.Clear(); PublishDiagnostics(); }
    }
}
