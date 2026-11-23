using System.Globalization;
using System.IO;
using System.Text;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Media.Media3D;

namespace MVPWorkshop.App.Viewers;

/// <summary>Exports the visible preview, including instances, chosen parts and texture alpha.</summary>
internal static class WavefrontSceneExport
{
    internal static int Write(Model3D root, string path)
    {
        path = Path.GetFullPath(path);
        string parent = Path.GetDirectoryName(path)!;
        string folderName = "nascar-assets-" + Guid.NewGuid().ToString("N");
        string folder = Path.Combine(parent, folderName), temporary = Path.Combine(parent, ".nascar-" + Guid.NewGuid().ToString("N") + ".tmp");
        var created = new List<string>();
        Directory.CreateDirectory(folder);
        try
        {
            var materials = new Dictionary<Material, string>();
            var textureNames = new Dictionary<BitmapSource, string>();
            var mtl = new StringBuilder("# NASCAR-Workshop visible preview materials\n");
            using (var output = new StreamWriter(temporary, false, new UTF8Encoding(false)))
            {
                output.WriteLine("# NASCAR-Workshop assembled static preview; game shaders are approximated");
                output.WriteLine($"mtllib {folderName}/materials.mtl");
                int vertexBase = 1, meshCount = 0;
                string F(double v) => v.ToString("R", CultureInfo.InvariantCulture);
                void Visit(Model3D model, Matrix3D parentTransform)
                {
                    var transform = model.Transform.Value; transform.Append(parentTransform);
                    if (model is Model3DGroup group) { foreach (var child in group.Children) Visit(child, transform); return; }
                    if (model is not GeometryModel3D { Geometry: MeshGeometry3D mesh, Material: { } material }) return;
                    if (!transform.HasInverse) throw new InvalidDataException("Cannot export a singular scene transform.");
                    if (!materials.TryGetValue(material, out string? name))
                    {
                        name = $"material_{materials.Count:D4}"; materials.Add(material, name);
                        mtl.AppendLine($"newmtl {name}\nKa 1 1 1\nKd 1 1 1\nd 1\nillum 1");
                        if (material is DiffuseMaterial { Brush: ImageBrush { ImageSource: BitmapSource bitmap } })
                        {
                            if (!textureNames.TryGetValue(bitmap, out string? textureName))
                            {
                                textureName = name + ".png"; textureNames.Add(bitmap, textureName);
                                string texturePath = Path.Combine(folder, textureName); created.Add(texturePath);
                                using var file = File.Create(texturePath);
                                var png = new PngBitmapEncoder(); png.Frames.Add(BitmapFrame.Create(bitmap)); png.Save(file);
                            }
                            mtl.AppendLine($"map_Kd {textureName}\nmap_d {textureName}");
                        }
                        else if (material is DiffuseMaterial { Brush: SolidColorBrush brush })
                            mtl.AppendLine($"Kd {F(brush.Color.R / 255d)} {F(brush.Color.G / 255d)} {F(brush.Color.B / 255d)}");
                        mtl.AppendLine();
                    }
                    output.WriteLine($"g mesh_{meshCount++:D6}\nusemtl {name}");
                    foreach (var position in mesh.Positions) { var p = transform.Transform(position); output.WriteLine($"v {F(p.X)} {F(p.Y)} {F(p.Z)}"); }
                    for (int i = 0; i < mesh.Positions.Count; i++)
                    {
                        var uv = i < mesh.TextureCoordinates.Count ? mesh.TextureCoordinates[i] : new System.Windows.Point();
                        output.WriteLine($"vt {F(uv.X)} {F(1 - uv.Y)}");
                    }
                    var inverse = transform; inverse.Invert();
                    for (int i = 0; i < mesh.Positions.Count; i++)
                    {
                        var n = i < mesh.Normals.Count ? mesh.Normals[i] : new Vector3D(0, 1, 0);
                        n = new(inverse.M11 * n.X + inverse.M12 * n.Y + inverse.M13 * n.Z,
                            inverse.M21 * n.X + inverse.M22 * n.Y + inverse.M23 * n.Z,
                            inverse.M31 * n.X + inverse.M32 * n.Y + inverse.M33 * n.Z);
                        if (n.LengthSquared > 0) n.Normalize();
                        output.WriteLine($"vn {F(n.X)} {F(n.Y)} {F(n.Z)}");
                    }
                    for (int i = 0; i < mesh.TriangleIndices.Count; i += 3)
                    {
                        int a = mesh.TriangleIndices[i] + vertexBase, b = mesh.TriangleIndices[i + 1] + vertexBase, c = mesh.TriangleIndices[i + 2] + vertexBase;
                        if (transform.Determinant < 0) (b, c) = (c, b);
                        output.WriteLine($"f {a}/{a}/{a} {b}/{b}/{b} {c}/{c}/{c}");
                    }
                    vertexBase = checked(vertexBase + mesh.Positions.Count);
                }
                Visit(root, Matrix3D.Identity);
                if (meshCount == 0) throw new InvalidDataException("The visible scene has no geometry to export.");
            }
            string materialPath = Path.Combine(folder, "materials.mtl"); created.Add(materialPath);
            File.WriteAllText(materialPath, mtl.ToString(), new UTF8Encoding(false));
            File.Move(temporary, path, true); // Commit the requested OBJ only after all its resources exist.
            return materials.Count;
        }
        catch
        {
            if (File.Exists(temporary)) File.Delete(temporary);
            foreach (string file in created) if (File.Exists(file)) File.Delete(file);
            Directory.Delete(folder);
            throw;
        }
    }
}
