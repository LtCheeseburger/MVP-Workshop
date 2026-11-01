using System.Numerics;
using MVPWorkshop.Core.Formats;
using MVPWorkshop.Core.Formats.Eagl;

namespace MVPWorkshop.Core.Editing;

public static class MeshPositionPatch
{
    public static byte[] Apply(byte[] original, byte[] joined, EaglModel model, IReadOnlyList<Vector3> positions)
    {
        var mesh = model.Mesh;
        if (positions.Count != mesh.Positions.Count || mesh.PositionDataOffsets.Count != positions.Count || mesh.PositionDataOffsets.Any(i => i < 0))
            throw new InvalidOperationException("Position replacement supports uncompressed Xbox FLOAT3 streams only. PS2 VIF and quantized Xbox streams remain export-only.");
        var elf = ElfFile.Read(joined); var data = elf.Sections.Single(s => s.Name == ".data");
        var result = original.ToArray(); var writes = new Dictionary<int, Vector3>(); var bounds = mesh.Bounds();
        for (int i = 0; i < positions.Count; i++)
        {
            var p = positions[i]; if (!float.IsFinite(p.X + p.Y + p.Z) || p.X < bounds.Min.X || p.Y < bounds.Min.Y || p.Z < bounds.Min.Z || p.X > bounds.Max.X || p.Y > bounds.Max.Y || p.Z > bounds.Max.Z)
                throw new InvalidDataException("Keep imported positions within the original bounds so native culling remains valid.");
            int offset = checked((int)data.Offset + mesh.PositionDataOffsets[i]);
            if (offset < data.Offset || offset > original.Length - 12 || mesh.PositionDataOffsets[i] > data.Size - 12) throw new InvalidDataException("The position stream is outside the original ORD file; import its ORD partner instead.");
            if (writes.TryGetValue(offset, out var shared) && shared != p) throw new InvalidDataException("Vertices sharing a native stream position must keep identical coordinates.");
            writes[offset] = p;
        }
        foreach (var (offset, p) in writes) { BitConverter.GetBytes(p.X).CopyTo(result, offset); BitConverter.GetBytes(p.Y).CopyTo(result, offset + 4); BitConverter.GetBytes(p.Z).CopyTo(result, offset + 8); }
        // Every untouched byte, including topology, UVs, bones and relocation tables, remains exact.
        return result;
    }
}
