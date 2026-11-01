using System.Buffers.Binary;
using System.Text.Json;
using MVPWorkshop.Core.Editing;
using MVPWorkshop.Core.Formats;
using MVPWorkshop.Core.Install;
using MVPWorkshop.Core.Vfs;

var directory = Path.Combine(Path.GetTempPath(), "workshop-live-textures-" + Guid.NewGuid().ToString("N"));
int failures = 0;
void Check(string label, bool value) { Console.WriteLine($"{(value ? "PASS" : "FAIL")} {label}"); if (!value) failures++; }
LiveTexturePlan Read(string generation) => JsonSerializer.Deserialize<LiveTexturePlan>(File.ReadAllText(Path.Combine(generation, "live-textures.json")), new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
try
{
    var game = Path.Combine(directory, "game"); Directory.CreateDirectory(Path.Combine(game, "sample_big"));
    File.WriteAllBytes(Path.Combine(game, "default.xbe"), [0,1,2,3]); File.WriteAllText(Path.Combine(game,"sample_big","_big.json"), "{}");
    var native = new byte[48]; "SHPX"u8.CopyTo(native); BinaryPrimitives.WriteInt32LittleEndian(native.AsSpan(4), native.Length); BinaryPrimitives.WriteInt32LittleEndian(native.AsSpan(8), 1);
    "one\0"u8.CopyTo(native.AsSpan(16)); BinaryPrimitives.WriteInt32LittleEndian(native.AsSpan(20), 24);
    BinaryPrimitives.WriteInt32LittleEndian(native.AsSpan(24), (24 << 8) | 0x60); BinaryPrimitives.WriteUInt16LittleEndian(native.AsSpan(28), 4); BinaryPrimitives.WriteUInt16LittleEndian(native.AsSpan(30), 4);
    if (!LooseInstall.WriteTexturePack(native, Path.Combine(game,"logo.xsh"), out var error)) throw new Exception(error);
    var root = GameFileSystem.OpenGame(game); var node = root.Resolve("logo.xsh")!; var session = new EditSession(root, Path.Combine(directory,"out")); var info = new ModInfo { Id="live-test",Name="Live test" };
    var originalKey = LiveTexturePlan.TextureKey(native, SshFile.Read(native).Images.Single())!;
    Check("content key matches the native renderer test vector", originalKey == "6175d92eed39ad7d");
    if (args.Length > 0) { Directory.CreateDirectory(args[0]); File.WriteAllBytes(Path.Combine(args[0],"original.dds"),XshFolder.Split(native)!["00_one.dds"]); }
    var edited = native.ToArray(); edited[40] = 8; session.Commit(node,edited,"first texture");
    var first = TestModPublisher.Publish(session.Snapshot(),info,Path.Combine(directory,"published")); var plan = Read(first.Folder);
    Check("live plan covers the edited slot with no ownership problems", plan.Problems.Count == 0 && plan.Mappings.Count == 1);
    Check("plan covers original and current resident texture keys", plan.Mappings[0].Keys.Contains(originalKey) && plan.Mappings[0].Keys.Count == 2);
    Check("live DDS matches the current native image", File.ReadAllBytes(Path.Combine(first.Folder,plan.Mappings[0].File)).SequenceEqual(XshFolder.Split(edited)!["00_one.dds"]));
    var secondBytes=edited.ToArray();secondBytes[41]=9;session.Commit(node,secondBytes,"second texture");
    var second=TestModPublisher.Publish(session.Snapshot(),info,Path.Combine(directory,"published"));plan=Read(second.Folder);
    Check("later revisions retain all previously resident keys", plan.Mappings.Single().Keys.Count==3);
    session.Revert(node);var reverted=TestModPublisher.Publish(session.Snapshot(),info,Path.Combine(directory,"published"));plan=Read(reverted.Folder);
    Check("revert-all still carries a live restoration image", reverted.Report.Files.Count==0 && plan.Mappings.Single().Keys.Count==3 && File.ReadAllBytes(Path.Combine(reverted.Folder,plan.Mappings[0].File)).SequenceEqual(XshFolder.Split(native)!["00_one.dds"]));
    if (!LooseInstall.WriteTexturePack(native,Path.Combine(game,"duplicate.xsh"),out error))throw new Exception(error);
    root=GameFileSystem.OpenGame(game);node=root.Resolve("logo.xsh")!;session=new EditSession(root,Path.Combine(directory,"out"));session.Commit(node,edited,"ambiguous texture");
    var ambiguous=TestModPublisher.Publish(session.Snapshot(),info,Path.Combine(directory,"ambiguous"));plan=Read(ambiguous.Folder);
    Check("shared content cannot silently override another asset", plan.Problems.Any(p=>p.Contains("shared with")));
    Check("live sidecars are excluded from exported mod payloads", !MvpModExporter.Collect(session,[],[]).Keys.Any(k=>k.Contains("live-textures")));
    Check("source texture bytes remain unchanged", node.ReadOriginal().SequenceEqual(native));
    Console.WriteLine("Native key vector: "+originalKey);
}
catch(Exception ex){Console.WriteLine("FAIL "+ex);failures++;}
finally { AssetNode.Overlay=null;AssetNode.ChildrenOverlay=null;if(Directory.Exists(directory))Directory.Delete(directory,true); }
return failures==0?0:1;
