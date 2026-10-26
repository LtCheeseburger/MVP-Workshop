namespace MVPWorkshop.Core.Formats;

public sealed record MenuControl(string Id, double X, double Y, bool Enabled = true);
public sealed record MenuRoute(string Screen, string Control, string Destination, bool Popup);

/// <summary>Explicit host services. Routes never claim to be decoded native script behavior.</summary>
public sealed class MenuSimulation
{
    public string Screen { get; private set; } = "";
    public string? Focus { get; private set; }
    public List<MenuRoute> Routes { get; } = [];
    public List<string> Trace { get; } = [];
    public IReadOnlyList<MenuControl> Controls => _controls;
    private List<MenuControl> _controls = [];
    private readonly Stack<(string Screen, string? Focus, bool Popup)> _history = [];
    public bool IsPopup => _history.TryPeek(out var entry) && entry.Popup;
    public int NavigationDepth => _history.Count;
    public event Action? Changed;
    public void Log(string origin, string message)
    {
        Trace.Add($"{origin}: {message}");
        if (Trace.Count > 1000) Trace.RemoveAt(0);
        Changed?.Invoke();
    }
    public void Open(string screen, bool popup = false)
    {
        if (Screen.Length > 0) _history.Push((Screen, Focus, popup));
        Screen = screen; Focus = null; _controls = [];
        Log("simulated service", $"{(popup ? "popup" : "transition")} → {screen}");
    }
    public bool Back()
    {
        if (!_history.TryPop(out var entry)) { Log("diagnostic", "No previous screen"); return false; }
        Screen = entry.Screen; Focus = entry.Focus; _controls = [];
        Log("simulated service", $"back → {Screen}"); return true;
    }
    public void Reset(string screen)
    {
        _history.Clear(); Screen = screen; Focus = null; _controls = [];
        Log("simulated service", "reset → " + screen);
    }
    public void SetControls(IEnumerable<MenuControl> controls)
    {
        _controls = controls.Where(c => c.Enabled).DistinctBy(c => c.Id).ToList();
        if (!_controls.Any(c => c.Id == Focus))
        {
            var next = _controls.FirstOrDefault()?.Id;
            if (Focus != next) { Focus = next; Log("input", "focus → " + (Focus ?? "<none>")); }
        }
    }
    public void Select(string id)
    {
        if (!_controls.Any(c => c.Id == id)) return;
        Focus = id; Log("input", "focus → " + id);
    }
    public void Move(int dx, int dy)
    {
        var current = _controls.FirstOrDefault(c => c.Id == Focus);
        if (current is null) return;
        var next = _controls.Where(c => c.Id != current.Id)
            .Select(c => (Control: c, Along: (c.X - current.X) * dx + (c.Y - current.Y) * dy,
                Across: Math.Abs((c.X - current.X) * dy - (c.Y - current.Y) * dx)))
            .Where(c => c.Along > .01).OrderBy(c => c.Along + c.Across * 3).FirstOrDefault();
        if (next.Control is not null) Select(next.Control.Id);
    }
    public bool Activate()
    {
        Log("input", "activate " + (Focus ?? "<none>"));
        if (Focus is null) return false;
        var route = Routes.LastOrDefault(r => r.Screen == Screen && r.Control == Focus);
        if (route is null) return false;
        Open(route.Destination, route.Popup); return true;
    }
}
