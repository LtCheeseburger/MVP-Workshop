using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using MVPWorkshop.Core.Editing;
using MVPWorkshop.Core.Formats;
using MVPWorkshop.Core.Install;
using MVPWorkshop.Core.Vfs;

var folder = Path.Combine(Path.GetTempPath(), "mvp-base-mods-" + Guid.NewGuid().ToString("N"));
var game = Path.Combine(folder, "game"); Directory.CreateDirectory(Path.Combine(game, "sample_big"));
int failures = 0;
void Check(string label, bool value) { Console.WriteLine($"{(value ? "PASS" : "FAIL")} {label}"); if (!value) failures++; }
try
{
    File.WriteAllBytes(Path.Combine(game, "default.xbe"), [0, 1, 2, 3]); File.WriteAllText(Path.Combine(game, "sample_big", "_big.json"), "{}");
    File.WriteAllText(Path.Combine(game, "sample_big", "a.txt"), "vanilla"); File.WriteAllText(Path.Combine(game, "sample_big", "b.txt"), "untouched");
    var texture = new byte[80]; "SHPX"u8.CopyTo(texture); BinaryPrimitives.WriteInt32LittleEndian(texture.AsSpan(4), texture.Length); BinaryPrimitives.WriteInt32LittleEndian(texture.AsSpan(8), 2);
    for (int i = 0; i < 2; i++)
    {
        int at = 32 + i * 24; Encoding.ASCII.GetBytes(i == 0 ? "one\0" : "two\0").CopyTo(texture, 16 + i * 8); BinaryPrimitives.WriteInt32LittleEndian(texture.AsSpan(20 + i * 8), at);
        BinaryPrimitives.WriteInt32LittleEndian(texture.AsSpan(at), (24 << 8) | 0x60); BinaryPrimitives.WriteUInt16LittleEndian(texture.AsSpan(at + 4), 4); BinaryPrimitives.WriteUInt16LittleEndian(texture.AsSpan(at + 6), 4);
    }
    if (!LooseInstall.WriteTexturePack(texture, Path.Combine(game, "logo.xsh"), out var error)) throw new Exception(error);
    var originals = Directory.GetFiles(game, "*", SearchOption.AllDirectories).ToDictionary(p => p, p => SHA256.HashData(File.ReadAllBytes(p)));
    var root = GameFileSystem.OpenGame(game); var plain = root.Resolve("sample.big/a.txt")!; var pack = root.Resolve("logo.xsh")!;
    var author = new EditSession(root, Path.Combine(folder, "output"));
    var vanillaSnapshot = new EditSession(root, author.OutputFolder, activateOverlay: false);
    vanillaSnapshot.Commit(root.Resolve("sample.big/b.txt")!, [7, 7], "snapshot before base selection");
    author.Commit(plain, Encoding.ASCII.GetBytes("mod A"), "A"); var fromA = texture.ToArray(); fromA[48] = 1; fromA[72] = 2; author.Commit(pack, fromA, "A texture");
    var firstMod = Path.Combine(folder, "a.mvpmod");
    Check("synthetic ZIP base exports", MvpModExporter.Export(author, new ModInfo { Id = "base-a", Name = "Base A" }, firstMod, false).Ok);
    var secondMod = Path.Combine(folder, "base-b"); Directory.CreateDirectory(Path.Combine(secondMod, "files", "sample_big")); Directory.CreateDirectory(Path.Combine(secondMod, "files", "logo.xsh"));
    File.WriteAllText(Path.Combine(secondMod, "files", "sample_big", "a.txt"), "mod B"); File.WriteAllText(Path.Combine(secondMod, "files", "sample_big", "new.txt"), "new base member");
    var fromB = texture.ToArray(); fromB[72] = 3;
    var partsB = XshFolder.Split(fromB)!;
    File.WriteAllBytes(Path.Combine(secondMod, "files", "logo.xsh", "01_two.dds"), partsB["01_two.dds"]);
    Directory.CreateDirectory(Path.Combine(secondMod, "files", "new.xsh"));
    foreach (var (name, bytes) in partsB) File.WriteAllBytes(Path.Combine(secondMod, "files", "new.xsh", name), bytes);
    var json = JsonNode.Parse(MvpModExporter.ModJson(new ModInfo { Id = "base-b", Name = "Base B" }, Convert.ToHexString(SHA1.HashData([0, 1, 2, 3]))))!;
    json["remove"] = new JsonArray("sample_big/b.txt"); json["refpack"] = new JsonArray("sample_big/new.txt");
    File.WriteAllText(Path.Combine(secondMod, "mod.json"), json.ToJsonString());
    var modHashes = new[] { firstMod }.Concat(Directory.GetFiles(secondMod, "*", SearchOption.AllDirectories)).ToDictionary(p => p, p => SHA256.HashData(File.ReadAllBytes(p)));
    var layer = BaseModLayer.Build(root, [firstMod, secondMod]); var session = new EditSession(root, author.OutputFolder); session.SetBaseLayer(layer);
    Check("selected ZIP/folder mods obey later-wins order", Encoding.ASCII.GetString(plain.ReadAll()) == "mod B" && layer.Origin(plain) == "Base B");
    Check("mounting a base does not create project edits", !session.IsDirty && session.Overrides.Count == 0 && session.Added.Count == 0);
    Check("snapshots without a base keep the original namespace", MvpModExporter.Collect(vanillaSnapshot, [], []).ContainsKey("sample_big/b.txt"));
    var added = root.Resolve("sample.big/new.txt");
    Check("base-added files resolve through the tree", added is not null && Encoding.ASCII.GetString(added.ReadAll()) == "new base member" && layer.Origin(added) == "Base B");
    Check("removals hide members without deleting source data", root.Resolve("sample.big/b.txt") is null && File.ReadAllText(Path.Combine(game, "sample_big", "b.txt")) == "untouched");
    var mergedParts = XshFolder.Split(pack.ReadAll())!; var partsA = XshFolder.Split(fromA)!;
    Check("partial DDS changes merge across base mods", mergedParts["00_one.dds"].SequenceEqual(partsA["00_one.dds"]) && mergedParts["01_two.dds"].SequenceEqual(partsB["01_two.dds"]));
    var newPack = root.Resolve("new.xsh");
    Check("new base texture folders expose native bytes and slots", newPack is { IsLooseTexturePack: true } && newPack.Children.Count == 2 && newPack.ReadAll().SequenceEqual(fromB));
    var problems = new List<string>(); var refpack = new List<string>();
    Check("unmodified base bytes are never collected for export", MvpModExporter.Collect(session, problems, refpack).Count == 0 && problems.Count == 0);
    session.Commit(plain, plain.ReadOriginal(), "restore vanilla explicitly");
    var collected = MvpModExporter.Collect(session, problems, refpack);
    Check("explicit vanilla bytes override a changed base", collected["sample_big/a.txt"].SequenceEqual(plain.ReadOriginal()));
    session.Undo(); Check("undo returns to the selected base", Encoding.ASCII.GetString(plain.ReadAll()) == "mod B");
    var editedPack = pack.ReadAll().ToArray(); editedPack[48] = 9; session.Commit(pack, editedPack, "own slot edit");
    collected = MvpModExporter.Collect(session, problems, refpack);
    Check("editing a base pack exports only this project's changed slot", collected.Count == 1 && collected.ContainsKey("logo.xsh/00_one.dds"));
    session.Revert(pack);
    session.Commit(added!, Encoding.ASCII.GetBytes("own new member edit"), "edit base addition");
    Check("editing a base addition promotes a project-owned file", session.Added.ContainsKey("sample_big/new.txt") && !session.Overrides.ContainsKey(added!.Key));
    session.Undo(); Check("undo removes the project copy", session.Added.Count == 0);
    session.Redo(); Check("redo restores the project copy", session.Added.ContainsKey("sample_big/new.txt"));
    var project = Path.Combine(folder, "layered.mvpwproj"); ProjectFile.Save(session, project);
    var restored = ProjectFile.Load(project, root, out var loadWarnings);
    Check("project restores base references before edits", restored.BaseLayer?.References.Count == 2 && restored.Added.ContainsKey("sample_big/new.txt") && loadWarnings.Count == 0 && !restored.IsDirty);
    Check("restored project keeps merged viewers", Encoding.ASCII.GetString(plain.ReadAll()) == "mod B" && Encoding.ASCII.GetString(added!.ReadAll()) == "own new member edit");
    restored.SetBaseLayer(null, markDirty: true);
    Check("selection changes are unsaved project changes", restored.IsDirty);
    var retained = MvpModExporter.Collect(restored, [], []);
    Check("project copies export after their base is deselected", retained.ContainsKey("sample_big/new.txt"));
    ProjectFile.Save(restored, project); restored = ProjectFile.Load(project, root, out loadWarnings);
    Check("deselected project copy survives save and reload", restored.BaseLayer is null && restored.Added.ContainsKey("sample_big/new.txt") && loadWarnings.Count == 0);
    restored.SetBaseLayer(layer);
    try { ProjectFile.Load(Path.Combine(folder, "missing.mvpwproj"), root, out _); }
    catch (FileNotFoundException) { }
    Check("failed project loads preserve the active overlay", Encoding.ASCII.GetString(plain.ReadAll()) == "mod B");
    ProjectFile.Save(restored, project);
    File.WriteAllText(Path.Combine(secondMod, "files", "sample_big", "a.txt"), "changed base");
    var changed = ProjectFile.Load(project, root, out loadWarnings);
    Check("changed base contents produce a project warning", loadWarnings.Any(w => w.Contains("changed since")) && changed.Added.ContainsKey("sample_big/new.txt"));
    File.WriteAllText(Path.Combine(secondMod, "files", "sample_big", "a.txt"), "mod B");
    File.Move(firstMod, firstMod + ".missing");
    try { ProjectFile.Load(project, root, out _); Check("missing base is rejected", false); }
    catch (Exception) { Check("missing base leaves current session overlay intact", Encoding.ASCII.GetString(plain.ReadAll()) == "changed base"); }
    File.Move(firstMod + ".missing", firstMod);
    changed.SetBaseLayer(null);
    var removedOriginal = root.Resolve("sample.big/b.txt")!;
    changed.Commit(removedOriginal, [5, 6, 7], "own edit before base removal"); changed.SetBaseLayer(layer);
    Check("explicit edits remain visible over base removals", root.Resolve("sample.big/b.txt") == removedOriginal && removedOriginal.ReadAll().SequenceEqual(new byte[] { 5, 6, 7 }));
    Check("explicit edits remain exportable over base removals", MvpModExporter.Collect(changed, [], []).ContainsKey("sample_big/b.txt"));
    changed.Revert(removedOriginal); Check("revert exposes the base removal again", root.Resolve("sample.big/b.txt") is null);
    var snapshot = session.Snapshot(); session.Revert(added!); session.SetBaseLayer(null);
    restored.SetBaseLayer(null);
    changed.SetBaseLayer(null);
    collected = MvpModExporter.Collect(snapshot, problems, refpack);
    Check("snapshot retains the base and explicitly copied edits", collected["sample_big/new.txt"].SequenceEqual(Encoding.ASCII.GetBytes("own new member edit")) && refpack.Contains("sample_big/new.txt"));
    Check("unmount restores the original tree and viewer data", root.Resolve("sample.big/new.txt") is null && root.Resolve("sample.big/b.txt") is not null && Encoding.ASCII.GetString(plain.ReadAll()) == "vanilla");
    Check("base selection never writes game or installed mods", originals.All(p => SHA256.HashData(File.ReadAllBytes(p.Key)).SequenceEqual(p.Value)) && modHashes.All(p => SHA256.HashData(File.ReadAllBytes(p.Key)).SequenceEqual(p.Value)));
    Check("base references retain ids, versions and fingerprints", layer.References.Count == 2 && layer.References.All(r => r.Fingerprint.Length == 64));
}
catch (Exception ex) { Console.WriteLine("FAIL " + ex); failures++; }
finally { AssetNode.Overlay = null; AssetNode.ChildrenOverlay = null; Directory.Delete(folder, true); }
return failures == 0 ? 0 : 1;
