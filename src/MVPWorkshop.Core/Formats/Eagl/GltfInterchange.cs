using System.Numerics;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace MVPWorkshop.Core.Formats.Eagl;

/// <summary>glTF 2.0 mesh/skin export. Imports preserve native topology and vertex identity.</summary>
public static class GltfInterchange
{
    public static void Export(string path, EaglModel model, IReadOnlyDictionary<string, byte[]>? textures = null,
        EaglSkeleton? skeleton = null, EaglAnim? animation = null, EaglAnim? root = null)
    {
        var mesh = model.Mesh;
        if (mesh.Positions.Count == 0 || mesh.Indices.Count == 0) throw new InvalidDataException("The model has no triangles.");
        if (!mesh.Draws.Any(d => d.TriangleCount > 0)) throw new InvalidDataException("The model has no drawable triangles.");
        if (animation is not null && (skeleton is null || animation.SkeletonHash16 != (skeleton.Hash & 65535))) throw new InvalidDataException("Animation skeleton does not match the model.");
        using var stream = new MemoryStream(); using var writer = new BinaryWriter(stream);
        var views = new JsonArray(); var accessors = new JsonArray();
        int Access(float[] data, int width, string type, bool bounds = false)
        {
            int offset = (int)stream.Position; foreach (float value in data) { if (!float.IsFinite(value)) throw new InvalidDataException("Nonfinite vertex or animation value."); writer.Write(value); }
            int view = views.Count; views.Add(new JsonObject { ["buffer"] = 0, ["byteOffset"] = offset, ["byteLength"] = data.Length * 4 });
            var acc = new JsonObject { ["bufferView"] = view, ["componentType"] = 5126, ["count"] = data.Length / width, ["type"] = type };
            if (bounds) { acc["min"] = new JsonArray(Enumerable.Range(0, width).Select(c => JsonValue.Create(Enumerable.Range(0, data.Length / width).Min(i => data[i * width + c]))).ToArray()); acc["max"] = new JsonArray(Enumerable.Range(0, width).Select(c => JsonValue.Create(Enumerable.Range(0, data.Length / width).Max(i => data[i * width + c]))).ToArray()); }
            int index = accessors.Count; accessors.Add(acc); return index;
        }
        int Integers(IEnumerable<int> data, int width, string type, bool shorts = false)
        {
            while (stream.Position % 4 != 0) writer.Write((byte)0);
            int offset = (int)stream.Position; var items = data.ToArray(); foreach (int value in items) { if (value < 0) throw new InvalidDataException("Negative index."); if (shorts) writer.Write(checked((ushort)value)); else writer.Write((uint)value); }
            int view = views.Count; views.Add(new JsonObject { ["buffer"] = 0, ["byteOffset"] = offset, ["byteLength"] = items.Length * (shorts ? 2 : 4) });
            int index = accessors.Count; accessors.Add(new JsonObject { ["bufferView"] = view, ["componentType"] = shorts ? 5123 : 5125, ["count"] = items.Length / width, ["type"] = type });
            while (stream.Position % 4 != 0) writer.Write((byte)0); return index;
        }
        int position = Access(mesh.Positions.SelectMany(v => new[] { v.X, v.Y, v.Z }).ToArray(), 3, "VEC3", true);
        var normals = mesh.ComputeSmoothNormals(); int normal = Access(normals.SelectMany(v => new[] { v.X, v.Y, v.Z }).ToArray(), 3, "VEC3");
        int uv = Access(Enumerable.Range(0, mesh.Positions.Count).SelectMany(i => { var t = i < mesh.Uvs.Count ? mesh.Uvs[i] : Vector2.Zero; return new[] { t.X, t.Y }; }).ToArray(), 2, "VEC2");
        bool skinned = skeleton is not null && mesh.Skin.Count == mesh.Positions.Count && mesh.IsSkinned;
        int joints = -1, weights = -1;
        if (skinned)
        {
            var skin = mesh.Skin;
            if (skin.Any(s => new[] { s.B0, s.B1, s.B2 }.Any(b => b >= skeleton!.Bones.Count))) throw new InvalidDataException("Invalid bone index.");
            joints = Integers(skin.SelectMany(s => new[] { Math.Max(0, s.B0), Math.Max(0, s.B1), Math.Max(0, s.B2), 0 }), 4, "VEC4", true);
            weights = Access(skin.SelectMany(s => s.W0 + s.W1 + s.W2 > 0 ? new[] { s.W0, s.W1, s.W2, 0f } : new[] { 1f, 0f, 0f, 0f }).ToArray(), 4, "VEC4");
        }
        var materials = new JsonArray(); var images = new JsonArray(); var textureRows = new JsonArray(); var primitives = new JsonArray();
        foreach (var draw in mesh.Draws.Where(d => d.TriangleCount > 0))
        {
            var material = new JsonObject { ["name"] = draw.Texture ?? "untextured", ["doubleSided"] = true, ["pbrMetallicRoughness"] = new JsonObject { ["metallicFactor"] = 0, ["roughnessFactor"] = 1 } };
            if (draw.Texture is { } tag && textures?.TryGetValue(tag, out var png) == true)
            {
                int image = images.Count; images.Add(new JsonObject { ["uri"] = "data:image/png;base64," + Convert.ToBase64String(png) }); textureRows.Add(new JsonObject { ["source"] = image });
                material["pbrMetallicRoughness"]!["baseColorTexture"] = new JsonObject { ["index"] = image }; material["alphaMode"] = "MASK"; material["alphaCutoff"] = 0.5;
            }
            int mat = materials.Count; materials.Add(material);
            var attributes = new JsonObject { ["POSITION"] = position, ["NORMAL"] = normal, ["TEXCOORD_0"] = uv };
            if (skinned) { attributes["JOINTS_0"] = joints; attributes["WEIGHTS_0"] = weights; }
            primitives.Add(new JsonObject { ["attributes"] = attributes, ["indices"] = Integers(mesh.Indices.Skip(draw.FirstTriangle * 3).Take(draw.TriangleCount * 3), 1, "SCALAR"), ["material"] = mat, ["mode"] = 4, ["extras"] = new JsonObject { ["nativePart"] = draw.Part, ["nativeDraw"] = draw.Index } });
        }
        var nodes = new JsonArray(new JsonObject { ["name"] = model.Name, ["mesh"] = 0 });
        var sceneNodes = new JsonArray(0); var skins = new JsonArray(); var animations = new JsonArray();
        if (skeleton is not null)
        {
            foreach (var bone in skeleton.Bones)
            {
                nodes.Add(new JsonObject { ["name"] = bone.Name, ["translation"] = new JsonArray(bone.Translation.X, bone.Translation.Y, bone.Translation.Z), ["rotation"] = new JsonArray(bone.Rotation.X, bone.Rotation.Y, bone.Rotation.Z, bone.Rotation.W), ["scale"] = new JsonArray(bone.Scale.X, bone.Scale.Y, bone.Scale.Z), ["children"] = new JsonArray(skeleton.Bones.Where(b => b.Parent == bone.Index).Select(b => JsonValue.Create(b.Index + 1)).ToArray()) });
                if (bone.Parent < 0) sceneNodes.Add(bone.Index + 1);
            }
            if (skinned)
            {
                int inverse = Access(skeleton.Bones.SelectMany(b => { var m = b.InverseBind; return new[] { m.M11,m.M12,m.M13,m.M14,m.M21,m.M22,m.M23,m.M24,m.M31,m.M32,m.M33,m.M34,m.M41,m.M42,m.M43,m.M44 }; }).ToArray(), 16, "MAT4");
                skins.Add(new JsonObject { ["joints"] = new JsonArray(skeleton.Bones.Select(b => JsonValue.Create(b.Index + 1)).ToArray()), ["inverseBindMatrices"] = inverse }); nodes[0]!["skin"] = 0;
            }
            if (animation is not null)
            {
                int frames = Math.Max(1, animation.Frames); if ((long)frames * skeleton.Bones.Count > 2_000_000) throw new InvalidDataException("Clip exceeds the export sampling limit.");
                var rotations = skeleton.Bones.Select(_ => new List<float>()).ToArray(); var translations = skeleton.Bones.Select(_ => new List<float>()).ToArray();
                for (int frame = 0; frame < frames; frame++)
                {
                    var r = new Quaternion?[skeleton.Bones.Count]; var t = new Vector3?[r.Length]; root?.Sample(Math.Min(frame, root.Frames - 1), r, t); animation.Sample(frame, r, t);
                    for (int i = 0; i < r.Length; i++)
                    {
                        var q = Quaternion.Normalize(r[i] ?? skeleton.Bones[i].Rotation);
                        if (rotations[i].Count >= 4) { var previous = rotations[i]; int last = previous.Count - 4; if (Quaternion.Dot(q, new(previous[last], previous[last+1], previous[last+2], previous[last+3])) < 0) q = new(-q.X,-q.Y,-q.Z,-q.W); }
                        var v = t[i] ?? skeleton.Bones[i].Translation; rotations[i].AddRange([q.X,q.Y,q.Z,q.W]); translations[i].AddRange([float.IsNaN(v.X) ? skeleton.Bones[i].Translation.X : v.X,float.IsNaN(v.Y) ? skeleton.Bones[i].Translation.Y : v.Y,float.IsNaN(v.Z) ? skeleton.Bones[i].Translation.Z : v.Z]);
                    }
                }
                int times = Access(Enumerable.Range(0, frames).Select(f => f / 30f).ToArray(), 1, "SCALAR", true); var samplers = new JsonArray(); var channels = new JsonArray();
                for (int i = 0; i < skeleton.Bones.Count; i++) foreach (var entry in new[] { (Data: translations[i], Width: 3, Type: "VEC3", Path: "translation"), (Data: rotations[i], Width: 4, Type: "VEC4", Path: "rotation") })
                { int sampler = samplers.Count; samplers.Add(new JsonObject { ["input"] = times, ["output"] = Access(entry.Data.ToArray(), entry.Width, entry.Type), ["interpolation"] = "LINEAR" }); channels.Add(new JsonObject { ["sampler"] = sampler, ["target"] = new JsonObject { ["node"] = i + 1, ["path"] = entry.Path } }); }
                animations.Add(new JsonObject { ["name"] = animation.Name, ["samplers"] = samplers, ["channels"] = channels });
            }
        }
        var document = new JsonObject { ["asset"] = new JsonObject { ["version"] = "2.0", ["generator"] = "MVP Workshop", ["extras"] = new JsonObject { ["coordinateUnits"] = "native game units; apply scale in destination application" } }, ["scene"] = 0, ["scenes"] = new JsonArray(new JsonObject { ["nodes"] = sceneNodes }), ["nodes"] = nodes, ["meshes"] = new JsonArray(new JsonObject { ["name"] = model.Name, ["primitives"] = primitives }), ["materials"] = materials, ["bufferViews"] = views, ["accessors"] = accessors, ["buffers"] = new JsonArray(new JsonObject { ["byteLength"] = (int)stream.Length, ["uri"] = "data:application/octet-stream;base64," + Convert.ToBase64String(stream.ToArray()) }) };
        if (images.Count > 0) { document["images"] = images; document["textures"] = textureRows; }
        if (skins.Count > 0) document["skins"] = skins;
        if (animations.Count > 0) document["animations"] = animations;
        File.WriteAllText(path, document.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
    }

    public static Vector3[] ReadPositions(string path, EaglMesh native)
    {
        if (new FileInfo(path).Length > 256 * 1024 * 1024) throw new InvalidDataException("Oversized glTF.");
        using var json = JsonDocument.Parse(File.ReadAllText(path)); var doc = json.RootElement;
        if (doc.GetProperty("asset").GetProperty("version").GetString() != "2.0") throw new InvalidDataException("Expected glTF 2.0 JSON (.gltf).");
        if (doc.TryGetProperty("nodes", out var nodes))
        {
            var parents = new Dictionary<int, int>();
            for (int i = 0; i < nodes.GetArrayLength(); i++) if (nodes[i].TryGetProperty("children", out var children)) foreach (var child in children.EnumerateArray()) parents[child.GetInt32()] = i;
            for (int i = 0; i < nodes.GetArrayLength(); i++) if (nodes[i].TryGetProperty("mesh", out _))
            { int at = i; var seen = new HashSet<int>(); while (seen.Add(at)) { var n = nodes[at]; if (new[] { "matrix", "translation", "rotation", "scale" }.Any(k => n.TryGetProperty(k, out _))) throw new InvalidDataException("Apply node transforms in the modeling application before importing native positions."); if (!parents.TryGetValue(at, out at)) break; } }
        }
        var meshes = doc.GetProperty("meshes"); if (meshes.GetArrayLength() != 1) throw new InvalidDataException("Import one mesh, retaining native vertex order/topology.");
        var primitives = meshes[0].GetProperty("primitives"); var draws = native.Draws.Where(d => d.TriangleCount > 0).ToArray();
        if (primitives.GetArrayLength() != draws.Length) throw new InvalidDataException("Material primitive count changed.");
        var buffers = doc.GetProperty("buffers"); var loaded = new Dictionary<int, byte[]>();
        byte[] Buffer(int index)
        {
            if (loaded.TryGetValue(index, out var bytes)) return bytes;
            string uri = buffers[index].GetProperty("uri").GetString()!;
            if (uri.StartsWith("data:application/octet-stream;base64,") || uri.StartsWith("data:application/gltf-buffer;base64,")) bytes = Convert.FromBase64String(uri[(uri.IndexOf(',') + 1)..]);
            else { var full = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(Path.GetFullPath(path))!, Uri.UnescapeDataString(uri))); if (!full.StartsWith(Path.GetDirectoryName(Path.GetFullPath(path))! + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Buffer must be inside the import folder."); if (new FileInfo(full).Length > 256 * 1024 * 1024) throw new InvalidDataException("Oversized buffer."); bytes = File.ReadAllBytes(full); }
            return loaded[index] = bytes;
        }
        (JsonElement A, byte[] Bytes, int Offset, int Stride, int Component) Reader(int accessor, int width)
        {
            var a = doc.GetProperty("accessors")[accessor]; if (a.TryGetProperty("sparse", out _) || a.TryGetProperty("normalized", out var norm) && norm.GetBoolean()) throw new InvalidDataException("Sparse/normalized accessors are not supported for native mesh replacement.");
            var v = doc.GetProperty("bufferViews")[a.GetProperty("bufferView").GetInt32()]; int component = a.GetProperty("componentType").GetInt32(); int size = component == 5123 ? 2 : component == 5121 ? 1 : 4;
            var bytes = Buffer(v.GetProperty("buffer").GetInt32()); int vo = v.TryGetProperty("byteOffset", out var vv) ? vv.GetInt32() : 0; int offset = checked(vo + (a.TryGetProperty("byteOffset", out var off) ? off.GetInt32() : 0)); int stride = v.TryGetProperty("byteStride", out var s) ? s.GetInt32() : size * width;
            long end = offset + (long)Math.Max(0, a.GetProperty("count").GetInt32() - 1) * stride + width * size;
            if (stride < width * size || offset < vo || end > (long)vo + v.GetProperty("byteLength").GetInt32() || end > bytes.Length) throw new InvalidDataException("Accessor outside its buffer view.");
            return (a, bytes, offset, stride, component);
        }
        Vector3[]? positions = null;
        for (int p = 0; p < draws.Length; p++)
        {
            var primitive = primitives[p]; if (primitive.TryGetProperty("mode", out var mode) && mode.GetInt32() != 4) throw new InvalidDataException("Triangles required.");
            var reader = Reader(primitive.GetProperty("attributes").GetProperty("POSITION").GetInt32(), 3);
            if (reader.Component != 5126 || reader.A.GetProperty("type").GetString() != "VEC3" || reader.A.GetProperty("count").GetInt32() != native.Positions.Count) throw new InvalidDataException("Keep the complete native FLOAT VEC3 vertex pool and order.");
            var current = Enumerable.Range(0, native.Positions.Count).Select(i => { int at = reader.Offset + i * reader.Stride; return new Vector3(BitConverter.ToSingle(reader.Bytes, at), BitConverter.ToSingle(reader.Bytes, at + 4), BitConverter.ToSingle(reader.Bytes, at + 8)); }).ToArray();
            if (current.Any(v => !float.IsFinite(v.X + v.Y + v.Z))) throw new InvalidDataException("Nonfinite vertex.");
            if (positions is not null && !positions.SequenceEqual(current)) throw new InvalidDataException("Primitives must share the same native vertex pool."); positions = current;
            var indices = Reader(primitive.GetProperty("indices").GetInt32(), 1);
            if (indices.A.GetProperty("type").GetString() != "SCALAR" || indices.A.GetProperty("count").GetInt32() != draws[p].TriangleCount * 3 || indices.Component is not (5121 or 5123 or 5125)) throw new InvalidDataException("Topology changed.");
            for (int i = 0; i < draws[p].TriangleCount * 3; i++) { int at = indices.Offset + i * indices.Stride; uint value = indices.Component == 5121 ? indices.Bytes[at] : indices.Component == 5123 ? BitConverter.ToUInt16(indices.Bytes, at) : BitConverter.ToUInt32(indices.Bytes, at); if (value != native.Indices[draws[p].FirstTriangle * 3 + i]) throw new InvalidDataException("Native topology/vertex order changed."); }
        }
        return positions ?? throw new InvalidDataException("No mesh primitives.");
    }
}
