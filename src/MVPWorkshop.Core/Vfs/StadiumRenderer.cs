using System.Numerics;
using MVPWorkshop.Core.Formats;
using MVPWorkshop.Core.Formats.Eagl;

namespace MVPWorkshop.Core.Vfs;

/// <summary>Perspective stadium rasterization using the existing GS sampler and blend equations.</summary>
public sealed class StadiumRenderer
{
    public sealed record Resource(StadiumComponent Component, Dictionary<string, AptTexture> Textures);
    public List<Resource> Resources { get; } = [];
    public List<string> Diagnostics { get; } = [];
    public Dictionary<string, string> Sources { get; } = new(StringComparer.OrdinalIgnoreCase);
    public StadiumRenderer(StadiumScene scene, int crowd = 0)
    {
        var packs = new Dictionary<AssetNode, Dictionary<string,AptTexture>>();
        foreach(var pack in scene.Archive.Children.Where(n=>n.IsTexturePack))
        {
            var images = new Dictionary<string,AptTexture>(StringComparer.OrdinalIgnoreCase);
            try {
                var ssh=SshFile.Read(pack.ReadAll());
                foreach(var i in ssh.Images) {
                    try { var d=ssh.Decode(i); images.TryAdd(i.Tag,new(d.Width,d.Height,d.Bgra)); }
                    catch(Exception ex) { Diagnostics.Add($"{pack.Name}/{i.Tag}: {ex.Message}"); }
                }
            } catch(Exception ex) { Diagnostics.Add($"{pack.Name}: {ex.Message}"); }
            packs.Add(pack,images);
        }
        foreach(var c in scene.Components)
        {
            var textures=new Dictionary<string,AptTexture>(StringComparer.OrdinalIgnoreCase);
            var packExt=packs.Keys.FirstOrDefault()?.Extension ?? ".ssh";
            var preferred=c.Source.Name == "crowd.ord" ? $"crowd{crowd}{packExt}" : c.Source.Name.Replace("mfield","field").Replace(".ord",packExt);
            foreach(var tag in c.Model.Mesh.Draws.Where(d=>d.State.Textured).Select(d=>d.Texture).OfType<string>().Distinct())
            {
                foreach(var pack in packs.Where(p => !(c.Source.Name=="crowd.ord" && p.Key.Name.StartsWith("crowd") && p.Key.Name!=preferred) && !(c.Source.Name.StartsWith("sky") && p.Key.Name.StartsWith("sky") && p.Key.Name!="skyc"+packExt && p.Key.Name!=preferred) && !(c.Source.Name.StartsWith("mfield") && (p.Key.Name.StartsWith("field") || p.Key.Name.StartsWith("vfield")) && p.Key.Name!=preferred && p.Key.Name!="v"+preferred)).OrderByDescending(p=>p.Key.Name==preferred).ThenByDescending(p=>p.Key.Name=="v"+preferred).ThenByDescending(p=>p.Key.Name=="cram"+packExt))
                    if(pack.Value.TryGetValue(c.Source.Name=="crowd.ord" && tag=="v000" ? "x000" : tag,out var image)) { textures[tag]=image; Sources[c.Source.Name+"/"+tag]=pack.Key.Path+"/"+(c.Source.Name=="crowd.ord" && tag=="v000" ? "x000 (runtime v000)" : tag); break; }
                if(!textures.ContainsKey(tag)) Diagnostics.Add($"Missing texture {c.Source.Name}/{tag} (archive-local only)");
            }
            if(c.Model.Mesh.Draws.Any(d=>d.State.Textured && d.Texture is null)) if(!Diagnostics.Contains($"Missing runtime material: {c.Source.Name} (unbound TAR)")) Diagnostics.Add($"Missing runtime material: {c.Source.Name} (unbound TAR)");
            Resources.Add(new(c,textures));
        }
    }
    readonly record struct Vertex(Vector3 P, Vector2 Uv, Vector4 Color)
    {
        public static Vertex Lerp(Vertex a,Vertex b,float t)=>new(Vector3.Lerp(a.P,b.P,t),Vector2.Lerp(a.Uv,b.Uv,t),Vector4.Lerp(a.Color,b.Color,t));
    }
    public byte[] Render(int width,int height,Vector3 eye,Vector3 look,float fov,ISet<StadiumComponent> visible)
    {
        var surface=new AptSurface(width,height); surface.Clear(.025f,.025f,.035f);
        var depth=new float[width*height]; Array.Fill(depth,float.PositiveInfinity);
        var forward=Vector3.Normalize(look); var right=Vector3.Normalize(Vector3.Cross(forward,Vector3.UnitY)); var up=Vector3.Cross(right,forward);
        var focal=width/(2*MathF.Tan(fov*MathF.PI/360));
        Vector3 ToCamera(Vector3 p) {var v=p-eye;return new(Vector3.Dot(v,right),Vector3.Dot(v,up),Vector3.Dot(v,forward));}
        Vector2 Project(Vector3 p)=>new(width*.5f+p.X/p.Z*focal,height*.5f-p.Y/p.Z*focal);
        // Sky/ground painter passes run first. Other geometry uses the z buffer.
        int Order(Resource r)=>r.Component.Source.Name.StartsWith("sky")?0:r.Component.Source.Name.StartsWith("mfield")?1:r.Component.Source.Name=="mshadow.ord"?2:r.Component.Source.Name=="crowd.ord"?4:r.Component.Source.Name=="alphablend.ord"?5:3;
        foreach(var resource in Resources.Where(r=>visible.Contains(r.Component)).OrderBy(Order))
        {
            var c=resource.Component; var mesh=c.Model.Mesh;
            var transform=c.Transform==default?Matrix4x4.Identity:c.Transform;
            var vertices=mesh.Positions.Select((p,i)=>new Vertex(ToCamera(Vector3.Transform(p,transform)),mesh.Uvs[i],i<mesh.Colors.Count?mesh.Colors[i]:Vector4.One)).ToArray();
            foreach(var d in mesh.Draws)
            {
                if(c.Source.Name.StartsWith("mfield") && d.Texture=="lod1") continue; // distant-field alternative, not a high-detail overlay
                var state=d.State; resource.Textures.TryGetValue(d.Texture??"",out var texture);
                var blend=AptBlend.From(state); var sampler=new AptSampler(d.Clamp.U,d.Clamp.V);
                if(d.IsLineList) foreach(var (ia,ib) in mesh.Lines.Skip(d.FirstLine).Take(d.LineCount)) {
                    var a=vertices[ia];var b=vertices[ib];
                    if(a.P.Z<1 && b.P.Z<1)continue;
                    if(a.P.Z<1)a=Vertex.Lerp(a,b,(1-a.P.Z)/(b.P.Z-a.P.Z));
                    if(b.P.Z<1)b=Vertex.Lerp(b,a,(1-b.P.Z)/(a.P.Z-b.P.Z));
                    var pa=Project(a.P);var pb=Project(b.P);
                    surface.Line(new(pa.X,pa.Y,0,0,a.Color),new(pb.X,pb.Y,0,0,b.Color),new(null,sampler,blend,Vector4.One,Vector4.Zero,null,false));
                }
                for(int at=d.FirstTriangle*3;at<(d.FirstTriangle+d.TriangleCount)*3;at+=3)
                {
                    var input=new[]{vertices[mesh.Indices[at]],vertices[mesh.Indices[at+1]],vertices[mesh.Indices[at+2]]};
                    // Clip crossing triangles at the near plane instead of dropping entire faces.
                    var polygon=new List<Vertex>(4);
                    for(int i=0;i<3;i++) {
                        var a=input[i];var b=input[(i+1)%3];bool ai=a.P.Z>=1,bi=b.P.Z>=1;
                        if(ai)polygon.Add(a);
                        if(ai!=bi)polygon.Add(Vertex.Lerp(a,b,(1-a.P.Z)/(b.P.Z-a.P.Z)));
                    }
                    for(int triangle=1;triangle+1<polygon.Count;triangle++)
                    {
                        var a=polygon[0];var b=polygon[triangle];var cc=polygon[triangle+1];
                        var pa=Project(a.P);var pb=Project(b.P);var pc=Project(cc.P);
                        surface.Cover(pa,pb,pc,(pixel,w0,w1,w2)=>{
                            float q0=w0/a.P.Z,q1=w1/b.P.Z,q2=w2/cc.P.Z,q=q0+q1+q2,z=1/q;
                            if(state.DepthWrite && z>depth[pixel]+.01f)return;
                            var color=(a.Color*q0+b.Color*q1+cc.Color*q2)/q;
                            if(state.Textured) {
                                var uv=(a.Uv*q0+b.Uv*q1+cc.Uv*q2)/q;
                                if(texture is not null)color*=AptSurface.Sample(texture,uv.X*texture.Width,uv.Y*texture.Height,sampler);
                                else color=new Vector4(1,0,1,1);
                            }
                            color=Vector4.Clamp(color,Vector4.Zero,Vector4.One);
                            if(state.AlphaTest && color.W<1f/128)return;
                            surface.Blend(pixel,color,blend);
                            if(state.DepthWrite && (state.Blend==EaglBlend.Opaque || color.W>0)) depth[pixel]=z;
                        });
                    }
                }
            }
        }
        return surface.ToBgra();
    }
}







