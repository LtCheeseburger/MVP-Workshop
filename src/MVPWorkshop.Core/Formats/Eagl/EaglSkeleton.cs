using System.Numerics;
using MVPWorkshop.Core.IO;

namespace MVPWorkshop.Core.Formats.Eagl;

public sealed class EaglBone
{
    public required int Index { get; init; }
    public required string Name { get; init; }
    public required int Parent { get; init; }
    public Vector3 Scale { get; init; } = Vector3.One;
    public Quaternion Rotation { get; init; } = Quaternion.Identity;
    public Vector3 Translation { get; init; }
    /// <summary>Stored inverse bind matrix (model space → bone space), row-vector convention.</summary>
    public Matrix4x4 InverseBind { get; init; } = Matrix4x4.Identity;
    /// <summary>Bind-pose world matrix rebuilt from the local transforms.</summary>
    public Matrix4x4 BindWorld { get; set; } = Matrix4x4.Identity;
    public List<int> Children { get; } = [];
}

/// <summary>
/// EAGL <c>__Skeleton</c>: header {u32 hash, 0, u32 boneCount, 0} then boneCount × 0x70-byte records:
/// {float3 scale, s32 parent} {quat xyzw} {float3 translation, u32} {4×4 inverse bind matrix}.
/// World = Scale·Rotation·Translation (local) × parent World; World × InverseBind = identity in the bind pose.
/// </summary>
public sealed class EaglSkeleton
{
    public string Name { get; init; } = "";
    public uint Hash { get; init; }
    public List<EaglBone> Bones { get; } = [];
    /// <summary>Largest |World·InverseBind − I| over all bones; ~0 confirms the decode.</summary>
    public float BindError { get; private set; }

    public static EaglSkeleton? Read(RelocatedObject o, IReadOnlyDictionary<int, string> boneNames)
    {
        var sym = o.Symbols.FirstOrDefault(s => s.Name.StartsWith("__Skeleton:::", StringComparison.Ordinal));
        if (sym.Name is null) return null;
        var at = sym.Value;
        var count = (int)o.U32(at + 8);
        if (count is <= 0 or > 512 || at + 0x10 + count * 0x70 > o.Data.Length) return null;
        var sk = new EaglSkeleton { Name = sym.Name[13..], Hash = o.U32(at) };
        for (var i = 0; i < count; i++)
        {
            var r = at + 0x10 + i * 0x70;
            float F(int k) => o.F32(r + k * 4);
            var m = new Matrix4x4(F(12), F(13), F(14), F(15), F(16), F(17), F(18), F(19), F(20), F(21), F(22), F(23), F(24), F(25), F(26), F(27));
            sk.Bones.Add(new EaglBone
            {
                Index = i, Name = boneNames.TryGetValue(i, out var n) ? n : $"bone{i}",
                Scale = new Vector3(F(0), F(1), F(2)), Parent = BitConverter.ToInt32(o.Data, r + 12),
                Rotation = new Quaternion(F(4), F(5), F(6), F(7)), Translation = new Vector3(F(8), F(9), F(10)),
                InverseBind = m,
            });
        }
        foreach (var b in sk.Bones) if (b.Parent >= 0 && b.Parent < sk.Bones.Count) sk.Bones[b.Parent].Children.Add(b.Index);
        var world = sk.Pose(null);
        float err = 0;
        for (var i = 0; i < count; i++)
        {
            sk.Bones[i].BindWorld = world[i];
            var id = world[i] * sk.Bones[i].InverseBind;
            err = MathF.Max(err, MathF.Abs(id.M11 - 1) + MathF.Abs(id.M22 - 1) + MathF.Abs(id.M33 - 1) + MathF.Abs(id.M41) + MathF.Abs(id.M42) + MathF.Abs(id.M43));
        }
        sk.BindError = err;
        return sk;
    }

    /// <summary>
    /// World matrices. <paramref name="extra"/> rotates a bone (and its children) about the bone's current
    /// position using model-space axes, which is what the pose sliders expect.
    /// </summary>
    public Matrix4x4[] Pose(IReadOnlyDictionary<int, Quaternion>? extra) => Pose(null, null, extra);

    /// <summary>
    /// World matrices from per-bone local overrides (animation samples; null = bind value, NaN translation
    /// components keep the bind component) plus optional model-space pivot rotations on top.
    /// </summary>
    public Matrix4x4[] Pose(Quaternion?[]? localRotation, Vector3?[]? localTranslation, IReadOnlyDictionary<int, Quaternion>? extra)
    {
        var w = new Matrix4x4[Bones.Count];
        for (var i = 0; i < Bones.Count; i++)
        {
            var b = Bones[i];
            var rot = localRotation is not null && i < localRotation.Length && localRotation[i] is { } r ? r : b.Rotation;
            var tr = b.Translation;
            if (localTranslation is not null && i < localTranslation.Length && localTranslation[i] is { } t)
                tr = new Vector3(float.IsNaN(t.X) ? tr.X : t.X, float.IsNaN(t.Y) ? tr.Y : t.Y, float.IsNaN(t.Z) ? tr.Z : t.Z);
            var local = Matrix4x4.CreateScale(b.Scale) * Matrix4x4.CreateFromQuaternion(rot) * Matrix4x4.CreateTranslation(tr);
            w[i] = b.Parent >= 0 && b.Parent < i ? local * w[b.Parent] : local; // parents precede children
            if (extra is not null && extra.TryGetValue(i, out var q))
            {
                var pivot = w[i].Translation;
                w[i] = w[i] * Matrix4x4.CreateTranslation(-pivot) * Matrix4x4.CreateFromQuaternion(q) * Matrix4x4.CreateTranslation(pivot);
            }
        }
        return w;
    }

    /// <summary>Skinning matrices (InverseBind × World) for a pose.</summary>
    public Matrix4x4[] SkinMatrices(Matrix4x4[] world)
    {
        var s = new Matrix4x4[world.Length];
        for (var i = 0; i < s.Length; i++) s[i] = Bones[i].InverseBind * world[i];
        return s;
    }
}

/// <summary>
/// EAGL <c>__Morph</c> (e.g. hihead_morph.ord): {u32 shapeCount, →shape names, →runtime slots, 0, u32 channelCount,
/// →channel names, …}, then per channel and shape a group {u32 shapeIndex, u32 recordCount} of records
/// {u32 0xE0000000|bytes (V3-16) or 0xC0000000|bytes (V3-32), u32 vertexCount, 0, u32 batchIndex, data}.
/// Batch indices refer to the model's vertex-channel table (__Model+0xB8). Coordinates are in quantised units.
/// </summary>
public sealed class EaglMorphSet
{
    public string ModelName { get; init; } = "";
    public List<string> ShapeNames { get; } = [];
    /// <summary>Per shape: (batch index, per-vertex delta in quantised units).</summary>
    public List<List<(int Batch, short[] Deltas)>> Shapes { get; } = [];

    public static EaglMorphSet? Read(RelocatedObject o)
    {
        var sym = o.Symbols.FirstOrDefault(s => s.Name.StartsWith("__Morph:::", StringComparison.Ordinal));
        if (sym.Name is null) return null;
        var at = sym.Value;
        var count = (int)o.U32(at);
        if (count is <= 0 or > 256) return null;
        var set = new EaglMorphSet { ModelName = sym.Name[10..] };
        var names = o.Ptr(at + 4);
        for (var i = 0; i < count; i++)
        {
            set.ShapeNames.Add(names is { } np && o.Ptr(np + i * 4) is { } sp ? Bin.CString(o.Data, sp, 64) : $"shape{i}");
            set.Shapes.Add([]);
        }

        // Coordinate groups start right after the 0x1C-byte header; stop at the first non-V3-16 record (normals channel).
        var p = at + 0x1C;
        while (p + 8 <= o.Data.Length)
        {
            var shape = (int)o.U32(p); var records = (int)o.U32(p + 4);
            if (shape < 0 || shape >= count || records is <= 0 or > 256) break;
            var q = p + 8;
            var ok = true;
            for (var r = 0; r < records; r++)
            {
                var e = o.U32(q);
                if (e >> 24 != 0xE0) { ok = false; break; }
                var bytes = (int)(e & 0xFFFFFF); var n = (int)o.U32(q + 4); var batch = (int)o.U32(q + 12);
                var d = new short[n * 3];
                for (var k = 0; k < n * 3 && q + 16 + k * 2 + 2 <= o.Data.Length; k++) d[k] = Bin.S16LE(o.Data, q + 16 + k * 2);
                set.Shapes[shape].Add((batch, d));
                q = (q + 16 + bytes + 3) & ~3;
            }
            if (!ok) break;
            p = q;
        }
        return set;
    }
}

/// <summary>Applies blend shapes then linear-blend skinning to a decoded mesh.</summary>
public static class EaglDeformer
{
    /// <summary>
    /// Resolve morph records to mesh vertex indices via the model's vertex-channel table (__Model+0xB8):
    /// {0, →"Coordinates", u32 count, 0} then count × {u16 vertexCount, u16 flags, u32 size, u32 posDataOffset, u32 id}.
    /// </summary>
    public static List<(int FirstVertex, short[] Deltas)>[] Bind(RelocatedObject modelObject, EaglModel model, EaglMorphSet morph)
    {
        var res = new List<(int, short[])>[morph.Shapes.Count];
        var table = modelObject.Ptr(model.Offset + 0xB8);
        var offsets = new List<int>();
        if (table is { } t)
        {
            var n = (int)modelObject.U32(t + 8);
            for (var i = 0; i < n && i < 4096; i++) offsets.Add((int)modelObject.U32(t + 0x10 + i * 16 + 8));
        }
        for (var s = 0; s < morph.Shapes.Count; s++)
        {
            res[s] = [];
            foreach (var (batch, deltas) in morph.Shapes[s])
                if (batch < offsets.Count && model.Mesh.BatchByDataOffset.TryGetValue(offsets[batch], out var first))
                    res[s].Add((first, deltas));
        }
        return res;
    }

    public static Vector3[] Deform(EaglModel model, EaglSkeleton? skeleton, IReadOnlyDictionary<int, Quaternion>? pose,
        List<(int FirstVertex, short[] Deltas)>[]? morphs, IReadOnlyList<float>? morphWeights)
    {
        var mesh = model.Mesh;
        var pos = mesh.Positions.ToArray();
        if (morphs is not null && morphWeights is not null)
        {
            var unit = model.QuantScale / 65536f;
            for (var s = 0; s < morphs.Length && s < morphWeights.Count; s++)
            {
                var wgt = morphWeights[s];
                if (wgt == 0) continue;
                foreach (var (first, d) in morphs[s])
                    for (var k = 0; k * 3 + 2 < d.Length && first + k < pos.Length; k++)
                        pos[first + k] += new Vector3(d[k * 3], d[k * 3 + 1], d[k * 3 + 2]) * unit * wgt;
            }
        }
        if (skeleton is null || pose is null || pose.Count == 0 || mesh.Skin.Count != pos.Length) return pos;
        return Skin(model, skeleton, skeleton.Pose(pose), pos);
    }

    /// <summary>Morph (optional) then skin with explicit world matrices (e.g. an animation frame).</summary>
    public static Vector3[] Deform(EaglModel model, EaglSkeleton skeleton, Matrix4x4[] world,
        List<(int FirstVertex, short[] Deltas)>[]? morphs, IReadOnlyList<float>? morphWeights)
    {
        var pos = Deform(model, null, (IReadOnlyDictionary<int, Quaternion>?)null, morphs, morphWeights);
        return model.Mesh.Skin.Count == pos.Length ? Skin(model, skeleton, world, pos) : pos;
    }

    private static Vector3[] Skin(EaglModel model, EaglSkeleton skeleton, Matrix4x4[] world, Vector3[] pos)
    {
        var mesh = model.Mesh;
        var skin = skeleton.SkinMatrices(world);
        for (var i = 0; i < pos.Length; i++)
        {
            var sw = mesh.Skin[i];
            if (sw.B0 < 0 || sw.B0 >= skin.Length) continue;
            var v = Vector3.Transform(pos[i], skin[sw.B0]) * sw.W0;
            if (sw.B1 >= 0 && sw.B1 < skin.Length) v += Vector3.Transform(pos[i], skin[sw.B1]) * sw.W1;
            if (sw.B2 >= 0 && sw.B2 < skin.Length) v += Vector3.Transform(pos[i], skin[sw.B2]) * sw.W2;
            pos[i] = v;
        }
        return pos;
    }
}
