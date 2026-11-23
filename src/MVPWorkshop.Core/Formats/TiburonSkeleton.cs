using MVPWorkshop.Core.IO;

namespace MVPWorkshop.Core.Formats;

/// <summary>SKL1 little-endian hierarchy, local translations and fixed bone names.
/// Rotation and binding payloads are preserved but are not interpreted.</summary>
public static class TiburonSkeleton
{
    public sealed record Bone(int Index, string Name, int Parent, float X, float Y, float Z, uint BindingOffset);
    public static IReadOnlyList<Bone> Read(byte[] data)
    {
        void Bounds(long offset, long size)
        { if (offset < 0 || size < 0 || offset > data.Length || size > data.Length - offset) throw new InvalidDataException("Skeleton reference exceeds file bounds."); }
        Bounds(0, 16);
        if (Bin.FourCC(data) != "1LKS") throw new InvalidDataException("Missing SKL1 header.");
        int count = Bin.U16LE(data, 6); uint names = Bin.U32LE(data, 12);
        Bounds(16, count * 16L); Bounds(names, count * 20L);
        var bones = new List<Bone>();
        for (int i = 0; i < count; i++)
        {
            int p = 16 + i * 16, name = checked((int)names + i * 20);
            int parent = unchecked((short)Bin.U16LE(data, p + 12));
            if (parent < -1 || parent >= i) throw new InvalidDataException("Invalid skeleton parent order.");
            float F(int at) { float v = BitConverter.UInt32BitsToSingle(Bin.U32LE(data, at)); if (!float.IsFinite(v)) throw new InvalidDataException("Invalid bone translation."); return v; }
            uint binding = Bin.U32LE(data, name); Bounds(binding, 0);
            bones.Add(new(i, Bin.CString(data, name + 4, 16), parent, F(p), F(p + 4), F(p + 8), binding));
        }
        return bones;
    }
}
