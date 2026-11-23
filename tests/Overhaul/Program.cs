// Adjacent MVP 27 overhaul: discovery, identity/format failures, layered override/added resolution with base access,
// deduplication, project persistence, edit → rebuild → reopen, explicit override of a base file, no-edit roundtrip.
// Usage: dotnet run --project tests/Overhaul -c Release -- <layout with MVP06.exe, game/ and the .mvpmod> <scratch folder>
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using MVPWorkshop.Core.Editing;
using MVPWorkshop.Core.Vfs;

var layout = Path.GetFullPath(args[0]);
var scratch = Path.GetFullPath(args[1]);
if (Directory.Exists(scratch)) Directory.Delete(scratch, true);
Directory.CreateDirectory(scratch);
var game = Path.Combine(layout, "game");
int failures = 0, checks = 0;
void Check(string what, bool ok) { checks++; if (!ok) failures++; Console.WriteLine($"{(ok ? "PASS" : "FAIL")} {what}"); }
string Hash(byte[] b) => Convert.ToHexStringLower(SHA256.HashData(b));
Dictionary<string, string> Payload(string archive)
{
    using var z = ZipFile.OpenRead(archive);
    return z.Entries.ToDictionary(e => e.FullName, e => { using var s = e.Open(); using var m = new MemoryStream(); s.CopyTo(m); return Hash(m.ToArray()); });
}
var guard = new[] { "default.xbe", "data/database/database_big/roster.bin", "data/database/dbmisc_big/teaminfo.csv" }
    .Select(p => Path.Combine(game, p)).Where(File.Exists).ToDictionary(p => p, p => Hash(File.ReadAllBytes(p)));

var root = GameFileSystem.OpenGame(game);
var adjacent = OverhaulPackage.FindAdjacent(game);
Check($"adjacent package found case-insensitively ({adjacent})", adjacent is not null && Path.GetDirectoryName(adjacent)!.Equals(layout, StringComparison.OrdinalIgnoreCase));
var emptyGame = Path.Combine(scratch, "empty", "game"); Directory.CreateDirectory(emptyGame);
Check("missing package → none", OverhaulPackage.FindAdjacent(emptyGame) is null);

void Rejects(string label, Action<ZipArchive> build, string expect)
{
    var p = Path.Combine(scratch, label + ".mvpmod");
    using (var z = ZipFile.Open(p, ZipArchiveMode.Create)) build(z);
    try { OverhaulPackage.Validate(p, root); Check($"{label} rejected", false); }
    catch (InvalidDataException ex) { Check($"{label} rejected: {ex.Message}", ex.Message.Contains(expect, StringComparison.OrdinalIgnoreCase)); }
}
void Entry(ZipArchive z, string name, string text) { using var w = new StreamWriter(z.CreateEntry(name).Open()); w.Write(text); }
const string good = "{\"format\":1,\"layout\":1,\"id\":\"mvp27-overhaul\",\"name\":\"T\",\"version\":\"1.0.0\",\"game\":{\"xbeSha1\":\"2f57040bab2c8b2eb33ebc7aa93209ad30dd103f\"}}";
Rejects("wrong-id", z => { Entry(z, "mod.json", good.Replace("mvp27-overhaul", "other-mod")); Entry(z, "files/data/x.txt", "x"); }, "not the MVP 27 overhaul");
Rejects("no-manifest", z => Entry(z, "files/data/x.txt", "x"), "mod.json");
Rejects("bad-json", z => { Entry(z, "mod.json", "{nope"); Entry(z, "files/data/x.txt", "x"); }, "can't be read");
Rejects("unsafe-path", z => { Entry(z, "mod.json", good); Entry(z, "files/../evil.txt", "x"); }, "Unsafe");
Rejects("duplicate", z => { Entry(z, "mod.json", good); Entry(z, "files/data/A.txt", "x"); Entry(z, "files/data/a.txt", "y"); }, "Duplicate");
Rejects("wrong-game", z => { Entry(z, "mod.json", good.Replace("2f57040b", "00000000")); Entry(z, "files/data/x.txt", "x"); }, "different default.xbe");
var junk = Path.Combine(scratch, "junk.mvpmod"); File.WriteAllText(junk, "not a zip");
try { OverhaulPackage.Validate(junk, root); Check("malformed zip rejected", false); } catch (InvalidDataException ex) { Check("malformed zip rejected: " + ex.Message, true); }

var package = OverhaulPackage.Validate(adjacent!, root);
Check($"identity {package.Info.Id} {package.Info.Version} affectsSaves={package.Info.AffectsSaves}", package.Info.Id == "mvp27-overhaul" && package.Info.AffectsSaves);
var session = new EditSession(root, Path.Combine(scratch, "out"));
session.SetBaseLayer(BaseModLayer.Build(root, [adjacent!]));
var layer = session.BaseLayer!;
Check("dedup: the loaded package is recognised (same file)", OverhaulPackage.Loaded(session, adjacent) is not null);
var copy = Path.Combine(scratch, "copy", OverhaulPackage.FileName); Directory.CreateDirectory(Path.GetDirectoryName(copy)!); File.Copy(adjacent!, copy);
Check("dedup: another file with the same id is recognised", OverhaulPackage.Loaded(session, copy) is not null);
try { BaseModLayer.Build(root, [adjacent!, copy]); Check("same id twice in one base is refused", false); } catch (InvalidDataException) { Check("same id twice in one base is refused", true); }

var roster = session.Resolve("data/database/database.big/roster.bin")!;
var modRoster = package.Files["data/database/database_big/roster.bin"];
Check("override resolves to the overhaul", layer.Origin(roster) is not null && Hash(session.GetBytes(roster)) == Hash(modRoster));
Check("base version stays accessible", Hash(roster.ReadOriginal()) == Hash(File.ReadAllBytes(Path.Combine(game, "data/database/database_big/roster.bin"))));
var teams = session.Resolve("data/league/teams.json");
Check("mod-added file appears", teams is not null && layer.Origin(teams) is not null && !File.Exists(teams.DiskPath));
var logos = session.Resolve("data/frontend/logos.big")!;
var added = logos.Children.FirstOrDefault(c => c.IsLooseTexturePack && !Directory.Exists(c.DiskPath));
Check($"mod-added texture pack ({added?.Name}) builds", added is not null && layer.GetBytes(added)?.Length > 0);
var overridden = logos.Children.FirstOrDefault(c => c.IsLooseTexturePack && Directory.Exists(c.DiskPath) && layer.Origin(c) is not null);
Check($"overridden texture pack ({overridden?.Name}) differs from base", overridden is not null && Hash(layer.GetBytes(overridden)!) != Hash(overridden.ReadOriginal()));
var catalog = TeamAssetCatalog.Build(root.EnumerateDeep().ToList(), session.GetBytes(roster));
Check($"team catalog reads the overhaul roster ({catalog.Count} schools)", catalog.Count >= 309);

var original = Payload(adjacent!);
var same = Path.Combine(scratch, "roundtrip.mvpmod");
var r0 = OverhaulPackage.Rebuild(session, adjacent!, same, null, false);
var rebuilt = Payload(same);
Check($"no-edit roundtrip: {r0.Entries} entries, same names and content hashes", r0.Ok && original.Count == rebuilt.Count && original.All(kv => rebuilt.GetValueOrDefault(kv.Key) == kv.Value));
Check("refuses to overwrite the loaded package without consent", !OverhaulPackage.Rebuild(session, adjacent!, adjacent!, null, false).Ok);
Check("refuses output inside the game folder", !OverhaulPackage.Rebuild(session, adjacent!, Path.Combine(game, "x.mvpmod"), null, false).Ok);

var csv = session.Resolve("data/league/school-logos.csv")!;
var edited = Encoding.Latin1.GetBytes(Encoding.Latin1.GetString(session.GetBytes(csv)) + "\r\n");
session.Commit(csv, edited, "harmless edit: trailing blank line");
var dbmisc = session.Resolve("data/database/dbmisc.big")!;
var baseOnly = dbmisc.Children.First(c => !c.IsFolder && layer.Origin(c) is null && c.Extension == ".csv");
var baseEdit = Encoding.Latin1.GetBytes(Encoding.Latin1.GetString(baseOnly.ReadAll()) + "\r\n");
session.Commit(baseOnly, baseEdit, "explicit override of a base file");
var project = Path.Combine(scratch, "overhaul-edit" + ProjectFile.Extension);
ProjectFile.Save(session, project);
var reloaded = ProjectFile.Load(project, root, out var warnings);
foreach (var w in warnings) Console.WriteLine("  project note: " + w);
Check($"project keeps the overhaul base and both edits ({warnings.Count} warnings)", OverhaulPackage.Loaded(reloaded, adjacent) is not null && reloaded.Overrides.Count + reloaded.Added.Count == 2 && warnings.All(layer.Warnings.Contains));
var output = Path.Combine(scratch, "MVP27-Overhaul-edited.mvpmod");
var r1 = OverhaulPackage.Rebuild(reloaded, adjacent!, output, null, false);
foreach (var p in r1.Problems) Console.WriteLine("  problem: " + p);
var after = Payload(output);
var diff = original.Keys.Union(after.Keys).Where(k => original.GetValueOrDefault(k) != after.GetValueOrDefault(k)).ToList();
var baseRel = Path.GetRelativePath(game, baseOnly.DiskPath!).Replace('\\', '/');
Check($"rebuild differs only by the two edits ({string.Join(", ", diff)})", r1.Ok && diff.Count == 2 && diff.Contains("files/data/league/school-logos.csv") && diff.Contains("files/" + baseRel));
Check("mod.json kept byte-for-byte", after["mod.json"] == original["mod.json"]);
var root2 = GameFileSystem.OpenGame(game);
var reopened = new EditSession(root2, Path.Combine(scratch, "out2"));
reopened.SetBaseLayer(BaseModLayer.Build(root2, [output]));
Check("reopened rebuild shows the edit", Hash(reopened.GetBytes(reopened.Resolve("data/league/school-logos.csv")!)) == Hash(edited));
var promoted = reopened.Resolve(baseOnly.Key)!;
Check("the base edit is now an overhaul override", reopened.BaseLayer!.Origin(promoted) is not null && Hash(reopened.GetBytes(promoted)) == Hash(baseEdit));
var versioned = Path.Combine(scratch, "versioned.mvpmod");
OverhaulPackage.Rebuild(reloaded, adjacent!, versioned, new ManifestEdit { Version = "0.1.0-preview.4" }, false);
var vInfo = MvpModImporter.ReadInfo(versioned);
using (var vz = ZipFile.OpenRead(versioned)) using (var oz = ZipFile.OpenRead(adjacent!))
{
    string Json(ZipArchive z) { using var r = new StreamReader(z.GetEntry("mod.json")!.Open()); return r.ReadToEnd(); }
    var a = System.Text.Json.Nodes.JsonNode.Parse(Json(oz))!.AsObject(); var b = System.Text.Json.Nodes.JsonNode.Parse(Json(vz))!.AsObject();
    Check("edited version written; every other mod.json field kept", vInfo.Version == "0.1.0-preview.4" && a.Where(kv => kv.Key != "version").All(kv => System.Text.Json.Nodes.JsonNode.DeepEquals(kv.Value, b[kv.Key])) && a.Count == b.Count);
}
Check("pre-release version accepted", new ModInfo { Id = "x", Name = "x", Version = "0.1.0-preview.3" }.Problem() is null && MvpModImporter.NextVersion("0.1.0-preview.3") == "0.1.0-preview.4");
Check("game files untouched", guard.All(kv => Hash(File.ReadAllBytes(kv.Key)) == kv.Value));
Check("source package untouched", OverhaulPackage.Sha256(adjacent!) == OverhaulPackage.Sha256(copy));
Console.WriteLine($"{checks - failures}/{checks} checks passed");
return failures == 0 ? 0 : 1;
