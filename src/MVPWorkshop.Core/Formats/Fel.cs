using System.Globalization;

namespace MVPWorkshop.Core.Formats;

/// <summary>Indentation in FE layout files defines ownership; coordinates are authored in stage space.</summary>
public sealed class FelElement
{
    public required string Kind { get; init; }
    public required string[] Fields { get; init; }
    public required string Source { get; init; }
    public int Line { get; init; }
    public FelElement? Parent { get; init; }
    public List<FelElement> Children { get; } = [];
    public string Name => Fields.ElementAtOrDefault(0) ?? "";
    public string Field(int i) => Fields.ElementAtOrDefault(i) ?? "";
    public double Number(int i, double fallback = 0) => double.TryParse(Field(i), NumberStyles.Float, CultureInfo.InvariantCulture, out var n) && double.IsFinite(n) ? n : fallback;
    public override string ToString() => $"{Kind}: {Name}";
}

public sealed class FelFile
{
    public List<FelElement> Elements { get; } = [];
    public static FelFile Read(string text)
    {
        var file = new FelFile();
        var stack = new Stack<(int Indent, FelElement Element)>();
        var lines = text.Replace("\r", "").Split('\n');
        for (int i = 0; i < lines.Length; i++)
        {
            var raw = lines[i];
            var line = raw.TrimStart();
            if (line.Length < 3 || line[2] != ':') continue;
            var indent = raw.Length - line.Length;
            while (stack.Count > 0 && stack.Peek().Indent >= indent) stack.Pop();
            var e = new FelElement { Kind = line[..2], Fields = line[3..].Split(','), Source = raw, Line = i + 1, Parent = stack.Count > 0 ? stack.Peek().Element : null };
            e.Parent?.Children.Add(e);
            file.Elements.Add(e);
            stack.Push((indent, e));
        }
        return file;
    }
}
