using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json.Nodes;
using MVPWorkshop.Core.Editing;
using MVPWorkshop.Core.Vfs;

var folder = Path.Combine(Path.GetTempPath(), "mvp-installed-mods-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(Path.Combine(folder, "game", "sample_big"));
Directory.CreateDirectory(Path.Combine(folder, "mods", "folder-mod", "files", "sample_big"));
int failures = 0;
void Check(string label, bool value) { Console.WriteLine($"{(value ? "PASS" : "FAIL")} {label}"); if (!value) failures++; }
try
{
    var game = Path.Combine(folder, "game"); var mods = Path.Combine(folder, "mods");
    File.WriteAllText(Path.Combine(game, "sample_big", "_big.json"), "{}");
    File.WriteAllBytes(Path.Combine(game, "default.xbe"), [1, 2, 3]); File.WriteAllText(Path.Combine(game, "sample_big", "a.txt"), "original");
    var sha = Convert.ToHexString(SHA1.HashData([1, 2, 3]));
    var json = JsonNode.Parse(MvpModExporter.ModJson(new ModInfo { Id = "folder-mod", Name = "Folder mod" }, sha))!;
    json["remove"] = new JsonArray("hidden.txt");
    File.WriteAllText(Path.Combine(mods, "folder-mod", "mod.json"), json.ToJsonString());
    File.WriteAllText(Path.Combine(mods, "folder-mod", "files", "sample_big", "a.txt"), "other");
    Directory.CreateDirectory(Path.Combine(mods, "folder-mod", "files", "logo.xsh"));
    File.WriteAllText(Path.Combine(mods, "folder-mod", "files", "logo.xsh", "xsh.meta"), "layout");
    void Zip(string name, string id, string path, string hash)
    {
        using var zip = ZipFile.Open(Path.Combine(mods, name), ZipArchiveMode.Create);
        using (var writer = new StreamWriter(zip.CreateEntry("mod.json").Open())) writer.Write(MvpModExporter.ModJson(new ModInfo { Id = id, Name = id }, hash));
        using var file = new StreamWriter(zip.CreateEntry("files/" + path).Open()); file.Write("modded");
    }
    Zip("zip-mod.mvpmod", "zip-mod", "sample_big/a.txt", sha);
    Zip("unlisted.mvpmod", "unlisted", "other.txt", sha);
    Zip("incompatible.mvpmod", "incompatible", "other.txt", "bad-sha");
    var orderPath = Path.Combine(mods, "mods.json");
    File.WriteAllText(orderPath, "{\"format\":1,\"order\":[{\"id\":\"folder-mod\",\"enabled\":false},{\"id\":\"zip-mod\",\"enabled\":true},{\"id\":\"ghost\",\"enabled\":true}]}");
    var before = Directory.GetFiles(folder, "*", SearchOption.AllDirectories).ToDictionary(p => p, p => SHA256.HashData(File.ReadAllBytes(p)));
    var root = GameFileSystem.OpenGame(game);
    var catalog = InstalledModCatalog.Read(mods, root);
    Check("ZIP and folder mods are discovered", catalog.Mods.Count == 4);
    Check("launcher enable flags and order are preserved", catalog.Mods.Single(m => m.Id == "zip-mod") is { Order: 1, Enabled: true } && catalog.Mods.Single(m => m.Id == "folder-mod") is { Order: 0, Enabled: false });
    Check("unlisted mods are disabled", !catalog.Mods.Single(m => m.Id == "unlisted").Enabled);
    Check("missing launcher entries are reported", catalog.Problems.Any(p => p.Contains("ghost")));
    Check("XBE compatibility is reported", catalog.Mods.Single(m => m.Id == "incompatible").Problem == "XBE SHA-1 mismatch");
    var conflicts = catalog.Conflicts(["SAMPLE_BIG/A.TXT"], "my-project");
    Check("case-insensitive conflicts include enabled and disabled mods", conflicts.Count == 2 && conflicts.Any(c => c.Other.Enabled) && conflicts.Any(c => !c.Other.Enabled));
    Check("load-order notes describe later-wins and test priority", conflicts.All(c => c.Message.Contains("Later entries win") && c.Message.Contains("above the enabled list")));
    Check("updating the same mod id excludes itself", catalog.Conflicts(["sample_big/a.txt"], "zip-mod").Count == 1);
    Check("whole texture-folder replacements conflict with sibling DDS edits", catalog.Conflicts(["LOGO.XSH/02_new.dds"], "my-project").Any(c => c.Other.Id == "folder-mod"));
    Check("removed paths conflict with exports", catalog.Conflicts(["hidden.txt"], "my-project").Single().Removed);
    var session = new EditSession(root, Path.Combine(folder, "output")); session.Commit(root.Resolve("sample.big/a.txt")!, [9], "edit");
    var rows = ModDiagnostics.Check(session.Snapshot(), new ModInfo { Id = "my-project", Name = "My project" }, installed: catalog);
    Check("conflicts are nonblocking diagnostics with asset links", rows.Count(r => r.Origin == "Installed mod" && r.AssetKey == "sample.big/a.txt") == 2 && rows.All(r => r.Severity != DiagnosticSeverity.Error));
    Check("catalog does not write game or launcher files", before.All(p => SHA256.HashData(File.ReadAllBytes(p.Key)).SequenceEqual(p.Value)) && Directory.GetFiles(folder, "*", SearchOption.AllDirectories).Length == before.Count);
    File.WriteAllText(orderPath, "invalid JSON");
    var invalid = InstalledModCatalog.Read(mods, root);
    Check("unreadable order falls back to disabled with a warning", invalid.Mods.All(m => !m.Enabled) && invalid.Problems.Any(p => p.Contains("Cannot read launcher mod order")));
}
catch (Exception ex) { Console.WriteLine("FAIL " + ex); failures++; }
finally { AssetNode.Overlay = null; Directory.Delete(folder, true); }
return failures == 0 ? 0 : 1;
