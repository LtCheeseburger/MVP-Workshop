using MVPWorkshop.Core.Editing;
using MVPWorkshop.Core.Formats;
using MVPWorkshop.Core.IO;
using MVPWorkshop.Core.Vfs;
using MVPWorkshop.Core.Formats.Eagl;
using System.Numerics;

internal static class UniformValidation
{
    public static void Run(AssetNode root, string output)
    {
        Directory.CreateDirectory(output);
        var profiles = TeamAssetCatalog.Build(root.EnumerateDeep().ToList());
        var nodes=root.EnumerateDeep().ToList();
        var bodyObject=RelocatedObject.Load(nodes.First(n=>n.Name=="mebody.ord").ReadElfJoined().Data);
        var body=EaglReader.Read(bodyObject).Models[0].Mesh;
        var regions=body.Draws.Where(d=>d.Part==16&&d.Texture is "nb33" or "name" or "dcht").ToList();
        foreach(var draw in regions)
        {
            Require(draw.BaseTexture is "jerk" or "jers",$"Native first TAR is jersey fabric: {draw.Texture}/{draw.BaseTexture}");
            Require(draw.SecondaryUvs.Count==draw.TriangleCount*3,"All secondary output-slot UVs retained");
            Require(draw.SecondaryUvs.Values.All(uv=>float.IsFinite(uv.X+uv.Y)),"Finite secondary UVs");
        }
        var numberDraw=regions.Single(d=>d.Texture=="nb33");
        var vif=EaglReader.FlattenChain(bodyObject,numberDraw.ChainOffset);
        var nativeUv=Vif.Parse(vif).First(c=>c.IsUnpack&&c.Format==4&&c.TopsRelative);
        for(int slot=0;slot<3;slot++)
            Require(numberDraw.SecondaryUvs[numberDraw.FirstTriangle*3+slot]==new Vector2(BitConverter.ToSingle(vif,nativeUv.PayloadOffset+slot*8),BitConverter.ToSingle(vif,nativeUv.PayloadOffset+slot*8+4)),"Secondary corners match native VIF output slots");
        Require(UniformCompositor.BlendChannel(200,40,0)==40&&UniformCompositor.BlendChannel(200,40,64)==120&&UniformCompositor.BlendChannel(200,40,128)==200,"GS zero/half/full alpha");
        Require(UniformCompositor.BlendChannel(0,255,1)==253&&UniformCompositor.BlendChannel(255,0,255)==255,"GS signed shift and saturation");
        var twoTexels=new DecodedImage { Width=2,Height=1,Bgra=[0,0,0,0,200,100,50,128] };
        Require(UniformCompositor.Sample(twoTexels,new Vector2(-.25f,.5f),(false,false)).X==200,"GS negative ST repeat");
        Require(UniformCompositor.Sample(twoTexels,new Vector2(-.25f,.5f),(true,true)).X==0,"TAR clamp");
        var dirtMap=UniformPreview.ReadDirtBindings(GameExecutable.Find(nodes)!.ReadAll());
        Require(dirtMap["dcht"]=="dcht"&&dirtMap["dbtr"]=="dbtl"&&dirtMap.Count==11,"Executable dirt source/material table");
        var baseBody=SshFile.Read(nodes.First(n=>n.Name=="basebody.ssh").ReadAll());
        int cases = 0;
        foreach (var abbreviation in new[] { "LSU", "ALA", "STAN" })
        {
            var school = profiles.FirstOrDefault(p => p.Team.Abbreviation == abbreviation)
                ?? profiles.First(p => p.Team.Name.Contains(abbreviation == "ALA" ? "Alabama" : "Stanford", StringComparison.OrdinalIgnoreCase));
            foreach (var variant in UniformPreview.Variants(school))
            {
                var font = SshFile.Read(variant.Font!.ReadAll());
                var uniform=SshFile.Read(variant.Texture.ReadAll());
                var fabric=uniform.Decode(uniform.Images.Single(i=>i.Tag=="tors"),new DecodeOptions { ScaleAlpha=false });
                foreach(var draw in regions)
                {
                    var layer=draw.Texture switch { "nb33"=>UniformLettering.Number(font,"33",true),"name"=>UniformLettering.Name(font,"WILLIAMS",true),_=>baseBody.Decode(baseBody.Images.Single(i=>i.Tag=="dcht"),new DecodeOptions { ScaleAlpha=false }) };
                    var composed=UniformCompositor.Compose(body,[draw],fabric,layer,512);
                    Require(composed.Bgra.Where((_,i)=>i%4==3).All(a=>a==255),"Flattened passes have opaque output");
                    var clean=ImageOps.Resize(fabric.Bgra,fabric.Width,fabric.Height,512,512);
                    Require(composed.Bgra.Where((b,i)=>i%4!=3&&b!=clean[i]).Any(),"Native secondary layer changes fabric");
                    File.WriteAllBytes(Path.Combine(output,$"{school.Team.Abbreviation}-{variant.Suffix}-{draw.Texture}-native.png"),PngWriter.Encode(512,512,composed.Bgra));
                }
                foreach (var number in new[] { "0", "7", "10", "33", "99", "00" })
                {
                    var canvas = UniformLettering.Number(font, number);
                    Require(canvas.Width == 64 && canvas.Height == 64, "Number allocation");
                    var glyph = font.Decode(font.Images.Single(i => i.Tag == "zig" + UniformLettering.NumberText(number)[0]));
                    int x = (64 - UniformLettering.NumberText(number).Length * 32) / 2 + 1;
                    Require(canvas.Bgra.AsSpan((64+x)*4,30*4).SequenceEqual(glyph.Bgra.AsSpan((32+1)*4,30*4)), "Number crop/centering");
                    File.WriteAllBytes(Path.Combine(output,$"{school.Team.Abbreviation}-{variant.Suffix}-{number}.png"),PngWriter.Encode(64,64,canvas.Bgra));
                    cases++;
                }
                foreach (var name in new[] { "", "I", "LEE", "WILLIAMS", "ABCDEFGHIJ", "ABCDEFGHIJKL", "O'NEIL", "DE LA CRUZ" })
                {
                    var canvas=UniformLettering.Name(font,name);
                    Require(canvas.Width==256&&canvas.Height==32,"Name allocation");
                    Require(canvas.Bgra.SequenceEqual(UniformLettering.Name(font,name.ToLowerInvariant()).Bgra),"Case normalization");
                    if(name.Length>10)Require(canvas.Bgra.SequenceEqual(UniformLettering.Name(font,name[..10]).Bgra),"Native truncation");
                    cases++;
                }
                var letter=font.Decode(font.Images.Single(i=>i.Tag=="I___"||i.Tag=="i___"));
                Require(UniformLettering.Name(font,"I").Bgra.AsSpan(72*4,16*4).SequenceEqual(letter.Bgra.AsSpan(0,16*4)),"One-letter native placement at x=72");
                Require(UniformLettering.Number(font,"00").Bgra.SequenceEqual(UniformLettering.Number(font,"0").Bgra),"Numeric leading zero normalization");
                Console.WriteLine($"{school.Team.Name} {variant.Suffix}: native glyph canvases / number and name edge cases passed");
            }
        }
        foreach(var invalid in new[]{"-1","100","A",""})
        {
            try { UniformLettering.NumberText(invalid); throw new InvalidDataException("Accepted invalid number"); }
            catch(ArgumentException) { }
        }
        var profile=profiles.First(p=>p.Team.Abbreviation=="LSU");
        var pack=UniformPreview.Variants(profile)[0].Font!;
        var session=new EditSession(root,Path.Combine(output,"mod"));
        var ssh=SshFile.Read(pack.ReadOriginal()); var image=ssh.Images.Single(i=>i.Tag=="zig3");
        var pixels=ssh.Decode(image).Bgra; pixels[4]= (byte)(pixels[4]^255);
        var changed=SshWriter.ReplaceImage(ssh,image,pixels,image.Header.Width,image.Header.Height).File;
        session.Commit(pack,changed,"Uniform validation: native number glyph replacement");
        session.Undo(); Require(pack.ReadAll().SequenceEqual(pack.ReadOriginal()),"Texture undo");
        session.Redo(); Require(pack.ReadAll().SequenceEqual(changed),"Texture redo");
        var player=profile.Players.First(p=>p.Sources.Any(s=>s.Asset.Name=="attrib.dat"));
        var dat=player.Sources.First(s=>s.Asset.Name=="attrib.dat").Asset;
        var edited=PlayerAttributeEditor.Patch(dat.ReadAll(),player.Id,new Dictionary<int,string>{{1,"WILLIAMS"},{2,"99"}});
        session.Commit(dat,edited,"Uniform validation: roster surname / jersey");
        bool shown=false;
        session.CommitPreviewOnly(pack,"Uniform validation: GST preview","Unsupported writer",apply=>shown=apply);
        session.Undo(); Require(!shown,"Preview undo"); session.Redo(); Require(shown,"Preview redo");
        string project=Path.Combine(output,"uniform.mvpwproj"); ProjectFile.Save(session,project);
        var report=ModExporter.Export(session,session.OutputFolder);
        Require(report.Ok&&report.PreviewOnlySkipped==1&&report.Files.Count==2,"Verified archive export");
        var loaded=ProjectFile.Load(project,root,out var warnings);
        Require(warnings.Count==0&&loaded.Overrides.Count==2&&loaded.PreviewOnlyCount==0,"Project excludes preview settings");
        Require(loaded.GetBytes(pack).SequenceEqual(changed)&&loaded.GetBytes(dat).SequenceEqual(edited),"Project reload bytes");
        Console.WriteLine($"Passed {cases} canvas cases, invalid input, texture/preview undo-redo, roster edits, project reload and verified archive export; preview excluded.");
    }
    private static void Require(bool ok,string message) { if(!ok)throw new InvalidDataException(message); }
}
