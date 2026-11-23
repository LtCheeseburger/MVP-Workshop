using System.Numerics;
using MVPWorkshop.Core.IO;

namespace MVPWorkshop.Core.Formats;

/// <summary>Xbox APT style batches. Coordinates and texture matrices use authored stage/texel units.</summary>
public static class NascarAptShape
{
    public static byte[] Edit(byte[] data, int drawIndex, uint color, int texture, Matrix3x2 matrix, IReadOnlyList<Vector2> vertices)
    {
        var draw = Read(data).ElementAtOrDefault(drawIndex) ?? throw new ArgumentOutOfRangeException(nameof(drawIndex));
        if (texture < 0 || vertices.Count != draw.Vertices.Length) throw new InvalidDataException("Texture IDs must be nonnegative and vertex count must stay unchanged.");
        var result = data.ToArray();
        void U(int p, uint v) => System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(p), v);
        void F(int p, float v) { if (!float.IsFinite(v)) throw new InvalidDataException("Artwork values must be finite."); U(p, BitConverter.SingleToUInt32Bits(v)); }
        U(draw.Offset + 12, color); U(draw.Offset + 16, (uint)texture);
        var values = new[] { matrix.M11, matrix.M12, matrix.M21, matrix.M22, matrix.M31, matrix.M32 };
        for (int i = 0; i < 6; i++) F(draw.Offset + 20 + i * 4, values[i]);
        for (int i = 0; i < vertices.Count; i++) { F(draw.Offset + 44 + i * 8, vertices[i].X); F(draw.Offset + 48 + i * 8, vertices[i].Y); }
        Read(result); return result;
    }
    public sealed record Draw(int Offset, int Kind, uint Color, int Texture, float LineWidth, Matrix3x2 TextureMatrix, Vector2[] Vertices);
    public static IReadOnlyList<Draw> Read(byte[] data)
    {
        int p = 0;
        void Bounds(long length) { if (length < 0 || p < 0 || p > data.Length - length) throw new InvalidDataException("Xbox APT shape is truncated."); }
        int H() { Bounds(2); int n = Bin.U16LE(data, p); p += 2; return n; }
        uint U() { Bounds(4); uint n = Bin.U32LE(data, p); p += 4; return n; }
        float F() { float f = BitConverter.UInt32BitsToSingle(U()); if (!float.IsFinite(f)) throw new InvalidDataException("Invalid Xbox APT shape coordinate."); return f; }
        int styles = H(); var result = new List<Draw>();
        for (int style = 0; style < styles; style++)
        {
            int batches = H();
            for (int batch = 0; batch < batches; batch++)
            {
                int offset = p, primitives = checked((int)U()), kind = checked((int)U());
                float width = F(); uint color = U(); int texture = checked((int)U());
                if (kind > 3) throw new NotSupportedException($"Xbox APT fill {kind} is not decoded.");
                var matrix = new Matrix3x2(F(), F(), F(), F(), F(), F());
                int count = checked(primitives * (kind == 3 ? 2 : 3)); Bounds(count * 8L);
                var vertices = new Vector2[count];
                for (int i = 0; i < count; i++) vertices[i] = new(F(), F());
                result.Add(new(offset, kind, color, texture, width, matrix, vertices));
            }
        }
        if (p != data.Length) throw new InvalidDataException("Xbox APT shape has trailing data.");
        return result;
    }
}
