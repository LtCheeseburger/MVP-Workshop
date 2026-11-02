using MVPWorkshop.Core.Formats;

namespace MVPWorkshop.Core.Editing;

/// <summary>
/// Matches a folder of audio files to the sounds of one native container (stream, bank or Sony pair).
/// File names follow Export All: <c>&lt;asset stem&gt;_&lt;Title with spaces as underscores&gt;.wav|mp3|ogg</c>,
/// e.g. <c>crowd_Sound_3.wav</c>, <c>intro_Stream_0.ogg</c>, <c>sfx_Sony_sample_12.mp3</c>. Matching is case-insensitive
/// and exact: nothing is guessed, and two inputs for one sound are reported as a collision, never resolved.
/// </summary>
public static class AudioBulkImport
{
    public static readonly string[] Extensions = [".wav", ".mp3", ".ogg"];

    public sealed record Target(int Index, string Title, EaPatchHeader Header, double Duration);

    public enum Status { Matched, Unsupported, Duplicate, Unmatched, UnsupportedInput }

    public sealed record Row(string? File, Target? Target, Status Status, string? Problem)
    {
        public string? Error { get; set; }
        public DecodedAudio? Input { get; set; }
        public bool Ready => Status == Status.Matched && Error is null && Input is not null;
    }

    public static string FileStem(string assetName, string title) => Path.GetFileNameWithoutExtension(assetName) + "_" + title.Replace(' ', '_');

    public static List<Row> Match(string assetName, IReadOnlyList<Target> targets, IEnumerable<string> files)
    {
        var rows = new List<Row>(); var byStem = targets.ToDictionary(t => FileStem(assetName, t.Title), StringComparer.OrdinalIgnoreCase);
        var candidates = new List<(string File, Target Target)>();
        foreach (var file in files.Order(StringComparer.OrdinalIgnoreCase))
        {
            var stem = Path.GetFileNameWithoutExtension(file);
            if (!Extensions.Contains(Path.GetExtension(file), StringComparer.OrdinalIgnoreCase))
            { rows.Add(new(file, byStem.GetValueOrDefault(stem), Status.UnsupportedInput, "Unsupported input type; use WAV, MP3 or OGG.")); continue; }
            if (byStem.TryGetValue(stem, out var target)) candidates.Add((file, target));
            else rows.Add(new(file, null, Status.Unmatched, "No sound named " + stem + " in this asset."));
        }
        foreach (var group in candidates.GroupBy(c => c.Target.Index))
        {
            if (group.Count() > 1) { foreach (var c in group) rows.Add(new(c.File, c.Target, Status.Duplicate, "Ambiguous: " + string.Join(", ", group.Select(g => Path.GetFileName(g.File))) + " all name this sound; keep one.")); continue; }
            var (file, t) = group.Single();
            rows.Add(AudioReplacement.Unsupported(t.Header) is { } why ? new(file, t, Status.Unsupported, why) : new(file, t, Status.Matched, null));
        }
        foreach (var t in targets.Where(t => rows.All(r => r.Target?.Index != t.Index)))
            if (AudioReplacement.Unsupported(t.Header) is { } why) rows.Add(new(null, t, Status.Unsupported, why));
        return rows.OrderBy(r => r.Target?.Index ?? int.MaxValue).ThenBy(r => r.File, StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>Applies every ready row to one copy of the container, in sound order, so multiple sounds in one bank form one edit.</summary>
    public static byte[] Build(byte[] source, IEnumerable<Row> rows, Func<byte[], int, DecodedAudio, byte[]> replace)
    {
        var current = source.ToArray();
        foreach (var row in rows.Where(r => r.Ready).OrderBy(r => r.Target!.Index))
        {
            var next = replace(current, row.Target!.Index, row.Input!);
            if (next.Length != current.Length) throw new InvalidOperationException("Replacement changed the container size; offsets would move.");
            current = next;
        }
        return current;
    }
}
