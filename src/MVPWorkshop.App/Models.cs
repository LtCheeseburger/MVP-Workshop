using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Windows;
using MVPWorkshop.Core.Formats;
using MVPWorkshop.Core.IO;
using MVPWorkshop.Core.Vfs;

namespace MVPWorkshop.App;

public abstract class Observable : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;
    protected void Raise([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    protected bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value; Raise(name); return true;
    }
}

public sealed record LogLine(DateTime Time, string Level, string Text)
{
    public string Stamp => Time.ToString("HH:mm:ss");
}

public static class Log
{
    public static ObservableCollection<LogLine> Lines { get; } = [];
    private static void Add(string level, string text)
    {
        void Do() { Lines.Add(new LogLine(DateTime.Now, level, text)); while (Lines.Count > 2000) Lines.RemoveAt(0); }
        var d = Application.Current?.Dispatcher;
        if (d is null || d.CheckAccess()) Do(); else d.BeginInvoke(Do);
    }
    public static void Info(string t) => Add("INFO", t);
    public static void Warn(string t) => Add("WARN", t);
    public static void Error(string t) => Add("ERROR", t);
}

/// <summary>Lazy tree node wrapper over <see cref="AssetNode"/>.</summary>
public sealed class TreeItem : Observable
{
    private static readonly TreeItem Placeholder = new();
    private bool _expanded, _selected, _loaded;
    private readonly string? _groupName;
    public TeamProfile? Team { get; init; }
    public TeamProfile? SchoolScope { get; init; }

    public TreeItem(string name, int depth, IEnumerable<TreeItem> children)
    {
        Node = null!; Depth = depth; _groupName = name; _loaded = true;
        Children = new ObservableCollection<TreeItem>(children);
    }

    private TreeItem() { Node = null!; Children = []; }

    public TreeItem(AssetNode node, int depth)
    {
        Node = node; Depth = depth;
        Children = [];
        if (node.IsFolder || IsLikelyContainer(node)) Children.Add(Placeholder);
        else _loaded = true;
    }

    public AssetNode Node { get; }
    public int Depth { get; }
    public ObservableCollection<TreeItem> Children { get; }
    public string Name => _groupName ?? Node?.Name ?? "Loading…";
    public Thickness Indent => new(Depth * 14, 0, 0, 0);
    public string Glyph => Node is null ? (_groupName is null ? "" : "") : Node.IsFolder ? (Node.IsVirtualFolder ? "" : "") : Node.FormatInfo.Glyph;
    public string Meta => Team is not null ? $"{Team.Assets.Count} links" : _groupName == "Teams" && Children.Any(c => c.Team is not null) ? $"{Children.Count(c => c.Team is not null)} schools" : _groupName is not null ? LeafCount.ToString("N0") : Node is null || Node.IsFolder ? "" : Bin.Size(Node.StoredSize);
    public int LeafCount => _groupName is null ? 1 : Children.Sum(c => c.LeafCount);
    public System.Windows.Media.Brush GlyphBrush => (System.Windows.Media.Brush)Application.Current.Resources[
        Node is null ? "Accent" : Node.IsFolder ? "Accent" : CategoryBrush(Node.FormatInfo.Category)];

    public static string CategoryBrush(FormatCategory c) => c switch
    {
        FormatCategory.Archive => "Accent", FormatCategory.Texture => "Good", FormatCategory.Audio => "Info",
        FormatCategory.Code or FormatCategory.Model => "Bad", _ => "Muted"
    };

    public bool IsSelected { get => _selected; set => Set(ref _selected, value); }

    private bool _edited;
    /// <summary>True when the asset (or something inside this archive/folder) has been changed in the editing session.</summary>
    public bool IsEdited => _edited;

    /// <summary>Recompute the edited marker for this item and every loaded descendant.</summary>
    public void RefreshEdited(MVPWorkshop.Core.Editing.EditSession? session)
    {
        var now = Node is not null && session is not null && session.IsAffected(Node);
        if (now != _edited) { _edited = now; Raise(nameof(IsEdited)); }
        if (_loaded) foreach (var c in Children) c.RefreshEdited(session);
    }

    public bool IsExpanded
    {
        get => _expanded;
        set { if (Set(ref _expanded, value) && value) EnsureLoaded(); }
    }

    private static bool IsLikelyContainer(AssetNode n)
    {
        // Avoid reading every file just to draw the tree: only probe extensions that can be containers.
        var e = n.Extension;
        if (n.IsLooseTexturePack) return true;
        return e is ".big" or ".ast" or ".dat" or ".viv" || n.Name.EndsWith(".big", StringComparison.OrdinalIgnoreCase);
    }

    public void EnsureLoaded()
    {
        if (_loaded || Node is null) return;
        _loaded = true;
        Children.Clear();
        try
        {
            if (!Node.IsContainer && !Node.IsLooseTexturePack) return;
            foreach (var c in Node.Children) Children.Add(new TreeItem(c, Depth + 1));
            if (Node.LoadError is { } err) Log.Warn($"{Node.Name}: {err}");
        }
        catch (Exception ex) { Log.Error($"{Node.Name}: {ex.Message}"); }
        Raise(nameof(Glyph));
    }
}

public sealed class DocumentTab : Observable
{
    private string _title = "";
    private bool _edited;
    public required string Title { get => _title; init => _title = value; }
    /// <summary>Shown as a dot on the tab when the asset has pending changes.</summary>
    public bool IsEdited { get => _edited; set { if (Set(ref _edited, value)) Raise(nameof(EditedTip)); } }
    public string EditedTip => "Edited in this project";
    public string ToolTip { get; init; } = "";
    public string Glyph { get; init; } = "";
    public AssetNode? Node { get; init; }
    public bool CanClose { get; init; } = true;
    private FrameworkElement _content = null!;
    public required FrameworkElement Content { get => _content; set => Set(ref _content, value); }
    private List<Prop> _props = [];
    public List<Prop> Props { get => _props; set => Set(ref _props, value); }
}

public sealed class Settings
{
    public string? LastGameFolder { get; set; }
    /// <summary>Game folder to output folder chosen for it (default: a mod_output folder next to the game folder).</summary>
    public Dictionary<string, string> OutputFolders { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public string? LastProject { get; set; }
    public List<string> Recent { get; set; } = [];

    private static string FilePath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "MVP-Workshop", "settings.json");
    private static string LegacyFilePath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "MVP07AssetTool", "settings.json");

    public static Settings Load()
    {
        // One-time migration of the pre-1.0 "MVP07 Asset Tool" settings (recent games, output folders).
        try
        {
            if (!File.Exists(FilePath) && File.Exists(LegacyFilePath))
            {
                Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
                File.Copy(LegacyFilePath, FilePath);
            }
        }
        catch { }
        try { return File.Exists(FilePath) ? JsonSerializer.Deserialize<Settings>(File.ReadAllText(FilePath)) ?? new() : new(); }
        catch { return new(); }
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception ex) { Log.Warn("Could not save settings: " + ex.Message); }
    }

    public void Remember(string folder)
    {
        LastGameFolder = folder;
        Recent.RemoveAll(r => r.Equals(folder, StringComparison.OrdinalIgnoreCase));
        Recent.Insert(0, folder);
        if (Recent.Count > 8) Recent.RemoveRange(8, Recent.Count - 8);
        Save();
    }
}

