using System.Text;
using MVPWorkshop.Core.Install;

var folder = Path.Combine(Path.GetTempPath(), "mvp-port-compatibility-" + Guid.NewGuid().ToString("N"));
var game = Path.Combine(folder, "game"); Directory.CreateDirectory(game);
int failures = 0;
void Check(string label, bool value) { Console.WriteLine($"{(value ? "PASS" : "FAIL")} {label}"); if (!value) failures++; }
try
{
    var version = Path.Combine(folder, "version.txt"); var manifest = Path.Combine(folder, "port-manifest.json"); var installed = Path.Combine(game, "manifest.json");
    File.WriteAllText(version, "0.2.2-beta\r\n"); File.WriteAllText(installed, "{\"format\":1,\"files\":[]}");
    File.WriteAllText(manifest, "{\"format\":1,\"version\":\"0.2.2-beta\",\"modFormat\":1,\"layout\":1,\"files\":[]}", new UTF8Encoding(true));
    var matching = PortCompatibility.Read(folder, game);
    Check("matching release and layout metadata is accepted", matching.Version == "0.2.2-beta" && matching.ModFormat == 1 && matching.ExpectedLayout == 1 && matching.InstalledLayout == 1 && matching.Warnings.Count == 0);
    Check("Windows PowerShell UTF-8 BOM manifest is readable", !matching.Warnings.Any(w => w.Contains("Cannot read")));
    using (var writer = new StreamWriter(installed, false, new UTF8Encoding(true)))
    {
        writer.Write("{\"format\":1,\"files\":[");
        for (int i = 0; i < 180000; i++) { if (i > 0) writer.Write(','); writer.Write("{\"path\":\"" + new string('a', 100) + "\",\"size\":1}"); }
        writer.Write("]}");
    }
    var large = PortCompatibility.Read(folder, game);
    Check("large BOM-prefixed installed inventory is streamed without an oversized error", new FileInfo(installed).Length > 16 * 1024 * 1024 && large.InstalledLayout == 1 && !large.Warnings.Any(w => w.Contains("Cannot read")));
    File.WriteAllText(installed, "{\"format\":1}");
    File.WriteAllText(manifest, "{\"format\":1,\"version\":\"0.2.2-beta\",\"modFormat\":2,\"layout\":3}");
    var newer = PortCompatibility.Read(folder, game);
    Check("newer port formats request a Workshop update", newer.RequiresNewerWorkshop && newer.Warnings.Any(w => w.Contains("mod format 2")) && newer.Warnings.Any(w => w.Contains("layout 3")));
    Check("port/install layout mismatch is identified", newer.Warnings.Any(w => w.Contains("installed game advertises 1")));
    Check("summary states exact Workshop output capabilities", newer.Summary.Contains("Workshop writes mod format 1, layout 1"));
    File.WriteAllText(manifest, "{\"format\":99,\"version\":\"0.2.2-beta\",\"files\":[]}");
    var legacy = PortCompatibility.Read(folder, game);
    Check("packaging schema is not confused with mod format", legacy.ModFormat is null && legacy.ExpectedLayout is null && !legacy.RequiresNewerWorkshop);
    Check("legacy manifests explicitly leave compatibility unconfirmed", legacy.Warnings.Any(w => w.Contains("does not advertise")));
    File.WriteAllText(manifest, "{\"format\":1,\"version\":\"different\",\"modFormat\":1,\"layout\":1}");
    Check("mixed release version files are warned", PortCompatibility.Read(folder, game).Warnings.Any(w => w.Contains("disagree")));
    File.WriteAllText(installed, "{\"format\":2}");
    Check("newer installed loose layout requests an update", PortCompatibility.Read(folder, game).Warnings.Any(w => w.Contains("installed game uses loose layout 2")));
    File.WriteAllText(manifest, "not JSON");
    Check("unreadable compatibility metadata produces a warning", PortCompatibility.Read(folder, game).Warnings.Any(w => w.Contains("Cannot read port-manifest")));
    File.Delete(manifest); File.Delete(version); File.Delete(installed);
    var missing = PortCompatibility.Read(folder, game);
    Check("missing metadata stays unknown rather than inventing a version", missing.Version == "unknown" && missing.ExpectedLayout is null && missing.InstalledLayout is null);
    Check("update links target the actual Workshop and port releases", PortCompatibilityInfo.WorkshopUpdates.EndsWith("MVP-Workshop/releases/latest") && PortCompatibilityInfo.PortUpdates.EndsWith("MVP-College-Baseball/releases"));
}
catch (Exception ex) { Console.WriteLine("FAIL " + ex); failures++; }
finally { Directory.Delete(folder, true); }
return failures == 0 ? 0 : 1;
