using System.Buffers.Binary;
using System.Text;
using System.Security.Cryptography;
using MVPWorkshop.Core.Editing;
using MVPWorkshop.Core.Vfs;

var temp = Path.Combine(Path.GetTempPath(), "mvp-school-fork-" + Guid.NewGuid().ToString("N"));
var game = Path.Combine(temp, "game");
int failures = 0;
void Check(string label, bool value) { Console.WriteLine($"{(value ? "PASS" : "FAIL")} {label}"); if (!value) failures++; }
void Write(string path, byte[] bytes) { var file = Path.Combine(game, path); Directory.CreateDirectory(Path.GetDirectoryName(file)!); File.WriteAllBytes(file, bytes); }
try
{
    Write("default.xbe", [0, 1, 2, 3]);
    foreach (var folder in new[] { "database_big", "models_big", "uniforms_big", "logos_big" }) Write(folder + "/_big.json", "{}"u8.ToArray());
    var roster = new byte[4 + 487 * 2]; BinaryPrimitives.WriteInt32LittleEndian(roster, 2);
    for (int i = 0; i < 2; i++) { Encoding.ASCII.GetBytes("School " + i).CopyTo(roster, 4 + i * 487 + 8); BinaryPrimitives.WriteUInt32LittleEndian(roster.AsSpan(4 + i * 487 + 164), 7u | 7u << 9); }
    Write("database_big/roster.bin", roster);
    foreach (var name in new[] { "u007a.xsh", "u007b.xsh", "u007c.xsh", "f007a.xsh", "f007b.xsh" }) Write("models_big/" + name, [1, 2, 3]);
    Write("uniforms_big/007a.xsh", [4, 5]); Write("logos_big/l007.xsh", [8, 9]); Write("models_big/u000a.xsh", [9]);
    var originals = Directory.GetFiles(game, "*", SearchOption.AllDirectories).ToDictionary(p => p, p => SHA256.HashData(File.ReadAllBytes(p)));
    var root = GameFileSystem.OpenGame(game); var session = new EditSession(root, Path.Combine(temp, "output"));
    var schools = TeamAssetCatalog.Build(root.EnumerateDeep().ToList()); var source = root.Resolve("models.big/u007a.xsh")!;
    var index = await DependencyIndex.BuildAsync(root);
    Check("used-by identifies both decoded school consumers", index.Impact("asset:" + source.Key).Count(s => s.Id.StartsWith("team:")) == 2);
    var plan = SchoolAssetFork.Plan(session, source, schools[0]);
    Check("allocation avoids existing unassigned family files", plan.Family == 1);
    Check("fork copies all variants fonts and menu artwork", plan.Copies.Count == 6);
    plan.Apply(session, [77, 88], "Replace uniform");
    var current = TeamAssetCatalog.Build(root.EnumerateDeep().ToList());
    Check("only selected school changes assignment", current.Single(p => p.Team.RosterIndex == 0).Team.UniformId == 1 && current.Single(p => p.Team.RosterIndex == 1).Team.UniformId == 7);
    Check("original shared asset stays unchanged", source.ReadAll().SequenceEqual(new byte[] { 1, 2, 3 }));
    Check("added family resolves in the VFS and school catalog", current.Single(p => p.Team.RosterIndex == 0).Assets.Any(a => a.Asset.Name == "u001a.xsh" && a.Asset.ReadAll().SequenceEqual(new byte[] { 77, 88 })));
    Check("fork is one history entry", session.History.Count == 1 && session.Added.Count == 6);
    session.Undo(); Check("one undo restores assignments and removes entire copy", session.Added.Count == 0 && session.Overrides.Count == 0 && !root.EnumerateDeep().Any(n => n.Name == "u001a.xsh"));
    session.Redo(); Check("redo restores entire family", session.Added.Count == 6 && root.EnumerateDeep().Any(n => n.Name == "u001a.xsh"));
    session.Undo(); var stale = SchoolAssetFork.Plan(session, source, schools[0]); session.Commit(source, [6], "concurrent edit");
    bool rejected = false; try { stale.Apply(session, [8], "stale"); } catch (InvalidOperationException) { rejected = true; }
    Check("stale family review refuses partial publication", rejected && session.Added.Count == 0);
    session.Undo(); var logo = root.Resolve("logos.big/l007.xsh")!; var logoPlan = SchoolAssetFork.Plan(session, logo, schools[0]); logoPlan.Apply(session, [42], "Replace logo");
    var assigned = MVPWorkshop.Core.Formats.TeamRoster.Read(schools[0].Roster.ReadAll(), true);
    Check("logo isolation retains the uniform assignment", assigned[0].LogoId == logoPlan.Family && assigned[0].UniformId == 7 && assigned[1].LogoId == 7);
    Check("source game files were never written", originals.All(p => SHA256.HashData(File.ReadAllBytes(p.Key)).SequenceEqual(p.Value)) && Directory.GetFiles(game, "*", SearchOption.AllDirectories).Length == originals.Count);
}
finally { AssetNode.Overlay = null; AssetNode.ChildrenOverlay = null; Directory.Delete(temp, true); }
return failures == 0 ? 0 : 1;
