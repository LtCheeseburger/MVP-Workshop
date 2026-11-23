using MVPWorkshop.Core.IO;
using System.Numerics;
using System.Globalization;
using System.Text;

namespace MVPWorkshop.Core.Formats;

public static class TiburonModel
{
    public sealed record Section(string Tag, uint Offset, uint Length, uint Count);
    public sealed record Mesh(string Name, Vector3[] Positions, Vector3[] Normals, Vector2[] Uv, int[] Triangles, string Texture)
    {
        public int Index { get; init; }
        public string MaterialName { get; init; } = "";
    }
    public sealed record Material(string Name, string Shader, string Texture);
    public sealed record Variant(string Name, IReadOnlyList<int> Meshes);
    public sealed record Part(string Name, IReadOnlyList<Variant> Variants);
    public static int DefaultVariant(Part part, string? body = null)
    {
        var values = part.Variants.ToList();
        if (!string.IsNullOrEmpty(body))
        {
            int manufacturer = values.FindIndex(v => v.Name.Equals(body, StringComparison.OrdinalIgnoreCase) && v.Meshes.Count > 0);
            if (manufacturer >= 0) return manufacturer;
        }
        int none = values.FindIndex(v => v.Name.Equals("None", StringComparison.OrdinalIgnoreCase));
        if (none >= 0 && part.Name.Contains("Shades", StringComparison.OrdinalIgnoreCase)) return none;
        int normal = values.FindIndex(v => v.Name.Equals("Normal", StringComparison.OrdinalIgnoreCase) && v.Meshes.Count > 0);
        return normal >= 0 ? normal : Math.Max(0, values.FindIndex(v => v.Meshes.Count > 0));
    }
    public static IReadOnlyList<Part> ReadParts(byte[] data)
    {
        var section = Read(new MemoryByteSource(data)).FirstOrDefault(s => s.Tag == "Swap");
        if (section is null) return [];
        void Bounds(long at, long count) { if (at < section.Offset || count < 0 || at > section.Offset + (long)section.Length || count > section.Offset + (long)section.Length - at) throw new InvalidDataException("TMdl part reference exceeds Swap section."); }
        uint Word(int at) { Bounds(at, 4); return Bin.U32LE(data, at); }
        int p = checked((int)section.Offset); int count = checked((int)Word(p)), groups = checked((int)Word(p + 4)), variants = checked((int)Word(p + 8)), indices = checked((int)Word(p + 12));
        Bounds(groups, count * 20L); var result = new List<Part>();
        for (int i = 0; i < count; i++)
        {
            int at = groups + i * 20, first = Bin.U16LE(data, at + 16), total = Bin.U16LE(data, at + 18);
            Bounds(variants + first * 20L, total * 20L); var options = new List<Variant>();
            for (int j = 0; j < total; j++)
            {
                int opt = variants + (first + j) * 20, start = Bin.U16LE(data, opt + 16), length = Bin.U16LE(data, opt + 18);
                Bounds(indices + start * 2L, length * 2L);
                options.Add(new(Bin.CString(data, opt, 16), Enumerable.Range(0, length).Select(k => (int)Bin.U16LE(data, indices + (start + k) * 2)).ToArray()));
            }
            result.Add(new(Bin.CString(data, at, 16), options));
        }
        return result;
    }
    public static IReadOnlyList<Material> ReadMaterials(byte[] data)
    {
        var section = Read(new MemoryByteSource(data)).FirstOrDefault(s => s.Tag == "Matl");
        if (section is null) return [];
        int p = checked((int)section.Offset);
        void Bounds(long at, long length) { if (at < 0 || length < 0 || at > data.Length || length > data.Length - at) throw new InvalidDataException("TMdl material exceeds file bounds."); }
        uint Word(int at) { Bounds(at, 4); return Bin.U32LE(data, at); }
        string Text(int at) { int pointer = checked((int)Word(at)); Bounds(pointer, 1); return Bin.CString(data, pointer, Math.Min(128, data.Length - pointer)); }
        int count = checked((int)Word(p)); Bounds(p + 4, count * 4L);
        var result = new List<Material>();
        for (int i = 0; i < count; i++)
        {
            int record = checked((int)Word(p + 4 + i * 4)); Bounds(record, 16);
            result.Add(new(Text(record), Text(record + 4), Word(record + 8) == 0 ? "" : Text(record + 12)));
        }
        return result;
    }
    public sealed record Geometry(IReadOnlyList<Mesh> Meshes, IReadOnlyList<string> Limitations);

    public static Geometry ReadGeometry(byte[] data, string? body = null)
    {
        var sections = Read(new MemoryByteSource(data));
        var geom = sections.FirstOrDefault(s => s.Tag == "Geom");
        if (geom is null) return new([], ["No Geom section."]);
        void Bounds(long p, long n) { if (p < 0 || n < 0 || p > data.Length || n > data.Length - p) throw new InvalidDataException("TMdl geometry reference exceeds file bounds."); }
        uint Word(int p) { Bounds(p, 4); return Bin.U32LE(data, p); }
        int Pointer(int p) => checked((int)Word(p));
        float Float(int p) { float v = BitConverter.UInt32BitsToSingle(Word(p)); if (!float.IsFinite(v) || Math.Abs(v) > 10000000) throw new InvalidDataException("Invalid TMdl coordinate."); return v; }
        int offset = checked((int)geom.Offset); Bounds(offset, 16);
        int meshes = Pointer(offset), meshCount = Pointer(offset + 4), streams = Pointer(offset + 8), streamCount = Pointer(offset + 12);
        if (meshCount > 65536 || streamCount > 65536) throw new InvalidDataException("Invalid TMdl geometry count.");
        Bounds(meshes, meshCount * 20L); Bounds(streams, streamCount * 12L);
        var materials = sections.FirstOrDefault(s => s.Tag == "Matl");
        var materialNames = ReadMaterials(data);
        string MaterialTexture(int index)
        {
            if (materials is null) return "";
            int p = checked((int)materials.Offset); int count = Pointer(p);
            if (index < 0 || index >= count) return "";
            int material = Pointer(p + 4 + index * 4); Bounds(material, 16);
            return Word(material + 8) == 0 ? "" : Bin.CString(data, Pointer(material + 12), 128);
        }
        var result = new List<Mesh>(); var limitations = new List<string>();
        Matrix4x4[]? clusters = null;
        var skeleton = sections.FirstOrDefault(s => s.Tag == "Skel");
        var clusterSection = sections.FirstOrDefault(s => s.Tag == "Clst");
        if (skeleton is not null && clusterSection is not null)
        {
            int sk = checked((int)skeleton.Offset), total = Pointer(sk), translations = Pointer(sk + 8), rotations = Pointer(sk + 12), scales = Pointer(sk + 16);
            if (total > 256) throw new InvalidDataException("Invalid skin bone count.");
            Bounds(translations, total * 16L); Bounds(rotations, total * 6L); Bounds(scales, total * 12L);
            var bones = new Matrix4x4[total];
            for (int i = 0; i < total; i++)
            {
                int t = translations + i * 16, r = rotations + i * 6, scale = scales + i * 12;
                float Angle(int at) => unchecked((short)Bin.U16LE(data, at)) * (MathF.PI / 32768f);
                int parent = Bin.U16LE(data, t + 12);
                var local = Matrix4x4.CreateScale(Float(scale), Float(scale + 4), Float(scale + 8))
                    * Matrix4x4.CreateRotationX(Angle(r)) * Matrix4x4.CreateRotationY(Angle(r + 2)) * Matrix4x4.CreateRotationZ(Angle(r + 4))
                    // Skeleton translations are centimetres; TMdl vertex positions use feet.
                    * Matrix4x4.CreateTranslation(Float(t) / 30.48f, Float(t + 4) / 30.48f, Float(t + 8) / 30.48f);
                if (i > 0 && parent < i) local *= bones[parent];
                else if (i > 0 && parent != 65535) throw new InvalidDataException("Invalid skin bone parent.");
                bones[i] = local;
            }
            int cl = checked((int)clusterSection.Offset), records = Pointer(cl), clusterCount = Pointer(cl + 4);
            if (clusterCount > 65536) throw new InvalidDataException("Invalid skin cluster count.");
            Bounds(records, clusterCount * 4L); clusters = new Matrix4x4[clusterCount];
            for (int i = 0; i < clusterCount; i++)
            {
                int at = records + i * 4, bone = data[at], previous = data[at + 1]; float weight = Bin.U16LE(data, at + 2) / 4096f;
                if (bone >= total || weight > 1 || previous != 255 && previous >= i) throw new InvalidDataException("Invalid skin cluster binding.");
                clusters[i] = weight >= 1 || previous == 255 ? bones[bone] : Matrix4x4.Lerp(clusters[previous], bones[bone], weight);
            }
        }
        for (int mesh = 0; mesh < meshCount; mesh++)
        {
            int p = meshes + mesh * 20, draw = Pointer(p);
            if (draw == 0) continue;
            try
            {
                int firstStream = Bin.U16LE(data, p + 14), streamTotal = data[p + 16];
                if (firstStream >= streamCount || streamTotal > streamCount - firstStream) throw new InvalidDataException("Invalid stream range.");
                int st = streams + firstStream * 12; uint format = Word(st + 8);
                int count = Bin.U16LE(data, st + 4), stride = data[st + 6], vertex = Pointer(st) + 12;
                int minimumStride = format switch { 2 => 12, 18 => 24, 0x152 => 36, _ => 0 };
                if (minimumStride == 0 || stride < minimumStride) { limitations.Add($"Mesh {mesh}: vertex declaration 0x{format:X}, stride {stride} is not decoded."); continue; }
                Bounds(vertex, count * (long)stride);
                var positions = new Vector3[count]; var normals = new Vector3[count]; var uv = new Vector2[count];
                for (int i = 0; i < count; i++)
                {
                    int at = vertex + i * stride;
                    positions[i] = new(Float(at), Float(at + 4), Float(at + 8));
                    if (format is 18 or 0x152) normals[i] = new(Float(at + 12), Float(at + 16), Float(at + 20));
                    if (format == 0x152) uv[i] = new(Float(at + 28), Float(at + 32));
                }
                if (streamTotal > 1)
                {
                    int uvStream = body == "Dodge" && streamTotal >= 3 ? 2 : body == "Ford" && streamTotal >= 4 ? 3 : 1;
                    int next = st + uvStream * 12;
                    if (Word(next + 8) == 256 && Bin.U16LE(data, next + 4) == count && data[next + 6] == 8)
                    { int pixels = Pointer(next) + 12; Bounds(pixels, count * 8L); for (int i = 0; i < count; i++) uv[i] = new(Float(pixels + i * 8), Float(pixels + i * 8 + 4)); }
                }
                int bodyMorph = body == "Dodge" ? 1 : body == "Ford" ? 2 : 0;
                if (bodyMorph > 0 && data[p + 19] >= bodyMorph && mesh + bodyMorph < meshCount)
                {
                    // The first two morph children of stock-car panels are Dodge/Ford
                    // shape deltas; later children are damage targets. Each record has
                    // a vertex index, three 12-bit-fraction signed deltas and normal data.
                    int child = meshes + (mesh + bodyMorph) * 20;
                    int morphStream = streams + Bin.U16LE(data, child + 14) * 12;
                    Bounds(morphStream, 12);
                    if (Word(child) == 0 && Word(morphStream + 8) == uint.MaxValue && data[morphStream + 6] == 12 && data[morphStream + 7] == 12)
                    {
                        int records = Bin.U16LE(data, morphStream + 4), delta = Pointer(morphStream) + 12; Bounds(delta, records * 12L);
                        for (int i = 0; i < records; i++)
                        {
                            int at = delta + i * 12, index = Bin.U16LE(data, at);
                            if (index >= count) throw new InvalidDataException("Body morph vertex index out of bounds.");
                            float D(int offset) => unchecked((short)Bin.U16LE(data, at + offset)) / 4096f;
                            positions[index] += new Vector3(D(2), D(4), D(6));
                        }
                    }
                }
                int groups = Pointer(draw); if (groups > 65536) throw new InvalidDataException("Invalid draw group count.");
                Bounds(draw + 4, groups * 4L); var triangles = new List<int>();
                var outPositions = new List<Vector3>(); var outNormals = new List<Vector3>(); var outUv = new List<Vector2>();
                var mapping = new Dictionary<int, int>();
                for (int group = 0; group < groups; group++)
                {
                    var groupPositions = (Vector3[])positions.Clone(); var groupNormals = (Vector3[])normals.Clone();
                    HashSet<int>? mappedVertices = null;
                    if (clusters is not null && format == 0x152 && stride >= 40)
                    {
                        mappedVertices = [];
                        int palette = Pointer(p + 4), palettes = Pointer(palette);
                        if (palettes > 4096) throw new InvalidDataException("Invalid skin palette count.");
                        if (group >= palettes) throw new InvalidDataException("Draw group has no skin palette.");
                        Bounds(palette + 4, palettes * 4L);
                        int table = Pointer(palette + 4 + group * 4), entries = Pointer(table);
                        if (entries > 65536) throw new InvalidDataException("Invalid skin palette size.");
                        Bounds(table + 4, entries * 4L);
                        for (int i = 0; i < entries; i++)
                        {
                            int at = table + 4 + i * 4, key = unchecked((short)Bin.U16LE(data, at)), cluster = Bin.U16LE(data, at + 2);
                            if (cluster >= clusters.Length) throw new InvalidDataException("Skin palette cluster out of bounds.");
                            // Later draw groups upload only changed palette registers.
                            mapping[key] = cluster;
                        }
                        for (int i = 0; i < count; i++)
                        {
                            float register = Float(vertex + i * stride + 36) / 3;
                            if (register != MathF.Truncate(register)) throw new InvalidDataException("Invalid vertex skin register.");
                            if (!mapping.TryGetValue((int)register, out int cluster)) continue; // Other draw groups own these vertices.
                            mappedVertices.Add(i);
                            groupPositions[i] = Vector3.Transform(positions[i], clusters[cluster]);
                            var normal = Vector3.TransformNormal(normals[i], clusters[cluster]); groupNormals[i] = normal.LengthSquared() > 0 ? Vector3.Normalize(normal) : normal;
                        }
                    }
                    int vertexBase = outPositions.Count;
                    outPositions.AddRange(groupPositions); outNormals.AddRange(groupNormals); outUv.AddRange(uv);
                    int indices = Pointer(draw + 4 + group * 4); Bounds(indices, 16);
                    int indexCount = Bin.U16LE(data, indices), flags = Bin.U16LE(data, indices + 2);
                    // This word identifies the draw group's palette, not an index encoding.
                    if (flags != group) throw new NotSupportedException($"Unexpected draw palette {flags} for group {group}.");
                    Bounds(indices + 16, indexCount * 2L);
                    for (int i = 2; i < indexCount; i++)
                    {
                        int a = Bin.U16LE(data, indices + 16 + (i - 2) * 2), b = Bin.U16LE(data, indices + 16 + (i - 1) * 2), c = Bin.U16LE(data, indices + 16 + i * 2);
                        if (a >= count || b >= count || c >= count) throw new InvalidDataException("Index exceeds vertex count.");
                        if (a == b || b == c || a == c) continue;
                        if (mappedVertices is not null && (!mappedVertices.Contains(a) || !mappedVertices.Contains(b) || !mappedVertices.Contains(c))) throw new InvalidDataException("Draw group references an unmapped skin register.");
                        if ((i & 1) != 0) (a, b) = (b, a);
                        triangles.Add(a + vertexBase); triangles.Add(b + vertexBase); triangles.Add(c + vertexBase);
                    }
                }
                if (triangles.Count > 0) result.Add(new($"Mesh {mesh}", outPositions.ToArray(), outNormals.ToArray(), outUv.ToArray(), triangles.ToArray(), MaterialTexture(Bin.U16LE(data, p + 8))) { Index = mesh, MaterialName = materialNames.ElementAtOrDefault(Bin.U16LE(data, p + 8))?.Name ?? "" });
            }
            catch (Exception ex) when (ex is InvalidDataException or NotSupportedException or OverflowException) { limitations.Add($"Mesh {mesh}: {ex.Message}"); }
        }
        return new(result, limitations);
    }

    public static string ToObj(Geometry geometry)
    {
        var output = new StringBuilder("# NASCAR-Workshop decoded TMdl geometry\n"); int vertexBase = 1;
        string F(float value) => value.ToString("R", CultureInfo.InvariantCulture);
        foreach (var mesh in geometry.Meshes)
        {
            output.AppendLine("g " + mesh.Name.Replace(' ', '_'));
            foreach (var p in mesh.Positions) output.AppendLine($"v {F(p.X)} {F(p.Y)} {F(p.Z)}");
            foreach (var p in mesh.Uv) output.AppendLine($"vt {F(p.X)} {F(1 - p.Y)}");
            foreach (var p in mesh.Normals) output.AppendLine($"vn {F(p.X)} {F(p.Y)} {F(p.Z)}");
            for (int i = 0; i < mesh.Triangles.Length; i += 3)
            { var indices = mesh.Triangles.AsSpan(i, 3).ToArray().Select(v => v + vertexBase); output.AppendLine("f " + string.Join(" ", indices.Select(v => $"{v}/{v}/{v}"))); }
            vertexBase += mesh.Positions.Length;
        }
        foreach (var limitation in geometry.Limitations) output.AppendLine("# Omitted: " + limitation);
        return output.ToString();
    }
    public static IReadOnlyList<Section> Read(IByteSource source)
    {
        var header = source.ReadBytes(0, 16);
        if (header.Length != 16 || Bin.FourCC(header) != "TMdl") throw new InvalidDataException("Missing TMdl header.");
        int count = Bin.U16LE(header, 8);
        if (count > 4096 || 16L + count * 16L > source.Length) throw new InvalidDataException("Invalid TMdl section table.");
        var table = source.ReadBytes(16, count * 16);
        var sections = new List<Section>();
        for (int i = 0; i < count; i++)
        {
            int p = i * 16; uint offset = Bin.U32LE(table, p + 4), length = Bin.U32LE(table, p + 8);
            if (offset > source.Length || length > source.Length - offset) throw new InvalidDataException("TMdl section exceeds file bounds.");
            sections.Add(new(Bin.FourCC(table, p), offset, length, Bin.U32LE(table, p + 12)));
        }
        return sections;
    }
}
