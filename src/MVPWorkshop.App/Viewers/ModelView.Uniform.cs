using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using MVPWorkshop.Core.Formats;
using MVPWorkshop.Core.Formats.Eagl;
using MVPWorkshop.Core.Vfs;

namespace MVPWorkshop.App.Viewers;

public sealed partial class ModelView
{
    public sealed record UniformCamera(double Yaw, double Pitch, double Distance, System.Windows.Media.Media3D.Point3D Target);
    public event Action<UniformCamera>? UniformCameraChanged;
    public UniformCamera CaptureUniformCamera() => new(_yaw, _pitch, _distance, _target);
    public void RestoreUniformCamera(UniformCamera camera)
    {
        _yaw=camera.Yaw; _pitch=camera.Pitch; _distance=camera.Distance; _target=camera.Target; UpdateCamera();
    }
    private void FitUniformCamera()
    {
        var (min, max) = _model.Mesh.Bounds();
        var half = (max - min) / 2;
        double aspect = _viewport.ActualWidth / Math.Max(1, _viewport.ActualHeight);
        _distance = Math.Max(Math.Sqrt(half.X * half.X + half.Z * half.Z), half.Y * aspect) /
            Math.Tan(_camera.FieldOfView * Math.PI / 360) * 1.12 + half.Z;
        UpdateCamera();
    }

    public static ModelView CreateUniform(AssetNode body, AssetNode head)
    {
        var data = body.ReadElfJoined().Data;
        var obj = RelocatedObject.Load(data);
        var file = EaglReader.Read(obj);
        var model = file.Models[0];
        var hm = EaglReader.Read(RelocatedObject.Load(head.ReadElfJoined().Data)).Models[0].Mesh;
        var mesh = model.Mesh;
        int vertex = mesh.Positions.Count, triangle = mesh.Indices.Count / 3;
        mesh.Positions.AddRange(hm.Positions); mesh.Normals.AddRange(hm.Normals);
        mesh.Uvs.AddRange(hm.Uvs); mesh.Colors.AddRange(hm.Colors); mesh.Skin.AddRange(hm.Skin);
        mesh.Indices.AddRange(hm.Indices.Select(i => i + vertex));
        var part = new EaglPart { Index = model.Parts.Count };
        foreach (var d in hm.Draws)
        {
            var draw = new EaglDraw { Index = mesh.Draws.Count, Part = part.Index, Texture = d.Texture,
                PrimState = d.PrimState, TextureSymbol = d.TextureSymbol, TextureMatrix = d.TextureMatrix,
                FirstTriangle = d.FirstTriangle + triangle, TriangleCount = d.TriangleCount, VertexCount = d.VertexCount };
            mesh.Draws.Add(draw); part.Draws.Add(draw);
        }
        model.Parts.Add(part);
        var view = new ModelView(body, obj, file, data, _ => { }, true);
        // A representative equipment configuration, not a claim about any roster player's appearance.
        foreach (var p in view._parts) p.Visible = new[] { 16, 17, 18, 28, 31, 32, 33, part.Index }.Contains(p.Part.Index);
        view._texSource.IsEnabled = false;
        var args = Environment.GetCommandLineArgs();
        view._yaw = args.Contains("--uniform-back") ? Math.PI : 0;
        view._pitch = 0; view._distance *= 1.15; view.UpdateCamera();
        return view;
    }

    public void DressUniform(UniformVariant variant, IReadOnlyList<AssetNode> nodes, Dictionary<string, string> bindings, Panel inspection, string number = "", string name = "", bool original = false, IReadOnlyDictionary<string, DecodedImage>? replacements = null, ISet<string>? dirtRegions = null)
    {
        _textures.Clear(); inspection.Children.Clear();
        var native = new Dictionary<string,DecodedImage>(StringComparer.OrdinalIgnoreCase);
        void Load(AssetNode pack, Dictionary<string, string>? aliases = null)
        {
            var ssh = SshFile.Read(original ? pack.ReadOriginal() : pack.ReadAll());
            var tags = ssh.Images.Select(i => i.Tag).ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach(var i in ssh.Images)
            {
                var raw=ssh.Decode(i,new DecodeOptions { ScaleAlpha=false });
                if(!original&&replacements?.TryGetValue(pack.Key+"/"+i.Index,out var replacement)==true)
                {
                    var bytes=(byte[])replacement.Bgra.Clone();
                    for(int at=3;at<bytes.Length;at+=4)bytes[at]=SshWriter.ToPs2Alpha(bytes[at]);
                    raw=new DecodedImage { Width=replacement.Width,Height=replacement.Height,Bgra=bytes };
                }
                native[i.Tag.TrimEnd()]=raw;
            }
            var decoded = ssh.Images.ToDictionary(i => i.Tag.TrimEnd(), i => UniformBitmap(
                !original && replacements?.TryGetValue(pack.Key + "/" + i.Index, out var replacement) == true ? replacement : ssh.Decode(i)), StringComparer.OrdinalIgnoreCase);
            foreach (var (tag, image) in decoded) _textures[tag] = image;
            var rows = new List<string>();
            if (aliases is not null)
                foreach (var (material, tag) in aliases)
                    if (decoded.TryGetValue(tag, out var image)) { _textures[material] = image; native[material]=native[tag]; rows.Add($"{material} ← {tag}"); }
            inspection.Children.Add(B.Button(pack.Name, null, (_, _) => MainWindow.Instance?.OpenAsset(pack), "Outline", pack.Path));
            inspection.Children.Add(new TextBlock { Text = string.Join(", ", rows.Count > 0 ? rows : tags), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(8, 0, 8, 8), Foreground = Ui.Res("Muted"), FontSize = 11 });
        }
        AssetNode? Find(string name) => nodes.FirstOrDefault(n => n.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
        Load(variant.Texture, bindings);
        foreach (var sourceName in new[] { "basebody.ssh", "basehead.ssh", "flesh.ssh", "bshoe0.ssh", "gaaaaa.ssh" })
            if (Find(sourceName) is { } pack) Load(pack, sourceName == "flesh.ssh" ? new() { ["arms"] = "0arm", ["hndb"] = "0hdb", ["hndt"] = "0hdb" } : null);
        var dirtBindings=UniformPreview.ReadDirtBindings(GameExecutable.Find(nodes)!.ReadAll());
        if (variant.Font is { } font)
        {
            inspection.Children.Add(B.Button("Number / name font: " + font.Name, null, (_, _) => MainWindow.Instance?.OpenAsset(font), "Outline", font.Path));
            var digits = new WrapPanel { Margin = new Thickness(8) };
            var glyphs = DecodeFrom(font, Enumerable.Range(0, 10).Select(i => $"zig{i}").ToHashSet());
            foreach (var (tag, image) in glyphs)
                digits.Children.Add(new Image { Source = image, Width = 20, Height = 40, ToolTip = $"{font.Path} / {tag}", Margin = new Thickness(1) });
            inspection.Children.Add(digits);
        }
        if (variant.Font is { } paired && number.Length > 0)
        {
            var fontFile = SshFile.Read(original ? paired.ReadOriginal() : paired.ReadAll());
            native["nb33"] = UniformLettering.Number(fontFile,number,true);
            native["name"] = UniformLettering.Name(fontFile,name,true);
        }
        var visible=_parts.Where(p=>p.Visible).Select(p=>p.Part.Index).ToHashSet();
        foreach(var group in _model.Mesh.Draws.Where(d=>visible.Contains(d.Part)&&d.BaseTexture is not null&&d.SecondaryUvs.Count>0).GroupBy(d=>d.Texture))
        {
            if(group.Key is not { } material)continue;
            var baseMaterial=group.First().BaseTexture!;
            if(!native.TryGetValue(baseMaterial,out var fabric))throw new InvalidDataException($"Missing fabric {baseMaterial} for {material}.");
            if(dirtBindings.TryGetValue(material,out var source))
            {
                if(!original&&dirtRegions?.Contains(material)==true)
                    _textures[material]=UniformBitmap(UniformCompositor.Compose(_model.Mesh,group,fabric,native[source]));
                else _textures[material]=_textures[baseMaterial];
            }
            else if(native.TryGetValue(material,out var layer))
                _textures[material]=UniformBitmap(UniformCompositor.Compose(_model.Mesh,group,fabric,layer));
        }
        inspection.Children.Add(new TextBlock { Text="Native output-slot secondary UVs; GS (Cs−Cd)×As/128+Cd composition. Dirt sources: "+string.Join(", ",dirtBindings.Select(p=>$"{p.Key} ← {p.Value}")),TextWrapping=TextWrapping.Wrap,Margin=new Thickness(8),Foreground=Ui.Res("Muted") });
        var missing = _model.Mesh.Draws.Where(d => _parts.Any(p => p.Visible && p.Part.Index == d.Part))
            .Select(d => d.Texture).OfType<string>().Distinct().Where(t => !_textures.ContainsKey(t)).ToList();
        _texStatus.Text = $"{variant.Texture.Name} · executable material bindings\n" +
            "Native secondary UVs and GS alpha composition" +
            (missing.Count > 0 ? "\nUnresolved: " + string.Join(", ", missing) : "");
        _texStatus.TextWrapping = TextWrapping.Wrap;
        inspection.Children.Add(new TextBlock { Text = "Sample equipment, head and skin; not a roster-player likeness. Dirt regions are explicit preview event states. Scene lighting uses WPF; no gameplay dirt events are simulated.", TextWrapping = TextWrapping.Wrap, Margin = new Thickness(8), Foreground = Ui.Res("Muted") });
        Rebuild();
    }

    private static BitmapSource UniformBitmap(DecodedImage d)
    {
        var bitmap = BitmapSource.Create(d.Width, d.Height, 96, 96, PixelFormats.Bgra32, null, d.Bgra, d.Width * 4);
        bitmap.Freeze(); return bitmap;
    }

    public void MatchUniformCamera(ModelView other)
    {
        _yaw = other._yaw; _pitch = other._pitch; _distance = other._distance;
        _target = other._target; UpdateCamera();
    }
}

