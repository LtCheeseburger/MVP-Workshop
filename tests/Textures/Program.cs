using System.Buffers.Binary;
using MVPWorkshop.Core.Formats;
using MVPWorkshop.Core.Editing;
using MVPWorkshop.Core.Vfs;
using MVPWorkshop.Core.IO;

static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
var count = 0;
foreach (var (w,h) in new[] { (32,32), (64,32), (128,128), (256,128) })
foreach (var type in new[] { 1, 2, 4, 5, 8, 9, 10, 11, 12, 13, 14, 15 })
foreach (var swizzle in new[] { false, true })
{
    if (type is 4 or 5 && swizzle) continue;
    var indexed = type is 1 or 2 or >=8;
    var n = type == 1 ? 16 : 256;
    var bytes = type >= 8 ? Gst.TotalSize(w,h,type) : type switch { _ when type == 1 => w*h/2, _ when type == 2 => w*h, _ when type == 4 => w*h*3, _ => w*h*4 };
    var raw = new byte[32 + bytes + (indexed ? 16+n*4 : 0)];
    "SHPS"u8.CopyTo(raw); BinaryPrimitives.WriteInt32LittleEndian(raw.AsSpan(4),raw.Length);
    BinaryPrimitives.WriteInt32LittleEndian(raw.AsSpan(8),1); "TEST"u8.CopyTo(raw.AsSpan(16));
    BinaryPrimitives.WriteInt32LittleEndian(raw.AsSpan(20),16+8);
    // Directory ends at 24; leave header at 24 and adjust allocation.
    Array.Resize(ref raw, raw.Length + 8);
    var o = 24; raw[o]=(byte)type; var size=16+bytes;
    raw[o+1]=(byte)size; raw[o+2]=(byte)(size>>8); raw[o+3]=(byte)(size>>16);
    BinaryPrimitives.WriteUInt16LittleEndian(raw.AsSpan(o+4),(ushort)w); BinaryPrimitives.WriteUInt16LittleEndian(raw.AsSpan(o+6),(ushort)h);
    if(swizzle) raw[o+13]=0x20;
    if(indexed) { var p=o+size; raw[p]=0x21; BinaryPrimitives.WriteUInt16LittleEndian(raw.AsSpan(p+4),(ushort)n); BinaryPrimitives.WriteUInt16LittleEndian(raw.AsSpan(p+6),1); }
    BinaryPrimitives.WriteInt32LittleEndian(raw.AsSpan(4),raw.Length);
    var file=SshFile.Read(raw); var pixels=new byte[w*h*4];
    // A solid colour is exactly representable in every writer including GST.
    for(var i=0;i<w*h;i++) { pixels[i*4]=40; pixels[i*4+1]=80; pixels[i*4+2]=120; pixels[i*4+3]=255; }
    var result=SshWriter.ReplaceImage(file,file.Images[0],pixels,w,h);
    var reopened=SshFile.Read(result.File); var decoded=reopened.Decode(reopened.Images[0]);
    Check(decoded.Bgra.SequenceEqual(pixels),$"roundtrip {type:X2} {w}x{h} swz={swizzle}");
    Check(result.File.AsSpan(0,40).SequenceEqual(raw.AsSpan(0,40)),"headers/tags preserved");
    if (w == 32 && h == 32)
    {
        var large = new byte[64 * 64 * 4]; for (int i = 0; i < large.Length; i += 4) { large[i] = 40; large[i + 1] = 80; large[i + 2] = 120; large[i + 3] = 255; }
        var upscale = SshWriter.ReplaceImage(file, file.Images[0], large, 64, 64, allowResize: true);
        var up = SshFile.Read(upscale.File);
        Check(up.Images[0].Header.Width == 64 && up.Images[0].Header.Height == 64 && up.Decode(up.Images[0]).Bgra.SequenceEqual(large), $"native upscale {type:X2} swz={swizzle}");
        Check(SshWriter.ResizeUnsupported(file, file.Images[0], 48, 48) is not null, "non-power-of-two upscale blocked");
    }
    if(type == 14 && w == 64 && !swizzle)
    {
        var temp=Path.Combine(AppContext.BaseDirectory,"fixtures","MVPWorkshop-textures-"+Guid.NewGuid());
        var game=Path.Combine(temp,"game"); Directory.CreateDirectory(game);
        File.WriteAllBytes(Path.Combine(game,"test.ssh"),raw);
        var root=GameFileSystem.OpenFolder(game); var node=root.Children.Single();
        var session=new EditSession(root,Path.Combine(temp,"mod"));
        session.Commit(node,result.File,"GST import");
        session.Undo(); Check(node.ReadAll().SequenceEqual(raw),"undo");
        session.Redo(); Check(node.ReadAll().SequenceEqual(result.File),"redo");
        var project=Path.Combine(temp,"test.mvpwproj"); ProjectFile.Save(session,project);
        session=ProjectFile.Load(project,root,out var warnings); Check(warnings.Count==0,"project load");
        Check(node.ReadAll().SequenceEqual(result.File),"project bytes");
        var report=ModExporter.Export(session,session.OutputFolder); Check(report.Ok,"mod export");
        var exported=SshFile.Read(File.ReadAllBytes(Path.Combine(session.OutputFolder,"test.ssh")));
        Check(exported.Decode(exported.Images[0]).Bgra.SequenceEqual(pixels),"reopened mod");
        AssetNode.Overlay=null;
        Console.WriteLine("PASS GST undo/redo, project save/load, mod export/reopen");
    }
    count++;
    if(!swizzle && w==32)
    {
        var tail=new byte[]{0x45,0x67,0x89,0xAB};
        var uncompressed=raw.AsSpan(40,bytes).ToArray().Concat(tail).ToArray();
        var packed=RefPack.Compress(uncompressed);
        var compressed=new byte[40+packed.Length+raw.Length-40-bytes];
        raw.AsSpan(0,40).CopyTo(compressed); packed.CopyTo(compressed,40);
        raw.AsSpan(40+bytes).CopyTo(compressed.AsSpan(40+packed.Length));
        compressed[24]|=0x80;
        var block=16+packed.Length; compressed[25]=(byte)block; compressed[26]=(byte)(block>>8); compressed[27]=(byte)(block>>16);
        BinaryPrimitives.WriteInt32LittleEndian(compressed.AsSpan(4),compressed.Length);
        var cf=SshFile.Read(compressed); var cr=SshWriter.ReplaceImage(cf,cf.Images[0],pixels,w,h);
        var rf=SshFile.Read(cr.File); var ri=rf.Images[0];
        Check(rf.Decode(ri).Bgra.SequenceEqual(pixels),"RefPack roundtrip");
        Check(RefPack.Decompress(cr.File.AsSpan(ri.DataOffset,ri.DataLength)).AsSpan(bytes).SequenceEqual(tail),"RefPack tail preserved");
        if (h == 32)
        {
            var large = ImageOps.Resize(pixels, w, h, w * 2, h * 2, ResizeFilter.Nearest);
            var enlarged = SshWriter.ReplaceImage(cf, cf.Images[0], large, w * 2, h * 2, allowResize: true);
            var resizedFile = SshFile.Read(enlarged.File); var resizedImage = resizedFile.Images[0];
            Check(resizedFile.Decode(resizedImage).Bgra.SequenceEqual(large), "RefPack native upscale");
            Check(RefPack.Decompress(enlarged.File.AsSpan(resizedImage.DataOffset, resizedImage.DataLength)).AsSpan(SshWriter.ExpectedBytes(resizedImage)).SequenceEqual(tail), "RefPack upscale retains trailing bytes");
        }

    }
}
Console.WriteLine($"PASS {count} format/dimension/swizzle round trips");

foreach(var type in Enumerable.Range(8,8))
foreach(var swz in new[]{false,true})
{
    const int w=64,h=32;
    var (bw,bh,bits)=Gst.Params(type); var palette=new byte[1024];
    for(var c=0;c<256;c++) { palette[c*4]=(byte)c; palette[c*4+3]=255; }
    var pixels=new byte[w*h*4]; var expected=new byte[w*h];
    for(var y=0;y<h;y++) for(var x=0;x<w;x++)
    {
        var i=y*w+x; var value=(byte)(((y/bh*17+x/bw*13)%240)+(x+y)% (1<<bits));
        expected[i]=value; palette.AsSpan(value*4,4).CopyTo(pixels.AsSpan(i*4));
    }
    var encoded=Gst.Encode(pixels,palette,256,w,h,type,swz);
    Check(Gst.Decode(encoded,w,h,type,swz).SequenceEqual(expected),"GST patterned plane inversion");
}
Console.WriteLine("PASS 16 patterned GST plane round trips");

// Real loose-file samples, including GST packs when supplied as arguments.
foreach(var path in args)
{
    var data=File.ReadAllBytes(path);
    if(Path.GetExtension(path).Equals(".big",StringComparison.OrdinalIgnoreCase))
    {
        var root=GameFileSystem.OpenFolder(Path.GetDirectoryName(Path.GetFullPath(path))!);
        var archive=root.Children.Single(n=>n.Name.Equals(Path.GetFileName(path),StringComparison.OrdinalIgnoreCase));
        data=archive.EnumerateDeep().First(n=>n.Extension==".ssh").ReadAll();
    }
    var file=SshFile.Read(data);
    foreach(var img in file.Images)
    {
        if(SshWriter.Unsupported(file,img) != null) continue;
        var d=file.Decode(img);
        var result=SshWriter.ReplaceImage(file,img,d.Bgra,d.Width,d.Height,PaletteMode.KeepExisting);
        var r=SshFile.Read(result.File); Check(r.Images.Count==file.Images.Count,"image count");
        Check(r.Images.Select(i=>i.Tag).SequenceEqual(file.Images.Select(i=>i.Tag)),"tags");
        foreach(var other in file.Images.Where(i=>i.Index!=img.Index))
            Check(file.Decode(other).Bgra.SequenceEqual(r.Decode(r.Images.Single(i=>i.Index==other.Index)).Bgra),"unrelated image");
        Console.WriteLine($"PASS real {Path.GetFileName(path)} {img.Tag} {d.Width}x{d.Height} {img.TypeMasked:X2}");
    }
}



var reduction = new byte[8 * 8 * 4]; for (int i = 0; i < 64; i++) { reduction[i * 4] = (byte)(i < 32 ? 0 : 255); reduction[i * 4 + 3] = 255; }
var averaged = ImageOps.Resize(reduction, 8, 8, 1, 1);
Check(averaged[0] == 128 && averaged[3] == 255, "area reduction averages complete source coverage");
var edge = new byte[] { 255, 0, 0, 255, 0, 0, 255, 0 };
var alphaEdge = ImageOps.Resize(edge, 2, 1, 1, 1);
Check(alphaEdge[0] == 255 && alphaEdge[2] == 0 && alphaEdge[3] == 128, "area filtering preserves premultiplied-alpha edges");
Check(ImageOps.Resize(edge, 2, 1, 4, 1, ResizeFilter.Nearest).AsSpan(0, 4).SequenceEqual(edge.AsSpan(0, 4)), "nearest filter retains pixel-art samples");
Console.WriteLine("PASS native upscale variants and area/nearest import quality");
