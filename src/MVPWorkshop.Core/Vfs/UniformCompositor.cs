using System.Numerics;
using MVPWorkshop.Core.Formats;
using MVPWorkshop.Core.Formats.Eagl;

namespace MVPWorkshop.Core.Vfs;

/// <summary>MVPIrradTexture2 fabric/decal passes baked through the native two UV streams.
/// The output atlas is a WPF presentation cache, not an edited game texture.</summary>
public static class UniformCompositor
{
    public static int BlendChannel(int source, int destination, int gsAlpha) =>
        Math.Clamp(destination + ((source-destination)*gsAlpha >> 7), 0, 255);

    public static DecodedImage Compose(EaglMesh mesh, IEnumerable<EaglDraw> draws, DecodedImage fabric, DecodedImage layer, int size = 1024)
    {
        var pixels=ImageOps.Resize(fabric.Bgra,fabric.Width,fabric.Height,size,size);
        for(int at=3;at<pixels.Length;at+=4)pixels[at]=255;
        var result=new DecodedImage { Width=size,Height=size,Bgra=pixels };
        // Keep an unmodified destination: adjacent strip triangles must not blend their shared edge twice.
        var clean=(byte[])pixels.Clone();
        foreach(var draw in draws)
        for(int corner=draw.FirstTriangle*3;corner<(draw.FirstTriangle+draw.TriangleCount)*3;corner+=3)
        {
            if(!draw.SecondaryUvs.TryGetValue(corner,out var sa)||!draw.SecondaryUvs.TryGetValue(corner+1,out var sb)||!draw.SecondaryUvs.TryGetValue(corner+2,out var sc))
                throw new InvalidDataException($"{draw.Texture}: missing native secondary coordinates at {corner}.");
            var a=mesh.Uvs[mesh.Indices[corner]]*size;
            var b=mesh.Uvs[mesh.Indices[corner+1]]*size;
            var c=mesh.Uvs[mesh.Indices[corner+2]]*size;
            float area=Cross(b-a,c-a); if(Math.Abs(area)<1e-8f)continue;
            int x0=Math.Max(0,(int)MathF.Floor(Math.Min(a.X,Math.Min(b.X,c.X)))-1);
            int x1=Math.Min(size-1,(int)MathF.Ceiling(Math.Max(a.X,Math.Max(b.X,c.X)))+1);
            int y0=Math.Max(0,(int)MathF.Floor(Math.Min(a.Y,Math.Min(b.Y,c.Y)))-1);
            int y1=Math.Min(size-1,(int)MathF.Ceiling(Math.Max(a.Y,Math.Max(b.Y,c.Y)))+1);
            for(int y=y0;y<=y1;y++)for(int x=x0;x<=x1;x++)
            {
                var p=new Vector2(x+.5f,y+.5f);
                float wb=Cross(p-a,c-a)/area, wc=Cross(b-a,p-a)/area, wa=1-wb-wc;
                // One atlas texel of dilation prevents WPF's bilinear sampler exposing a clean seam.
                float tolerance=1.5f/MathF.Sqrt(Math.Abs(area));
                if(wa < -tolerance||wb < -tolerance||wc < -tolerance)continue;
                var uv=sa*wa+sb*wb+sc*wc;
                var sample=Sample(layer,uv,draw.Clamp);
                int at=(y*size+x)*4;
                for(int channel=0;channel<3;channel++)pixels[at+channel]=(byte)BlendChannel((int)sample[channel],clean[at+channel],(int)sample.W);
                pixels[at+3]=255;
            }
        }
        return result;
    }

    private static float Cross(Vector2 a,Vector2 b)=>a.X*b.Y-a.Y*b.X;

    /// <summary>GS bilinear texel sampling, normalized ST with the half-texel footprint and TAR wrap modes.</summary>
    public static Vector4 Sample(DecodedImage image,Vector2 uv,(bool U,bool V) clamp)
    {
        float x=uv.X*image.Width-.5f,y=uv.Y*image.Height-.5f;
        int ix=(int)MathF.Floor(x),iy=(int)MathF.Floor(y);
        Vector4 At(int u,int v)
        {
            u=clamp.U?Math.Clamp(u,0,image.Width-1):((u%image.Width)+image.Width)%image.Width;
            v=clamp.V?Math.Clamp(v,0,image.Height-1):((v%image.Height)+image.Height)%image.Height;
            int at=(v*image.Width+u)*4;
            return new Vector4(image.Bgra[at],image.Bgra[at+1],image.Bgra[at+2],image.Bgra[at+3]);
        }
        return Vector4.Lerp(Vector4.Lerp(At(ix,iy),At(ix+1,iy),x-ix),Vector4.Lerp(At(ix,iy+1),At(ix+1,iy+1),x-ix),y-iy);
    }
}
