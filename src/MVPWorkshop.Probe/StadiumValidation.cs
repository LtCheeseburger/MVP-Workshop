using System.Numerics;
using MVPWorkshop.Core.Formats.Eagl;
using MVPWorkshop.Core.Vfs;

internal static class StadiumValidation
{
    public static void Raster()
    {
        var node = new AssetNode("fixture", null, true, 0, null, "test");
        var scene = new StadiumScene { Archive = node };
        var renderer = new StadiumRenderer(scene);
        StadiumComponent Triangle(float z, Vector4 color, bool alpha = false)
        {
            var mesh = new EaglMesh();
            mesh.Positions.AddRange([new(-1,-1,z),new(1,-1,z),new(0,1,z)]);
            mesh.Uvs.AddRange([Vector2.Zero,Vector2.Zero,Vector2.Zero]);
            mesh.Colors.AddRange([color,color,color]); mesh.Indices.AddRange([0,1,2]);
            mesh.Draws.Add(new EaglDraw { TriangleCount=1, PrimState="__EAGL::GeoPrimState:::RUNTIME_ALLOC::SetTextureEnable=false;SetAlphaBlendMode="+(alpha?"EAGL::ABM_BLEND":"EAGL::ABM_NONE")+";SetTransparencyMethod="+(alpha?"EAGL::TM_ALPHA":"EAGL::TM_OPAQUE")+";" });
            var component=new StadiumComponent(node,new EaglModel {Name="fixture",Offset=0,Mesh=mesh});
            renderer.Resources.Add(new(component,[])); return component;
        }
        var red=Triangle(3,new(1,0,0,1)); var blue=Triangle(5,new(0,0,1,1));
        byte[] Render(params StadiumComponent[] visible)=>renderer.Render(64,64,Vector3.Zero,Vector3.UnitZ,60,visible.ToHashSet());
        int centre=(32*64+32)*4;
        void Check(bool condition,string reason) { if(!condition)throw new InvalidDataException(reason); }
        var p=Render(red,blue); Check(p[centre+2]==255 && p[centre]==0,"Stadium depth buffer failed.");
        p=Render(blue);Check(p[centre]==255 && p[centre+2]==0,"Stadium component visibility failed.");
        var half=Triangle(2,new(0,0,1,.5f),true);p=Render(red,half);
        Check(p[centre]>120 && p[centre]<136 && p[centre+2]>120 && p[centre+2]<136,"Stadium vertex-alpha blending failed.");
        var clipped=Triangle(2,new(0,1,0,1));clipped.Model.Mesh.Positions[0]=new(-1,-1,.5f);
        p=Render(clipped);Check(Enumerable.Range(0,64*64).Any(i=>p[i*4+1]>100),"Near-plane clipping dropped crossing geometry.");
        var missing = Triangle(2, Vector4.One);
        missing.Model.Mesh.Draws[0] = new EaglDraw { TriangleCount = 1, Texture = "missing" };
        p = Render(missing);
        Check(p[centre] == 255 && p[centre + 1] == 0 && p[centre + 2] == 255, "Missing material must be magenta.");
        var field=EaglPrimState.Parse("__EAGL::GeoPrimState:::RUNTIME_ALLOC::SetDepthTestMethod=EAGL::DTM_ALWAYS;SetTextureEnable=true;");
        Check(!field.DepthWrite,"Field painter state failed.");
        Check(new EaglDraw {PrimState="__EAGL::GeoPrimState:::line_no_depth_write_state"}.IsLineList,"Named field line state regressed.");
        Console.WriteLine("Raster checks passed: depth, visibility, vertex alpha, near-plane clipping, missing-material magenta and native field-line state.");
    }

    public static void Variants(AssetNode archive)
    {
        for (int variant = 0; variant < 3; variant++)
        {
            var scene = StadiumScene.Load(archive, variant, variant + 1);
            var renderer = new StadiumRenderer(scene, variant);
            if (scene.Components.Any(c => c.Source.Name.StartsWith("mfield") && c.Source.Name != $"mfield{variant}.ord") ||
                scene.Components.Any(c => c.Source.Name.StartsWith("sky") && c.Source.Name != $"sky{variant + 1}.ord"))
                throw new InvalidDataException("Alternative field/sky meshes were stacked.");
            if (!renderer.Sources[$"mfield{variant}.ord/uni2"].Contains($"field{variant}.") ||
                !renderer.Sources[$"sky{variant + 1}.ord/sktr"].Contains($"/sky{variant + 1}.") ||
                renderer.Sources.TryGetValue("crowd.ord/v000", out var crowd) && !crowd.Contains($"/crowd{variant}."))
                throw new InvalidDataException($"Selected field/sky/crowd texture variant was not respected: {renderer.Sources.GetValueOrDefault($"mfield{variant}.ord/uni2")} | {renderer.Sources.GetValueOrDefault($"sky{variant + 1}.ord/sktr")} | {renderer.Sources.GetValueOrDefault("crowd.ord/v000")}");
        }
        Console.WriteLine("LSU variant checks passed: exclusive field/sky geometry and selected local field/sky/crowd packs.");
    }
}
