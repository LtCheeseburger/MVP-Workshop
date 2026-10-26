using System.Numerics;
using MVPWorkshop.Core.IO;

namespace MVPWorkshop.Core.Formats.Eagl;

/// <summary>
/// EAGL animation banks (ANIMS.BIG, symbol <c>__AnimationBank:::NAME</c>). Decoders follow the game's own
/// evaluators in SLUS_215.82 (factory switch at 0x570628; evaluators 0x5725C0, 0x578D70, 0x573DE8, 0x577700).
/// Every node starts with u32 (skeletonHash16 &lt;&lt; 16 | type). All channel outputs are absolute local
/// transforms written into a per-bone record of 12 floats: [0..3] unused here, [4..7] rotation quaternion,
/// [8..10] translation.
/// </summary>
public sealed class EaglAnimBank
{
    public string Name { get; init; } = "";
    public List<EaglAnim> Anims { get; } = [];
    public List<string> Warnings { get; } = [];

    public static bool LooksLikeBank(RelocatedObject o) => o.Symbols.Any(s => s.Name.StartsWith("__AnimationBank:::", StringComparison.Ordinal));

    /// <summary>Bank: {u32 0x1C, u32 count, →?, 0, →anim pointer array, →name pointer array}.</summary>
    public static EaglAnimBank Read(RelocatedObject o)
    {
        var sym = o.Symbols.First(s => s.Name.StartsWith("__AnimationBank:::", StringComparison.Ordinal));
        var bank = new EaglAnimBank { Name = sym.Name[18..] };
        var count = (int)o.U32(sym.Value + 4);
        var anims = o.Ptr(sym.Value + 0x10);
        var names = o.Ptr(sym.Value + 0x14);
        for (var i = 0; i < count && i < 4096; i++)
        {
            if (anims is null || o.Ptr(anims.Value + i * 4) is not { } node) continue;
            var name = names is { } np && o.Ptr(np + i * 4) is { } sp ? Bin.CString(o.Data, sp, 128) : $"anim{i}";
            try { bank.Anims.Add(EaglAnim.Read(o, node, name)); }
            catch (Exception ex) { bank.Warnings.Add($"{name}: {ex.Message}"); }
        }
        return bank;
    }
}

public enum EaglChannelKind { Rotation = 0x12, AxisRotation = 0x13, Translation = 0x14, Scalar = 0x15 }

/// <summary>One decoded channel: key times plus per-track key values (quaternions or float vectors).</summary>
public sealed class EaglChannel
{
    public EaglChannelKind Kind { get; init; }
    public int NodeOffset { get; init; }
    /// <summary>Key times in frames (first key at 0).</summary>
    public int[] Times { get; init; } = [];
    /// <summary>Animated tracks: target (bone index for rotations, float slot = bone·12+k for translation/scalar).</summary>
    public int[] Targets { get; init; } = [];
    /// <summary>[key][track] values; quaternions as (x,y,z,w), translations as (x,y,z,0), scalars in X.</summary>
    public Vector4[][] Keys { get; init; } = [];
    public int[] ConstTargets { get; init; } = [];
    public Vector4[] ConstValues { get; init; } = [];
}

public sealed class EaglAnim
{
    public required string Name { get; init; }
    public int NodeOffset { get; init; }
    public ushort SkeletonHash16 { get; init; }
    public int Frames { get; init; }
    public List<EaglChannel> Channels { get; } = [];
    public List<string> Warnings { get; } = [];

    public string Summary => string.Join(" ", Channels.GroupBy(c => c.Kind).Select(g => $"{g.Key}×{g.Sum(c => c.Targets.Length + c.ConstTargets.Length)}"));

    /// <summary>Compound node (type 0x0F): {hdr, →shared, u16 childCount, u16 frames, →child[childCount]}.</summary>
    public static EaglAnim Read(RelocatedObject o, int node, string name)
    {
        var hdr = o.U32(node);
        var a = new EaglAnim { Name = name, NodeOffset = node, SkeletonHash16 = (ushort)(hdr >> 16), Frames = Bin.U16LE(o.Data, node + 10) };
        if ((hdr & 0xFFFF) != 0x0F) { a.AddChannel(o, node); return a; }
        int children = Bin.U16LE(o.Data, node + 8);
        for (var i = 0; i < children; i++)
            if (o.Ptr(node + 12 + i * 4) is { } c) a.AddChannel(o, c);
        return a;
    }

    private void AddChannel(RelocatedObject o, int n)
    {
        var type = (int)(o.U32(n) & 0xFFFF);
        try
        {
            EaglChannel? ch = type switch
            {
                0x12 => EaglAnimDecoders.Rotation(o, n),
                0x13 => EaglAnimDecoders.AxisRotation(o, n),
                0x14 => EaglAnimDecoders.Vector(o, n, 3, EaglChannelKind.Translation),
                0x15 => EaglAnimDecoders.Vector(o, n, 1, EaglChannelKind.Scalar),
                0x0F => null,
                _ => throw new NotSupportedException($"node type 0x{type:X2}"),
            };
            if (ch is not null) Channels.Add(ch);
            else foreach (var c in Read(o, n, Name).Channels) Channels.Add(c);
        }
        catch (Exception ex) { Warnings.Add($"@0x{n:X} type 0x{type:X2}: {ex.Message}"); }
    }

    /// <summary>
    /// Sample at <paramref name="frame"/> into per-bone local rotation / translation overrides (null = keep bind).
    /// </summary>
    public void Sample(float frame, Quaternion?[] rot, Vector3?[] trans)
    {
        var floats = new Dictionary<int, float>();
        foreach (var ch in Channels)
        {
            var (k0, k1, f) = Locate(ch.Times, frame);
            switch (ch.Kind)
            {
                case EaglChannelKind.Rotation or EaglChannelKind.AxisRotation:
                    for (var t = 0; t < ch.Targets.Length; t++)
                        if (ch.Targets[t] < rot.Length) rot[ch.Targets[t]] = Nlerp(ch.Keys[k0][t], ch.Keys[k1][t], f);
                    for (var t = 0; t < ch.ConstTargets.Length; t++)
                        if (ch.ConstTargets[t] < rot.Length) rot[ch.ConstTargets[t]] = Quaternion.Normalize(Q(ch.ConstValues[t]));
                    break;
                case EaglChannelKind.Translation:
                    for (var t = 0; t < ch.Targets.Length; t++)
                    {
                        var v = Vector4.Lerp(ch.Keys[k0][t], ch.Keys[k1][t], f);
                        floats[ch.Targets[t]] = v.X; floats[ch.Targets[t] + 1] = v.Y; floats[ch.Targets[t] + 2] = v.Z;
                    }
                    for (var t = 0; t < ch.ConstTargets.Length; t++)
                    {
                        var v = ch.ConstValues[t];
                        floats[ch.ConstTargets[t]] = v.X; floats[ch.ConstTargets[t] + 1] = v.Y; floats[ch.ConstTargets[t] + 2] = v.Z;
                    }
                    break;
                case EaglChannelKind.Scalar:
                    for (var t = 0; t < ch.Targets.Length; t++) floats[ch.Targets[t]] = float.Lerp(ch.Keys[k0][t].X, ch.Keys[k1][t].X, f);
                    for (var t = 0; t < ch.ConstTargets.Length; t++) floats[ch.ConstTargets[t]] = ch.ConstValues[t].X;
                    break;
            }
        }
        // float slots: bone·12 + 8..10 = translation x..z (other slots are not used by these banks)
        foreach (var (slot, value) in floats)
        {
            var bone = slot / 12; var comp = slot % 12 - 8;
            if (bone >= trans.Length || comp is < 0 or > 2) continue;
            var v = trans[bone] ?? new Vector3(float.NaN);
            if (comp == 0) v.X = value; else if (comp == 1) v.Y = value; else v.Z = value;
            trans[bone] = v;
        }
    }

    private static Quaternion Q(Vector4 v) => new(v.X, v.Y, v.Z, v.W);

    private static Quaternion Nlerp(Vector4 a, Vector4 b, float f)
    {
        if (Vector4.Dot(a, b) < 0) b = -b;
        var q = Q(Vector4.Lerp(a, b, f));
        return q.LengthSquared() > 1e-12f ? Quaternion.Normalize(q) : Quaternion.Identity;
    }

    private static (int K0, int K1, float F) Locate(int[] times, float frame)
    {
        if (times.Length <= 1 || frame <= times[0]) return (0, 0, 0);
        if (frame >= times[^1]) return (times.Length - 1, times.Length - 1, 0);
        var i = Array.BinarySearch(times, (int)MathF.Floor(frame));
        if (i < 0) i = ~i - 1;
        i = Math.Clamp(i, 0, times.Length - 2);
        var span = times[i + 1] - times[i];
        return (i, i + 1, span <= 0 ? 0 : (frame - times[i]) / span);
    }
}

internal static class EaglAnimDecoders
{
    private static int[] Times(RelocatedObject o, int? table, int keys)
    {
        // The table holds the times of keys 1..n-1 (key 0 is at frame 0). No table = one key per frame.
        var t = new int[keys];
        for (var i = 1; i < keys; i++) t[i] = table is { } p ? Bin.U16LE(o.Data, p + (i - 1) * 2) : i;
        return t;
    }

    /// <summary>6-byte key: three u16; x,y,z = (u16>>4)/2047 − 1, w = (low nibbles of the three) /2047 − 1.</summary>
    private static Vector4 Key6(byte[] d, int p)
    {
        int a = Bin.U16LE(d, p), b = Bin.U16LE(d, p + 2), c = Bin.U16LE(d, p + 4);
        var w = (d[p] & 15) << 8 | (d[p + 2] & 15) << 4 | (d[p + 4] & 15);
        return new Vector4((a >> 4) / 2047f, (b >> 4) / 2047f, (c >> 4) / 2047f, w / 2047f) - Vector4.One;
    }

    private static int Even(int v) => (v + 1) / 2 * 2;

    /// <summary>
    /// Type 0x12 — full quaternions. Node: +4 u16 keys, +6 u8 tracks, +7 u8 constants, +8 →key times,
    /// +0xC →u8 bone list, +0x10 u8 block shift; +0x12 per-track {u16 min[4], u16 range[4]} (min = u/32767 − 1,
    /// range = u/32767); then blocks of 2^shift keys: a 6-byte key per track, then 3 bytes per track per key
    /// (x,y,z = byte>>2, w = the three low 2-bit pairs; delta = min + range·v/63, accumulated).
    /// Constants follow the last block: u8 bone list, then 6-byte keys (2-aligned).
    /// </summary>
    public static EaglChannel Rotation(RelocatedObject o, int n)
    {
        var d = o.Data;
        int keys = Bin.U16LE(d, n + 4), tracks = d[n + 6], consts = d[n + 7], shift = d[n + 0x10];
        var bones = o.Ptr(n + 0xC) is { } bl ? Enumerable.Range(0, tracks).Select(i => (int)d[bl + i]).ToArray() : new int[tracks];
        var min = new Vector4[tracks]; var range = new Vector4[tracks];
        for (var t = 0; t < tracks; t++)
        {
            var e = n + 0x12 + t * 16;
            min[t] = new Vector4(Bin.U16LE(d, e), Bin.U16LE(d, e + 2), Bin.U16LE(d, e + 4), Bin.U16LE(d, e + 6)) / 32767f - Vector4.One;
            range[t] = new Vector4(Bin.U16LE(d, e + 8), Bin.U16LE(d, e + 10), Bin.U16LE(d, e + 12), Bin.U16LE(d, e + 14)) / 32767f;
        }
        var bs = 1 << shift;
        var stride = Even(tracks * ((bs - 1) * 3 + 6));
        var data = n + 0x12 + tracks * 16;
        var values = new Vector4[keys][];
        for (var k = 0; k < keys; k++)
        {
            values[k] = new Vector4[tracks];
            var (blk, off) = (k / bs, k % bs);
            var p = data + blk * stride;
            for (var t = 0; t < tracks; t++)
            {
                var q = Key6(d, p + 6 * t);
                for (var j = 1; j <= off; j++)
                {
                    var q3 = p + 6 * tracks + (j - 1) * tracks * 3 + t * 3;
                    int x = d[q3], y = d[q3 + 1], z = d[q3 + 2];
                    var v = new Vector4(x >> 2, y >> 2, z >> 2, (x & 3) << 4 | (y & 3) << 2 | (z & 3)) / 63f;
                    q += min[t] + range[t] * v;
                }
                values[k][t] = q;
            }
        }
        var (full, rem) = (keys / bs, keys % bs);
        var cl = data + full * stride + (rem > 0 ? tracks * ((rem - 1) * 3 + 6) : 0);
        var cd = Even(cl + consts);
        return new EaglChannel
        {
            Kind = EaglChannelKind.Rotation, NodeOffset = n, Times = Times(o, o.Ptr(n + 8), keys), Targets = bones, Keys = values,
            ConstTargets = Enumerable.Range(0, consts).Select(i => (int)d[cl + i]).ToArray(),
            ConstValues = Enumerable.Range(0, consts).Select(i => Key6(d, cd + 6 * i)).ToArray(),
        };
    }

    /// <summary>
    /// Type 0x13 — single-axis rotations (twist bones). Node: +4 u16 keys, +6 u8 tracks, +7 u8 block shift,
    /// +8 →times, +0xC →u8 bone list, +0x10 per-track 14 bytes {u16 angleA, u16 angleB (u·2π/65536 − π),
    /// u16 min0, u16 min1 (u/32767 − 1), u16 range0, u16 range1 (u/32767), u8 mode, pad}; then blocks:
    /// 2 bytes per track (axis component, w: b·2/255 − 1) then 1 byte per track per key
    /// (hi nibble → axis delta min0 + range0·n/15, lo nibble → w delta min1 + range1·n/15).
    /// Result = post ⊗ normalize(axisQuat) ⊗ pre, with mode 0: axis X, post = Euler(0,A,B);
    /// mode 1: axis Y, pre = Euler(A,0,0), post = Euler(0,0,B); mode 2: axis Z, pre = Euler(A,B,0).
    /// The composition is applied when sampling, so keys hold the composed quaternion of the stored samples.
    /// </summary>
    public static EaglChannel AxisRotation(RelocatedObject o, int n)
    {
        var d = o.Data;
        int keys = Bin.U16LE(d, n + 4), tracks = d[n + 6], shift = d[n + 7];
        var bones = o.Ptr(n + 0xC) is { } bl ? Enumerable.Range(0, tracks).Select(i => (int)d[bl + i]).ToArray() : new int[tracks];
        var pre = new Quaternion[tracks]; var post = new Quaternion[tracks]; var mode = new int[tracks];
        var min = new Vector2[tracks]; var range = new Vector2[tracks];
        for (var t = 0; t < tracks; t++)
        {
            var e = n + 0x10 + t * 14;
            const float k = MathF.PI * 2 / 65536f;
            float a = Bin.U16LE(d, e) * k - MathF.PI, b = Bin.U16LE(d, e + 2) * k - MathF.PI;
            min[t] = new Vector2(Bin.U16LE(d, e + 4), Bin.U16LE(d, e + 6)) / 32767f - Vector2.One;
            range[t] = new Vector2(Bin.U16LE(d, e + 8), Bin.U16LE(d, e + 10)) / 32767f;
            mode[t] = d[e + 12];
            (pre[t], post[t]) = mode[t] switch
            {
                0 => (Quaternion.Identity, Euler(0, a, b)),
                1 => (Euler(a, 0, 0), Euler(0, 0, b)),
                _ => (Euler(a, b, 0), Quaternion.Identity),
            };
        }
        var bs = 1 << shift;
        var stride = Even(tracks * (bs + 1));
        var data = n + 0x10 + tracks * 14;
        var values = new Vector4[keys][];
        for (var key = 0; key < keys; key++)
        {
            values[key] = new Vector4[tracks];
            var (blk, off) = (key / bs, key % bs);
            var p = data + blk * stride;
            for (var t = 0; t < tracks; t++)
            {
                var axis = d[p + 2 * t] * (2f / 255f) - 1;
                var w = d[p + 2 * t + 1] * (2f / 255f) - 1;
                for (var j = 1; j <= off; j++)
                {
                    var b = d[p + 2 * tracks + (j - 1) * tracks + t];
                    axis += min[t].X + range[t].X * ((b >> 4) / 15f);
                    w += min[t].Y + range[t].Y * ((b & 15) / 15f);
                }
                var aq = mode[t] switch { 0 => new Quaternion(axis, 0, 0, w), 1 => new Quaternion(0, axis, 0, w), _ => new Quaternion(0, 0, axis, w) };
                if (aq.LengthSquared() > 1e-12f) aq = Quaternion.Normalize(aq);
                var r = Hamilton(Hamilton(post[t], aq), pre[t]);
                values[key][t] = new Vector4(r.X, r.Y, r.Z, r.W);
            }
        }
        return new EaglChannel { Kind = EaglChannelKind.AxisRotation, NodeOffset = n, Times = Times(o, o.Ptr(n + 8), keys), Targets = bones, Keys = values };
    }

    /// <summary>
    /// Types 0x14 (float3, translations) and 0x15 (scalar). Node: +4 →u16 target list (float slot = bone·12 + k),
    /// +8 →times, +0xC u16 keys, +0xE u16 tracks, +0x10 u8 shift, +0x11 u8 constants; +0x14 per-track
    /// {float min[c], float range[c], u16 dmin[c], u16 dscale[c]}; blocks: u16 per component per track
    /// (min + u·range/65535), then u8 per component per track per key
    /// (delta = (dmin/65535·2 − 1)·range + b·(dscale/65535·2·range)/255, accumulated).
    /// Constants: u16 target list after the last block (2-aligned), then c floats each (4-aligned).
    /// </summary>
    public static EaglChannel Vector(RelocatedObject o, int n, int comps, EaglChannelKind kind)
    {
        var d = o.Data;
        int keys = Bin.U16LE(d, n + 0xC), tracks = Bin.U16LE(d, n + 0xE), shift = d[n + 0x10], consts = d[n + 0x11];
        var targets = o.Ptr(n + 4) is { } tl ? Enumerable.Range(0, tracks).Select(i => (int)Bin.U16LE(d, tl + i * 2)).ToArray() : new int[tracks];
        var rec = comps * 12;
        var mn = new float[tracks, comps]; var full = new float[tracks, comps]; var dmin = new float[tracks, comps]; var dsc = new float[tracks, comps];
        for (var t = 0; t < tracks; t++)
        {
            var e = n + 0x14 + t * rec;
            for (var c = 0; c < comps; c++)
            {
                var m = BitConverter.ToSingle(d, e + c * 4);
                var r = BitConverter.ToSingle(d, e + comps * 4 + c * 4);
                mn[t, c] = m;
                full[t, c] = r / 65535f;
                dmin[t, c] = Bin.U16LE(d, e + comps * 8 + c * 2) / 65535f * 2 * r - r;
                dsc[t, c] = Bin.U16LE(d, e + comps * 10 + c * 2) / 65535f * 2 * r / 255f;
            }
        }
        var bs = 1 << shift;
        var keyBytes = tracks * comps * 2;
        var deltaBytes = tracks * comps;
        var stride = Even(keyBytes + (bs - 1) * deltaBytes);
        var data = n + 0x14 + tracks * rec;
        var values = new Vector4[keys][];
        Span<float> v = stackalloc float[4];
        for (var k = 0; k < keys; k++)
        {
            values[k] = new Vector4[tracks];
            var (blk, off) = (k / bs, k % bs);
            var p = data + blk * stride;
            for (var t = 0; t < tracks; t++)
            {
                v.Clear();
                for (var c = 0; c < comps; c++)
                {
                    var x = mn[t, c] + Bin.U16LE(d, p + (t * comps + c) * 2) * full[t, c];
                    for (var j = 1; j <= off; j++) x += dmin[t, c] + d[p + keyBytes + (j - 1) * deltaBytes + t * comps + c] * dsc[t, c];
                    v[c] = x;
                }
                values[k][t] = new Vector4(v[0], v[1], v[2], v[3]);
            }
        }
        var (fb, rem) = (keys / bs, keys % bs);
        var cl = Even(data + fb * stride + (rem > 0 ? Even(keyBytes + (rem - 1) * deltaBytes) : 0));
        var cf = (cl + consts * 2 + 3) & ~3;
        return new EaglChannel
        {
            Kind = kind, NodeOffset = n, Times = Times(o, o.Ptr(n + 8), keys), Targets = targets, Keys = values,
            ConstTargets = Enumerable.Range(0, consts).Select(i => (int)Bin.U16LE(d, cl + i * 2)).ToArray(),
            ConstValues = Enumerable.Range(0, consts).Select(i =>
            {
                var b = cf + i * comps * 4;
                return comps == 3 ? new Vector4(BitConverter.ToSingle(d, b), BitConverter.ToSingle(d, b + 4), BitConverter.ToSingle(d, b + 8), 0)
                                  : new Vector4(BitConverter.ToSingle(d, b), 0, 0, 0);
            }).ToArray(),
        };
    }

    /// <summary>The game's Euler → quaternion (half angles; cos/sin per axis X, Y, Z).</summary>
    private static Quaternion Euler(float x, float y, float z)
    {
        float cx = MathF.Cos(x / 2), sx = MathF.Sin(x / 2), cy = MathF.Cos(y / 2), sy = MathF.Sin(y / 2), cz = MathF.Cos(z / 2), sz = MathF.Sin(z / 2);
        return new Quaternion(
            cy * sx * cz - sy * cx * sz,
            cy * sx * sz + sy * cx * cz,
            cy * cx * sz - sy * sx * cz,
            cy * cx * cz + sy * sx * sz);
    }

    private static Quaternion Hamilton(Quaternion a, Quaternion b) => new(
        a.W * b.X + a.X * b.W + a.Y * b.Z - a.Z * b.Y,
        a.W * b.Y - a.X * b.Z + a.Y * b.W + a.Z * b.X,
        a.W * b.Z + a.X * b.Y - a.Y * b.X + a.Z * b.W,
        a.W * b.W - a.X * b.X - a.Y * b.Y - a.Z * b.Z);
}
