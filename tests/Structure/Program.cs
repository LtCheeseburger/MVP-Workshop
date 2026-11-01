using System.Buffers.Binary;
using MVPWorkshop.Core.Formats;
using MVPWorkshop.Core.IO;
using MVPWorkshop.Core.Vfs;

int failed=0;
void Check(string label,bool value){Console.WriteLine($"{(value?"PASS":"FAIL")} {label}");if(!value)failed++;}
var texture=new byte[48];"SHPX"u8.CopyTo(texture);BinaryPrimitives.WriteInt32LittleEndian(texture.AsSpan(4),48);BinaryPrimitives.WriteInt32LittleEndian(texture.AsSpan(8),1);"test"u8.CopyTo(texture.AsSpan(16));BinaryPrimitives.WriteInt32LittleEndian(texture.AsSpan(20),24);BinaryPrimitives.WriteInt32LittleEndian(texture.AsSpan(24),(24<<8)|0x60);BinaryPrimitives.WriteUInt16LittleEndian(texture.AsSpan(28),4);BinaryPrimitives.WriteUInt16LittleEndian(texture.AsSpan(30),4);
var regions=AssetStructure.Read(FormatId.SshTexture,new MemoryByteSource(texture));
Check("native texture structure identifies directory header and pixels",regions.Any(r=>r.Offset==16&&r.Length==8)&&regions.Any(r=>r.Offset==24&&r.Length==16)&&regions.Any(r=>r.Offset==40&&r.Length==8));
Check("every exposed byte region stays within the source",regions.All(r=>r.Offset>=0&&r.Length>0&&r.Offset+r.Length<=texture.Length));
foreach(var format in Enum.GetValues<FormatId>())
{
    bool safe=true;try{var parsed=AssetStructure.Read(format,new MemoryByteSource(new byte[]{1,2,3}));safe=parsed.All(r=>r.Offset+r.Length<=3);}catch{safe=false;}
    Check(format+" truncated bytes retain safe structure fallback",safe);
}
Check("all recognized formats declare their viewer coverage",Enum.GetValues<FormatId>().All(f=>!string.IsNullOrWhiteSpace(ViewerCoverage.For(f))));
Check("unknown binary does not claim specialized decoding",ViewerCoverage.For(FormatId.Binary)=="Raw / undecoded");
var row=new SurveyRow{Id=FormatId.Binary,Format="Binary",Count=20};Check("survey markdown includes actual coverage",FormatSurvey.ToMarkdown([row],"Test").Contains("Raw / undecoded"));
return failed==0?0:1;
