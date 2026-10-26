using System.Globalization;

namespace MVPWorkshop.Core.Formats;

public sealed record FelTrack(int From, int To, double Start, double End);

/// <summary>Author-authored scene snapshots are sparse overrides, never independent visible screens.</summary>
public sealed class FelAnimation
{
    public required FelElement Scene { get; init; }
    public required FelElement Definition { get; init; }
    public required FelFile File { get; init; }
    public List<FelTrack> Tracks { get; } = [];
    public double Duration => Math.Max(.001, Tracks.Select(t => t.End).DefaultIfEmpty(1).Max());
    public override string ToString() => $"{Scene.Name} / {Definition.Name}";

    public static IEnumerable<FelAnimation> Read(FelFile file)
    {
        foreach (var scene in file.Elements.Where(e => e.Kind == "SC" && e.Number(14) == 0))
            foreach (var definition in scene.Children.Where(e => e.Kind == "KA"))
            {
                var animation = new FelAnimation { Scene = scene, Definition = definition, File = file };
                for (int i = 0; i < Math.Min(4096, definition.Number(3)); i++)
                {
                    var at = 4 + i * 8;
                    if (at + 7 >= definition.Fields.Length) break;
                    var start = definition.Number(at + 4); var end = definition.Number(at + 5);
                    if (end >= start) animation.Tracks.Add(new FelTrack((int)definition.Number(at + 2), (int)definition.Number(at + 3), start, end));
                }
                if (animation.Tracks.Count > 0) yield return animation;
            }
    }

    public Dictionary<FelElement, string[]> Sample(double time)
    {
        var result = new Dictionary<FelElement, string[]>();
        var originals = Descendants(Scene).GroupBy(Key).ToDictionary(g => g.Key, g => g.First());
        foreach (var track in Tracks.Where(t => time >= t.Start).OrderBy(t => t.Start))
        {
            var from = Snapshot(track.From, originals); var to = Snapshot(track.To, originals);
            var amount = track.End <= track.Start ? 1 : Math.Clamp((time - track.Start) / (track.End - track.Start), 0, 1);
            foreach (var key in from.Keys.Concat(to.Keys).Distinct())
            {
                if (!originals.TryGetValue(key, out var original)) continue;
                var a = from.GetValueOrDefault(key, original); var b = to.GetValueOrDefault(key, original);
                var fields = (string[])(amount < 1 ? a.Fields : b.Fields).Clone();
                foreach (var index in AnimatedFields(original.Kind))
                    if (index < fields.Length)
                        fields[index] = (a.Number(index) + (b.Number(index) - a.Number(index)) * amount).ToString("R", CultureInfo.InvariantCulture);
                result[original] = fields;
            }
        }
        return result;
    }

    private Dictionary<string, FelElement> Snapshot(int index, Dictionary<string, FelElement> originals)
    {
        var scene = index == 0 ? Scene : File.Elements.FirstOrDefault(e => e.Kind == "SC" && e.Name == Scene.Name + index);
        var result = new Dictionary<string, FelElement>();
        if (scene is null) return result;
        foreach (var element in Descendants(scene))
        {
            var key = Key(element);
            if (originals.ContainsKey(key)) { result[key] = element; continue; }
            // Platform variants can reuse element names; each receives the pose and visibility selects the active variant.
            foreach (var match in originals.Keys.Where(k => k.EndsWith("/" + key, StringComparison.Ordinal))) result[match] = element;
        }
        return result;
    }
    public static IEnumerable<FelElement> Descendants(FelElement element)
    {
        foreach (var child in element.Children) { yield return child; foreach (var descendant in Descendants(child)) yield return descendant; }
    }
    private static IEnumerable<int> AnimatedFields(string kind) => kind switch
    {
        "SH" => new[] { 3, 4, 5, 6, 17, 25 },
        "GR" or "GG" or "FR" => new[] { 3, 4, 5, 6, 11 },
        _ => new[] { 3, 4, 5, 6 },
    };
    private static string Key(FelElement element)
    {
        var parts = new Stack<string>();
        for (var p = element; p is not null && p.Kind != "SC"; p = p.Parent) parts.Push(p.Kind + ":" + p.Name);
        return string.Join('/', parts);
    }
}
