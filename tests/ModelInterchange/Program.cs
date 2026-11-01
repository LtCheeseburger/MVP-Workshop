using System.Numerics;
using System.Text.Json.Nodes;
using MVPWorkshop.Core.Formats.Eagl;
using MVPWorkshop.Core.Editing;
using MVPWorkshop.Core.Vfs;

var folder = Path.Combine(Path.GetTempPath(), "mvp-gltf-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(folder);
int failed = 0;
void Check(string name, bool value) { Console.WriteLine($"{(value ? "PASS" : "FAIL")} {name}"); if (!value) failed++; }
try
{
    var mesh = new EaglMesh(); mesh.Positions.AddRange([Vector3.Zero, Vector3.UnitX, Vector3.UnitY]); mesh.Uvs.AddRange([Vector2.Zero, Vector2.UnitX, Vector2.UnitY]); mesh.Indices.AddRange([0,1,2]); mesh.Draws.Add(new EaglDraw { Index = 0, Texture = "fabric", TriangleCount = 1 });
    var model = new EaglModel { Name = "triangle", Offset = 0, Mesh = mesh }; string path = Path.Combine(folder, "triangle.gltf");
    GltfInterchange.Export(path, model); Check("static glTF round trip preserves native vertex identities", GltfInterchange.ReadPositions(path, mesh).SequenceEqual(mesh.Positions));
    var json = JsonNode.Parse(File.ReadAllText(path))!; Check("glTF exports normals UVs materials and indexed triangles", json["meshes"]![0]!["primitives"]![0]!["attributes"]!["NORMAL"] is not null && json["materials"]!.AsArray().Count == 1);
    json["nodes"]![0]!["translation"] = new JsonArray(1,2,3); File.WriteAllText(path, json.ToJsonString()); bool rejected = false; try { GltfInterchange.ReadPositions(path, mesh); } catch (InvalidDataException) { rejected = true; } Check("unapplied transforms are rejected", rejected);
    var skeleton = new EaglSkeleton { Hash = 12 }; skeleton.Bones.Add(new EaglBone { Index = 0, Name = "root", Parent = -1 });
    mesh.Skin.AddRange(Enumerable.Repeat(new SkinWeights(0,-1,-1,1,0,0),3));
    var clip = new EaglAnim { Name = "move", SkeletonHash16 = 12, Frames = 3 };
    clip.Channels.Add(new EaglChannel { Kind = EaglChannelKind.Translation, Targets = [8], Times = [0,2], Keys = [[new Vector4(0,0,0,0)], [new Vector4(2,0,0,0)]] });
    GltfInterchange.Export(path, model, skeleton: skeleton, animation: clip); json = JsonNode.Parse(File.ReadAllText(path))!;
    Check("animated glTF contains skin joint hierarchy and sampled clip", json["skins"]!.AsArray().Count == 1 && json["animations"]![0]!["channels"]!.AsArray().Count == 2 && json["nodes"]!.AsArray().Count == 2);
    var buffer = Convert.FromBase64String(json["buffers"]![0]!["uri"]!.GetValue<string>().Split(',')[1]);
    float[] Values(int index, int width) { var accessor = json["accessors"]![index]!; var view = json["bufferViews"]![accessor["bufferView"]!.GetValue<int>()]!; int at = view["byteOffset"]!.GetValue<int>(); return Enumerable.Range(0, accessor["count"]!.GetValue<int>() * width).Select(i => BitConverter.ToSingle(buffer, at + i*4)).ToArray(); }
    var sampler = json["animations"]![0]!["samplers"]![0]!;
    Check("clip export samples native translation slots at 30 fps", Values(sampler["input"]!.GetValue<int>(),1).SequenceEqual(new[]{0f,1/30f,2/30f}) && Values(sampler["output"]!.GetValue<int>(),3).SequenceEqual(new[]{0f,0,0,1,0,0,2,0,0}));
    rejected = false; try { GltfInterchange.Export(path, model, skeleton: skeleton, animation: new EaglAnim { Name = "wrong", SkeletonHash16 = 99, Frames = 1 }); } catch (InvalidDataException) { rejected = true; } Check("mismatched clip skeleton cannot export", rejected);
    rejected = false; try { MeshPositionPatch.Apply([], [], model, mesh.Positions); } catch (InvalidOperationException) { rejected = true; } Check("unsupported native position storage refuses writes", rejected);
    if (args.Length > 0)
    {
        var root = GameFileSystem.OpenGame(args[0]); var node = root.EnumerateDeep().First(n => n.Name == "mebody.ord"); var original = node.ReadAll(); var joined = node.ReadElfJoined().Data; var native = EaglReader.Read(joined).Models[0];
        GltfInterchange.Export(path, native); var positions = GltfInterchange.ReadPositions(path, native.Mesh);
        Check("native Xbox mesh export reimports byte-exact positions", positions.SequenceEqual(native.Mesh.Positions));
        Check("unchanged Xbox position replacement preserves every source byte", MeshPositionPatch.Apply(original, joined, native, positions).SequenceEqual(original));
        positions[0] = Vector3.Lerp(positions[0], (native.Mesh.Bounds().Min + native.Mesh.Bounds().Max) / 2, 0.01f);
        // A shared position may appear in several draws; update all aliases together.
        for (int i = 1; i < positions.Length; i++) if (native.Mesh.PositionDataOffsets[i] == native.Mesh.PositionDataOffsets[0]) positions[i] = positions[0];
        var patched = MeshPositionPatch.Apply(original, joined, native, positions); patched.CopyTo(joined, 0);
        Check("edited native positions reopen through the actual Xbox decoder", EaglReader.Read(joined).Models[0].Mesh.Positions.SequenceEqual(positions));
        Check("native game file remains untouched", node.ReadOriginal().SequenceEqual(original));
    }
}
finally { Directory.Delete(folder, true); }
return failed == 0 ? 0 : 1;
