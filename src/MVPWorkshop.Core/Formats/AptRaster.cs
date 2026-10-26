using System.Numerics;
using MVPWorkshop.Core.Formats.Eagl;

namespace MVPWorkshop.Core.Formats;

/// <summary>Decoded image: straight-alpha BGRA, as produced by <see cref="SshFile.Decode"/>.</summary>
public sealed class AptTexture(int width, int height, byte[] bgra)
{
    public int Width { get; } = width;
    public int Height { get; } = height;
    public byte[] Bgra { get; } = bgra;
}

public readonly record struct AptVertex(float X, float Y, float U, float V, Vector4 Color);

/// <summary>GS texture wrap (TAR <c>PS2EXTOBJ_SetClampH/V</c>) and filter.</summary>
public readonly record struct AptSampler(bool ClampU, bool ClampV, bool Bilinear = true);

/// <summary>
/// PS2 GS alpha blend <c>((A − B) · C) + D</c>. A/B/D select Cs (source), Cd (destination) or 0; C selects As, Ad or FIX/128.
/// APT geometry only ever uses <c>Cs,Cd,As,Cd</c> (source-over) — checked over all 2,304 APT draws — but the full equation is kept so
/// additive/subtractive states from other EAGL content render faithfully.
/// </summary>
public readonly record struct AptBlend(char A, char B, char C, char D, float Fix)
{
    public static readonly AptBlend SourceOver = new('s', 'd', 's', 'd', 1);
    public static readonly AptBlend Additive = new('s', '0', 's', 'd', 1);
    public static readonly AptBlend Opaque = new('s', '0', 'f', '0', 1);

    public static AptBlend From(EaglPrimState state)
    {
        if (state.Equation.Length > 0)
        {
            var p = state.Equation.Replace("EAGL::ALPHA_", "").Split(',');
            if (p.Length >= 4)
            {
                static char Pick(string s) => s switch { "CS" => 's', "CD" => 'd', "AS" => 's', "AD" => 'd', "FIX" => 'f', _ => '0' };
                var fix = p.Length > 4 && float.TryParse(p[4], out var f) ? f / 128f : 1f;
                return new(Pick(p[0]), Pick(p[1]), p[2] == "AD" ? 'a' : p[2] == "FIX" ? 'f' : 's', Pick(p[3]), fix);
            }
        }
        return state.Blend switch { EaglBlend.Additive => Additive, EaglBlend.Opaque => Opaque, _ => SourceOver };
    }
}

/// <summary>
/// Software rasteriser that follows GS conventions: pixel-centre sampling with a top-left fill rule (no anti-aliasing),
/// 8-bit-style modulation <c>Cv = Ct · Cf</c> clamped to 1, texel-unit UVs with clamp/repeat addressing and bilinear filtering,
/// alpha test (alpha ≥ 1/128), then the blend equation above. Colour is carried as straight (non-premultiplied) RGB.
/// </summary>
public sealed class AptSurface
{
    public int Width { get; }
    public int Height { get; }
    private readonly float[] _rgb;
    public AptSurface(int width, int height) { Width = width; Height = height; _rgb = new float[width * height * 3]; }

    public void Clear(float r, float g, float b)
    {
        for (int i = 0; i < _rgb.Length; i += 3) { _rgb[i] = r; _rgb[i + 1] = g; _rgb[i + 2] = b; }
    }

    /// <summary>Per-pixel visibility from clip-depth mask shapes; combine with <see cref="And"/> for nested masks.</summary>
    public bool[] NewMask() => new bool[Width * Height];
    public static bool[]? And(bool[]? a, bool[]? b)
    {
        if (a is null) return b; if (b is null) return a;
        var r = new bool[a.Length]; for (int i = 0; i < r.Length; i++) r[i] = a[i] && b[i]; return r;
    }

    /// <summary>Per-draw colour pipeline: <c>rgba = clamp(texel · vertex · Multiply + Add)</c> (the placement's colour transform).</summary>
    public readonly record struct Pass(AptTexture? Texture, AptSampler Sampler, AptBlend Blend, Vector4 Multiply, Vector4 Add, bool[]? Clip, bool AlphaTest = true);

    private static float Lerp(float a, float b, float t) => a + (b - a) * t;

    private static Vector4 Texel(AptTexture t, int x, int y, AptSampler s)
    {
        x = s.ClampU ? Math.Clamp(x, 0, t.Width - 1) : ((x % t.Width) + t.Width) % t.Width;
        y = s.ClampV ? Math.Clamp(y, 0, t.Height - 1) : ((y % t.Height) + t.Height) % t.Height;
        int i = (y * t.Width + x) * 4;
        return new Vector4(t.Bgra[i + 2], t.Bgra[i + 1], t.Bgra[i], t.Bgra[i + 3]) * (1f / 255f);
    }

    internal static Vector4 Sample(AptTexture t, float u, float v, AptSampler s)
    {
        if (!s.Bilinear) return Texel(t, (int)MathF.Floor(u), (int)MathF.Floor(v), s);
        float fu = u - 0.5f, fv = v - 0.5f;
        int x0 = (int)MathF.Floor(fu), y0 = (int)MathF.Floor(fv);
        float tx = fu - x0, ty = fv - y0;
        var c00 = Texel(t, x0, y0, s); var c10 = Texel(t, x0 + 1, y0, s); var c01 = Texel(t, x0, y0 + 1, s); var c11 = Texel(t, x0 + 1, y0 + 1, s);
        // interpolate premultiplied so transparent texels do not bleed their (meaningless) colour
        static Vector4 P(Vector4 c) => new(c.X * c.W, c.Y * c.W, c.Z * c.W, c.W);
        var m = Vector4.Lerp(Vector4.Lerp(P(c00), P(c10), tx), Vector4.Lerp(P(c01), P(c11), tx), ty);
        return m.W > 1e-5f ? new Vector4(m.X / m.W, m.Y / m.W, m.Z / m.W, m.W) : Vector4.Zero;
    }

    /// <summary>Visit every pixel whose centre the triangle covers (GS top-left rule), with barycentric weights.</summary>
    internal void Cover(Vector2 pa, Vector2 pb, Vector2 pc, Action<int, float, float, float> visit)
    {
        float area = (pb.X - pa.X) * (pc.Y - pa.Y) - (pb.Y - pa.Y) * (pc.X - pa.X);
        if (!float.IsFinite(area) || MathF.Abs(area) < 1e-9f) return;
        float minX = MathF.Min(pa.X, MathF.Min(pb.X, pc.X)), maxX = MathF.Max(pa.X, MathF.Max(pb.X, pc.X));
        float minY = MathF.Min(pa.Y, MathF.Min(pb.Y, pc.Y)), maxY = MathF.Max(pa.Y, MathF.Max(pb.Y, pc.Y));
        if (!float.IsFinite(minX + maxX + minY + maxY)) return;
        int x0 = (int)Math.Max(0, Math.Floor(minX - 0.5f)), x1 = (int)Math.Min(Width - 1, Math.Ceiling(maxX + 0.5f));
        int y0 = (int)Math.Max(0, Math.Floor(minY - 0.5f)), y1 = (int)Math.Min(Height - 1, Math.Ceiling(maxY + 0.5f));
        if (x1 < x0 || y1 < y0) return;
        float sign = area > 0 ? 1 : -1, inv = 1 / area;
        // an edge owns pixel centres lying exactly on it only if it is a top or left edge
        static bool Owns(Vector2 p, Vector2 q, float sign) { float dx = (q.X - p.X) * sign, dy = (q.Y - p.Y) * sign; return dy < 0 || (dy == 0 && dx > 0); }
        bool o0 = Owns(pb, pc, sign), o1 = Owns(pc, pa, sign), o2 = Owns(pa, pb, sign);
        for (int y = y0; y <= y1; y++)
            for (int x = x0; x <= x1; x++)
            {
                var p = new Vector2(x + 0.5f, y + 0.5f);
                float w0 = (pc.X - pb.X) * (p.Y - pb.Y) - (pc.Y - pb.Y) * (p.X - pb.X);
                float w1 = (pa.X - pc.X) * (p.Y - pc.Y) - (pa.Y - pc.Y) * (p.X - pc.X);
                float w2 = (pb.X - pa.X) * (p.Y - pa.Y) - (pb.Y - pa.Y) * (p.X - pa.X);
                if (!((w0 * sign > 0 || (w0 == 0 && o0)) && (w1 * sign > 0 || (w1 == 0 && o1)) && (w2 * sign > 0 || (w2 == 0 && o2)))) continue;
                visit(y * Width + x, w0 * inv, w1 * inv, w2 * inv);
            }
    }

    /// <summary>Mark the pixels a mask triangle covers.</summary>
    public void MaskTriangle(bool[] mask, Vector2 a, Vector2 b, Vector2 c) => Cover(a, b, c, (i, _, _, _) => mask[i] = true);

    /// <summary>Fill a triangle (positions in surface pixels, optionally moved by <paramref name="transform"/>).</summary>
    public void Triangle(in AptVertex a, in AptVertex b, in AptVertex c, in Pass pass, Matrix3x2? transform = null)
    {
        var pa = new Vector2(a.X, a.Y); var pb = new Vector2(b.X, b.Y); var pc = new Vector2(c.X, c.Y);
        if (transform is { } m) { pa = Vector2.Transform(pa, m); pb = Vector2.Transform(pb, m); pc = Vector2.Transform(pc, m); }
        var (va, vb, vc) = (a, b, c); var ps = pass;
        Cover(pa, pb, pc, (index, l0, l1, l2) =>
        {
            if (ps.Clip is { } clip && !clip[index]) return;
            var color = va.Color * l0 + vb.Color * l1 + vc.Color * l2;
            if (ps.Texture is { } tex)
                color *= Sample(tex, va.U * l0 + vb.U * l1 + vc.U * l2, va.V * l0 + vb.V * l1 + vc.V * l2, ps.Sampler);
            color = Vector4.Clamp(color * ps.Multiply + ps.Add, Vector4.Zero, Vector4.One);
            if (ps.AlphaTest && color.W < 1f / 128f) return;
            Blend(index, color, ps.Blend);
        });
    }

    /// <summary>1-pixel line (GS line primitive): one fragment per step along the major axis, colour interpolated.</summary>
    public void Line(in AptVertex a, in AptVertex b, in Pass pass, Matrix3x2? transform = null)
    {
        var pa = new Vector2(a.X, a.Y); var pb = new Vector2(b.X, b.Y);
        if (transform is { } m) { pa = Vector2.Transform(pa, m); pb = Vector2.Transform(pb, m); }
        if (!float.IsFinite(pa.X + pa.Y + pb.X + pb.Y)) return;
        float dx = pb.X - pa.X, dy = pb.Y - pa.Y;
        int steps = (int)Math.Min(8192, Math.Ceiling(Math.Max(Math.Abs(dx), Math.Abs(dy))));
        for (int i = 0; i <= Math.Max(1, steps); i++)
        {
            float t = steps == 0 ? 0 : (float)i / steps;
            int x = (int)Math.Floor(pa.X + dx * t), y = (int)Math.Floor(pa.Y + dy * t);
            if (x < 0 || y < 0 || x >= Width || y >= Height) continue;
            int index = y * Width + x;
            if (pass.Clip is { } clip && !clip[index]) continue;
            var color = Vector4.Clamp(Vector4.Lerp(a.Color, b.Color, t) * pass.Multiply + pass.Add, Vector4.Zero, Vector4.One);
            if (pass.AlphaTest && color.W < 1f / 128f) continue;
            Blend(index, color, pass.Blend);
            if (steps == 0) break;
        }
    }

    internal void Blend(int pixel, Vector4 src, AptBlend eq)
    {
        int o = pixel * 3;
        float sa = src.W;
        for (int k = 0; k < 3; k++)
        {
            float cs = k == 0 ? src.X : k == 1 ? src.Y : src.Z, cd = _rgb[o + k];
            float Pick(char s) => s == 's' ? cs : s == 'd' ? cd : 0;
            float c = eq.C switch { 's' => sa, 'a' => 1f, _ => eq.Fix };   // destination alpha is not kept: Ad = 1 (opaque framebuffer)
            _rgb[o + k] = Math.Clamp((Pick(eq.A) - Pick(eq.B)) * c + Pick(eq.D), 0, 1);
        }
    }

    /// <summary>Composite a straight-alpha image (text rendered elsewhere) 1:1 at a pixel position with the same colour pipeline.</summary>
    public void Blit(AptTexture image, int x, int y, in Pass pass)
    {
        var p = pass with { Texture = image, Sampler = new AptSampler(true, true, false) };
        var white = Vector4.One; int w = image.Width, h = image.Height;
        Triangle(new(x, y, 0, 0, white), new(x + w, y, w, 0, white), new(x, y + h, 0, h, white), p);
        Triangle(new(x + w, y, w, 0, white), new(x + w, y + h, w, h, white), new(x, y + h, 0, h, white), p);
    }

    /// <summary>Opaque BGRA, box-filtered down by <paramref name="factor"/> when the surface was rendered supersampled.</summary>
    public byte[] ToBgra(int factor = 1)
    {
        int w = Width / factor, h = Height / factor;
        var bgra = new byte[w * h * 4];
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                float r = 0, g = 0, b = 0;
                for (int sy = 0; sy < factor; sy++)
                    for (int sx = 0; sx < factor; sx++) { int i = ((y * factor + sy) * Width + x * factor + sx) * 3; r += _rgb[i]; g += _rgb[i + 1]; b += _rgb[i + 2]; }
                float n = factor * factor; int d = (y * w + x) * 4;
                bgra[d] = (byte)MathF.Round(b / n * 255); bgra[d + 1] = (byte)MathF.Round(g / n * 255); bgra[d + 2] = (byte)MathF.Round(r / n * 255); bgra[d + 3] = 255;
            }
        return bgra;
    }
}

