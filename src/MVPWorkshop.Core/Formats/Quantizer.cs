namespace MVPWorkshop.Core.Formats;

/// <summary>Colour reduction for indexed texture replacement. Works on BGRA with straight 8-bit alpha.</summary>
public static class Quantizer
{
    [ThreadStatic] private static int _lastUnique;
    public static int LastUniqueColors => _lastUnique;

    /// <summary>
    /// Palette of at most <paramref name="maxColors"/> entries plus one index per pixel. Exact (palette order = first appearance) when the
    /// picture has few enough distinct colours, otherwise weighted median-cut. Fully transparent pixels collapse to one transparent entry.
    /// </summary>
    public static (byte[] Palette, byte[] Indices) Build(byte[] bgra, int maxColors)
    {
        var n = bgra.Length / 4;
        var colors = new Dictionary<uint, int>();   // packed colour → count (insertion order is first-appearance order)
        for (var i = 0; i < n; i++) { var c = Pack(bgra, i); colors[c] = colors.GetValueOrDefault(c) + 1; }
        _lastUnique = colors.Count;

        var palette = new List<uint>();
        if (colors.Count <= maxColors) palette.AddRange(colors.Keys);
        else palette.AddRange(MedianCut(colors, maxColors));

        var map = new Dictionary<uint, byte>();
        for (var i = 0; i < palette.Count; i++) map.TryAdd(palette[i], (byte)i);
        var idx = new byte[n];
        for (var i = 0; i < n; i++)
        {
            var c = Pack(bgra, i);
            if (!map.TryGetValue(c, out var v)) { v = Nearest(palette, c); map[c] = v; }
            idx[i] = v;
        }
        var pal = new byte[Math.Max(palette.Count, 1) * 4];
        for (var i = 0; i < palette.Count; i++) Unpack(palette[i], pal, i);
        return (pal, idx);
    }

    /// <summary>Nearest-colour mapping onto a fixed palette (only the first <paramref name="limit"/> entries are candidates).</summary>
    public static byte[] MapToPalette(byte[] bgra, byte[] paletteBgra, int limit)
    {
        var palette = new List<uint>();
        for (var i = 0; i < Math.Min(limit, paletteBgra.Length / 4); i++) palette.Add(Pack(paletteBgra, i, false));
        var n = bgra.Length / 4;
        var cache = new Dictionary<uint, byte>();
        for (var i = 0; i < palette.Count; i++) cache.TryAdd(palette[i], (byte)i);
        var idx = new byte[n];
        for (var i = 0; i < n; i++)
        {
            var c = Pack(bgra, i, false);
            if (!cache.TryGetValue(c, out var v)) { v = Nearest(palette, c); cache[c] = v; }
            idx[i] = v;
        }
        return idx;
    }

    private static uint Pack(byte[] bgra, int px, bool collapseInvisible = true)
    {
        var a = bgra[px * 4 + 3];
        // invisible pixels all look alike; keeping their hidden colour would waste palette entries
        return a == 0 && collapseInvisible ? 0u : (uint)(a << 24 | bgra[px * 4 + 2] << 16 | bgra[px * 4 + 1] << 8 | bgra[px * 4]);
    }

    private static void Unpack(uint c, byte[] pal, int i)
    {
        pal[i * 4] = (byte)c; pal[i * 4 + 1] = (byte)(c >> 8); pal[i * 4 + 2] = (byte)(c >> 16); pal[i * 4 + 3] = (byte)(c >> 24);
    }

    private static byte Nearest(List<uint> palette, uint c)
    {
        int best = 0, bestD = int.MaxValue;
        for (var i = 0; i < palette.Count; i++)
        {
            var d = Distance(palette[i], c);
            if (d < bestD) { bestD = d; best = i; if (d == 0) break; }
        }
        return (byte)best;
    }

    private static int Distance(uint a, uint b)
    {
        int da = (int)(a >> 24) - (int)(b >> 24), dr = (int)(a >> 16 & 255) - (int)(b >> 16 & 255), dg = (int)(a >> 8 & 255) - (int)(b >> 8 & 255), db = (int)(a & 255) - (int)(b & 255);
        return dr * dr * 3 + dg * dg * 4 + db * db * 2 + da * da * 4;
    }

    private static List<uint> MedianCut(Dictionary<uint, int> colors, int target)
    {
        var boxes = new List<List<(uint C, int N)>> { colors.Select(kv => (kv.Key, kv.Value)).ToList() };
        while (boxes.Count < target)
        {
            // split the box with the widest weighted spread
            var split = -1; var widest = 0; var channel = 0;
            for (var b = 0; b < boxes.Count; b++)
            {
                if (boxes[b].Count < 2) continue;
                for (var ch = 0; ch < 4; ch++)
                {
                    var lo = 255; var hi = 0;
                    foreach (var (c, _) in boxes[b]) { var v = (int)(c >> ch * 8 & 255); lo = Math.Min(lo, v); hi = Math.Max(hi, v); }
                    var range = (hi - lo) * (ch == 3 ? 2 : 1);
                    if (range > widest) { widest = range; split = b; channel = ch; }
                }
            }
            if (split < 0) break;
            var box = boxes[split];
            box.Sort((x, y) => (int)(x.C >> channel * 8 & 255) - (int)(y.C >> channel * 8 & 255));
            var half = box.Sum(c => (long)c.N) / 2; long run = 0; var cut = 1;
            for (var i = 0; i < box.Count - 1; i++) { run += box[i].N; if (run >= half) { cut = i + 1; break; } cut = i + 1; }
            boxes[split] = box.GetRange(0, cut);
            boxes.Add(box.GetRange(cut, box.Count - cut));
        }
        return boxes.Select(b =>
        {
            long w = b.Sum(c => (long)c.N), a = 0, r = 0, g = 0, bl = 0;
            foreach (var (c, nn) in b) { a += (c >> 24) * nn; r += (c >> 16 & 255) * nn; g += (c >> 8 & 255) * nn; bl += (c & 255) * nn; }
            return (uint)((a + w / 2) / w << 24 | (r + w / 2) / w << 16 | (g + w / 2) / w << 8 | (bl + w / 2) / w);
        }).ToList();
    }
}
