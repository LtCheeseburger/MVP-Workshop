using System.Buffers.Binary;
using System.Text;
using MVPWorkshop.Core.Editing;
using MVPWorkshop.Core.IO;
using MVPWorkshop.Core.Vfs;

var folder = Path.Combine(Path.GetTempPath(), "mvp-diagnostics-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(Path.Combine(folder, "sample_big"));
int failures = 0;
void Check(string label, bool value) { Console.WriteLine($"{(value ? "PASS" : "FAIL")} {label}"); if (!value) failures++; }
try
{
    File.WriteAllText(Path.Combine(folder, "sample_big", "_big.json"), "{}");
    File.WriteAllBytes(Path.Combine(folder, "default.xbe"), [1, 2, 3]);
    File.WriteAllText(Path.Combine(folder, "menu.fel"), "SF:CHILD,missing.fel\n");
    File.WriteAllBytes(Path.Combine(folder, "roster.bin"), [0]);
    var root = GameFileSystem.OpenGame(folder);
    var session = new EditSession(root, folder + "-output");
    var info = new ModInfo { Id = "diagnostic-test", Name = "Diagnostics" };
    var roster = root.Resolve("roster.bin")!;
    session.Commit(roster, [1], "roster change");
    session.CommitPreviewBytes(root.Resolve("menu.fel")!, Encoding.ASCII.GetBytes("SF:CHILD,missing.fel\n"), "preview", "test");
    var index = await DependencyIndex.BuildAsync(root);
    var snapshot = session.Snapshot();
    var rows = ModDiagnostics.Check(snapshot, info, index);
    Check("roster warning when AffectsSaves is off", rows.Any(r => r.Message.Contains("AffectsSaves") && r.AssetKey == roster.Key));
    Check("preview-only warning identifies source", rows.Any(r => r.Message.Contains("Preview-only") && r.AssetKey == "menu.fel"));
    Check("missing decoded references warned", rows.Any(r => r.Message.Contains("Missing loose reference: missing.fel")));
    info.AffectsSaves = true;
    Check("AffectsSaves clears roster warning", !ModDiagnostics.Check(snapshot, info).Any(r => r.Message.Contains("AffectsSaves")));
    session.AddFile("missing.fel", Encoding.ASCII.GetBytes("SC:child"));
    Check("Added file satisfies missing reference", !ModDiagnostics.Check(session.Snapshot(), info, index).Any(r => r.Message.Contains("Missing loose reference: missing.fel")));
    session.AddFile("packed.big", [1, 2]);
    Check("packed archives produce blocking errors", ModDiagnostics.Check(session.Snapshot(), info).Any(r => r.Severity == DiagnosticSeverity.Error && r.Message.Contains("packed archive")));
    session.RemoveAdded("packed.big");
    session.AddFile("broken.xsh", [0, 1, 2]);
    Check("malformed textures produce blocking errors", ModDiagnostics.Check(session.Snapshot(), info).Any(r => r.Severity == DiagnosticSeverity.Error && r.Asset == "broken.xsh"));
    session.RemoveAdded("broken.xsh");
    var dds = new byte[128]; "DDS "u8.CopyTo(dds); BinaryPrimitives.WriteInt32LittleEndian(dds.AsSpan(12), 8192); BinaryPrimitives.WriteInt32LittleEndian(dds.AsSpan(16), 8192);
    session.AddFile("large.dds", dds);
    Check("invalid DDS formats and sizes block", ModDiagnostics.Check(session.Snapshot(), info).Any(r => r.Severity == DiagnosticSeverity.Error && r.Asset == "large.dds"));
    session.RemoveAdded("large.dds");
    info.Id = "INVALID ID";
    Check("invalid mod metadata blocks", ModDiagnostics.Check(snapshot, info).Any(r => r.Severity == DiagnosticSeverity.Error && r.Asset == "Mod details"));
    info.Id = "diagnostic-test";
    var icon = Path.Combine(folder, "icon.png");
    File.WriteAllBytes(icon, PngWriter.Encode(64, 64, new byte[64 * 64 * 4]));
    info.IconPath = icon;
    Check("valid icon accepted", info.Problem() is null);
    var corrupt = File.ReadAllBytes(icon); corrupt[40] ^= 1; File.WriteAllBytes(icon, corrupt);
    Check("corrupt icon blocks", ModDiagnostics.Check(snapshot, info).Any(r => r.Severity == DiagnosticSeverity.Error && r.Asset == "Mod details"));
    File.WriteAllBytes(icon, corrupt[..24]);
    Check("header-only PNG rejected", ModInfo.IconProblem(icon) is not null);
    info.IconPath = "";
    var texture = new byte[48]; "SHPX"u8.CopyTo(texture);
    BinaryPrimitives.WriteInt32LittleEndian(texture.AsSpan(4), texture.Length); BinaryPrimitives.WriteInt32LittleEndian(texture.AsSpan(8), 1);
    "logo"u8.CopyTo(texture.AsSpan(16)); BinaryPrimitives.WriteInt32LittleEndian(texture.AsSpan(20), 24);
    BinaryPrimitives.WriteInt32LittleEndian(texture.AsSpan(24), (24 << 8) | 0x60); BinaryPrimitives.WriteUInt16LittleEndian(texture.AsSpan(28), 4); BinaryPrimitives.WriteUInt16LittleEndian(texture.AsSpan(30), 4);
    session.AddFile("valid.xsh", texture);
    Check("valid native DXT texture accepted", !ModDiagnostics.Check(session.Snapshot(), info).Any(r => r.Asset == "valid.xsh" && r.Severity == DiagnosticSeverity.Error));
    var badSize = texture.ToArray(); BinaryPrimitives.WriteUInt16LittleEndian(badSize.AsSpan(28), 3);
    session.AddFile("bad-size.xsh", badSize);
    Check("native DXT alignment errors block", ModDiagnostics.Check(session.Snapshot(), info).Any(r => r.Asset == "bad-size.xsh" && r.Message.Contains("multiples of four")));
    var shared = new DependencySubject("asset:" + roster.Key, "Shared asset", roster, "fixture");
    var schoolA = new DependencySubject("team:a", "School A", roster, "fixture");
    var schoolB = new DependencySubject("team:b", "School B", roster, "fixture");
    var unverified = new DependencySubject("team:c", "Unverified school", roster, "fixture");
    var relationships = new List<AssetRelationship>
    {
        new(schoolA, shared, roster.Key, RelationshipKind.DecodedAssignment, "fixture", "fixture"),
        new(schoolB, shared, roster.Key, RelationshipKind.DecodedAssignment, "fixture", "fixture"),
        new(unverified, shared, roster.Key, RelationshipKind.HeuristicCandidate, "fixture", "fixture")
    };
    var sharedIndex = (DependencyIndex)Activator.CreateInstance(typeof(DependencyIndex), System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance, null, new object[] { new List<DependencySubject> { shared, schoolA, schoolB, unverified }, relationships, new List<string>() }, null)!;
    Check("shared-school warning includes confirmed users only", ModDiagnostics.Check(snapshot, info, sharedIndex).Any(r => r.AssetKey == roster.Key && r.Message.Contains("all 2 schools") && !r.Message.Contains("Unverified school")));
    session.Commit(roster, [2], "later edit");
    Check("snapshot isolates edits and preserves viewer overlay", snapshot.Overrides[roster.Key].SequenceEqual(new byte[] { 1 }) && roster.ReadAll().SequenceEqual(new byte[] { 2 }));
    using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
    bool cancelled = false;
    try { ModDiagnostics.Check(snapshot, new ModInfo { Id = "ok", Name = "ok" }, index, cancellation.Token); } catch (OperationCanceledException) { cancelled = true; }
    Check("obsolete diagnostics can be cancelled", cancelled);
}
catch (Exception ex) { Console.WriteLine("FAIL " + ex); failures++; }
finally { AssetNode.Overlay = null; Directory.Delete(folder, true); }
return failures == 0 ? 0 : 1;
