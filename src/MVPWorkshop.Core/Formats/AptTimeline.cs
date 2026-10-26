using System.Numerics;

namespace MVPWorkshop.Core.Formats;

public sealed record AptInstance(int Character, int Depth, string Name, Matrix3x2 Matrix, uint Tint, uint Additive, int ClipDepth, int Born);

public static class AptTimeline
{
    /// <summary>Replays deltas through a frame. Move-only records retain the prior character and omitted attributes.</summary>
    public static List<AptInstance> Sample(AptCharacter timeline, int frame)
    {
        var display = new SortedDictionary<int, AptInstance>();
        if (timeline.Frames.Count == 0) return [];
        frame = Math.Clamp(frame, 0, timeline.Frames.Count - 1);
        for (int i = 0; i <= frame; i++)
            foreach (var item in timeline.Frames[i].Items)
            {
                if (item.Depth is not { } depth) continue;
                if (item.Type == 4) { display.Remove(depth); continue; }
                if (item.Type != 3) continue;
                display.TryGetValue(depth, out var previous);
                var moved = (item.Flags & 1) != 0;
                if (!moved || previous is null) previous = new AptInstance(-1, depth, "", Matrix3x2.Identity, uint.MaxValue, 0, 0, i);
                var instance = previous with
                {
                    Character = item.Character ?? previous.Character,
                    Matrix = (item.Flags & 4) != 0 ? item.Matrix : previous.Matrix,
                    Tint = (item.Flags & 8) != 0 ? item.Tint : previous.Tint,
                    Additive = (item.Flags & 8) != 0 ? item.Additive : previous.Additive,
                    Name = (item.Flags & 32) != 0 ? item.Name ?? "" : previous.Name,
                    ClipDepth = (item.Flags & 64) != 0 ? item.ClipDepth : previous.ClipDepth,
                    Born = item.Character.HasValue && item.Character != previous.Character ? i : previous.Born,
                };
                if (instance.Character >= 0) display[depth] = instance;
            }
        return display.Values.ToList();
    }
}
