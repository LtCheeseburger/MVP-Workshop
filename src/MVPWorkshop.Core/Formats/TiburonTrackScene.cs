using System.Numerics;
using MVPWorkshop.Core.IO;

namespace MVPWorkshop.Core.Formats;

/// <summary>Authored TMdl render instances and their world transforms.</summary>
public static class TiburonTrackScene
{
    public sealed record Instance(string Name, Matrix4x4 Transform);
    public static IReadOnlyDictionary<int, IReadOnlyList<Instance>>? Read(byte[] data)
    {
        var section = TiburonModel.Read(new MemoryByteSource(data)).FirstOrDefault(s => s.Tag == "Inst");
        if (section is null) return null;
        void Bounds(int p, long length) { if (p < 0 || length < 0 || p > data.Length - length) throw new InvalidDataException("Track instance reference exceeds file bounds."); }
        int U(int p) { Bounds(p, 4); return checked((int)Bin.U32LE(data, p)); }
        int H(int p) { Bounds(p, 2); return Bin.U16LE(data, p); }
        float F(int p) { Bounds(p, 4); float f = BitConverter.ToSingle(data, p); if (!float.IsFinite(f)) throw new InvalidDataException("Invalid track transform."); return f; }
        int at = checked((int)section.Offset); Bounds(at, 20);
        int meshes = U(at), ranges = U(at + 4), records = U(at + 8), hierarchy = U(at + 16);
        if (meshes > 65536) throw new InvalidDataException("Invalid track mesh count.");
        Bounds(ranges, meshes * 4L); Bounds(hierarchy, 20);
        int total = U(hierarchy), names = U(hierarchy + 4), translations = U(hierarchy + 8), rotations = U(hierarchy + 12), scales = U(hierarchy + 16);
        if (total > 65536) throw new InvalidDataException("Invalid track transform count.");
        Bounds(names, total * 16L); Bounds(translations, total * 16L); Bounds(rotations, total * 6L); Bounds(scales, total * 12L);
        var world = new Matrix4x4[total];
        for (int i = 0; i < total; i++)
        {
            int t = translations + i * 16, r = rotations + i * 6, s = scales + i * 12;
            float Angle(int p) => unchecked((short)H(p)) * (MathF.PI / 32768f);
            // Track geometry and its scene translations are both in centimetres.
            var local = Matrix4x4.CreateScale(F(s), F(s + 4), F(s + 8))
                * Matrix4x4.CreateRotationX(Angle(r)) * Matrix4x4.CreateRotationY(Angle(r + 2)) * Matrix4x4.CreateRotationZ(Angle(r + 4))
                * Matrix4x4.CreateTranslation(F(t), F(t + 4), F(t + 8));
            // Unlike Skel, the fourth translation word contains object metadata,
            // including IDs outside this table. These are world-space placements.
            world[i] = local;
        }
        var result = new Dictionary<int, IReadOnlyList<Instance>>();
        for (int mesh = 0; mesh < meshes; mesh++)
        {
            int first = H(ranges + mesh * 4), end = H(ranges + mesh * 4 + 2);
            if (end < first) throw new InvalidDataException("Invalid track instance range.");
            Bounds(records + first * 16, (end - first) * 16L);
            var instances = new List<Instance>();
            for (int j = first; j < end; j++)
            {
                int p = records + j * 16, transform = U(p + 8);
                if (U(p + 12) != mesh || transform >= total) throw new InvalidDataException("Invalid track instance binding.");
                instances.Add(new(Bin.CString(data, names + transform * 16, 16), world[transform]));
            }
            result[mesh] = instances;
        }
        return result;
    }
}
