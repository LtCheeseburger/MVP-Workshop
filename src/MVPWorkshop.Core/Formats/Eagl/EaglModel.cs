using System.Numerics;

namespace MVPWorkshop.Core.Formats.Eagl;

/// <summary>One draw call: a DMA chain rendered with the texture bound before it.</summary>
public sealed class EaglDraw
{
    public int Index { get; init; }
    public int ChainOffset { get; init; }
    public string? Texture { get; init; }
    public string? PrimState { get; init; }
    /// <summary>The TAR symbol bound before the draw (<c>SHAPENAME=…;PS2EXTOBJ_SetClampH=EAGL::PS2CM_CLAMP;…</c>).</summary>
    public string? TextureSymbol { get; init; }
    /// <summary>GS wrap mode from the TAR symbol: true = clamp, false = repeat (the GS default when absent).</summary>
    public (bool U, bool V) Clamp => (SymbolValue(TextureSymbol, "PS2EXTOBJ_SetClampH") == "EAGL::PS2CM_CLAMP", SymbolValue(TextureSymbol, "PS2EXTOBJ_SetClampV") == "EAGL::PS2CM_CLAMP");
    public static string? SymbolValue(string? symbol, string key)
    {
        if (symbol is null) return null;
        var at = symbol.IndexOf(key + "=", StringComparison.Ordinal);
        if (at < 0) return null;
        var v = symbol[(at + key.Length + 1)..]; var end = v.IndexOf(';');
        return end >= 0 ? v[..end] : v;
    }
    public EaglPrimState State => _state ??= EaglPrimState.Parse(PrimState);
    private EaglPrimState? _state;
    public Matrix3x2? TextureMatrix { get; init; }
    /// <summary>First TAR of a two-texture program (fabric); Texture is the second TAR.</summary>
    public string? BaseTexture { get; init; }
    /// <summary>Native V2-32 secondary UVs in output-slot order, keyed by index-buffer corner.
    /// A source vertex can occupy multiple output slots with different secondary UVs.</summary>
    public Dictionary<int, Vector2> SecondaryUvs { get; } = [];
    /// <summary>GeoPrimState <c>PT_LINELIST</c>: vertices are independent 1-pixel line segments (pairs), not a triangle strip.</summary>
    public bool IsLineList => PrimState?.Contains("PT_LINELIST", StringComparison.Ordinal) == true || PrimState?.EndsWith(":::line_no_depth_write_state", StringComparison.Ordinal) == true;
    public int Part { get; set; }
    /// <summary>Xbox draws: description of the vertex streams and index buffer behind the draw (there is no DMA chain).</summary>
    public string? StreamInfo { get; set; }
    public int FirstLine { get; set; }
    public int LineCount { get; set; }
    /// <summary>Matrix palette: skeleton bone index for each VU matrix slot (program opcode 0x0B).</summary>
    public int[]? Palette { get; init; }
    public int Batches { get; set; }
    public int FirstTriangle { get; set; }
    public int TriangleCount { get; set; }
    public int VertexCount { get; set; }
}

public readonly record struct SkinWeights(int B0, int B1, int B2, float W0, float W1, float W2)
{
    public static readonly SkinWeights None = new(-1, -1, -1, 0, 0, 0);

    public static SkinWeights From((float W, int Slot)[] entry, int[] palette)
    {
        int Bone(int k) => k < entry.Length && entry[k].Slot < palette.Length ? palette[entry[k].Slot] : -1;
        float Wt(int k) => k < entry.Length && entry[k].Slot < palette.Length ? entry[k].W : 0;
        var sum = Wt(0) + Wt(1) + Wt(2);
        if (sum <= 0) return None;
        return new(Bone(0), Bone(1), Bone(2), Wt(0) / sum, Wt(1) / sum, Wt(2) / sum);
    }
}

/// <summary>Decoded, render-ready mesh for one __Model (all draws share one vertex/index pool).</summary>
public sealed class EaglMesh
{
    public List<Vector3> Positions { get; } = [];
    public List<Vector3> Normals { get; } = [];
    public List<Vector2> Uvs { get; } = [];
    /// <summary>Vertex colour, 1.0 = neutral (PS2 0x80).</summary>
    public List<Vector4> Colors { get; } = [];
    /// <summary>Up to three skeleton bones per vertex (bone -1 = unused); empty weights for rigid geometry.</summary>
    public List<SkinWeights> Skin { get; } = [];
    /// <summary>.data offset of each batch's position UNPACK → index of its first vertex (used to apply morph targets).</summary>
    public Dictionary<int, int> BatchByDataOffset { get; } = [];
    public bool IsSkinned => Skin.Any(s => s.B0 >= 0);
    public List<int> Indices { get; } = [];
    /// <summary>Line-list draws: pairs of vertex indices.</summary>
    public List<(int A, int B)> Lines { get; } = [];
    public List<EaglDraw> Draws { get; } = [];
    public List<string> Warnings { get; } = [];

    public (Vector3 Min, Vector3 Max) Bounds()
    {
        if (Positions.Count == 0) return (Vector3.Zero, Vector3.Zero);
        var mn = new Vector3(float.MaxValue); var mx = new Vector3(float.MinValue);
        foreach (var p in Positions) { mn = Vector3.Min(mn, p); mx = Vector3.Max(mx, p); }
        return (mn, mx);
    }

    /// <summary>
    /// Smooth normals computed from the triangles, welding vertices that share a position (batches duplicate
    /// vertices, so per-batch normals would show seams).
    /// </summary>
    public Vector3[] ComputeSmoothNormals() => ComputeSmoothNormals(Positions);

    private int[]? _weld;
    private int _weldCount;

    /// <summary>Vertices sharing a bind-pose position (they keep sharing it under skinning: same weights).</summary>
    private int[] WeldGroups()
    {
        if (_weld is not null && _weld.Length == this.Positions.Count) return _weld;
        var key = new Dictionary<(int, int, int), int>();
        var group = new int[this.Positions.Count];
        for (var i = 0; i < group.Length; i++)
        {
            var p = this.Positions[i];
            var k = ((int)MathF.Round(p.X * 2000), (int)MathF.Round(p.Y * 2000), (int)MathF.Round(p.Z * 2000));
            if (!key.TryGetValue(k, out var g)) key[k] = g = key.Count;
            group[i] = g;
        }
        _weldCount = key.Count;
        return _weld = group;
    }

    public Vector3[] ComputeSmoothNormals(IReadOnlyList<Vector3> Positions)
    {
        var group = WeldGroups();
        var acc = new Vector3[_weldCount];
        var bmin = new Vector3(float.MaxValue); var bmax = new Vector3(float.MinValue);
        foreach (var q in Positions) { bmin = Vector3.Min(bmin, q); bmax = Vector3.Max(bmax, q); }
        var centreOfModel = (bmin + bmax) / 2;
        for (var t = 0; t + 2 < Indices.Count; t += 3)
        {
            int a = Indices[t], b = Indices[t + 1], c = Indices[t + 2];
            var n = Vector3.Cross(Positions[b] - Positions[a], Positions[c] - Positions[a]);
            // strips have inconsistent winding; orient each face normal away from the model centre
            var centre = (Positions[a] + Positions[b] + Positions[c]) / 3;
            if (Vector3.Dot(n, centre - centreOfModel) < 0) n = -n;
            acc[group[a]] += n; acc[group[b]] += n; acc[group[c]] += n;
        }
        var res = new Vector3[Positions.Count];
        for (var i = 0; i < res.Length; i++)
        {
            var n = acc[group[i]];
            res[i] = n.LengthSquared() > 0 ? Vector3.Normalize(n) : Vector3.UnitY;
        }
        return res;
    }

    public string ToObj(string name)
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"# {name} — exported by MVP-Workshop");
        var ci = System.Globalization.CultureInfo.InvariantCulture;
        foreach (var p in Positions) sb.AppendLine(string.Format(ci, "v {0} {1} {2}", p.X, p.Y, p.Z));
        foreach (var t in Uvs) sb.AppendLine(string.Format(ci, "vt {0} {1}", t.X, 1 - t.Y));
        foreach (var n in Normals) sb.AppendLine(string.Format(ci, "vn {0} {1} {2}", n.X, n.Y, n.Z));
        foreach (var d in Draws)
        {
            sb.AppendLine($"g draw{d.Index}_{d.Texture ?? "untextured"}");
            if (d.Texture is not null) sb.AppendLine($"usemtl {d.Texture}");
            for (var t = d.FirstTriangle; t < d.FirstTriangle + d.TriangleCount; t++)
            {
                int a = Indices[t * 3] + 1, b = Indices[t * 3 + 1] + 1, c = Indices[t * 3 + 2] + 1;
                sb.AppendLine($"f {a}/{a}/{a} {b}/{b}/{b} {c}/{c}/{c}");
            }
        }
        return sb.ToString();
    }
}

/// <summary>One entry of the model's geometry list (a toggleable piece such as a helmet, glove or sock style).</summary>
public sealed class EaglPart
{
    public int Index { get; init; }
    public List<int> Programs { get; } = [];
    public List<EaglDraw> Draws { get; } = [];
    public IEnumerable<string> Textures => Draws.Select(d => d.Texture ?? "?").Distinct();
    public string Label => $"Part {Index}: {string.Join(", ", Textures)}";
}

public sealed class EaglModel
{
    public bool IsApt { get; init; }
    public required string Name { get; init; }
    public required int Offset { get; init; }
    public List<string> Variations { get; } = [];
    public Vector3 QuantScale { get; init; }
    public Vector3 QuantOffset { get; init; }
    public Vector3 BoundsMin { get; init; }
    public Vector3 BoundsMax { get; init; }
    public Vector4 Sphere { get; init; }
    public int GeometryKey { get; init; }
    public List<int> Programs { get; } = [];
    public List<EaglPart> Parts { get; } = [];
    /// <summary>Number of geometry variations found in draw jump tables (1 = none).</summary>
    public int VariantCount { get; set; } = 1;
    public List<string> Names { get; } = [];
    public required EaglMesh Mesh { get; init; }
}

public sealed class EaglFile
{
    public List<EaglModel> Models { get; } = [];
    public List<(string Name, int Index)> Bones { get; } = [];
    public string? SkeletonName { get; set; }
    public int SkeletonBoneCount { get; set; }
    public List<string> Textures { get; } = [];
    public List<string> Microcode { get; } = [];
    public string? ToolLibVersion { get; set; }
    public EaglSkeleton? Skeleton { get; set; }
    /// <summary>Morph targets defined in this object (heads keep them in a separate *_morph.ord).</summary>
    public EaglMorphSet? Morphs { get; set; }
    public RelocatedObject? Object { get; set; }
}

public enum EaglBlend { Opaque, Alpha, Additive, Subtractive }

/// <summary>
/// What a GeoPrimState symbol asks of the GS, parsed from its name (<c>RUNTIME_ALLOC::SetAlphaBlendMode=EAGL::ABM_ADD;…</c>)
/// or recognised from the handful of named states. PS2 blend is <c>((A − B) · C &gt;&gt; 7) + D</c> over Cs/Cd/As/FIX.
/// </summary>
public sealed record EaglPrimState(EaglBlend Blend, bool Textured, bool AlphaTest, bool DepthWrite, string Equation)
{
    public static readonly EaglPrimState Default = new(EaglBlend.Opaque, true, false, true, "");

    public static EaglPrimState Parse(string? symbol)
    {
        if (string.IsNullOrEmpty(symbol)) return Default;
        var s = symbol[(symbol.LastIndexOf(":::", StringComparison.Ordinal) is var i and >= 0 ? i + 3 : 0)..];
        switch (s)
        {
            case "clip_texture_blend_state" or "crowd_state" or "crowdpoly_state" or "jumbo_state": return new(EaglBlend.Alpha, true, true, true, "");
            case "glower_state": return new(EaglBlend.Additive, true, false, false, "");
            case "line_no_depth_write_state": return new(EaglBlend.Alpha, false, false, false, "");
        }
        string? Get(string key)
        {
            var at = s.IndexOf(key + "=", StringComparison.Ordinal);
            if (at < 0) return null;
            var v = s[(at + key.Length + 1)..];
            var end = v.IndexOf(';');
            return end >= 0 ? v[..end] : v;
        }
        var textured = Get("SetTextureEnable") != "false";
        var alphaTest = Get("SetAlphaTestEnable") == "true" || (Get("SetAlphaCompareValue") is not null && Get("SetAlphaTestEnable") != "false");
        var transparent = Get("SetTransparencyMethod") is not "EAGL::TM_OPAQUE";
        var blend = Get("SetAlphaBlendMode") switch
        {
            "EAGL::ABM_ADD" => EaglBlend.Additive,
            "EAGL::ABM_BLEND" when transparent => EaglBlend.Alpha,
            _ => EaglBlend.Opaque,
        };
        var eq = Get("PS2EXTOBJ_SetAlphaBlend") ?? "";
        if (eq.Length > 0)
        {
            // (A, B, C, D, FIX): Cs,Cd,As,Cd = standard alpha blend; Cs,0,FIX,Cd = add; Cd,Cs,FIX,0 = subtract
            var p = eq.Replace("EAGL::ALPHA_", "").Split(',');
            if (p.Length >= 4)
                blend = (p[0], p[1], p[3]) switch
                {
                    ("CS", "CD", "CD") => EaglBlend.Alpha,
                    ("CS", "ZERO", "CD") => EaglBlend.Additive,
                    ("CD", "CS", _) => EaglBlend.Subtractive,
                    _ => blend,
                };
        }
        return new(blend, textured, alphaTest, Get("SetDepthTestMethod") is not "EAGL::DTM_ALWAYS", eq);
    }

    public override string ToString() =>
        $"{Blend}{(Textured ? "" : ", untextured")}{(AlphaTest ? ", alpha test" : "")}{(DepthWrite ? "" : ", no depth")}{(Equation.Length > 0 ? $" ({Equation.Replace("EAGL::ALPHA_", "")})" : "")}";
}


