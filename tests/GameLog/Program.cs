using MVPWorkshop.Core.Editing;
using MVPWorkshop.Core.Vfs;

var folder = Path.Combine(Path.GetTempPath(), "mvp-game-log-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(Path.Combine(folder, "game", "sample_big"));
Directory.CreateDirectory(Path.Combine(folder, "logs"));
int failures = 0;
void Check(string label, bool value) { Console.WriteLine($"{(value ? "PASS" : "FAIL")} {label}"); if (!value) failures++; }
try
{
    File.WriteAllText(Path.Combine(folder, "game", "sample_big", "_big.json"), "{}");
    File.WriteAllText(Path.Combine(folder, "game", "sample_big", "a.txt"), "fixture");
    var root = GameFileSystem.OpenGame(Path.Combine(folder, "game"));
    var assets = new Dictionary<string, AssetNode>();
    Check("launch failures are visible even at release log level", GameLogReader.Parse("[1.0] workshop launch: failed: Requested school was not found", root, assets)?.Severity == DiagnosticSeverity.Error);
    Check("launch cancellation is a nonblocking runtime warning", GameLogReader.Parse("[1.0] workshop launch: cancelled: User input", root, assets)?.Severity == DiagnosticSeverity.Warning);
    Check("reload refusal is visible from the initial launch log", GameLogReader.Parse("[1.0] mods: Workshop reload restart-required: Loaded database", root, assets)?.Severity == DiagnosticSeverity.Warning);
    var old = Path.Combine(folder, "logs", "mvp06_001.log");
    File.WriteAllText(old, "[error] mods: stale error\n"); File.SetLastWriteTimeUtc(old, DateTime.UtcNow.AddMinutes(-5));
    var started = DateTime.UtcNow.AddSeconds(-1);
    var reader = new GameLogReader(Path.Combine(folder, "logs"), root, started);
    var current = Path.Combine(folder, "logs", "mvp06_002.log");
    File.WriteAllText(current, "[0.01] [error] [t1234] mods: sample_big/a.txt is damaged\n[0.02] [warn] audio: unrelated\n[0.03] mods: none on\n");
    var rows = reader.ReadNew();
    Check("only new mod-related errors are included", rows.Count == 1 && rows[0].Severity == DiagnosticSeverity.Error && rows[0].Origin == "Game log");
    Check("loose path links to the asset key", rows[0].AssetKey == "sample.big/a.txt");
    Check("incremental polling does not replay lines", reader.ReadNew().Count == 0);
    File.AppendAllText(current, "[0.04] [warn] mods: sample_big/a.txt is packed, skipped");
    Check("partial lines wait for completion", reader.ReadNew().Count == 0);
    File.AppendAllText(current, "\n");
    Check("completed warnings are published", reader.ReadNew().Single().Severity == DiagnosticSeverity.Warning);
    File.AppendAllText(current, "[0.05] mods: sample_big/a.txt from newer (over older)\n");
    Check("release-format load-order conflicts are warnings", reader.ReadNew().Single().Message.Contains("over older"));
    File.WriteAllText(current, "error: textures: sample_big/a.txt has no readable xsh.meta\n");
    Check("truncated release logs reset the cursor", reader.ReadNew().Single().Severity == DiagnosticSeverity.Error);
    var release = Path.Combine(folder, "logs", "mvp06.log");
    File.WriteAllText(release, "[0.0] error: mods: failed to load\n");
    Check("release log filename is supported", reader.ReadNew().Single().Message.Contains("failed to load"));
    var documents = Path.Combine(folder, "Documents");
    var exe = Path.Combine(folder, "MVP06.exe");
    Check("normal log path follows Documents even before first launch", GameLogReader.LogDirectory(exe, documents) == Path.Combine(documents, "MVP06 Recomp", "logs"));
    File.WriteAllText(Path.Combine(folder, "portable.txt"), "");
    Check("portable log path is beside the executable", GameLogReader.LogDirectory(exe, documents) == Path.Combine(folder, "logs"));
}
catch (Exception ex) { Console.WriteLine("FAIL " + ex); failures++; }
finally { Directory.Delete(folder, true); }
return failures == 0 ? 0 : 1;
