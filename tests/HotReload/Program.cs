using System.Text.Json;
using MVPWorkshop.Core.Editing;
using MVPWorkshop.Core.Vfs;

// Disposable protocol peer for the live WPF test; it never loads a real game.
if (args.Contains("--mod"))
{
    var at = Array.IndexOf(args, "--workshop-reload");
    if (at < 0 || at + 1 >= args.Length) return 2;
    var request = args[at + 1]; var directory = Path.GetDirectoryName(request)!;
    File.WriteAllText(Path.Combine(directory, "launch-args.json"), JsonSerializer.Serialize(args));
    var until = DateTime.UtcNow.AddSeconds(120); string? last = null;
    while (DateTime.UtcNow < until && !File.Exists(Path.Combine(directory, "stop")))
    {
        try
        {
            using var json = JsonDocument.Parse(File.ReadAllText(request));
            var revision = json.RootElement.GetProperty("revision").GetString();
            if (revision != last)
            {
                var mod = Path.Combine(directory, json.RootElement.GetProperty("mod").GetString()!);
                if (!File.Exists(Path.Combine(mod, "mod.json"))) return 3;
                var temporary = Path.Combine(directory, "ack.tmp");
                File.WriteAllText(temporary, JsonSerializer.Serialize(new { format = 1, revision, status = "applied", messages = new[] { "synthetic protocol peer" } }));
                File.Move(temporary, Path.Combine(directory, "reload.ack.json"), true); last = revision;
            }
        }
        catch (IOException) { }
        await Task.Delay(25);
    }
    return 0;
}

var folder = Path.Combine(Path.GetTempPath(), "workshop-reload-" + Guid.NewGuid().ToString("N"));
int failures = 0;
void Check(string label, bool value) { Console.WriteLine($"{(value ? "PASS" : "FAIL")} {label}"); if (!value) failures++; }
try
{
    var game = Path.Combine(folder, "game"); Directory.CreateDirectory(Path.Combine(game, "sample_big"));
    File.WriteAllBytes(Path.Combine(game, "default.xbe"), [0, 1, 2, 3]); File.WriteAllText(Path.Combine(game, "sample_big", "_big.json"), "{}");
    File.WriteAllText(Path.Combine(game, "sample_big", "a.txt"), "original");
    var root = GameFileSystem.OpenGame(game); var session = new EditSession(root, Path.Combine(folder, "out")); var node = root.Resolve("sample.big/a.txt")!;
    var info = new ModInfo { Id = "workshop-test", Name = "Workshop test" }; var directory = Path.Combine(folder, "published");
    session.Commit(node, [1, 2, 3], "first"); var first = TestModPublisher.Publish(session.Snapshot(), info, directory);
    Check("complete generation is published", first.Report.Ok && File.Exists(Path.Combine(first.Folder, "mod.json")) && File.Exists(first.RequestPath));
    using (var request = JsonDocument.Parse(File.ReadAllText(first.RequestPath)))
        Check("request identifies protocol and relative immutable generation", request.RootElement.GetProperty("format").GetInt32() == 1 && request.RootElement.GetProperty("revision").GetString() == first.Revision && Path.GetFullPath(Path.Combine(directory, request.RootElement.GetProperty("mod").GetString()!)) == first.Folder);
    var requestBytes = File.ReadAllBytes(first.RequestPath); var firstBytes = File.ReadAllBytes(Path.Combine(first.Folder, "files", "sample_big", "a.txt"));
    session.AddFile("bad.big", [1]); var rejected = TestModPublisher.Publish(session.Snapshot(), info, directory);
    Check("invalid export leaves active generation pointer unchanged", !rejected.Report.Ok && requestBytes.SequenceEqual(File.ReadAllBytes(first.RequestPath)));
    session.RemoveAdded("bad.big"); session.Commit(node, [4, 5, 6], "second"); var second = TestModPublisher.Publish(session.Snapshot(), info, directory);
    Check("new export never rewrites a generation being read", first.Folder != second.Folder && firstBytes.SequenceEqual(File.ReadAllBytes(Path.Combine(first.Folder, "files", "sample_big", "a.txt"))) && File.ReadAllBytes(Path.Combine(second.Folder, "files", "sample_big", "a.txt")).SequenceEqual(new byte[] { 4, 5, 6 }));
    var ack = Path.Combine(directory, "reload.ack.json");
    File.WriteAllText(ack, JsonSerializer.Serialize(new { format = 1, revision = first.Revision, status = "applied" }));
    Check("stale acknowledgement cannot confirm newer edits", TestModPublisher.ReadAcknowledgement(second.RequestPath, second.Revision) is null);
    foreach (var status in new[] { "applied", "restart-required", "rejected" })
    {
        File.WriteAllText(ack, JsonSerializer.Serialize(new { format = 1, revision = second.Revision, status, messages = new[] { "runtime detail" } }));
        var response = TestModPublisher.ReadAcknowledgement(second.RequestPath, second.Revision);
        Check("runtime status " + status + " is retained", response?.Status == status && response.Messages.Single() == "runtime detail");
    }
    session.Revert(node); var empty = TestModPublisher.Publish(session.Snapshot(), info, directory);
    Check("revert-all publishes an empty replacement layer", empty.Report.Ok && empty.Report.Files.Count == 0 && File.Exists(Path.Combine(empty.Folder, "mod.json")));
    Check("ordinary empty mod export remains rejected", !MvpModExporter.Export(session, info, Path.Combine(folder, "empty.mvpmod"), false).Ok);
    var exe = Path.Combine(folder, "MVP06.exe");
    Check("legacy ports do not silently opt into reload", !TestModPublisher.Supported(exe));
    File.WriteAllText(Path.Combine(folder, "port-manifest.json"), "{\"workshopReload\":1}"); Check("protocol 1 capability is explicit", TestModPublisher.Supported(exe));
    File.WriteAllText(Path.Combine(folder, "port-manifest.json"), "{\"workshopReload\":2}"); Check("unknown newer protocol is not assumed compatible", !TestModPublisher.Supported(exe));
    Check("game source remains unchanged", File.ReadAllText(Path.Combine(game, "sample_big", "a.txt")) == "original");
}
catch (Exception ex) { Console.WriteLine("FAIL " + ex); failures++; }
finally { AssetNode.Overlay = null; AssetNode.ChildrenOverlay = null; if (Directory.Exists(folder)) Directory.Delete(folder, true); }
return failures == 0 ? 0 : 1;
