using MVPWorkshop.Core.IO;
using System.Diagnostics;
using MVPWorkshop.Core.Formats;
using MVPWorkshop.Core.Vfs;

// MVPWorkshop.Probe — command-line companion for format research.
//   survey <gameDir> [--deep] [--out report.md]   walk everything, report formats and parse failures
//   ls <gameDir> <virtual/path>                   list a folder or container
//   export <gameDir> <virtual/path> <outDir>      export raw bytes + converted (PNG-less BMP / WAV)
if (args.Length < 2) { Console.WriteLine("usage: survey|ls|export <gameDir> ..."); return 1; }

var root = GameFileSystem.OpenFolder(args[1]);

AssetNode? Find(string vpath)
{
    var node = root;
    foreach (var part in vpath.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries))
    {
        node = node.Children.FirstOrDefault(c => c.Name.Equals(part, StringComparison.OrdinalIgnoreCase));
        if (node is null) return null;
    }
    return node;
}

switch (args[0])
{
    case "uniform-runtime":
    {
        foreach(var pack in root.EnumerateDeep().Where(n=>n.Name=="basebody.ssh"))
        {
            var ssh=SshFile.Read(pack.ReadAll()); Console.WriteLine(pack.Path);
            foreach(var image in ssh.Images)Console.WriteLine($"{image.Tag} {image.Header.Width}x{image.Header.Height}");
            foreach(var image in ssh.Images.Where(i=>i.Tag is "dcht" or "dbtl"))
            {
                var decoded=ssh.Decode(image);File.WriteAllBytes(Path.Combine(ResearchDir("uniform-runtime"), $"{image.Tag}.png"),PngWriter.Encode(decoded.Width,decoded.Height,decoded.Bgra));
            }
        }
        var node=Find("DATA/GEOMETRY.BIG/mebody.ord")!;
        var obj=MVPWorkshop.Core.Formats.Eagl.RelocatedObject.Load(node.ReadElfJoined().Data);
        var model=MVPWorkshop.Core.Formats.Eagl.EaglReader.Read(obj).Models[0];
        foreach(var program in model.Parts.Single(p=>p.Index==16).Programs)
        {
            Console.WriteLine($"PROGRAM {program:X}");
            for(int p=program;p<program+2400;p+=4)
            {
                uint w=obj.U32(p);
                if(w==0&&obj.U32(p+4)==0&&p>program)break;
                if(w>>24 is >=1 and <=0x14 || obj.Relocs.ContainsKey(p))Console.WriteLine($"{p:X}: {w:X8} ptr={obj.Ptr(p)} ext={obj.External(p)}");
                if(w>>24==5)Console.WriteLine("  "+Convert.ToHexString(obj.Data.AsSpan(p,Math.Min(100,obj.Data.Length-p))));
            }
        }
        foreach(var draw in model.Mesh.Draws.Where(d=>d.Part==16&&d.Texture is "nb33" or "name" or "dcht"))
        {
            Console.WriteLine($"DRAW {draw.Texture}");
            var bytes=MVPWorkshop.Core.Formats.Eagl.EaglReader.FlattenChain(obj,draw.ChainOffset);
            foreach(var c in MVPWorkshop.Core.Formats.Eagl.Vif.Parse(bytes).Take(22))Console.WriteLine(c+" "+Convert.ToHexString(bytes.AsSpan(c.PayloadOffset,Math.Min(c.PayloadSize,48))));
        }
        break;
    }
    case "uniform-workspace-test":
        UniformValidation.Run(root, args[2]);
        break;
    case "stadium-render":
    {
        var scene=StadiumScene.Load(Find(args[2])!);
        if(args.Length>5) foreach(var c in scene.Components) c.Model.Mesh.Draws.RemoveAll(d=>d.Index!=int.Parse(args[5]));
        var renderer=new StadiumRenderer(scene);
        var visible=scene.Components.Where(c=>args.Length<5 || c.Source.Name==args[4]).ToHashSet();
        var eye=new System.Numerics.Vector3(-1910,3397,-2177);
        var pixels=renderer.Render(1000,780,eye,new System.Numerics.Vector3(1000,0,1000)-eye,60,visible);
        File.WriteAllBytes(args[3],PngWriter.Encode(1000,780,pixels));
        Console.WriteLine(string.Join("\n",renderer.Diagnostics));
        break;
    }
    case "stadium-info":
    {
        var archive = Find(args[2])!;
        foreach (var n in archive.Children.Where(n => n.Extension == ".ord"))
        {
            var f=MVPWorkshop.Core.Formats.Eagl.EaglReader.Read(n.ReadElfJoined().Data);
            foreach(var m in f.Models) {
                Console.WriteLine($"{n.Name} {m.Name} bounds={m.Mesh.Bounds()} stored={m.BoundsMin}/{m.BoundsMax}");
                if (n.Name is "mfield0.ord" or "crowd.ord" or "sky0.ord" or "3dflag.ord")
                foreach(var d in m.Mesh.Draws) {
                    var ids=m.Mesh.Indices.Skip(d.FirstTriangle*3).Take(d.TriangleCount*3).Distinct().ToList();
                    if(ids.Count==0)continue;
                    Console.WriteLine($" draw {d.Index} tex={d.Texture} state={d.PrimState} tar={d.TextureSymbol} uv={ids.Select(i=>m.Mesh.Uvs[i]).Aggregate(System.Numerics.Vector2.Min)}/{ids.Select(i=>m.Mesh.Uvs[i]).Aggregate(System.Numerics.Vector2.Max)} pos={ids.Select(i=>m.Mesh.Positions[i]).Aggregate(System.Numerics.Vector3.Min)}/{ids.Select(i=>m.Mesh.Positions[i]).Aggregate(System.Numerics.Vector3.Max)} color={m.Mesh.Colors[ids[0]]}");
                }
            }
        }
        foreach(var n in archive.Children.Where(n=>n.Extension==".ssh")) {
            var ssh=SshFile.Read(n.ReadAll()); Console.WriteLine(n.Name+": "+string.Join(", ",ssh.Images.Select(i=>$"{i.Tag}:{i.Header.Width}x{i.Header.Height}")));
        }
        break;
    }
    case "stadium-test":
    {
        var nodes = root.EnumerateDeep().ToList();
        var map = StadiumAssignments.Read(GameExecutable.Find(nodes)!.ReadAll());
        var codes = new HashSet<string>();
        var xbox = GameExecutable.Find(nodes)!.Name.EndsWith(".xbe", StringComparison.OrdinalIgnoreCase);
        for (int id=0; id<map.Count; id++) for(int time=0;time<3;time++)
        {
            var r=map.Resolve(id,time) ?? throw new InvalidDataException($"Missing resolution {id}/{time}");
            codes.Add(r.Code);
        }
        foreach(var code in codes.Order())
        {
            var archive=nodes.Single(n=>n.Name.Equals(code+".big",StringComparison.OrdinalIgnoreCase) && n.Path.Contains("/STADIUM/",StringComparison.OrdinalIgnoreCase));
            var scene=StadiumScene.Load(archive);
            if(code=="aboxday") StadiumValidation.Variants(archive);
            if(scene.Diagnostics.Any(d=>!d.StartsWith("Customizable venue:"))) throw new InvalidDataException(code + ": " + string.Join("\n",scene.Diagnostics));
            var renderer=new StadiumRenderer(scene);
            if(renderer.Sources.Values.Any(p=>!p.StartsWith(archive.Path+"/",StringComparison.Ordinal))) throw new InvalidDataException("Texture escaped selected stadium archive.");
            if(scene.Components.SelectMany(c=>c.Model.Mesh.Draws).Any(d=>d.IsLineList && d.TriangleCount!=0)) { if(!xbox) throw new InvalidDataException("Line geometry decoded as triangles."); Console.WriteLine($"  note: {code} has line-list draws with triangles (Xbox geometry decode)."); }
            if(scene.Components.Any(c=>c.Model.Mesh.Positions.Any(p=>!float.IsFinite(p.X+p.Y+p.Z)))) throw new InvalidDataException("Non-finite geometry.");
            if(scene.Components.Count<4) throw new InvalidDataException("Incomplete scene: "+code);
            foreach(var c in scene.Components)
                if(c.Source.Parent!=archive || c.Model.Mesh.Indices.Any(i=>i<0 || i>=c.Model.Mesh.Positions.Count)) throw new InvalidDataException("Invalid scene geometry");
            Console.WriteLine($"{code}: {scene.Components.Count} components, {scene.Components.Sum(c=>c.Model.Mesh.Indices.Count/3)} triangles");
        }
        StadiumValidation.Raster();
        Console.WriteLine($"Passed {3 * map.Count} resolutions / {codes.Count} distinct archives; local geometry bounds and indices.");
        break;
    }
    case "uniform-test":
    {
        var nodes = root.EnumerateDeep().ToList();
        var profiles = TeamAssetCatalog.Build(nodes);
        var bindings = UniformPreview.ReadBindings(GameExecutable.Find(nodes)!.ReadAll());
        int count = 0;
        foreach (var p in profiles)
        {
            var variants = UniformPreview.Variants(p);
            if (variants.Count == 0) { if (GameExecutable.Find(nodes)!.Name.EndsWith(".xbe", StringComparison.OrdinalIgnoreCase)) { Console.WriteLine($"No uniform textures shipped: {p.Label}"); continue; } throw new InvalidDataException($"No variants: {p.Label}"); }
            foreach (var v in variants)
            {
                var ssh = SshFile.Read(v.Texture.ReadAll());
                foreach (var tag in bindings.Values.Distinct())
                    if (!ssh.Images.Any(i => i.Tag.TrimEnd() == tag)) throw new InvalidDataException($"{v.Texture.Path} lacks [{tag}]; actual {string.Join(",", ssh.Images.Select(i => i.Tag))}");
                foreach (var image in ssh.Images) ssh.Decode(image);
                var font = SshFile.Read((v.Font ?? throw new InvalidDataException("Missing font")).ReadAll());
                foreach (var digit in Enumerable.Range(0, 10))
                {
                    var image = font.Images.Single(i => i.Tag == $"zig{digit}"); font.Decode(image);
                }
                count++;
            }
        }
        Console.WriteLine($"Uniform checks passed: {profiles.Count} schools, {count} variants; executable material bindings, all uniform images and all paired number glyphs decoded.");
        break;
    }
    case "uniform-uv":
    {
        var n = Find("DATA/GEOMETRY.BIG/mebody.ord")!;
        var m = MVPWorkshop.Core.Formats.Eagl.EaglReader.Read(MVPWorkshop.Core.Formats.Eagl.RelocatedObject.Load(n.ReadElfJoined().Data)).Models[0].Mesh;
        foreach (var d in m.Draws.Where(d => d.Part == 16))
        {
            var indices = m.Indices.Skip(d.FirstTriangle * 3).Take(d.TriangleCount * 3).Distinct().ToList();
            Console.WriteLine($"draw {d.Index} {d.Texture} state={d.PrimState} matrix={d.TextureMatrix} minuv={indices.Select(i => m.Uvs[i]).Aggregate(System.Numerics.Vector2.Min)} maxuv={indices.Select(i => m.Uvs[i]).Aggregate(System.Numerics.Vector2.Max)}");
            if (d.Texture == "nb33") foreach (var i in indices) Console.WriteLine($"{m.Positions[i]} uv={m.Uvs[i]}");
        }
        break;
    }
    case "uniform-sources":
    {
        foreach (var n in root.EnumerateDeep().Where(n => n.Extension == ".ssh" && n.Format == FormatId.SshTexture))
        {
            var ssh = SshFile.Read(n.ReadAll());
            if (ssh.Images.Any(i => new[] { "sole", "hshd", "arms", "warm", "whdb", "hat", "zig3" }.Contains(i.Tag)) && !n.Name.StartsWith('u') && !n.Name.StartsWith('f'))
                Console.WriteLine(n.Path + ": " + string.Join(",", ssh.Images.Select(i => i.Tag)));
        }
        break;
    }
    case "apt-evidence":
    {
        foreach (var n in root.EnumerateDeep().Where(n => n.Extension == ".apt" && n.Path.Contains(args.Length > 2 ? args[2] : "easoMnu1", StringComparison.OrdinalIgnoreCase)))
        {
            var cn = n.Parent!.Children.FirstOrDefault(c => c.Extension == ".const");
            var apt = AptFile.Read(n.ReadAll(), cn is null ? 0 : AptConstants.Read(cn.ReadAll()).MovieOffset);
            Console.WriteLine(n.Path);
            foreach (var c in apt.Characters.OfType<AptCharacter>().Where(c => c.Type is AptCharacterType.Font or AptCharacterType.EditText or AptCharacterType.Shape))
                Console.WriteLine($"{c.Id} {c.Type} {c.Summary} text={c.Text} raw={Convert.ToHexString(apt.Data.AsSpan(c.Offset, Math.Min(100, apt.Data.Length-c.Offset)))}");
        }
        var exe = root.EnumerateDeep().First(n => n.Name.StartsWith("SLUS_"));
        foreach (var s in ElfFile.Read(exe.ReadAll()).Symbols.Where(s => s.Name.Contains("Apt") && (s.Name.Contains("Font") || s.Name.Contains("Texture") || s.Name.Contains("Text")))) Console.WriteLine($"{s.Value:X8} {s.Name}");
        break;
    }
    case "apt-script-test":
    {
        var node = Find(args[2]) ?? throw new FileNotFoundException(args[2]);
        var constantsNode = node.Parent!.Children.First(n => n.Name == Path.ChangeExtension(node.Name, ".const"));
        var constants = AptConstants.Read(constantsNode.ReadAll());
        var apt = AptFile.Read(node.ReadAll(), constants.MovieOffset);
        var script = new AptPreviewScript(); script.Initialize(apt, constants, apt.Movie, 0); script.Invoke("animate");
        if (!script.Visible("iconHL") || script.Number("iconHL", "_xscale", 0) != 100) throw new InvalidDataException("Startup/function property assignment failed.");
        script.Advance(200);
        if (script.Number("iconHL", "_xscale", 0) != 116) throw new InvalidDataException("Interval timing/arithmetic failed.");
        script.Advance(1500);
        if (script.Visible("iconHL")) throw new InvalidDataException("Conditional branch/clearInterval failed.");
        Console.WriteLine("APT script checks passed: native icon startup, nested function calls, interval timer, arithmetic, conditional branches, display properties and timer cancellation.");
        foreach (var limitation in script.Limitations) Console.WriteLine("  " + limitation);
        break;
    }
    case "ssh-tags":
    {
        foreach (var path in args.Skip(2))
        {
            var n = Find(path) ?? throw new FileNotFoundException(path);
            var ssh = SshFile.Read(n.ReadAll());
            Console.WriteLine($"{n.Path}: {ssh.Images.Count} images");
            foreach (var i in ssh.Images) Console.WriteLine($"   [{i.Index}] {i.Tag,-8} {i.Header.Width}x{i.Header.Height} type 0x{i.Header.Type:X2} name={i.Name}");
        }
        break;
    }
    case "team-test":
    {
        var nodes = root.EnumerateDeep().ToList();
        var profiles = TeamAssetCatalog.Build(nodes);
        if (profiles.Count != 152 || profiles.Select(p => p.Team.UniformId).Distinct().Count() != 152) throw new InvalidDataException("School count/identity mismatch.");
        foreach (var p in profiles)
            if (p.StadiumCode is null || !p.Assets.Any(a => a.Category == "Stadium/Assigned models" && a.Asset.Name == "mstadium.ord") || !p.Assets.Any(a => a.Category == "Uniforms/Player textures"))
                throw new InvalidDataException($"Unresolved school assets: {p.Label}: {p.StadiumEvidence}");
        var lsu = profiles.Single(p => p.Team.Abbreviation == "LSU");
        if (lsu.Team.StadiumId != 3 || lsu.StadiumCode != "aboxday" || lsu.Team.UniformId != 32) throw new InvalidDataException("LSU assignment mismatch.");
        var executable = nodes.First(n => n.Name.StartsWith("SLUS_"));
        var stadiums = StadiumAssignments.Read(executable.ReadAll());
        if (stadiums.Definitions.Count != 34) throw new InvalidDataException("Stadium registry mismatch.");
        foreach (var id in Enumerable.Range(0, 34)) foreach (var time in Enumerable.Range(0, 3))
            if (stadiums.Resolve(id, time) is null) throw new InvalidDataException($"Unresolved stadium {id}/{time}.");
        Console.WriteLine("Team checks passed: 152 unique schools, native uniform packs and assigned stadium models; LSU ID 32 → stadium 3 → ABOXDAY; all 102 stadium/time resolutions and 34 registry entries.");
        break;
    }
    case "teams":
    {
        var profiles = TeamAssetCatalog.Build(root.EnumerateDeep().ToList());
        foreach (var profile in profiles.Where(p => args.Length < 3 || p.Label.Contains(args[2], StringComparison.OrdinalIgnoreCase)))
        {
            Console.WriteLine($"{profile.Label} · roster {profile.Team.RosterIndex} @0x{profile.Team.Offset:X} · logo {profile.Team.LogoId} uniform {profile.Team.UniformId} slot3 {profile.Team.ThirdAssetId} · {profile.Assets.Count} links · stadium {profile.StadiumCode ?? "unresolved"}");
            if (args.Length > 2) foreach (var link in profile.Assets) Console.WriteLine($"  {link.Category}: {link.Asset.Path}");
        }
        Console.WriteLine($"{profiles.Count} named schools; {profiles.Count(p => p.StadiumCode is not null)} roster stadium assignments resolved through executable tables.");
        break;
    }
    case "apt-shape":
    {
        // apt-shape <game> <vpath.apt> [geometryId]: per-draw texture, matrix, blend state and triangles of the paired .o
        var node = Find(args[2]) ?? throw new FileNotFoundException(args[2]);
        var geometry = node.Parent!.Children.First(n => n.Name.Equals(Path.ChangeExtension(node.Name, ".o"), StringComparison.OrdinalIgnoreCase));
        var file = MVPWorkshop.Core.Formats.Eagl.EaglReader.Read(geometry.ReadAll());
        foreach (var model in file.Models.Where(m => args.Length < 4 || m.Name == args[3]))
        {
            var mesh = model.Mesh;
            Console.WriteLine($"geometry {model.Name}: {mesh.Positions.Count} verts {mesh.Indices.Count / 3} tris");
            foreach (var d in mesh.Draws)
            {
                Console.WriteLine($"  draw {d.Index} tex={d.Texture} state={d.State} tm={d.TextureMatrix} tris={d.TriangleCount} verts={d.VertexCount}");
                if (args.Contains("--verts")) { var f = mesh.Draws.TakeWhile(x => x != d).Sum(x => x.VertexCount); for (int v = f; v < f + d.VertexCount; v++) Console.WriteLine($"      v{v - f}: ({mesh.Positions[v].X:0.##},{mesh.Positions[v].Y:0.##})"); }
                for (int i = d.FirstTriangle * 3; i < (d.FirstTriangle + Math.Min(d.TriangleCount, 6)) * 3; i += 3)
                    Console.WriteLine("    " + string.Join(" | ", Enumerable.Range(0, 3).Select(j => { var v = mesh.Indices[i + j]; return $"({mesh.Positions[v].X:0.#},{mesh.Positions[v].Y:0.#},{mesh.Positions[v].Z:0.#}) uv({mesh.Uvs[v].X:0.##},{mesh.Uvs[v].Y:0.##}) c({mesh.Colors[v].X:0.##},{mesh.Colors[v].Y:0.##},{mesh.Colors[v].Z:0.##},{mesh.Colors[v].W:0.##})"; })));
            }
        }
        break;
    }
    case "apt-states":
    {
        // apt-states <game>: every distinct GeoPrimState / TAR option set used by APT geometry, with draw counts
        var states = new SortedDictionary<string, int>(); var tars = new SortedDictionary<string, int>();
        foreach (var node in root.EnumerateDeep().Where(n => n.Extension == ".apt"))
        {
            var geometry = node.Parent?.Children.FirstOrDefault(n => n.Name.Equals(Path.ChangeExtension(node.Name, ".o"), StringComparison.OrdinalIgnoreCase));
            if (geometry is null) continue;
            foreach (var d in MVPWorkshop.Core.Formats.Eagl.EaglReader.Read(geometry.ReadAll()).Models.SelectMany(m => m.Mesh.Draws))
            {
                var key = d.PrimState is null ? "(none)" : System.Text.RegularExpressions.Regex.Replace(d.PrimState[(d.PrimState.LastIndexOf(":::", StringComparison.Ordinal) is var i and >= 0 ? i + 3 : 0)..], @"UID=d+;", "");
                states[key] = states.GetValueOrDefault(key) + 1;
                if (key.Contains("LINELIST")) Console.WriteLine($"LINELIST in {node.Path} draw {d.Index} tris={d.TriangleCount} verts={d.VertexCount}");
                var tar = d.TextureSymbol is null ? "(none)" : System.Text.RegularExpressions.Regex.Replace(d.TextureSymbol[(d.TextureSymbol.LastIndexOf(":::", StringComparison.Ordinal) is var j and >= 0 ? j + 3 : 0)..], @"UID=\d+;|SHAPENAME=[^;]*;?", "");
                tars[tar] = tars.GetValueOrDefault(tar) + 1;
            }
        }
        foreach (var s in states) Console.WriteLine($"{s.Value,6}  {s.Key}");
        Console.WriteLine("--- TAR");
        foreach (var s in tars) Console.WriteLine($"{s.Value,6}  {s.Key}");
        break;
    }
    case "apt-fonts":
    {
        // apt-fonts <game>: font names, embedded glyph counts and EditText usage across every movie
        var fonts = new SortedDictionary<string, int>(); var edits = new SortedDictionary<string, int>();
        foreach (var node in root.EnumerateDeep().Where(n => n.Extension == ".apt"))
        {
            var cn = node.Parent?.Children.FirstOrDefault(c => c.Name.Equals(Path.ChangeExtension(node.Name, ".const"), StringComparison.OrdinalIgnoreCase));
            AptFile apt; try { apt = AptFile.Read(node.ReadAll(), cn is null ? 0 : AptConstants.Read(cn.ReadAll()).MovieOffset); } catch { continue; }
            foreach (var c in apt.Characters.OfType<AptCharacter>())
            {
                if (c.Type == AptCharacterType.Font) { var k = $"{c.FontName} {c.Summary}"; fonts[k] = fonts.GetValueOrDefault(k) + 1; }
                if (c.Type == AptCharacterType.EditText) { var f = c.FontId < apt.Characters.Count ? apt.Characters[c.FontId]?.FontName : null; var k = $"font={f} align={c.TextAlign} ml={c.Multiline} ww={c.WordWrap} ro={c.ReadOnly} var={(string.IsNullOrEmpty(c.Variable) ? "-" : "set")} text={(string.IsNullOrEmpty(c.Text) ? "-" : "set")}"; edits[k] = edits.GetValueOrDefault(k) + 1; }
            }
        }
        foreach (var f in fonts) Console.WriteLine($"{f.Value,5} FONT {f.Key}");
        foreach (var f in edits.OrderByDescending(e => e.Value).Take(40)) Console.WriteLine($"{f.Value,5} EDIT {f.Key}");
        break;
    }
    case "apt-colors":
    {
        // apt-colors <game>: distribution of PlaceObject colour-transform words (multiply = +0x28, add = +0x2C)
        var mul = new SortedDictionary<uint, int>(); var addv = new SortedDictionary<uint, int>(); var where = new Dictionary<uint, string>();
        foreach (var node in root.EnumerateDeep().Where(n => n.Extension == ".apt"))
        {
            var cn = node.Parent?.Children.FirstOrDefault(c => c.Name.Equals(Path.ChangeExtension(node.Name, ".const"), StringComparison.OrdinalIgnoreCase));
            AptFile apt; try { apt = AptFile.Read(node.ReadAll(), cn is null ? 0 : AptConstants.Read(cn.ReadAll()).MovieOffset); } catch { continue; }
            foreach (var it in apt.AllItems().Where(i => i.Type == 3 && (i.Flags & 8) != 0))
            {
                mul[it.Tint] = mul.GetValueOrDefault(it.Tint) + 1;
                addv[it.Additive] = addv.GetValueOrDefault(it.Additive) + 1;
                if (it.Additive != 0) where.TryAdd(it.Additive, node.Path);
            }
        }
        Console.WriteLine("MULTIPLY"); foreach (var m in mul.OrderByDescending(x => x.Value).Take(25)) Console.WriteLine($"{m.Value,6} {m.Key:X8}");
        Console.WriteLine("ADD"); foreach (var m in addv.OrderByDescending(x => x.Value).Take(40)) Console.WriteLine($"{m.Value,6} {m.Key:X8} {(where.TryGetValue(m.Key, out var w) ? w : "")}");
        break;
    }
    case "apt-raster-test":
    {
        // Rasteriser conformance: coverage rule, clamp/repeat addressing, colour transform, blend equations, masks, lines.
        static void Check(bool ok, string what) { if (!ok) throw new InvalidDataException("FAILED: " + what); Console.WriteLine("  ok  " + what); }
        var white = System.Numerics.Vector4.One;
        AptVertex V(float x, float y, float u = 0, float v = 0) => new(x, y, u, v, white);
        var opaque = new AptSurface.Pass(null, default, AptBlend.SourceOver, white, System.Numerics.Vector4.Zero, null);
        static (float R, float G, float B) Px(AptSurface s, int x, int y) { var b = s.ToBgra(); int i = (y * s.Width + x) * 4; return (b[i + 2] / 255f, b[i + 1] / 255f, b[i] / 255f); }
        static int Lit(AptSurface s) { var b = s.ToBgra(); int n = 0; for (int i = 0; i < b.Length; i += 4) if (b[i] > 0) n++; return n; }

        var quad = new AptSurface(16, 16);
        quad.Triangle(V(0, 0), V(8, 0), V(0, 8), opaque); quad.Triangle(V(8, 0), V(8, 8), V(0, 8), opaque);
        Check(Lit(quad) == 64, "8×8 quad built from two triangles covers exactly 64 pixels (shared diagonal owned once)");
        var shifted = new AptSurface(16, 16);
        shifted.Triangle(V(0.5f, 0.5f), V(8.5f, 0.5f), V(0.5f, 8.5f), opaque); shifted.Triangle(V(8.5f, 0.5f), V(8.5f, 8.5f), V(0.5f, 8.5f), opaque);
        Check(Lit(shifted) == 64, "half-pixel shifted quad still covers 64 pixels (pixel-centre sampling)");

        // 2×1 texture: left texel red, right texel blue; map u 0..2 over x 0..4, then u -2..0 (outside)
        var tex = new AptTexture(2, 1, [0, 0, 255, 255, 255, 0, 0, 255]);
        var clamp = new AptSurface(8, 2); var repeat = new AptSurface(8, 2);
        var nearest = new AptSampler(true, true, false);
        foreach (var (surface, sampler) in new[] { (clamp, new AptSampler(true, true, false)), (repeat, new AptSampler(false, false, false)) })
        {
            var pass = opaque with { Texture = tex, Sampler = sampler };
            // u runs 4..6 across x 0..8: well outside the 0..2 texel range
            surface.Triangle(V(0, 0, 4, 0), V(8, 0, 6, 0), V(0, 2, 4, 1), pass); surface.Triangle(V(8, 0, 6, 0), V(8, 2, 6, 1), V(0, 2, 4, 1), pass);
        }
        Check(Px(clamp, 1, 0) is { B: > 0.9f } && Px(clamp, 6, 0) is { B: > 0.9f }, "clamp keeps the edge texel (blue) outside 0..1");
        Check(Px(repeat, 1, 0) is { R: > 0.9f } && Px(repeat, 6, 0) is { B: > 0.9f }, "repeat wraps texels (red then blue) outside 0..1");

        // colour transform: multiply halves, add lifts
        var tinted = new AptSurface(4, 4);
        var half = opaque with { Multiply = new(0.5f, 0.5f, 0.5f, 1), Add = new(0.25f, 0, 0, 0) };
        tinted.Triangle(V(0, 0), V(4, 0), V(0, 4), half); tinted.Triangle(V(4, 0), V(4, 4), V(0, 4), half);
        var t = Px(tinted, 1, 1);
        Check(Math.Abs(t.R - 0.75f) < 0.01f && Math.Abs(t.G - 0.5f) < 0.01f, "colour transform: white·0.5 + 0.25 red offset → (0.75, 0.5, 0.5)");

        // blend equations
        var dst = new AptSurface(4, 4); dst.Clear(0.5f, 0.5f, 0.5f);
        var halfAlpha = new AptVertex(0, 0, 0, 0, new(1, 1, 1, 0.5f));
        AptVertex H(float x, float y) => halfAlpha with { X = x, Y = y };
        dst.Triangle(H(0, 0), H(4, 0), H(0, 4), opaque); dst.Triangle(H(4, 0), H(4, 4), H(0, 4), opaque);
        Check(Math.Abs(Px(dst, 1, 1).R - 0.75f) < 0.01f, "source-over: 50% white over 50% grey → 0.75");
        var add = new AptSurface(4, 4); add.Clear(0.5f, 0.5f, 0.5f);
        var addPass = opaque with { Blend = AptBlend.Additive };
        add.Triangle(H(0, 0), H(4, 0), H(0, 4), addPass); add.Triangle(H(4, 0), H(4, 4), H(0, 4), addPass);
        Check(Math.Abs(Px(add, 1, 1).R - 1f) < 0.01f, "additive (Cs,0,As,Cd): 50% white + 50% grey → 1.0");
        var parsed = AptBlend.From(MVPWorkshop.Core.Formats.Eagl.EaglPrimState.Parse("x:::RUNTIME_ALLOC::UID=1;;SetTextureEnable=false;PS2EXTOBJ_SetAlphaBlend=EAGL::ALPHA_CS,EAGL::ALPHA_CD,EAGL::ALPHA_AS,EAGL::ALPHA_CD,128;;SetPrimitiveType=EAGL::PT_TRIANGLESTRIP;"));
        Check(parsed.A == 's' && parsed.B == 'd' && parsed.C == 's' && parsed.D == 'd', "GS equation CS,CD,AS,CD,128 parses to source-over");

        // clip mask
        var masked = new AptSurface(8, 8); var mask = masked.NewMask();
        masked.MaskTriangle(mask, new(0, 0), new(4, 0), new(0, 4)); masked.MaskTriangle(mask, new(4, 0), new(4, 4), new(0, 4));
        var clipped = opaque with { Clip = mask };
        masked.Triangle(V(0, 0), V(8, 0), V(0, 8), clipped); masked.Triangle(V(8, 0), V(8, 8), V(0, 8), clipped);
        Check(Lit(masked) == 16, "clip-depth mask limits drawing to the mask's 4×4 area");

        var line = new AptSurface(8, 8); line.Line(V(0.5f, 0.5f), V(7.5f, 0.5f), opaque);
        Check(Lit(line) >= 7 && Px(line, 3, 0).R > 0.9f && Px(line, 3, 1).R < 0.1f, "line list: 1-pixel horizontal segment");

        // real data: the strip-restart bug drew bridging triangles between the two quads of one batch
        var node = Find("DATA/APTANIMS/EASOAPT.BIG/OSDKInterface/screens/game/gamSetup.big/gamSetup.o") ?? throw new FileNotFoundException("gamSetup.o");
        var model = MVPWorkshop.Core.Formats.Eagl.EaglReader.Read(node.ReadAll()).Models.First(m => m.Name == "11");
        var mesh = model.Mesh; var bad = 0;
        for (int i = 0; i < mesh.Indices.Count; i += 3)
        {
            var ys = Enumerable.Range(0, 3).Select(k => mesh.Positions[mesh.Indices[i + k]].Y).ToArray();
            if (ys.Max() - ys.Min() > 2) bad++;
        }
        Check(bad == 0, "gamSetup geometry 11 (two 1-px-tall bars per draw): no triangle spans between bars (strip restart stride 2 for APT)");
        Console.WriteLine("APT raster checks passed.");
        break;
    }
    case "apt-geometry-test":
    {
        // every decoded APT shape triangle must lie inside its character's bounds (± tolerance); long bridging triangles mean a bad strip decode
        int shapes = 0, triangles = 0, outside = 0, lines = 0, movies = 0; var worst = new List<string>();
        foreach (var node in root.EnumerateDeep().Where(n => n.Extension == ".apt" && (args.Length < 3 || n.Path.Contains(args[2], StringComparison.OrdinalIgnoreCase))))
        {
            var geometry = node.Parent?.Children.FirstOrDefault(n => n.Name.Equals(Path.ChangeExtension(node.Name, ".o"), StringComparison.OrdinalIgnoreCase));
            var cn = node.Parent?.Children.FirstOrDefault(c => c.Name.Equals(Path.ChangeExtension(node.Name, ".const"), StringComparison.OrdinalIgnoreCase));
            if (geometry is null) continue;
            movies++;
            var apt = AptFile.Read(node.ReadAll(), cn is null ? 0 : AptConstants.Read(cn.ReadAll()).MovieOffset);
            var models = MVPWorkshop.Core.Formats.Eagl.EaglReader.Read(geometry.ReadAll()).Models.Where(m => int.TryParse(m.Name, out _)).ToDictionary(m => int.Parse(m.Name));
            foreach (var shape in apt.Characters.OfType<AptCharacter>().Where(c => c.Type == AptCharacterType.Shape && c.GeometryId is not null && c.Bounds is not null))
            {
                if (!models.TryGetValue(shape.GeometryId!.Value, out var m)) continue;
                shapes++; var b = shape.Bounds!.Value; var mesh = m.Mesh; lines += mesh.Lines.Count;
                for (int i = 0; i < mesh.Indices.Count; i += 3)
                {
                    triangles++;
                    for (int k = 0; k < 3; k++)
                    {
                        var p = mesh.Positions[mesh.Indices[i + k]];
                        if (p.X < b.X - 2 || p.X > b.Z + 2 || p.Y < b.Y - 2 || p.Y > b.W + 2) { outside++; if (worst.Count < 8) worst.Add($"{node.Path} shape {shape.Id}: ({p.X:0.#},{p.Y:0.#}) outside ({b.X:0.#},{b.Y:0.#})–({b.Z:0.#},{b.W:0.#})"); break; }
                    }
                }
            }
        }
        Console.WriteLine($"movies={movies} shapes={shapes} triangles={triangles} lines={lines} trianglesOutsideBounds={outside}");
        foreach (var w in worst) Console.WriteLine("  " + w);
        break;
    }
    case "apt-grep":
    {
        // apt-grep <game> <text> [context]: find a string in the disassembled actions of every movie
        int context = args.Length > 3 ? int.Parse(args[3]) : 3, found = 0;
        foreach (var node in root.EnumerateDeep().Where(n => n.Extension == ".apt"))
        {
            var cn = node.Parent?.Children.FirstOrDefault(c => c.Name.Equals(Path.ChangeExtension(node.Name, ".const"), StringComparison.OrdinalIgnoreCase));
            AptFile apt; AptConstants? consts; byte[] data;
            try { data = node.ReadAll(); consts = cn is null ? null : AptConstants.Read(cn.ReadAll()); apt = AptFile.Read(data, consts?.MovieOffset ?? 0); } catch { continue; }
            foreach (var code in apt.AllItems().Where(i => i.Code is not null).Select(i => i.Code!.Value).Distinct())
            {
                var list = AptBytecode.Disassemble(data, code, consts);
                for (int i = 0; i < list.Count; i++)
                    if (list[i].Operands.Contains(args[2], StringComparison.Ordinal) && !list[i].Mnemonic.Contains("ConstantPool") && found++ < 40)
                    {
                        Console.WriteLine($"{node.Path} @{list[i].Offset:X}");
                        for (int k = Math.Max(0, i - context); k < Math.Min(list.Count, i + context + 1); k++) Console.WriteLine($"   {(k == i ? ">" : " ")} {list[k].Mnemonic} {list[k].Operands[..Math.Min(100, list[k].Operands.Length)]}");
                    }
            }
        }
        break;
    }
    case "apt-env":
    {
        // apt-env <game>: load the gEaso shared module and report the classes/palette the scripts leave behind
        var node = Find("DATA/APTANIMS/EASOAPT.BIG/OSDKInterface/screens/gEaso.big/gEaso.apt") ?? throw new FileNotFoundException("gEaso.apt");
        var constants = AptConstants.Read(node.Parent!.Children.First(n => n.Name == "gEaso.const").ReadAll());
        var apt = AptFile.Read(node.ReadAll(), constants.MovieOffset);
        var script = new AptPreviewScript(); script.LoadEnvironment(apt, constants, "gEaso");
        Console.WriteLine($"registered classes ({script.RegisteredClasses.Count()}): {string.Join(", ", script.RegisteredClasses.Order())}");
        Console.WriteLine($"functions: {script.Functions.Count()}");
        foreach (var l in script.Limitations.Order().Take(40)) Console.WriteLine("  limitation: " + l);
        break;
    }
    case "apt-native":
    {
        int movies = 0, shapes = 0, decoded = 0, triangles = 0, failed = 0, mapped = 0, unmapped = 0;
        foreach (var node in root.EnumerateDeep().Where(n => n.Extension == ".apt" && (args.Length < 3 || n.Path.Contains(args[2], StringComparison.OrdinalIgnoreCase))))
        {
            var geometry = node.Parent?.Children.FirstOrDefault(n => n.Name.Equals(Path.ChangeExtension(node.Name, ".o"), StringComparison.OrdinalIgnoreCase));
            if (geometry is null) continue;
            movies++;
            try
            {
                var file = MVPWorkshop.Core.Formats.Eagl.EaglReader.Read(geometry.ReadAll());
                foreach (var draw in file.Models.SelectMany(m => m.Mesh.Draws).Where(d => d.Texture is not null))
                    if (draw.TextureMatrix.HasValue) mapped++; else unmapped++;
                shapes += file.Models.Count;
                decoded += file.Models.Count(m => m.Mesh.Indices.Count > 0);
                triangles += file.Models.Sum(m => m.Mesh.Indices.Count / 3);
                if (args.Length > 2)
                    foreach (var model in file.Models) Console.WriteLine($"{node.Path} · geometry {model.Name}: {model.Mesh.Positions.Count} vertices / {model.Mesh.Indices.Count / 3} triangles / {string.Join(",", model.Mesh.Draws.Select(d => d.Texture))}; {string.Join("; ", model.Mesh.Warnings)}");
            }
            catch (Exception ex) { failed++; Console.WriteLine($"FAIL {node.Path}: {ex.Message}"); }
        }
        Console.WriteLine($"APT native movies={movies} shapes={shapes} nonempty={decoded} triangles={triangles} failed={failed} textured matrices={mapped} missing={unmapped}");
        break;
    }
    case "elfdump":
    {
        var node = Find(args[2]) ?? throw new FileNotFoundException(args[2]);
        var data = node.ReadElfJoined().Data;
        var elf = ElfFile.Read(data);
        foreach (var section in elf.Sections) Console.WriteLine($"{section.Name} {section.Type} offset={section.Offset:X} size={section.Size:X}");
        foreach (var symbol in elf.Symbols) Console.WriteLine($"{symbol.Name} {symbol.Section}+{symbol.Value:X} size={symbol.Size}");
        var obj = MVPWorkshop.Core.Formats.Eagl.RelocatedObject.Load(data);
        foreach (var (at, relocation) in obj.Relocs) Console.WriteLine($"reloc {at:X} -> {relocation.Target:X} {relocation.External}");
        for (int at = 0; at < Math.Min(4096, obj.Data.Length); at += 16)
            Console.WriteLine($"{at:X4}: {Convert.ToHexString(obj.Data.AsSpan(at, Math.Min(16, obj.Data.Length - at)))}");
        break;
    }
    case "objdump":
    {
        var node = Find(args[2]) ?? throw new FileNotFoundException(args[2]);
        var obj = MVPWorkshop.Core.Formats.Eagl.RelocatedObject.Load(node.ReadElfJoined().Data);
        var from = Convert.ToInt32(args[3], 16); var len = Convert.ToInt32(args[4], 16);
        for (var at = from; at < Math.Min(obj.Data.Length, from + len); at += 16)
        {
            var marks = string.Join(" ", Enumerable.Range(0, 4).Select(k => at + k * 4).Where(a => obj.Relocs.ContainsKey(a)).Select(a => obj.Relocs[a] is var r ? $"[{a:X}→{(r.External ?? r.Target.ToString("X"))}]" : ""));
            Console.WriteLine($"{at:X6}: {Convert.ToHexString(obj.Data.AsSpan(at, Math.Min(16, obj.Data.Length - at)))} {marks}");
        }
        break;
    }
    case "xskin":
    {
        // xskin <game> <model.ord> [anim bank .ord]: skin coverage plus tear tests (single-bone bends, and animation frames when a bank is given).
        var node = Find(args[2]) ?? throw new FileNotFoundException(args[2]);
        var obj = MVPWorkshop.Core.Formats.Eagl.RelocatedObject.Load(node.ReadElfJoined().Data);
        var f = MVPWorkshop.Core.Formats.Eagl.EaglXboxReader.LooksLikeXbox(obj) ? MVPWorkshop.Core.Formats.Eagl.EaglXboxReader.Read(obj) : MVPWorkshop.Core.Formats.Eagl.EaglReader.Read(obj);
        var model = f.Models[0]; var m = model.Mesh; var sk = f.Skeleton!;
        var skinned = m.Skin.Count(x => x.B0 >= 0);
        var badBone = m.Skin.Count(x => x.B0 >= sk.Bones.Count || x.B1 >= sk.Bones.Count || x.B2 >= sk.Bones.Count);
        Console.WriteLine($"{model.Name}: {m.Positions.Count} vertices, {skinned} skinned, {badBone} with out-of-range bones, {sk.Bones.Count} bones");
        if (Environment.GetEnvironmentVariable("XSKIN_OUT") is { Length: > 0 } outPath)
        {
            var drawOf = new int[m.Positions.Count];
            foreach (var d in m.Draws) for (var t = d.FirstTriangle * 3; t < (d.FirstTriangle + d.TriangleCount) * 3; t++) drawOf[m.Indices[t]] = d.Index;
            File.WriteAllLines(outPath, Enumerable.Range(0, m.Positions.Count).Select(i => FormattableString.Invariant($"{m.Positions[i].X:0.000} {m.Positions[i].Y:0.000} {m.Positions[i].Z:0.000} {m.Skin[i].B0} {m.Skin[i].W0:0.000} {m.Skin[i].B1} {m.Skin[i].W1:0.000} {m.Skin[i].B2} {m.Skin[i].W2:0.000} {drawOf[i]} 0")));
        }
        double Tear(System.Numerics.Vector3[] posed, out int torn, out int edges)
        {
            double sum = 0; torn = 0; edges = 0;
            for (var t = 0; t + 2 < m.Indices.Count; t += 3)
                for (var e = 0; e < 3; e++)
                {
                    int a = m.Indices[t + e], b = m.Indices[t + (e + 1) % 3];
                    var l0 = System.Numerics.Vector3.Distance(m.Positions[a], m.Positions[b]); if (l0 < 1e-4f) continue;
                    var rel = Math.Abs(System.Numerics.Vector3.Distance(posed[a], posed[b]) / l0 - 1);
                    sum += rel; edges++; if (rel > 0.5) torn++;
                }
            return sum / Math.Max(1, edges);
        }
        foreach (var bone in new[] { 4, 23, 25, 38, 40, 52, 53, 56, 57 }.Where(x => x < sk.Bones.Count))
        {
            var posed = MVPWorkshop.Core.Formats.Eagl.EaglDeformer.Deform(model, sk, new Dictionary<int, System.Numerics.Quaternion> { [bone] = System.Numerics.Quaternion.CreateFromAxisAngle(System.Numerics.Vector3.UnitX, 0.5f) }, null, null);
            var mean = Tear(posed, out var torn, out var edges);
            Console.WriteLine($"  bend {sk.Bones[bone].Name,-10} mean edge change {mean:0.0000}  torn(>50%) {torn}/{edges}");
        }
        if (args.Length > 3)
        {
            var bankNode = Find(args[3]) ?? throw new FileNotFoundException(args[3]);
            var bank = MVPWorkshop.Core.Formats.Eagl.EaglAnimBank.Read(MVPWorkshop.Core.Formats.Eagl.RelocatedObject.Load(bankNode.ReadElfJoined().Data));
            var clips = bank.Anims.Where(x => x.SkeletonHash16 == (sk.Hash & 0xFFFF) && x.Frames > 4).ToList();
            Console.WriteLine($"  bank {bankNode.Name}: {bank.Anims.Count} clips, {clips.Count} for this skeleton");
            double worst = 0; var tornTotal = 0; var edgeTotal = 0; var n = 0;
            foreach (var clip in clips.Where((_, i) => i % Math.Max(1, clips.Count / 40) == 0))
            {
                var rot = new System.Numerics.Quaternion?[sk.Bones.Count]; var trans = new System.Numerics.Vector3?[sk.Bones.Count];
                clip.Sample(clip.Frames / 2f, rot, trans);
                trans[1] = null; // keep the hips in place so only deformation is measured
                var posed = MVPWorkshop.Core.Formats.Eagl.EaglDeformer.Deform(model, sk, sk.Pose(rot, trans, null), null, null);
                var mean = Tear(posed, out var torn, out var edges);
                worst = Math.Max(worst, mean); tornTotal += torn; edgeTotal += edges; n++;
            }
            Console.WriteLine($"  {n} clips sampled at mid-frame: worst mean edge change {worst:0.0000}, torn edges {tornTotal}/{edgeTotal} ({100.0 * tornTotal / Math.Max(1, edgeTotal):0.00}%)");
        }
        break;
    }
    case "xvb":
    {
        var node = Find(args[2]) ?? throw new FileNotFoundException(args[2]);
        var obj = MVPWorkshop.Core.Formats.Eagl.RelocatedObject.Load(node.ReadElfJoined().Data);
        foreach (var sym in obj.Symbols.Where(x => x.Name.StartsWith("__VB:::")).OrderBy(x => x.Value))
        {
            var at = sym.Value;
            var w = Enumerable.Range(0, 6).Select(k => obj.U32(at + k * 4)).ToArray();
            Console.WriteLine($"{sym.Name[7..]} @{at:X}: {string.Join(" ", w.Select(v => v.ToString("X")))} | {Convert.ToHexString(obj.Data.AsSpan(at + 20, Math.Min(24, obj.Data.Length - at - 20)))}");
        }
        break;
    }
    case "joined":
    {
        var node = Find(args[2]) ?? throw new FileNotFoundException(args[2]);
        var obj = MVPWorkshop.Core.Formats.Eagl.RelocatedObject.Load(node.ReadElfJoined().Data);
        File.WriteAllBytes(args[3], obj.Data);
        using var w = new StreamWriter(args[3] + ".txt");
        foreach (var sym in obj.Symbols.OrderBy(x => x.Value)) w.WriteLine($"S {sym.Value:X} {sym.Name}");
        foreach (var (at, r) in obj.Relocs.OrderBy(x => x.Key)) w.WriteLine($"R {at:X} {(r.External is null ? r.Target.ToString("X") : "ext:" + r.External)}");
        break;
    }
    case "findtext":
    {
        var pattern = new System.Text.RegularExpressions.Regex(args[2], System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        foreach (var node in root.EnumerateDeep().Where(n => !n.IsFolder && n.Format is FormatId.Text or FormatId.Csv or FormatId.LocText or FormatId.LocBinary))
        {
            var lines = node.Format == FormatId.LocBinary ? LocTable.Read(node.ReadAll()).Strings.Select(s => $"{s.Id}: {s.Text}") : FormatInspector.ReadText(node.Open()).Split('\n').AsEnumerable();
            foreach (var (line, index) in lines.Select((line, index) => (line, index)))
                if (pattern.IsMatch(line)) Console.WriteLine($"{node.Path}:{index + 1}: {line.Trim()[..Math.Min(350, line.Trim().Length)]}");
        }
        break;
    }
    case "catalog":
    {
        var nodes = root.EnumerateDeep().ToList();
        var catalog = AssetCatalog.Build(nodes);
        var expected = nodes.Count(n => !n.IsFolder && !n.IsContainer);
        var actual = catalog.Values.SelectMany(v => v).ToList();
        if (actual.Count != expected || actual.Distinct().Count() != expected) throw new InvalidDataException("Category coverage or uniqueness failure.");
        foreach (var (category, entries) in catalog) Console.WriteLine($"{category,-45} {entries.Count,7:N0}");
        Console.WriteLine($"Validated {actual.Count:N0} unique assets; every leaf has one category.");
        break;
    }
    case "fel-test":
    {
        var fixture = FelFile.Read("""
            VR:83
            SC:MENU,1,0,0,0,640,480,1,1,1,1,0,0,-1,0,
             KA:ENTER,0,1,1,1,1,1,2,0,1,0,0
             GR:WRAPPER,1,0,0,0,100,100,1,1,1,1,255
              GR:PANEL,0,0,10,20,100,100,1,1,1,1,255
               TB:LABEL,1,0,20,30,80,15,1,1,1,1,0,0,font.ffn,12
            SC:MENU1,1,0,0,0,640,480,1,1,1,1,1,0,-1,1,
             GR:PANEL,1,0,30,40,100,100,1,1,1,1,0
            SC:MENU2,1,0,0,0,640,480,1,1,1,1,2,0,-1,1,
             GR:PANEL,1,0,50,60,100,100,1,1,1,1,255
            """);
        var animation = FelAnimation.Read(fixture).Single();
        var panel = fixture.Elements.First(e => e.Name == "PANEL");
        var half = animation.Sample(.5)[panel];
        if (half[3] != "40" || half[4] != "50" || half[11] != "127.5" || panel.Field(3) != "10") throw new InvalidDataException("Sparse snapshot interpolation or source isolation failed.");
        if (animation.Sample(0)[panel][11] != "0" || animation.Sample(1)[panel][11] != "255") throw new InvalidDataException("Timeline endpoint failure.");
        if (panel.Children.Single().Name != "LABEL" || panel.Line != 5) throw new InvalidDataException("FEL hierarchy or line provenance failed.");
        Console.WriteLine("FEL tests passed: hierarchy, source lines, sparse ancestor matching, timeline endpoints/interpolation, unchanged authored data.");
        break;
    }
    case "fel-shift":
    {
        // fel-shift <game> <vpath> <dx> <outDir>: shift the main-menu scenes' x by dx and export the rebuilt archive.
        var n = Find(args[2]) ?? throw new FileNotFoundException(args[2]);
        var dx = int.Parse(args[3]);
        var lines = System.Text.Encoding.Latin1.GetString(n.ReadAll()).Replace("\r\n", "\n").Split('\n');
        var inMenu = false; var skipIndent = -1; var shifted = 0;
        string[] kinds = ["GR", "GG", "FC", "FR", "BU", "SH", "TB", "TX"];
        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i]; var indent = line.Length - line.TrimStart().Length; var t = line.TrimStart();
            if (indent == 0 && t.StartsWith("SC:")) { inMenu = t.StartsWith("SC:MAINMENU"); skipIndent = -1; continue; }
            if (!inMenu || t.Length < 3 || t[2] != ':') continue;
            if (skipIndent >= 0) { if (indent > skipIndent) continue; skipIndent = -1; }
            if (t.StartsWith("GR:BRANDING")) { skipIndent = indent; continue; }
            if (!kinds.Contains(t[..2])) continue;
            var f = t.Split(',');
            if (f.Length < 5 || !int.TryParse(f[3], out var x)) continue;
            f[3] = (x + dx).ToString(); lines[i] = new string(' ', indent) + string.Join(',', f); shifted++;
        }
        var session = new MVPWorkshop.Core.Editing.EditSession(root, args[4]);
        session.Commit(n, System.Text.Encoding.Latin1.GetBytes(string.Join("\n", lines)), "menu shift");
        var report = MVPWorkshop.Core.Editing.ModExporter.Export(session, args[4]);
        Console.WriteLine($"shifted {shifted} elements; ok={report.Ok}");
        foreach (var p in report.Problems) Console.WriteLine(p);
        foreach (var fl in report.Files) Console.WriteLine($"{fl.RelativePath} {fl.Size}");
        break;
    }
    case "fel":
    {
        var nodes = args.Length > 2
            ? new[] { Find(args[2]) ?? throw new FileNotFoundException(args[2]) }
            : root.EnumerateDeep().Where(n => n.Extension == ".fel");
        int files = 0, elements = 0, shapes = 0, labels = 0, animations = 0, samples = 0;
        foreach (var node in nodes)
        {
            var file = FelFile.Read(FormatInspector.ReadText(node.Open()));
            foreach (var e in file.Elements)
                if (e.Parent is not null && (!e.Parent.Children.Contains(e) || e.Parent.Line >= e.Line))
                    throw new InvalidDataException($"Invalid FEL hierarchy: {node.Path}:{e.Line}");
            files++; elements += file.Elements.Count;
            shapes += file.Elements.Count(e => e.Kind == "SH");
            labels += file.Elements.Count(e => e.Kind == "TB");
            foreach (var animation in FelAnimation.Read(file))
            {
                animations++;
                foreach (var time in new[] { 0, animation.Duration / 2, animation.Duration })
                { animation.Sample(time); samples++; }
            }
            if (args.Length > 2)
                foreach (var e in file.Elements.Where(e => e.Kind is "SC" or "SF"))
                    Console.WriteLine($"line {e.Line}: {e}");
        }
        Console.WriteLine($"FEL files={files} elements={elements} shapes={shapes} text={labels} animations={animations} timeline samples={samples}; hierarchy validated");
        break;
    }
    case "survey":
    {
        var deep = args.Contains("--deep");
        var sw = Stopwatch.StartNew();
        var rows = FormatSurvey.Run(root, deep, new Progress<SurveyProgress>(p => Console.Error.Write($"\r{p.Files} files…")));
        Console.Error.WriteLine($"\rdone in {sw.Elapsed.TotalSeconds:0.0}s");
        var md = FormatSurvey.ToMarkdown(rows, "MVP-Workshop — format survey");
        var outIdx = Array.IndexOf(args, "--out");
        if (outIdx > 0) File.WriteAllText(args[outIdx + 1], md); else Console.WriteLine(md);
        break;
    }
    case "models":
    {
        // models <game> [--filter text]: decode every .ord/.orl model and report quality stats
        var filt = Array.IndexOf(args, "--filter") is var fi and > 0 ? args[fi + 1] : "";
        int files = 0, ok = 0, nomodel = 0, failed = 0; long tris = 0;
        foreach (var n in root.EnumerateDeep())
        {
            if (n.IsFolder || n.Extension != ".ord" || !n.Path.Contains(filt, StringComparison.OrdinalIgnoreCase)) continue;
            files++;
            try
            {
                var (data, partner) = n.ReadElfJoined();
                if (partner is null) { nomodel++; continue; }
                var obj = MVPWorkshop.Core.Formats.Eagl.RelocatedObject.Load(data);
                if (!MVPWorkshop.Core.Formats.Eagl.EaglReader.LooksLikeModel(obj)) { nomodel++; continue; }
                var f = MVPWorkshop.Core.Formats.Eagl.EaglReader.Read(obj);
                foreach (var m in f.Models)
                {
                    var mesh = m.Mesh;
                    var (mn, mx) = mesh.Bounds();
                    var diag = (mx - mn).Length();
                    var bad = 0;
                    for (var t = 0; t < mesh.Indices.Count; t += 3)
                    {
                        var a = mesh.Positions[mesh.Indices[t]]; var b = mesh.Positions[mesh.Indices[t + 1]]; var c = mesh.Positions[mesh.Indices[t + 2]];
                        var e = Math.Max((a - b).Length(), Math.Max((b - c).Length(), (a - c).Length()));
                        if (e > diag * 0.35f) bad++;
                    }
                    tris += mesh.Indices.Count / 3;
                    var inBox = mesh.Positions.Count(p => p.X >= m.BoundsMin.X - 0.5f && p.X <= m.BoundsMax.X + 0.5f && p.Y >= m.BoundsMin.Y - 0.5f && p.Y <= m.BoundsMax.Y + 0.5f);
                    if (args.Contains("--states"))
                        foreach (var d in mesh.Draws)
                            Console.WriteLine($"   draw {d.Index,3} part {d.Part,2} tex {d.Texture,-5} tris {d.TriangleCount,4} alpha {(d.TriangleCount == 0 || mesh.Colors.Count == 0 ? 1 : Enumerable.Range(d.FirstTriangle * 3, d.TriangleCount * 3).Average(i => mesh.Colors[mesh.Indices[i]].W)):0.00}  {d.State}");
                    Console.WriteLine($"{n.Path} | {m.Name} var={m.Variations.Count} progs={m.Programs.Count} parts={m.Parts.Count} draws={mesh.Draws.Count} v={mesh.Positions.Count} t={mesh.Indices.Count / 3} long={bad} inBox={(mesh.Positions.Count == 0 ? 0 : inBox * 100 / mesh.Positions.Count)}% tex={string.Join(",", mesh.Draws.Select(d => d.Texture).Distinct())} warn={mesh.Warnings.Count} {mesh.Warnings.FirstOrDefault()}");
                }
                ok++;
            }
            catch (Exception ex) { failed++; Console.WriteLine($"{n.Path} | FAIL {ex.Message}"); }
        }
        Console.WriteLine($"files={files} decoded={ok} notModel={nomodel} failed={failed} triangles={tris:N0}");
        break;
    }
    case "vif":
    {
        // vif <game> <vpath> [draw#]: disassemble the VIF stream of a model draw
        var n = Find(args[2]) ?? throw new FileNotFoundException(args[2]);
        var obj = MVPWorkshop.Core.Formats.Eagl.RelocatedObject.Load(n.ReadElfJoined().Data);
        var f = MVPWorkshop.Core.Formats.Eagl.EaglReader.Read(obj);
        var want = args.Length > 3 ? int.Parse(args[3]) : 0;
        var d = f.Models[0].Mesh.Draws[want];
        Console.WriteLine($"draw {want} chain @0x{d.ChainOffset:X} tex={d.Texture}");
        var single = obj.U32(d.ChainOffset) >> 28 is 3 or 4 && obj.U32(d.ChainOffset - 4) == 0x14000004;
        var bytes = MVPWorkshop.Core.Formats.Eagl.EaglReader.FlattenChain(obj, d.ChainOffset, single);
        foreach (var c in MVPWorkshop.Core.Formats.Eagl.Vif.Parse(bytes).Take(60))
        {
            Console.Write(c);
            if (c.PayloadSize > 0) Console.Write("   " + Convert.ToHexString(bytes, c.PayloadOffset, Math.Min(24, c.PayloadSize)));
            Console.WriteLine();
        }
        break;
    }
    case "syms":
    {
        // syms <game> [contains]: tally symbol kinds (text before ":::") across every ELF object; list matches
        var filter = args.Length > 2 ? args[2] : null;
        var kinds = new Dictionary<string, (int Count, string Example)>();
        foreach (var n in root.EnumerateDeep())
        {
            if (n.IsFolder || n.Format != FormatId.Elf) continue;
            try
            {
                var elf = ElfFile.Read(n.ReadElfJoined().Data);
                foreach (var s in elf.Symbols)
                {
                    if (s.Name.Length == 0) continue;
                    var k = s.Name.Contains(":::") ? s.Name[..s.Name.IndexOf(":::")] : (s.Name.Length > 40 ? s.Name[..40] : s.Name);
                    kinds[k] = (kinds.GetValueOrDefault(k).Count + 1, kinds.GetValueOrDefault(k).Example ?? $"{n.Path} {s.Name}");
                    if (filter is not null && s.Name.Contains(filter, StringComparison.OrdinalIgnoreCase)) Console.WriteLine($"{n.Path} | {s.Name} @0x{s.Value:X} size {s.Size} {s.Section}");
                }
            }
            catch { }
        }
        if (filter is null) foreach (var (k, v) in kinds.OrderByDescending(x => x.Value.Count).Take(80)) Console.WriteLine($"{v.Count,7}  {k}   e.g. {v.Example}");
        break;
    }
    case "skin":
    {
        // skin <game> <vpath> [morph vpath]: skeleton / skin / morph diagnostics
        var n = Find(args[2]) ?? throw new FileNotFoundException(args[2]);
        var f = MVPWorkshop.Core.Formats.Eagl.EaglReader.Read(n.ReadElfJoined().Data);
        var sk = f.Skeleton;
        Console.WriteLine(sk is null ? "no skeleton" : $"skeleton {sk.Name}: {sk.Bones.Count} bones, bind error {sk.BindError:G3}");
        foreach (var m in f.Models)
        {
            var skinned = m.Mesh.Skin.Count(s => s.B0 >= 0);
            var multi = m.Mesh.Skin.Count(s => s.B1 >= 0);
            var bones = m.Mesh.Skin.Where(s => s.B0 >= 0).SelectMany(s => new[] { s.B0, s.B1, s.B2 }).Where(b => b >= 0).Distinct().Count();
            Console.WriteLine($"{m.Name}: verts {m.Mesh.Positions.Count} skinned {skinned} multi-bone {multi} bones used {bones} batches mapped {m.Mesh.BatchByDataOffset.Count}");
            if (sk is not null && skinned > 0)
            {
                // distance from each vertex to its dominant bone's bind position — sanity metric
                var d = m.Mesh.Skin.Select((s, i) => (s, i)).Where(x => x.s.B0 >= 0)
                    .Select(x => (m.Mesh.Positions[x.i] - sk.Bones[x.s.W0 >= x.s.W1 ? x.s.B0 : x.s.B1].BindWorld.Translation).Length()).ToList();
                Console.WriteLine($"  mean distance vertex→dominant bone {d.Average():0.00}");
                // duplicated vertices (same position) must carry the same weights
                var groups = m.Mesh.Positions.Select((p, i) => (k: ((int)MathF.Round(p.X * 500), (int)MathF.Round(p.Y * 500), (int)MathF.Round(p.Z * 500)), i))
                    .GroupBy(x => x.k).Where(g => g.Count() > 1).ToList();
                string Key(MVPWorkshop.Core.Formats.Eagl.SkinWeights s) => string.Join(",", new[] { (s.B0, s.W0), (s.B1, s.W1), (s.B2, s.W2) }.Where(x => x.Item1 >= 0 && x.Item2 > 0.01f).OrderBy(x => x.Item1).Select(x => $"{x.Item1}:{x.Item2:0.00}"));
                var bad = groups.Count(g => g.Select(x => Key(m.Mesh.Skin[x.i])).Distinct().Count() > 1);
                Console.WriteLine($"  duplicate-vertex groups {groups.Count}, with inconsistent weights {bad} ({(groups.Count == 0 ? 0 : bad * 100.0 / groups.Count):0.0}%)");
                foreach (var g in groups.Where(g => g.Select(x => Key(m.Mesh.Skin[x.i])).Distinct().Count() > 1).Take(4))
                    Console.WriteLine("    " + string.Join("  |  ", g.Select(x => $"v{x.i} d{m.Mesh.Draws.First(dd => x.i < m.Mesh.Draws.Take(dd.Index + 1).Sum(q => q.VertexCount)).Index} {Key(m.Mesh.Skin[x.i])}")));
            }
        }
        if (args.Length > 3 && Find(args[3]) is { } mn)
        {
            var mo = MVPWorkshop.Core.Formats.Eagl.RelocatedObject.Load(mn.ReadElfJoined().Data);
            var morph = MVPWorkshop.Core.Formats.Eagl.EaglMorphSet.Read(mo)!;
            var bound = MVPWorkshop.Core.Formats.Eagl.EaglDeformer.Bind(f.Object!, f.Models[0], morph);
            Console.WriteLine($"morph {morph.ModelName}: {morph.Shapes.Count} shapes, records {morph.Shapes.Sum(s => s.Count)}, bound {bound.Sum(b => b.Count)}");
        }
        break;
    }
    case "anims":
    {
        // anims <game>: decode every animation bank and report coverage / sanity
        var n = Find("DATA/ANIMS/ANIMS.BIG") ?? throw new FileNotFoundException();
        int banks = 0, anims = 0, failed = 0, badQuat = 0, quats = 0;
        var kinds = new Dictionary<string, int>();
        float maxJump = 0; string worst = "";
        foreach (var c in n.Children.Where(c => c.Extension == ".ord"))
        {
            var (data, partner) = c.ReadElfJoined();
            if (partner is null) continue;
            var o = MVPWorkshop.Core.Formats.Eagl.RelocatedObject.Load(data);
            if (!MVPWorkshop.Core.Formats.Eagl.EaglAnimBank.LooksLikeBank(o)) continue;
            banks++;
            var bank = MVPWorkshop.Core.Formats.Eagl.EaglAnimBank.Read(o);
            failed += bank.Warnings.Count;
            foreach (var a in bank.Anims)
            {
                anims++;
                failed += a.Warnings.Count;
                foreach (var w in a.Warnings.Take(1)) Console.WriteLine($"{c.Name} {a.Name}: {w}");
                foreach (var ch in a.Channels)
                {
                    kinds[ch.Kind.ToString()] = kinds.GetValueOrDefault(ch.Kind.ToString()) + 1;
                    if (ch.Kind is MVPWorkshop.Core.Formats.Eagl.EaglChannelKind.Rotation or MVPWorkshop.Core.Formats.Eagl.EaglChannelKind.AxisRotation)
                        for (var k = 0; k < ch.Keys.Length; k++)
                            for (var t = 0; t < ch.Targets.Length; t++)
                            {
                                quats++;
                                var q = ch.Keys[k][t];
                                if (MathF.Abs(q.Length() - 1) > 0.05f) badQuat++;
                                if (k > 0)
                                {
                                    var p = ch.Keys[k - 1][t];
                                    var jump = MathF.Min((q - p).Length(), (q + p).Length()) / Math.Max(1, ch.Times[k] - ch.Times[k - 1]);
                                    if (jump > maxJump) { maxJump = jump; worst = $"{c.Name}/{a.Name} bone {ch.Targets[t]} key {k}"; }
                                }
                            }
                }
            }
        }
        Console.WriteLine($"banks {banks} anims {anims} channel failures {failed}");
        Console.WriteLine("channels: " + string.Join(", ", kinds.Select(k => $"{k.Key}×{k.Value}")));
        Console.WriteLine($"rotation keys {quats:N0}, non-unit (|1−|q||>0.05) {badQuat:N0} ({(quats == 0 ? 0 : badQuat * 100.0 / quats):0.00}%)");
        Console.WriteLine($"largest per-frame quaternion step {maxJump:0.000} at {worst}");
        break;
    }
    case "speech":
    {
        // speech <game>: decode every MicroTalk stream; report failures, sample-count mismatches and clipping
        int streams = 0, failed = 0, mismatch = 0; long samples = 0, clipped = 0; double seconds = 0;
        var sw = Stopwatch.StartNew();
        foreach (var n in root.EnumerateDeep())
        {
            if (n.IsFolder || n.Format is not (FormatId.SchlStream or FormatId.SchlContainer)) continue;
            var src = n.Open();
            foreach (var s in SchlStream.ScanAll(src))
            {
                if (s.Header is not { Codec2: 0x04 or 0x16 } h) continue;
                streams++;
                try
                {
                    var a = s.Decode(src);
                    samples += a.Pcm.Length; seconds += a.Duration;
                    clipped += a.Pcm.Count(x => x is short.MaxValue or short.MinValue);
                    if (h.Samples > 0 && Math.Abs(a.Pcm.Length / a.Channels - h.Samples) > 0) { if (mismatch++ < 5) Console.WriteLine($"len {n.Path}@0x{s.Offset:X}: {a.Pcm.Length / a.Channels} vs header {h.Samples}"); }
                }
                catch (Exception ex) { if (failed++ < 5) Console.WriteLine($"FAIL {n.Path}@0x{s.Offset:X}: {ex.Message}"); }
            }
            n.DropCache();
        }
        Console.WriteLine($"MicroTalk streams {streams:N0}, failed {failed}, length mismatches {mismatch}, {seconds / 60:0.0} min decoded in {sw.Elapsed.TotalSeconds:0.0}s, clipped samples {clipped:N0} ({(samples == 0 ? 0 : clipped * 100.0 / samples):0.000}%)");
        break;
    }
    case "extract":
    {
        // extract <game> <vpath> <outDir> [ext,ext]: write every file under a folder/archive (decompressed)
        var n = Find(args[2]) ?? throw new FileNotFoundException(args[2]);
        var exts = args.Length > 4 ? args[4].Split(',').Select(e => "." + e.Trim('.').ToLowerInvariant()).ToHashSet() : null;
        var count = 0;
        foreach (var x in n.EnumerateDeep())
        {
            if (x.IsFolder || x.IsContainer || (exts is not null && !exts.Contains(x.Extension))) continue;
            var rel = x.Path[(n.Path.Length + 1)..].Replace('/', '_').Replace('\\', '_');
            File.WriteAllBytes(Path.Combine(args[3], rel), x.ReadAll());
            count++;
        }
        Console.WriteLine($"extracted {count}");
        break;
    }
    case "banks":
    {
        // banks <game> <outDir>: export every ANIMS.BIG .ord/.orl pair joined, for research
        var n = Find("DATA/ANIMS/ANIMS.BIG") ?? throw new FileNotFoundException();
        Directory.CreateDirectory(args[2]);
        foreach (var c in n.Children.Where(c => c.Extension == ".ord"))
        {
            var (data, partner) = c.ReadElfJoined();
            if (partner is not null) File.WriteAllBytes(Path.Combine(args[2], Path.GetFileNameWithoutExtension(c.Name) + ".elf"), data);
        }
        break;
    }
    case "obj":
    {
        var n = Find(args[2]) ?? throw new FileNotFoundException(args[2]);
        var f = MVPWorkshop.Core.Formats.Eagl.EaglReader.Read(n.ReadElfJoined().Data);
        foreach (var m in f.Models) File.WriteAllText(Path.Combine(args[3], m.Name + ".obj"), m.Mesh.ToObj(m.Name));
        break;
    }
    case "apt":
    {
        // apt <game> [vpath.apt]: dump one movie (characters, frames, disassembly) or tally opcodes across every movie
        AptConstants? ConstsFor(AssetNode n) => n.Parent?.Children.FirstOrDefault(c => c.Name.Equals(Path.ChangeExtension(n.Name, ".const"), StringComparison.OrdinalIgnoreCase)) is { } cn ? AptConstants.Read(cn.ReadAll()) : null;
        if (args.Length > 2 && !args[2].StartsWith("--"))
        {
            var n = Find(args[2]) ?? throw new FileNotFoundException(args[2]);
            var consts = ConstsFor(n); var data = n.ReadAll();
            var apt = AptFile.Read(data, consts?.MovieOffset ?? 0);
            Console.WriteLine($"Apt {apt.Version} {apt.Width}x{apt.Height} frame time {apt.FrameTime} · {apt.Characters.Count} characters · {apt.Imports.Count} imports · {apt.Exports.Count} exports");
            foreach (var i in apt.Imports) Console.WriteLine($"import [{i.Character}] {i.Name} from {i.Movie}");
            foreach (var e in apt.Exports) Console.WriteLine($"export [{e.Character}] {e.Name}");
            foreach (var c in apt.Characters.OfType<AptCharacter>())
            {
                Console.WriteLine($"[{c.Id}] {c.Type} @0x{c.Offset:X} {c.Summary}");
                foreach (var f in c.Frames)
                    foreach (var it in f.Items)
                    {
                        Console.WriteLine($"   frame {f.Index}: {it.TypeName} {it.Detail}");
                        if (it.Code is { } code)
                            foreach (var ins in AptBytecode.Disassemble(data, code, consts))
                                Console.WriteLine($"      {ins.Offset:X6} {new string(' ', ins.Depth * 2)}{ins.Mnemonic} {ins.Operands}");
                    }
            }
            break;
        }
        var ops = new Dictionary<string, int>(); int movies = 0, actions = 0, stopped = 0, failed = 0;
        foreach (var n in root.EnumerateDeep())
        {
            if (n.IsFolder || n.Format != FormatId.AptMovie) continue;
            try
            {
                var consts = ConstsFor(n); var data = n.ReadAll();
                var apt = AptFile.Read(data, consts?.MovieOffset ?? 0);
                movies++;
                foreach (var it in apt.AllItems().Where(i => i.Code is not null))
                {
                    actions++;
                    var code = AptBytecode.Disassemble(data, it.Code!.Value, consts);
                    foreach (var ins in code) ops[ins.Mnemonic] = ops.GetValueOrDefault(ins.Mnemonic) + 1;
                    for (var k = 0; k < code.Count; k++)
                    {
                        if (!code[k].Mnemonic.StartsWith("op_")) continue;
                        stopped++;
                        var key = code[k].Mnemonic; ops["#" + key] = ops.GetValueOrDefault("#" + key) + 1;
                        if (ops["#" + key] <= 5 && args.Contains("--ctx"))
                            Console.WriteLine($"{key} {n.Path} @0x{code[k].Offset:X}: {string.Join(" | ", code.Skip(Math.Max(0, k - 4)).Take(Math.Min(k, 4)).Select(i => i.Mnemonic + " " + i.Operands))} || {Convert.ToHexString(data, code[k].Offset, Math.Min(28, data.Length - code[k].Offset))}");
                    }
                }
            }
            catch (Exception ex) { failed++; Console.WriteLine($"FAIL {n.Path}: {ex.Message}"); }
        }
        foreach (var (k, v) in ops.OrderByDescending(x => x.Value)) Console.WriteLine($"{v,8}  {k}");
        Console.WriteLine($"movies={movies} failed={failed} actions={actions} stoppedOnUnknown={stopped}");
        break;
    }
    case "hdrs":
    {
        // hdrs <game>: pair every PROJx speech header with its stream and compare the length field to the sample count
        int n = 0, paired = 0; var ratios = new List<double>();
        foreach (var node in root.EnumerateDeep())
        {
            if (node.IsFolder || node.Format != FormatId.SpeechHeader) continue;
            n++;
            var h = SpeechHeader.Parse(node.ReadAll());
            var s = SpeechPairs.StreamFor(node);
            if (h is null || s is null || h.Length == 0) continue;
            var st = SchlStream.ScanAll(s.Open()).FirstOrDefault();
            if (st?.Header is null) continue;
            paired++;
            if (h.SplitPoints.Length > 0) ratios.Add(h.SplitPoints[^1] * 256.0 / st.Header.Samples);
            if (paired <= 8) Console.WriteLine($"{node.Path} kind={h.Kind} seg={h.Segments} len={h.Length} samples={st.Header.Samples} ratio={st.Header.Samples / (double)h.Length:0.0} splits={string.Join(",", h.SplitPoints)}");
        }
        ratios.Sort();
        Console.WriteLine($"headers={n} paired={paired} last split×256/samples: min={ratios.FirstOrDefault():0.0} median={ratios.ElementAtOrDefault(ratios.Count / 2):0.0} max={ratios.LastOrDefault():0.0}");
        break;
    }
    case "big-scan":
    {
        // Layout census of every BIGF (recursively): alignment, gaps, overlaps, header tails, declared-size mismatches.
        int archives = 0, bad = 0, overlap = 0, gapful = 0, tails = 0, unsorted = 0, dupNames = 0, declMismatch = 0, compressedEntries = 0, entries = 0, nestedCompressed = 0;
        var aligns = new SortedDictionary<int, int>();
        foreach (var n in root.EnumerateDeep())
        {
            if (n.IsFolder || n.Format != FormatId.BigArchive) continue;
            archives++;
            try
            {
                var src = n.Open(); var big = MVPWorkshop.Core.Formats.BigArchive.Read(src);
                var data = src.ReadAll();
                var sorted = big.Entries.OrderBy(e => e.Offset).ToList();
                if (!big.Entries.Select(e => e.Offset).SequenceEqual(sorted.Select(e => e.Offset))) unsorted++;
                var dirEnd = 16; foreach (var e in big.Entries) dirEnd += 8 + System.Text.Encoding.Latin1.GetByteCount(e.Name) + 1;
                if (dirEnd != big.HeaderSize) tails++;
                if (big.DeclaredSize != data.Length) declMismatch++;
                if (big.Entries.Select(e => e.Name.ToLowerInvariant()).Distinct().Count() != big.Entries.Count) dupNames++;
                long prev = big.HeaderSize; var gap = false; var ov = false;
                foreach (var e in sorted) { if (e.Offset < prev) ov = true; else if (e.Offset > prev) gap = true; prev = Math.Max(prev, e.Offset + e.Size); }
                if (prev < data.Length) gap = true;
                if (ov) overlap++; if (gap) gapful++;
                var a = 1; foreach (var c in new[] { 4096, 2048, 1024, 512, 256, 128, 64, 32, 16, 8, 4, 2 }) if (big.Entries.All(e => e.Offset % c == 0)) { a = c; break; }
                aligns[a] = aligns.GetValueOrDefault(a) + 1;
                foreach (var e in big.Entries) { entries++; if (e.Size >= 5) { var h = src.ReadBytes(e.Offset, 8); if (MVPWorkshop.Core.IO.RefPack.IsCompressed(h)) { compressedEntries++; if (e.Name.EndsWith(".big", StringComparison.OrdinalIgnoreCase)) nestedCompressed++; } } }
            }
            catch (Exception ex) { bad++; Console.WriteLine($"{n.Path}: {ex.Message}"); }
        }
        Console.WriteLine($"archives={archives} bad={bad} entries={entries} refpack={compressedEntries} nestedRefpackBig={nestedCompressed} unsortedDir={unsorted} overlap={overlap} gaps/trailing={gapful} headerTail={tails} declaredSizeMismatch={declMismatch} dupNames={dupNames}");
        Console.WriteLine("alignment: " + string.Join(", ", aligns.Select(kv => $"{kv.Key}:{kv.Value}")));
        break;
    }
    case "xsh-mips":
    {
        var shown = new Dictionary<string, int>();
        foreach (var n in root.EnumerateDeep().Where(n => !n.IsFolder && n.IsTexturePack && n.Path.Contains(args.Length > 2 ? args[2] : "")))
        {
            SshFile ssh; try { ssh = SshFile.Read(n.ReadAll()); } catch { continue; }
            foreach (var img in ssh.Images.Where(i => i.Header.Mipmaps > 0))
            {
                var h = img.Header; var t = h.Type & 0x7F;
                var region = ssh.Data.AsSpan(img.DataOffset, img.DataLength);
                var len = (h.Type & 0x80) != 0 && RefPack.IsCompressed(region) ? RefPack.Decompress(region).Length : region.Length;
                long Level(int w, int hh) => t switch { 0x60 => XboxTexture.DxtSize(Math.Max(1, w), Math.Max(1, hh), 1), 0x61 => XboxTexture.DxtSize(Math.Max(1, w), Math.Max(1, hh), 3), 0x62 => XboxTexture.DxtSize(Math.Max(1, w), Math.Max(1, hh), 5), 0x7B => Math.Max(1, w) * Math.Max(1, hh), _ => Math.Max(1, w) * Math.Max(1, hh) * 4 };
                long sum = 0; var lv = new List<string>();
                for (var m = 0; m <= h.Mipmaps; m++) { var l = Level(h.Width >> m, h.Height >> m); sum += l; lv.Add($"{Math.Max(1, h.Width >> m)}x{Math.Max(1, h.Height >> m)}={l}"); }
                var key = $"0x{t:X2} {h.Width}x{h.Height} mips={h.Mipmaps} swz={h.FlagSwizzled} len={len} sum={sum} atts={string.Join(",", img.Attachments.Select(a => a.Header.Type.ToString("X2")))}";
                if (shown.TryAdd(key, 1) && shown.Count < 25) Console.WriteLine(key + "  " + string.Join(" ", lv));
            }
        }
        break;
    }
    case "ssh-roundtrip":
        return SshRoundTrip.Run(root, args.Length > 2 ? args[2] : null);
    case "edit-formats":
        return FormatEditTests.Run(root);
    case "validate":
    {
        // Everything the editor's write path depends on, against the real game files.
        var results = new List<(string, int)>
        {
            ("BIG layout (identity + grown, all archives incl. nested)", RoundTripCommands.Layout(root, null)),
            ("full re-encode of every top-level archive", RoundTripCommands.Reencode(root, null)),
            ("SSH replace round trip", SshRoundTrip.Run(root, null)),
            ("PS2 swizzle inverses", SwizzleTest.Run()),
            ("APT / const / LOCH / text editors", FormatEditTests.Run(root)),
            ("nested edit export + verification", RoundTripCommands.EditTest(root, ["EACN/BUNDLE.BIG/cafeLib/stdArt.big/stdArt.const", "DATA/FRONTEND/TITLE.BIG/fes_title.fel"])),
        };
        Console.WriteLine();
        foreach (var (name, code) in results) Console.WriteLine($"{(code == 0 ? "PASS" : "FAIL")}  {name}");
        return results.All(r => r.Item2 == 0) ? 0 : 1;
    }
    case "swizzle-test":
        return SwizzleTest.Run();
    case "layout":
        return RoundTripCommands.Layout(root, args.Length > 2 ? args[2] : null);
    case "reencode":
        return RoundTripCommands.Reencode(root, args.Length > 2 ? args[2] : null);
    case "edit-test":
        return RoundTripCommands.EditTest(root, args[2..]);
    case "ls":
    {
        var n = Find(args[2]) ?? throw new FileNotFoundException(args[2]);
        foreach (var c in n.Children) Console.WriteLine($"{c.FormatInfo.Name,-20} {c.StoredSize,12:N0}  {c.Name}");
        break;
    }
    case "export":
    {
        var n = Find(args[2]) ?? throw new FileNotFoundException(args[2]);
        Directory.CreateDirectory(args[3]);
        var data = n.ReadAll();
        File.WriteAllBytes(Path.Combine(args[3], n.Name), data);
        if (n.Format == FormatId.SshTexture)
        {
            var ssh = SshFile.Read(data);
            foreach (var img in ssh.Images)
            {
                try
                {
                    var d = ssh.Decode(img);
                    File.WriteAllBytes(Path.Combine(args[3], $"{img.Index:D3}_{Sanitize(img.DisplayName)}.png"), Bmp(d));
                    Console.WriteLine($"{img.Tag} {SshFile.TypeName(img.Header.Type)} {d.Width}x{d.Height} swz={img.Header.FlagSwizzled} pal={img.Palette?.Header.Type:X2}/{img.Palette?.Header.FlagSwizzled} {d.Notes}");
                }
                catch (Exception ex) { Console.WriteLine($"{img.Tag}: {ex.Message}"); }
            }
        }
        else if (n.Format is FormatId.SchlStream)
        {
            var src = n.Open();
            var s = SchlStream.ScanAll(src).First();
            var a = s.Decode(src);
            File.WriteAllBytes(Path.Combine(args[3], n.Name + ".wav"), AudioCodecs.ToWav(a));
            Console.WriteLine($"{a.Channels}ch {a.SampleRate}Hz {a.Duration:0.00}s {a.Notes}");
        }
        else if (n.Format is FormatId.EaBank)
        {
            var bank = EaBank.Read(data);
            foreach (var s in bank.Sounds.Take(8))
            {
                var a = EaBank.Decode(data, s);
                File.WriteAllBytes(Path.Combine(args[3], $"{n.Name}_{s.Index:D3}.wav"), AudioCodecs.ToWav(a));
                Console.WriteLine($"#{s.Index} {a.Channels}ch {a.SampleRate}Hz {a.Duration:0.00}s {a.Notes}");
            }
        }
        break;
    }
}
return 0;

static string Sanitize(string s) => string.Concat(s.Select(c => Path.GetInvalidFileNameChars().Contains(c) || c == ' ' ? '_' : c));

static byte[] Bmp(DecodedImage d) => MVPWorkshop.Core.IO.PngWriter.Encode(d.Width, d.Height, d.Bgra);




















static string ResearchDir(string sub) { var d = Path.Combine(Environment.GetEnvironmentVariable("MVPW_RESEARCH") ?? "research", sub); Directory.CreateDirectory(d); return d; }
