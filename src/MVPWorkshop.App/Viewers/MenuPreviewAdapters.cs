using System.Windows.Threading;
using MVPWorkshop.Core.Formats;

namespace MVPWorkshop.App.Viewers;

public sealed partial class AptSceneView
{
    internal MenuSimulation? Simulation { get; set; }
    internal Func<string, string, string>? PreviewText { get; set; }
    private AptPreviewScript? _interactiveScript;
    private readonly List<MenuControl> _simulationControls = [];
    private bool _pressing;
    private double _simulationTime;
    private readonly HashSet<string> _reported = [];
    internal void RefreshSimulation()
    {
        Render();
        foreach (var note in _interactiveScript?.Limitations ?? [])
            if (_reported.Add(note)) Simulation?.Log("diagnostic", note);
    }
    internal void SimulationEvent(string path, string handler)
    {
        var executed = _interactiveScript?.Dispatch(path, handler) == true;
        Simulation?.Log(executed ? "native script" : "diagnostic", $"{path}.{handler}: {(executed ? "executed" : "no supported authored handler")}");
        Render();
    }
    internal void ActivateSimulation()
    {
        var path = Simulation?.Focus;
        if (path is null) return;
        _pressing = true; SimulationEvent(path, "onPress");
        _pressing = false; SimulationEvent(path, "onRelease");
        foreach (var note in _interactiveScript?.Limitations ?? []) Simulation?.Log("diagnostic", note);
    }
    internal void TickSimulation() { _simulationTime = Math.Min(60000, _simulationTime + Math.Max(1, _root.Apt.FrameTime)); _frame.Value = _frame.Value >= _frame.Maximum ? 0 : _frame.Value + 1; RefreshSimulation(); }
}

public sealed partial class FelView
{
    internal string? FirstVisibleTextName => _layers.Where(l => l.Enabled).SelectMany(l => l.File.Elements).FirstOrDefault(e => e.Kind == "TB" && Visible(e))?.Name;
    internal byte[] SimulationPixels()
    {
        _stage.UpdateLayout();
        var image = new System.Windows.Media.Imaging.RenderTargetBitmap(640, 480, 96, 96, System.Windows.Media.PixelFormats.Pbgra32);
        image.Render(_stage);
        var pixels = new byte[640 * 480 * 4]; image.CopyPixels(pixels, 640 * 4, 0); return pixels;
    }
    internal MenuSimulation? Simulation { get; set; }
    internal Func<string, string, string>? PreviewText { get; set; }
    private bool _pressing;
    private string ControlId(FelElement e) => (_layers.FirstOrDefault(l => l.File.Elements.Contains(e))?.Node.Path ?? "") + ":" + e.Line + ":" + e.Name;
    private string ButtonState(FelElement e)
    {
        if (Simulation is null) return (string?)_state.SelectedItem ?? "NO";
        for (var group = e; group is not null; group = group.Parent)
        {
            var button = group.Kind == "BU" ? group : group.Children.FirstOrDefault(c => c.Kind == "BU");
            if (button is not null) return Simulation.Focus == ControlId(button) ? (_pressing ? "PR" : "HI") : "NO";
        }
        return "NO";
    }
    internal void RefreshSimulation() => Render();
    internal void ActivateSimulation()
    {
        _pressing = true; Render();
        Dispatcher.BeginInvoke(DispatcherPriority.Background, () => { _pressing = false; Render(); });
        Simulation?.Log("diagnostic", "FEL activation: navigation/game callbacks are not decoded; only explicit simulator routes execute");
    }
    internal void TickSimulation() { if (_time.Value < _time.Maximum) _time.Value = Math.Min(_time.Maximum, _time.Value + .033); }
}
