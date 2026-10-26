using System.IO;
using System.Buffers.Binary;
using System.Windows;
using System.Windows.Media;

namespace MVPWorkshop.App.Viewers;

/// <summary>Read-only TrueType glyf/cmap renderer for game fonts rejected by the Windows font loader.</summary>
internal sealed class AptNativeFont
{
    private readonly byte[] _data;
    private readonly Dictionary<string, int> _tables = [];
    private readonly Dictionary<int, Geometry> _outlines = [];
    private readonly Dictionary<int, int> _characters = [];
    private readonly Dictionary<int, int> _kerning = [];
    private readonly int _loca, _glyf, _hmtx, _metrics, _glyphs;
    private readonly bool _longLoca;
    public List<string> Names { get; } = [];
    public int Units { get; }
    public int Ascent { get; }
    public int LineHeight { get; }
    private int U16(int at) => BinaryPrimitives.ReadUInt16BigEndian(_data.AsSpan(at, 2));
    private int S16(int at) => BinaryPrimitives.ReadInt16BigEndian(_data.AsSpan(at, 2));
    private int U32(int at) => checked((int)BinaryPrimitives.ReadUInt32BigEndian(_data.AsSpan(at, 4)));
    public AptNativeFont(byte[] data)
    {
        _data = data;
        for (int i = 0; i < U16(4); i++) { int at = 12 + i * 16; _tables[System.Text.Encoding.ASCII.GetString(data, at, 4)] = U32(at + 8); }
        if (_tables.TryGetValue("name", out var names))
            for (int i = 0; i < U16(names + 2); i++)
            {
                int record = names + 6 + i * 12;
                if (U16(record + 6) is not (1 or 4 or 16)) continue;
                int offset = names + U16(names + 4) + U16(record + 10), length = U16(record + 8);
                Names.Add((U16(record) is 0 or 3 ? System.Text.Encoding.BigEndianUnicode : System.Text.Encoding.Latin1).GetString(data, offset, length));
            }
        int head = _tables["head"], hhea = _tables["hhea"];
        Units = U16(head + 18); _longLoca = S16(head + 50) != 0;
        Ascent = S16(hhea + 4); LineHeight = Ascent - S16(hhea + 6) + S16(hhea + 8);
        _loca = _tables["loca"]; _glyf = _tables["glyf"]; _hmtx = _tables["hmtx"]; _metrics = U16(hhea + 34); _glyphs = U16(_tables["maxp"] + 4);
        int cmap = _tables["cmap"];
        // Prefer a Unicode format 4 subtable; old game fonts may only expose a Macintosh format 0.
        var subtables = Enumerable.Range(0, U16(cmap + 2)).Select(i => cmap + U32(cmap + 4 + i * 8 + 4)).Distinct().ToList();
        foreach (var table in subtables.OrderBy(t => U16(t) == 4 ? 0 : 1))
        {
            int format = U16(table);
            if (format == 4)
            {
                int count = U16(table + 6) / 2, ends = table + 14, starts = ends + count * 2 + 2, deltas = starts + count * 2, ranges = deltas + count * 2;
                for (int i = 0; i < count; i++)
                    for (int code = U16(starts + i * 2); code <= U16(ends + i * 2) && code < 65535; code++)
                    {
                        int offset = U16(ranges + i * 2), glyph = offset == 0 ? code : U16(ranges + i * 2 + offset + 2 * (code - U16(starts + i * 2)));
                        if (offset == 0 || glyph != 0) glyph = (glyph + S16(deltas + i * 2)) & 65535;
                        if (glyph > 0 && glyph < _glyphs) _characters.TryAdd(code, glyph);
                    }
            }
            else if (format == 0) for (int code = 0; code < 256; code++) if (_data[table + 6 + code] > 0) _characters.TryAdd(code, _data[table + 6 + code]);
        }
        // 'kern' format-0 horizontal pairs, if the face has any
        if (_tables.TryGetValue("kern", out var kern) && U16(kern) == 0)
            for (int t = 0, at = kern + 4; t < U16(kern + 2) && at + 14 <= _data.Length; t++, at += Math.Max(6, U16(at + 2)))
                if (U16(at + 4) >> 8 == 0 && (U16(at + 4) & 1) != 0)
                    for (int k = 0; k < U16(at + 6) && at + 14 + k * 6 + 6 <= _data.Length; k++) _kerning[(U16(at + 14 + k * 6) << 16) | U16(at + 16 + k * 6)] = S16(at + 18 + k * 6);
        if (_characters.Count == 0) throw new InvalidDataException("No supported TrueType character map");
    }
    public IEnumerable<int> Codes => _characters.Keys;
    public bool Has(int code) => _characters.ContainsKey(code);
    public int KerningPairs => _kerning.Count;
    public int Kerning(int left, int right) => _kerning.GetValueOrDefault((left << 16) | right);
    public int Glyph(int code) => _characters.GetValueOrDefault(code);
    public double Advance(int glyph) => U16(_hmtx + Math.Min(glyph, _metrics - 1) * 4);
    private int GlyphOffset(int glyph) => _longLoca ? U32(_loca + glyph * 4) : U16(_loca + glyph * 2) * 2;
    public Geometry Outline(int glyph, int depth = 0)
    {
        if (_outlines.TryGetValue(glyph, out var cached)) return cached;
        if (depth > 16 || glyph < 0 || glyph >= _glyphs) return Geometry.Empty;
        int offset = GlyphOffset(glyph); if (offset == GlyphOffset(glyph + 1)) return Geometry.Empty;
        int at = _glyf + offset, contours = S16(at); at += 10;
        var result = new GeometryGroup { FillRule = FillRule.Nonzero };
        if (contours >= 0)
        {
            var ends = Enumerable.Range(0, contours).Select(i => U16(at + i * 2)).ToArray(); at += contours * 2;
            int instructions = U16(at); at += 2 + instructions;
            int count = ends.Length == 0 ? 0 : ends[^1] + 1;
            var flags = new byte[count]; var points = new Point[count];
            for (int i = 0; i < count; i++) { byte flag = _data[at++]; flags[i] = flag; if ((flag & 8) != 0) { int repeat = _data[at++]; while (repeat-- > 0) flags[++i] = flag; } }
            int x = 0, y = 0;
            for (int i = 0; i < count; i++) { int f = flags[i]; if ((f & 2) != 0) x += ((f & 16) != 0 ? 1 : -1) * _data[at++]; else if ((f & 16) == 0) { x += S16(at); at += 2; } points[i].X = x; }
            for (int i = 0; i < count; i++) { int f = flags[i]; if ((f & 4) != 0) y += ((f & 32) != 0 ? 1 : -1) * _data[at++]; else if ((f & 32) == 0) { y += S16(at); at += 2; } points[i].Y = -y; }
            int first = 0;
            foreach (int end in ends)
            {
                var path = new StreamGeometry { FillRule = FillRule.Nonzero };
                using (var dc = path.Open())
                {
                    bool On(int i) => (flags[i] & 1) != 0;
                    Point Mid(Point a, Point b) => new((a.X + b.X) / 2, (a.Y + b.Y) / 2);
                    var start = On(first) ? points[first] : On(end) ? points[end] : Mid(points[first], points[end]);
                    dc.BeginFigure(start, true, true);
                    int i = first;
                    while (i <= end)
                    {
                        if (On(i)) { dc.LineTo(points[i], true, false); i++; }
                        else
                        {
                            int next = i == end ? first : i + 1;
                            dc.QuadraticBezierTo(points[i], On(next) ? points[next] : Mid(points[i], points[next]), true, false);
                            i += On(next) && next != first ? 2 : 1;
                        }
                    }
                }
                result.Children.Add(path); first = end + 1;
            }
        }
        else
        {
            int flags;
            do
            {
                flags = U16(at); int child = U16(at + 2); at += 4;
                int a, b;
                if ((flags & 1) != 0) { a = S16(at); b = S16(at + 2); at += 4; } else { a = (sbyte)_data[at++]; b = (sbyte)_data[at++]; }
                if ((flags & 2) == 0) throw new NotSupportedException("TrueType point-attached composite glyph");
                double xx = 1, yy = 1, xy = 0, yx = 0;
                double F2() { double value = S16(at) / 16384.0; at += 2; return value; }
                if ((flags & 8) != 0) xx = yy = F2();
                else if ((flags & 64) != 0) { xx = F2(); yy = F2(); }
                else if ((flags & 128) != 0) { xx = F2(); xy = F2(); yx = F2(); yy = F2(); }
                var shape = Outline(child, depth + 1).Clone(); shape.Transform = new MatrixTransform(xx, -xy, -yx, yy, a, -b); result.Children.Add(shape);
            } while ((flags & 32) != 0);
        }
        result.Freeze(); _outlines[glyph] = result; return result;
    }
}


