using System.Buffers.Binary;
using MVPWorkshop.Core.Editing;
using MVPWorkshop.Core.Formats;
using MVPWorkshop.Core.IO;
using MVPWorkshop.Core.Vfs;

int failed = 0;
void Check(string name, bool value) { Console.WriteLine($"{(value ? "PASS" : "FAIL")} {name}"); if (!value) failed++; }
byte[] Header(int codec, int version = 0, int offset = 0)
{
    using var memory = new MemoryStream(); using var writer = new BinaryWriter(memory); writer.Write("PT"u8); writer.Write((ushort)5);
    void Tag(byte tag, uint value) { writer.Write(tag); writer.Write((byte)4); writer.Write(BinaryPrimitives.ReverseEndianness(value)); }
    Tag(0x80, (uint)version); Tag(0x82,1); Tag(0x84,22050); Tag(0x85,28); Tag(0xA0,(uint)codec); if (offset > 0) Tag(0x88,(uint)offset); writer.Write((byte)0xff); return memory.ToArray();
}
byte[] Stream(int codec, int version = 0)
{
    using var memory = new MemoryStream(); using var writer = new BinaryWriter(memory);
    var header = Header(codec,version); writer.Write("SCHl"u8); writer.Write(header.Length+8); writer.Write(header);
    int size = codec is 7 or 8 ? 56 : codec == 9 ? 28 : codec == 5 ? 16 : 15;
    int prefix = codec == 10 && version != 3 ? 4 : 0;
    writer.Write("SCDl"u8); writer.Write(16+prefix+size); writer.Write(28); writer.Write(0); writer.Write(new byte[prefix+size]); writer.Write("SCEl"u8); writer.Write(8); return memory.ToArray();
}
var input = new DecodedAudio(Enumerable.Range(0,28).Select(i => (short)(Math.Sin(i*0.3)*12000)).ToArray(),1,22050,"sine");
foreach (var codec in new[]{5,10,7,8,9}) foreach(int version in codec==10 ? new[]{0,3}:new[]{0})
{
    var source = Stream(codec, version); var before = source.ToArray(); var result = AudioReplacement.Stream(source,0,input); var memory = new MemoryByteSource(result); var stream = SchlStream.ScanAll(memory).Single(); var decoded = stream.Decode(memory);
    Check($"codec {codec:X2} v{version}: native encoding plays with preserved dimensions", decoded.Pcm.Length==28 && decoded.Channels==1 && decoded.SampleRate==22050 && stream.Header!.Codec2==codec && result.Length==source.Length);
    double error = Math.Sqrt(decoded.Pcm.Zip(input.Pcm).Average(pair=>Math.Pow(pair.First-pair.Second,2))); Check($"codec {codec:X2} v{version}: encoded signal stays within quantization error", error<1600);
    Check($"codec {codec:X2} v{version}: source and framing preserved", source.SequenceEqual(before) && result.AsSpan(0,8+Header(codec,version).Length).SequenceEqual(source.AsSpan(0,8+Header(codec,version).Length)) && result.AsSpan(^8).SequenceEqual(source.AsSpan(^8)));
}
var bankHeader = Header(5, offset:1); int payload = 24 + bankHeader.Length; bankHeader = Header(5, offset:payload); var bank = new byte[payload+16]; "BNKl"u8.CopyTo(bank); bank[4]=5; bank[6]=1; BinaryPrimitives.WriteInt32LittleEndian(bank.AsSpan(20),4); bankHeader.CopyTo(bank,24);
var replaced = AudioReplacement.Bank(bank,0,input); var b = EaBank.Read(replaced); Check("VAG bank replacement retains header pointers and sample count", replaced.AsSpan(0,payload).SequenceEqual(bank.AsSpan(0,payload)) && EaBank.Decode(replaced,b.Sounds[0]).Pcm.Length==28);
int sharedPayload=28+bankHeader.Length*2; var sharedHeader=Header(5,offset:sharedPayload); var sharedBank=new byte[sharedPayload+16]; "BNKl"u8.CopyTo(sharedBank); sharedBank[4]=5; sharedBank[6]=2;
BinaryPrimitives.WriteInt32LittleEndian(sharedBank.AsSpan(20),8); BinaryPrimitives.WriteInt32LittleEndian(sharedBank.AsSpan(24),4+sharedHeader.Length); sharedHeader.CopyTo(sharedBank,28); sharedHeader.CopyTo(sharedBank,28+sharedHeader.Length);
bool sharedRejected=false; try { AudioReplacement.Bank(sharedBank,0,input); } catch(InvalidOperationException) { sharedRejected=true; } Check("shared bank payload cannot silently replace another sound",sharedRejected);
Check("PCM WAV round trip retains exact samples", AudioReplacement.ReadPcmWav(AudioCodecs.ToWav(input)).Pcm.SequenceEqual(input.Pcm));
var stereo = new DecodedAudio([1000,-1000,2000,0],2,22050,""); Check("channel mixing and silence padding preserve target duration", AudioReplacement.Normalize(stereo,22050,1,4).Pcm.SequenceEqual(new short[]{0,1000,0,0}));
var unsupported = new EaPatchHeader { Platform=5 }; unsupported.Tags.Add((0xA0,4,0)); Check("MicroTalk replacement reports missing verified encoder", AudioReplacement.Unsupported(unsupported)?.Contains("No verified encoder") == true);
var bulkTargets = new List<AudioBulkImport.Target> { new(0,"Sound 0",Header5(),1), new(1,"Sound 1",Header5(),1), new(2,"Sound 2",unsupported,1) };
static EaPatchHeader Header5() { var h = new EaPatchHeader { Platform=5 }; h.Tags.Add((0xA0,5,0)); return h; }
var matched = AudioBulkImport.Match("crowd.bnk", bulkTargets, ["x/crowd_sound_0.WAV","x/crowd_Sound_1.wav","x/crowd_Sound_1.ogg","x/crowd_Sound_2.mp3","x/crowd_Sound_9.wav","x/crowd_Sound_0.flac","x/readme.txt"]);
Check("bulk names match Export All case-insensitively", matched.Single(r=>r.Status==AudioBulkImport.Status.Matched).File=="x/crowd_sound_0.WAV");
Check("bulk duplicate inputs stay ambiguous", matched.Count(r=>r.Status==AudioBulkImport.Status.Duplicate && r.Target!.Index==1)==2);
Check("bulk MicroTalk target stays visible with reason", matched.Single(r=>r.Target?.Index==2).Problem!.Contains("No verified encoder"));
Check("bulk unmatched and unsupported inputs are listed", matched.Count(r=>r.Status==AudioBulkImport.Status.Unmatched)==1 && matched.Count(r=>r.Status==AudioBulkImport.Status.UnsupportedInput)==2);
var twoBank = BankOf(2); var rowsTwo = new List<AudioBulkImport.Row> { new("a",bulkTargets[0],AudioBulkImport.Status.Matched,null){Input=input}, new("b",bulkTargets[1],AudioBulkImport.Status.Matched,null){Input=input} };
var built = AudioBulkImport.Build(twoBank, rowsTwo, (cur,i,a)=>AudioReplacement.Bank(cur,i,a)); var builtBank = EaBank.Read(built);
Check("bulk composes multiple sounds of one bank into one result", built.Length==twoBank.Length && builtBank.Sounds.Count==2 && builtBank.Sounds.All(s=>EaBank.Decode(built,s).Pcm.Zip(input.Pcm).All(p=>Math.Abs(p.First-p.Second)<1600)) && !built.SequenceEqual(twoBank));
var oneOnly = AudioBulkImport.Build(twoBank, rowsTwo.Take(1), (cur,i,a)=>AudioReplacement.Bank(cur,i,a)); int sound1At = (int)EaBank.Read(twoBank).Sounds[1].Header.Get(0x88)!.Value;
Check("bulk leaves unmatched sounds byte-identical", oneOnly.AsSpan(sound1At,16).SequenceEqual(twoBank.AsSpan(sound1At,16)) && !oneOnly.SequenceEqual(twoBank));
byte[] BankOf(int count)
{
    int headerLength = Header(5, offset:1).Length, table = 20 + count*4, payload = table + count*headerLength; var data = new byte[payload + count*16];
    "BNKl"u8.CopyTo(data); data[4]=5; data[6]=(byte)count;
    for (int i = 0; i < count; i++) { int at = table + i*headerLength; BinaryPrimitives.WriteInt32LittleEndian(data.AsSpan(20+i*4), at-(20+i*4)); Header(5, offset:payload+i*16).CopyTo(data, at); }
    return data;
}
// MicroTalk 10:1 revision 3: per-block budgets on a synthetic multi-block stream.
double Snr(short[] reference, short[] actual) { double s = 0, n = 0; for (int i = 0; i < reference.Length; i++) { s += (double)reference[i] * reference[i]; double d = reference[i] - actual[i]; n += d * d; } return 10 * Math.Log10(s / Math.Max(n, 1)); }
byte[] MicroTalkStreamOf((int Samples, int Capacity)[] blocks)
{
    using var memory = new MemoryStream(); using var writer = new BinaryWriter(memory);
    var header = Header(4, 3); writer.Write("SCHl"u8); writer.Write(header.Length + 8); writer.Write(header);
    foreach (var (samples, capacity) in blocks) { writer.Write("SCDl"u8); writer.Write(16 + 1 + capacity); writer.Write(samples); writer.Write(0); writer.Write((byte)0x5A); writer.Write(new byte[capacity]); }
    writer.Write("SCEl"u8); writer.Write(8); return memory.ToArray();
}
var mtBlocks = new[] { (1296, 230), (1728, 300), (1296, 120), (1728, 330), (700, 140) };
int mtTotal = mtBlocks.Sum(b => b.Item1); var rng = new Random(7);
var speech = new DecodedAudio(Enumerable.Range(0, mtTotal).Select(i => { double t = i / 22050.0, env = 0.5 + 0.5 * Math.Sin(2 * Math.PI * 3 * t), f0 = 140 + 30 * Math.Sin(2 * Math.PI * 1.5 * t);
    double v = 0; for (int h = 1; h <= 12; h++) v += Math.Sin(2 * Math.PI * f0 * h * t) / h; return (short)Math.Clamp(env * (7000 * v + 600 * (rng.NextDouble() - 0.5)), -32000, 32000); }).ToArray(), 1, 22050, "speech-like");
var mtSource = MicroTalkStreamOf(mtBlocks); var mtBefore = mtSource.ToArray();
var mtResult = AudioReplacement.MicroTalkStream(mtSource, 0, speech, out var mtReport);
var mtMemory = new MemoryByteSource(mtResult); var mtStream = SchlStream.ScanAll(mtMemory).Single(); var mtDecoded = mtStream.Decode(mtMemory);
Check("MicroTalk: stream length, sample count and source preserved", mtResult.Length == mtSource.Length && mtDecoded.Pcm.Length == mtTotal && mtSource.SequenceEqual(mtBefore));
bool framingKept = mtResult.AsSpan(0, 8 + Header(4, 3).Length).SequenceEqual(mtSource.AsSpan(0, 8 + Header(4, 3).Length)) && mtResult.AsSpan(^8).SequenceEqual(mtSource.AsSpan(^8));
foreach (var block in mtStream.Blocks.Where(b => b.Id == "SCDl")) { int at = (int)(mtStream.Offset + block.Offset); framingKept &= mtResult.AsSpan(at, 17).SequenceEqual(mtSource.AsSpan(at, 17)); }
Check("MicroTalk: block sizes, sample counts, channel offsets and flag bytes are exact", framingKept);
Check($"MicroTalk: every block fits its own capacity ({mtReport.BytesUsed}/{mtReport.BytesAvailable} bytes, targets {mtReport.MinTarget}–{mtReport.MaxTarget})", mtReport.Blocks == 5 && mtReport.Frames == 3 + 4 + 3 + 4 + 2 && mtReport.BytesUsed <= mtReport.BytesAvailable);
Check("MicroTalk: rate adapts per block (tight block gets a lower target)", mtReport.MinTarget < mtReport.MaxTarget);
var mtSnr = Snr(speech.Pcm, mtDecoded.Pcm); Check($"MicroTalk: decoded SNR {mtSnr:F2} dB on speech-like input", mtSnr > 6);
Check("MicroTalk: encoding is deterministic", AudioReplacement.MicroTalkStream(mtSource, 0, speech, out _).SequenceEqual(mtResult));
bool tooSmall = false; try { AudioReplacement.MicroTalkStream(MicroTalkStreamOf([(1728, 12)]), 0, speech, out _); } catch (InvalidOperationException) { tooSmall = true; }
Check("MicroTalk: an impossible block budget is rejected instead of overflowing", tooSmall);
var silentResult = AudioReplacement.MicroTalkStream(mtSource, 0, new DecodedAudio(new short[mtTotal], 1, 22050, "silence"), out _); var silentMemory = new MemoryByteSource(silentResult);
var silentReport = mtReport; AudioReplacement.MicroTalkStream(mtSource, 0, new DecodedAudio(new short[mtTotal], 1, 22050, "silence"), out silentReport);
Check($"MicroTalk: starved silent blocks fit by forcing the rate-controllable mode ({silentReport.ForcedHuffmanBlocks} forced)", silentReport.ForcedHuffmanBlocks > 0);
Check("MicroTalk: silence decodes to near-silence", SchlStream.ScanAll(silentMemory).Single().Decode(silentMemory).Pcm.All(s => Math.Abs((int)s) < 64));
var quiet = new DecodedAudio(Enumerable.Range(0, 432 + 1728).Select(i => (short)(50 * Math.Sin(i * 0.07))).ToArray(), 1, 22050, "quiet");
var quietResult = AudioReplacement.MicroTalkStream(MicroTalkStreamOf([(432, 120), (1728, 104)]), 0, quiet, out var quietReport);
Check($"MicroTalk: starved blocks reach the native 26-byte frame minimum ({quietReport.BytesUsed} bytes)", quietReport.BytesUsed <= 120 + 104);
var onsetBlocks = new[] { (1728, 400), (1728, 330), (1728, 330), (1296, 280) }; int onsetTotal = onsetBlocks.Sum(b => b.Item1);
var onset = new DecodedAudio(Enumerable.Range(0, onsetTotal).Select(i => i < 2000 ? (short)(i % 7 - 3) : (short)Math.Clamp(Enumerable.Range(1, 10).Sum(h => 9000.0 / h * Math.Sin(2 * Math.PI * 120 * h * i / 22050.0)), -32000, 32000)).ToArray(), 1, 22050, "onset");
var onsetResult = AudioReplacement.MicroTalkStream(MicroTalkStreamOf(onsetBlocks), 0, onset, out var onsetReport); var onsetMemory = new MemoryByteSource(onsetResult);
var onsetDecoded = SchlStream.ScanAll(onsetMemory).Single().Decode(onsetMemory);
Check($"MicroTalk: onset after silence stays stable (worst block {onsetReport.WorstBlockSnrDb:F1} dB, {onsetReport.StabilizedBlocks} stabilized, peak {onsetDecoded.Pcm.Max(x => Math.Abs((int)x))})", onsetReport.WorstBlockSnrDb > 0 && onsetDecoded.Pcm.Take(2000).All(x => Math.Abs((int)x) < 2000));
Check("MicroTalk: MT5:1, standard-revision and stereo streams are refused", AudioReplacement.MicroTalkUnsupported(EaPatchHeader.Parse(Header(0x16, 3), 0)!) is not null && AudioReplacement.MicroTalkUnsupported(EaPatchHeader.Parse(Header(4, 0), 0)!) is not null);
if(args.Length>0)
{
    var root=GameFileSystem.OpenGame(args[0]); var nodes=root.EnumerateDeep().ToList(); var hd=nodes.First(n=>n.Extension==".hd"); var bd=hd.Parent!.Children.First(n=>n.Name.Equals(Path.ChangeExtension(hd.Name,".bd"),StringComparison.OrdinalIgnoreCase)); var bytes=bd.ReadAll(); var sounds=SonySoundBank.Read(hd.ReadAll(),bytes.Length);
    Check("Sony paired HD resolves real sample offsets and rates", sounds.Count>0 && sounds.All(s=>SonySoundBank.Decode(bytes,s).Pcm.Length==s.Length/16*28));
    var sony=AudioReplacement.Sony(bytes,sounds[0],input); Check("Sony replacement preserves every other sample allocation", sony.AsSpan(sounds[0].Offset+sounds[0].Length).SequenceEqual(bytes.AsSpan(sounds[0].Offset+sounds[0].Length)));
    var asf=nodes.First(n=>n.Format==FormatId.SchlStream && SchlStream.ScanAll(n.Open()).FirstOrDefault()?.Header?.Codec2==10); var original=asf.ReadAll(); var result=AudioReplacement.Stream(original,0,input); var source=new MemoryByteSource(result); Check("real EA-XA stream replacement reopens and keeps original length", result.Length==original.Length && SchlStream.ScanAll(source)[0].Decode(source).Pcm.Length>0 && asf.ReadOriginal().SequenceEqual(original));
}
return failed==0?0:1;
