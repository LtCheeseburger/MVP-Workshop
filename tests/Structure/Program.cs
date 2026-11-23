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
var events = new byte[60]; BinaryPrimitives.WriteUInt32LittleEndian(events, SpeechEvents.Signature); BinaryPrimitives.WriteInt32LittleEndian(events.AsSpan(4),20); BinaryPrimitives.WriteInt32LittleEndian(events.AsSpan(16),1); "stadium"u8.CopyTo(events.AsSpan(52));
var directory=SpeechEvents.Read(events);
Check("EVT resolves directory-relative names",directory.Events.Single().Name=="stadium" && directory.Events[0].NameOffset==52);
Check("EVT detected by signature",FormatDetector.Detect("Events.evt",new MemoryByteSource(events))==FormatId.SpeechEvents);
Check("EVT exposes bounded record and name regions",AssetStructure.Read(FormatId.SpeechEvents,new MemoryByteSource(events)).Any(r=>r.Offset==20&&r.Length==32));
foreach(int length in Enumerable.Range(0,events.Length))
{ bool rejected=false;try{SpeechEvents.Read(events[..length]);}catch(InvalidDataException){rejected=true;} Check("EVT truncated at "+length,rejected); }
var invalid=events.ToArray();BinaryPrimitives.WriteUInt32LittleEndian(invalid.AsSpan(20),uint.MaxValue);
try{SpeechEvents.Read(invalid);Check("EVT rejects out-of-bounds name pointer",false);}catch(InvalidDataException){Check("EVT rejects out-of-bounds name pointer",true);}
var linked=Convert.FromHexString("01600102C900000000003F0000000000000003001F0270700C000000");
Check("linked speech header recognized without -1 sentinel",FormatDetector.Detect("201.hdr",new MemoryByteSource(linked))==FormatId.SpeechHeader&&SpeechHeader.Parse(linked)!.Link==201);
Check("speech header retains unknown fixed fields and payload",AssetStructure.Read(FormatId.SpeechHeader,new MemoryByteSource(linked)).Any(r=>r.Offset==12&&r.Length==6)&&SpeechHeader.Parse(linked)!.Payload.Length==linked.Length-18);
var roster=new byte[491];BinaryPrimitives.WriteInt32LittleEndian(roster,1);"Fixture"u8.CopyTo(roster.AsSpan(12));
Check("roster identified by validated records",FormatDetector.Detect("roster.bin",new MemoryByteSource(roster))==FormatId.TeamRoster);
Check("malformed roster remains raw",FormatDetector.Detect("roster.bin",new MemoryByteSource(roster[..490]))!=FormatId.TeamRoster);
Check("roster tail and slot regions remain bounded",AssetStructure.Read(FormatId.TeamRoster,new MemoryByteSource(roster)).All(r=>r.Offset+r.Length<=roster.Length));
if(args.Length>0)
{
    var root=GameFileSystem.OpenFolder(args[0]);int hdr=0,evt=0;var variants=new Dictionary<string,int>();
    foreach(var n in root.EnumerateDeep().Where(n=>n.Extension is ".hdr" or ".evt" || n.Name.Equals("roster.bin",StringComparison.OrdinalIgnoreCase)))
    {
        var bytes=n.ReadAll();
        if(n.Extension==".evt") { var parsed=SpeechEvents.Read(bytes);Check(n.Key+" EVT directory has bounded names",parsed.Events.All(e=>e.NameOffset>=parsed.NamesOffset&&e.NameOffset+e.Name.Length<bytes.Length));evt++; }
        else if(n.Extension==".hdr") {if(n.Format==FormatId.SpeechHeader)hdr++; var key=n.Format.ToString();variants[key]=variants.GetValueOrDefault(key)+1;}
        else Check(n.Key+" reuses validated roster parser",n.Format==FormatId.TeamRoster&&TeamRoster.Read(bytes,TeamRoster.IsXbox(n)).Count>0);
        Check(n.Key+" bounded Hex regions",AssetStructure.Read(n.Format,new MemoryByteSource(bytes)).All(r=>r.Offset>=0&&r.Length<=bytes.Length-r.Offset));
    }
    Check("real installation contains eight EVT directories",evt==8);Console.WriteLine($"REAL headers recognized={hdr}; variants="+string.Join(",",variants.Select(p=>$"{p.Key}:{p.Value}")));
}
var terf = new byte[32]; "TERF"u8.CopyTo(terf); BinaryPrimitives.WriteUInt32LittleEndian(terf.AsSpan(4), 16);
"DATA"u8.CopyTo(terf.AsSpan(16)); BinaryPrimitives.WriteUInt32LittleEndian(terf.AsSpan(20), 16);
Check("TERF signature detection", FormatDetector.Detect("test.dat", new MemoryByteSource(terf)) == FormatId.TerfArchive);
Check("TERF physical block boundaries", TerfArchive.Read(new MemoryByteSource(terf)).Last() == new TerfArchive.Block("DATA", 16, 16));
var terfNode = new AssetNode("test.dat", null, false, terf.Length, () => new MemoryByteSource(terf), "fixture");
Check("TERF payload nodes are bounded and read only", terfNode.Children.Count == 2 && terfNode.Children.All(n => n.ReadAll().Length == 8 && n.WriteBlocker != null && !n.IsContainer));
foreach (var badSize in new uint[] { 0, 7, 33, uint.MaxValue })
{
    BinaryPrimitives.WriteUInt32LittleEndian(terf.AsSpan(4), badSize);
    try { TerfArchive.Read(new MemoryByteSource(terf)); Check("TERF rejects invalid block size " + badSize, false); }
    catch (InvalidDataException) { Check("TERF rejects invalid block size " + badSize, true); }
}
return failed==0?0:1;
