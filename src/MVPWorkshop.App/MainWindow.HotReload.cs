using System.Diagnostics;
using System.IO;
using MVPWorkshop.Core.Editing;

namespace MVPWorkshop.App;

public partial class MainWindow
{
    private EditSession? _testRunSession;
    private string? _testRunExecutable, _testRunDirectory;
    private bool TestGameRunning => _testGameProcess is { HasExited: false };

    private async Task LaunchOrReloadTest(string executable, EditSession session, TestModPublication publication, bool running, bool supported)
    {
        if (!running)
        {
            StartGameLogWatch(executable);
            var start = new ProcessStartInfo(executable) { WorkingDirectory = Path.GetDirectoryName(executable)! };
            start.ArgumentList.Add("--mod"); start.ArgumentList.Add(publication.Folder);
            if (supported) { start.ArgumentList.Add("--workshop-reload"); start.ArgumentList.Add(publication.RequestPath); }
            try
            {
                _testGameProcess?.Dispose(); _testGameProcess = Process.Start(start);
                _testRunSession = session; _testRunExecutable = executable;
                _testRunDirectory = Path.GetDirectoryName(publication.RequestPath);
            }
            catch { StopGameLogWatch(clear: false); throw; }
            SetStatus($"Started game with {publication.Report.Files.Count} changed file(s)");
            return;
        }
        SetStatus("Published changes; waiting for the game's reload acknowledgement…");
        TestModAcknowledgement? acknowledgement = null;
        for (int i = 0; i < 80 && TestGameRunning && acknowledgement is null; i++)
        {
            await Task.Delay(100);
            try { acknowledgement = TestModPublisher.ReadAcknowledgement(publication.RequestPath, publication.Revision); }
            catch (IOException) { } // Runtime acknowledgement rename can briefly hold the file.
        }
        var applied = acknowledgement?.Status == "applied";
        _gameLogRows.RemoveAll(row => row.Origin == "Game reload"); PublishDiagnostics();
        var message = acknowledgement is null ? "Game did not acknowledge hot reload. The changes are published; check the game log or restart the game."
            : "Game reload: " + acknowledgement.Status + (acknowledgement.Messages.Count == 0 ? "" : ". " + string.Join("; ", acknowledgement.Messages));
        SetStatus(message);
        if (applied) Log.Info(message);
        else
        {
            Log.Warn(message);
            _gameLogRows.Add(new ModDiagnostic(DiagnosticSeverity.Warning, "", "Game reload", message, "Game reload")); PublishDiagnostics();
        }
    }
}
