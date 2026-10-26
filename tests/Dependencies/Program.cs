using MVPWorkshop.Core.Vfs;
using MVPWorkshop.Core.Editing;

static void Check(bool ok, string message) { if (!ok) throw new Exception(message); Console.WriteLine("PASS " + message); }
var fixture = Path.Combine(Path.GetTempPath(), "MVPWorkshop-dependencies-" + Guid.NewGuid());
Directory.CreateDirectory(fixture);
try
{
    File.WriteAllText(Path.Combine(fixture, "menu.fel"), "SF:CHILD,child.fel\nSF:MISSING,absent.fel\nSF:REMOTE,remote.fel\nLS:art\nTB:label,,,,,,,,,,,,,face.sfn,42\n");
    File.WriteAllText(Path.Combine(fixture, "child.fel"), "SF:CYCLE,menu.fel\n");
    File.WriteAllText(Path.Combine(fixture, "art.ssh"), "not a real texture");
    File.WriteAllText(Path.Combine(fixture, "face.sfn"), "font fixture");
    Directory.CreateDirectory(Path.Combine(fixture, "other"));
    File.WriteAllText(Path.Combine(fixture, "other", "remote.fel"), "SC:remote");
    File.WriteAllText(Path.Combine(fixture, "other", "child.fel"), "SC:duplicate");
    var entries = new[] { ("screen.fel", "SF:DUP,duplicate.fel\n"), ("duplicate.fel", "SC:first"), ("duplicate.fel", "SC:second") };
    var headerSize = 16 + entries.Sum(e => 9 + e.Item1.Length);
    var archive = new byte[headerSize + entries.Sum(e => e.Item2.Length)];
    System.Text.Encoding.ASCII.GetBytes("BIGF").CopyTo(archive, 0);
    System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(archive.AsSpan(4), (uint)archive.Length);
    System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(archive.AsSpan(8), (uint)entries.Length);
    System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(archive.AsSpan(12), (uint)headerSize);
    int directory = 16, payload = headerSize;
    foreach (var (name, text) in entries)
    {
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(archive.AsSpan(directory), (uint)payload);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(archive.AsSpan(directory + 4), (uint)text.Length);
        System.Text.Encoding.ASCII.GetBytes(name).CopyTo(archive, directory + 8);
        System.Text.Encoding.ASCII.GetBytes(text).CopyTo(archive, payload);
        directory += 9 + name.Length; payload += text.Length;
    }
    File.WriteAllBytes(Path.Combine(fixture, "duplicates.big"), archive);
    var root = GameFileSystem.OpenFolder(fixture);
    var index = await DependencyIndex.BuildAsync(root);
    var menu = index.Subjects.Single(s => s.Source.Name == "menu.fel");
    var child = index.Subjects.Single(s => s.Source.Name == "child.fel" && s.Source.Parent == root);
    Check(index.DependsOn(menu.Id).Any(e => e.To == child && e.Kind == RelationshipKind.ExplicitReference), "FEL include resolves in source directory");
    Check(!index.DependsOn(menu.Id).Any(e => e.To?.Source.Parent?.Name == "other" && e.Kind != RelationshipKind.HeuristicCandidate), "same filename elsewhere is never proof");
    Check(index.DependsOn(menu.Id).Any(e => e.Target == "absent.fel" && e.Missing), "missing explicit include detected");
    Check(!index.DependsOn(menu.Id).Any(e => e.Target == "remote.fel" && e.Missing), "unknown remote search scope is not falsely missing");
    var remote = index.Subjects.Single(s => s.Source.Name == "remote.fel");
    Check(index.Impact(remote.Id).Count == 0, "heuristic filename candidates are excluded from impact");
    Check(index.DependsOn(menu.Id).Any(e => e.To?.Source.Name == "face.sfn" && e.Kind == RelationshipKind.ExplicitReference), "typed FEL font field establishes reference");
    Check(index.DependsOn(menu.Id).Any(e => e.To?.Source.Name == "art.ssh" && e.Kind == RelationshipKind.ExplicitReference), "typed FEL pack declaration establishes reference");
    Check(index.UsedBy(child.Id).Any(e => e.From == menu), "reverse lookup retains evidence");
    Check(index.Impact(child.Id).Count == 1, "transitive impact terminates on cycles and excludes self");
    Check(index.DependsOn(menu.Id).All(e => e.SourceLocation.Length > 0 && e.Evidence.Length > 0), "every relationship has evidence and location");
    var screen = index.Subjects.Single(s => s.Source.Name == "screen.fel");
    Check(index.Subjects.Count(s => s.Source.Name == "duplicate.fel") == 2, "duplicate BIG entry identities are retained");
    Check(!index.DependsOn(screen.Id).Any(e => e.To != null && e.Kind == RelationshipKind.ExplicitReference), "ambiguous duplicate BIG names never resolve to first entry");
    var session = new EditSession(root, fixture + "-output");
    session.Commit(menu.Source, System.Text.Encoding.ASCII.GetBytes("SF:NEW,new.fel\n"), "Change include");
    var edited = await DependencyIndex.BuildAsync(root);
    Check(!edited.DependsOn(menu.Id).Any(e => e.To?.Id == child.Id), "rebuild removes stale outgoing relationships");
    Check(!edited.UsedBy(child.Id).Any(e => e.From.Id == menu.Id), "rebuild removes stale reverse relationships");
    session.Undo();
    Check((await DependencyIndex.BuildAsync(root)).UsedBy(child.Id).Any(e => e.From.Id == menu.Id), "undo restores dependency evidence");
    using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
    try { await DependencyIndex.BuildAsync(root, cancellation.Token); throw new Exception("Cancellation ignored"); }
    catch (OperationCanceledException) { Console.WriteLine("PASS cancelled build publishes no snapshot"); }
    using var activeCancellation = new CancellationTokenSource();
    try
    {
        await DependencyIndex.BuildAsync(root, activeCancellation.Token, new CancelProgress(activeCancellation));
        throw new Exception("Active cancellation ignored");
    }
    catch (OperationCanceledException) { Console.WriteLine("PASS cancellation during build discards incomplete relationships"); }
    AssetNode.Overlay = null;
}

finally { Directory.Delete(fixture, true); }

if (args.Length > 0)
{
    var game = GameFileSystem.OpenFolder(Path.GetFullPath(args[0]));
    var last = DateTime.UtcNow;
    var progress = new Progress<string>(s => { if ((DateTime.UtcNow - last).TotalSeconds > 10) { Console.WriteLine(s); last = DateTime.UtcNow; } });
    var index = await DependencyIndex.BuildAsync(game, progress: progress);
    var lsu = index.Subjects.Single(s => s.Id.StartsWith("team:") && s.Label.Contains("LSU"));
    Check(index.DependsOn(lsu.Id).Any(e => e.Target.Contains("u032", StringComparison.OrdinalIgnoreCase) && e.Kind == RelationshipKind.DecodedAssignment), "known LSU uniform family 032");
    Check(index.DependsOn(lsu.Id).Any(e => e.Target.Contains("ABOXDAY", StringComparison.OrdinalIgnoreCase) && e.Kind == RelationshipKind.DecodedAssignment), "known LSU Alex Box stadium mapping");
    Check(index.Subjects.Any(s => s.Id.StartsWith("player:")), "decoded player roster entities");
    var rosterSubject = index.Subjects.Single(s => s.Source.Name.Equals("roster.bin", StringComparison.OrdinalIgnoreCase) && s.Id.StartsWith("asset:"));
    Check(index.Impact(rosterSubject.Id).Any(s => s.Id == lsu.Id), "replacing roster impacts decoded team subjects");
    Check(index.Relationships.Any(e => e.Kind == RelationshipKind.ExplicitReference && e.Evidence.StartsWith("APT import")), "real APT import references");
    Check(index.Relationships.Where(e => e.Evidence.Contains("SSH tag")).All(e => e.Kind == RelationshipKind.HeuristicCandidate), "texture tag matches remain candidates");
    Check(index.Relationships.Any(e => e.Evidence.Contains("SSH tag")), "real model tag candidates carry relocation evidence");
    Console.WriteLine($"Game: {index.Subjects.Count} subjects, {index.Relationships.Count} edges, {index.Problems.Count} diagnostics");
    foreach (var problem in index.Problems.Take(10)) Console.WriteLine(problem);
}

sealed class CancelProgress(CancellationTokenSource source) : IProgress<string>
{
    public void Report(string value) => source.Cancel();
}
