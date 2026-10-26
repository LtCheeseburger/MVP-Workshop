using System.Text;
using MVPWorkshop.Core.Editing;
using MVPWorkshop.Core.Formats;
using MVPWorkshop.Core.Vfs;

if (args.Length != 2) throw new ArgumentException("PlayerEditor <game folder> <separate output folder>");
int checks = 0;
void Check(bool ok, string name) { if (!ok) throw new Exception(name); checks++; Console.WriteLine("PASS " + name); }
var root = GameFileSystem.OpenFolder(Path.GetFullPath(args[0]));
var nodes = root.EnumerateDeep().ToList();
var profiles = TeamAssetCatalog.Build(nodes);
var output = Path.GetFullPath(args[1]);
var session = new EditSession(root, output);
foreach (var node in profiles.SelectMany(p => p.Players).SelectMany(p => p.Sources).Select(s => s.Asset).Distinct())
{
    var bytes = node.ReadAll();
    var table = PlayerAttributes.Read(bytes);
    foreach (var record in table.Records.Values.Where((_, index) => index % 500 == 0).Concat(table.Records.Values.TakeLast(1)))
        if (!PlayerAttributeEditor.Patch(bytes, record.Id, record.Fields).AsSpan().SequenceEqual(bytes)) throw new Exception("Unchanged round trip failed");
    Check(true, $"{node.Name}: whole-table unchanged round trips from sampled rows are byte-identical ({table.Records.Count} records retained)");
    Console.WriteLine("HEADER " + Encoding.Latin1.GetString(bytes).Split('\n')[0]);
}
var player = profiles.First(p => p.Players.Count > 0).Players[0];
var source = player.Sources.Single(s => s.Asset.Name == "attrib.dat").Asset;
var original = source.ReadAll();
var edits = new Dictionary<int, string> { [0] = "Test Name", [1] = "Roundtrip", [2] = "99", [5] = "2", [13] = "1", [18] = "1", [35] = "77" };
var edited = PlayerAttributeEditor.Patch(original, player.Id, edits);
session.Commit(source, edited, "Structured player test");
Check(session.IsDirty && session.ModifiedAssetCount == 1, "saved change tracking");
Check(TeamAssetCatalog.Build(nodes).SelectMany(p => p.Players).First(p => p.Id == player.Id).Name == "Test Name Roundtrip", "team workspace reads project overlay");
session.Undo(); Check(source.ReadAll().AsSpan().SequenceEqual(original), "undo exact bytes");
session.Redo(); Check(source.ReadAll().AsSpan().SequenceEqual(edited), "redo exact bytes");
var expected = new Dictionary<string, byte[]> { [source.Key] = edited };
foreach (var extra in profiles.SelectMany(p => p.Players).SelectMany(p => p.Sources).Where(s => s.Asset.Name != "attrib.dat").DistinctBy(s => s.Asset.Key))
{
    var bytes = extra.Asset.ReadAll();
    var before = PlayerAttributes.Read(bytes).Records[extra.Record.Id];
    var value = before.Field(2) == "50" ? "49" : "50";
    var draft = PlayerAttributeEditor.Patch(bytes, before.Id, new Dictionary<int, string> { [2] = value });
    session.Commit(extra.Asset, draft, "Specialized rating: " + extra.Labels[2]);
    expected[extra.Asset.Key] = draft;
    Check(PlayerAttributes.Read(draft).Records[before.Id].Field(2) == value, extra.Asset.Name + " specialized rating reopens");
}
Directory.CreateDirectory(output);
var project = Path.Combine(output, "players.mvpwproj");
ProjectFile.Save(session, project);
var loaded = ProjectFile.Load(project, root, out var warnings);
Check(warnings.Count == 0 && loaded.GetBytes(source).AsSpan().SequenceEqual(edited), "project save and reopen");
var report = ModExporter.Export(loaded, output, verify: true);
Check(report.Ok, "archive export verifier: " + string.Join("; ", report.Problems));
var reopenedRoot = GameFileSystem.OpenFolder(output);
var actual = reopenedRoot.Resolve(source.Key)!.ReadAll();
Check(actual.AsSpan().SequenceEqual(edited), "exported DAT reopened with exact edited bytes");
foreach (var entry in expected)
    Check(reopenedRoot.Resolve(entry.Key)!.ReadAll().AsSpan().SequenceEqual(entry.Value), entry.Key + " exported bytes reopen exactly");
Check(profiles[0].Roster.ReadOriginal().AsSpan().SequenceEqual(profiles[0].Roster.ReadAll()), "roster relationships and all packed bits untouched");
// Adversarial formatting and unknown payload preservation.
var fixture = Encoding.Latin1.GetBytes("0 first_name,2 playerattrib_jerseynum,;\r\n00000001,0 Old  ,2 7,90 opaque bytes,;\r\n# tail\r\n");
var patched = PlayerAttributeEditor.Patch(fixture, 1, new Dictionary<int, string> { [0] = "New" });
Check(Encoding.Latin1.GetString(patched) == Encoding.Latin1.GetString(fixture).Replace("Old", "New"), "only selected span changes; unknown field, whitespace, CRLF and tail retained");
foreach (var invalid in new[] { "bad,name", "bad;name", "bad\nname", "😀" })
{
    bool rejected = false;
    try { PlayerAttributeEditor.Patch(fixture, 1, new Dictionary<int, string> { [0] = invalid }); } catch (InvalidDataException) { rejected = true; }
    Check(rejected, "reject delimiter/control/unencodable input");
}
bool unknown = false;
try { PlayerAttributeEditor.Patch(fixture, 1, new Dictionary<int, string> { [90] = "9" }); } catch (InvalidDataException) { unknown = true; }
Check(unknown, "unknown fields cannot be edited");
var external = PlayerAttributeEditor.Patch(fixture, 1, new Dictionary<int, string> { [2] = "8" });
var merged = PlayerAttributeEditor.ApplyDraft(fixture, external, 1, new Dictionary<int, string> { [0] = "Merged" });
Check(PlayerAttributes.Read(merged).Records[1].Field(0) == "Merged" && PlayerAttributes.Read(merged).Records[1].Field(2) == "8", "draft merges unrelated field edits without overwrite");
bool conflict = false;
try { PlayerAttributeEditor.ApplyDraft(fixture, external, 1, new Dictionary<int, string> { [2] = "9" }); } catch (InvalidDataException) { conflict = true; }
Check(conflict, "draft refuses concurrent same-field edits");
foreach (var invalid in new[] { "1.5", "name", "2147483648" })
{
    bool rejected = false;
    try { PlayerAttributeEditor.Patch(fixture, 1, new Dictionary<int, string> { [2] = invalid }); } catch (InvalidDataException) { rejected = true; }
    Check(rejected, "integer attribute rejects invalid shape/overflow");
}
Console.WriteLine($"{checks} checks passed.");
