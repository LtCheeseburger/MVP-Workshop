using MVPWorkshop.Core.Editing;
using MVPWorkshop.Core.Formats;
using MVPWorkshop.Core.Vfs;

static class TeamAssignmentChecks
{
    public static int Run(AssetNode root, string output)
    {
        int checks = 0;
        void Check(bool ok, string text) { if (!ok) throw new InvalidDataException(text); checks++; Console.WriteLine("PASS " + text); }
        var nodes = root.EnumerateDeep().ToList();
        var profiles = TeamAssetCatalog.Build(nodes);
        var first = profiles.First();
        var roster = first.Roster;
        var original = roster.ReadOriginal();
        var teams = TeamRoster.Read(original);
        foreach (var profile in profiles)
        {
            Check(profile.Players.Count == profile.Team.PlayerCount && profile.Players.All(p => p.Sources.Any(s => s.Asset.Name == "attrib.dat")), profile.Team.Abbreviation + ": all roster slots join to attrib.dat by key");
            Check(profile.Players.SelectMany(p => p.Sources.Select(s => (p, s))).All(pair => pair.p.Id == pair.s.Record.Id), profile.Team.Abbreviation + ": player source keys agree");
        }
        var lsu = profiles.Single(p => p.Team.Abbreviation == "LSU");
        Check(lsu.Players.Count == 25 && lsu.Players[0].Id == 0x0B8F1FD6 && lsu.Players[0].Sources.Single(s => s.Asset.Name == "attrib.dat").Record.Field(0) == "LSU", "LSU exact first slot → attrib.dat field evidence");
        Check(lsu.SharedAssets.Any(a => a.Category.StartsWith("Portraits")) && lsu.SharedAssets.Any(a => a.Category.StartsWith("Equipment")) && lsu.SharedAssets.Any(a => a.Category.StartsWith("Audio")) && lsu.SharedAssets.All(a => a.Status.Contains("unresolved")), "shared sources never assert player/school ownership");
        Check(profiles.Any(p => p.RegisteredVenue is not null && p.StadiumEvidence.Contains("also links")), "authentic venue differs from roster assignment for validation schools");
        foreach (var t in teams)
        {
            foreach (var ids in new[] { (0, 0, 0), (511, 511, 33) })
            {
                var bytes = TeamAssignmentEditor.Patch(original, t.RosterIndex, ids.Item1, ids.Item2, ids.Item3);
                TeamAssignmentEditor.Validate(original, bytes, t.RosterIndex, ids.Item1, ids.Item2, ids.Item3);
            }
        }
        Check(true, $"{teams.Count} variable records: boundary IDs reopen; unknown bytes, third asset and packed bits preserved");
        bool rejected = false;
        try { TeamAssignmentEditor.Patch(original, first.Team.RosterIndex, 512, 0, 0); } catch (ArgumentOutOfRangeException) { rejected = true; }
        Check(rejected, "out-of-range family rejected");
        rejected = false;
        try { TeamAssignmentEditor.Patch(original, first.Team.RosterIndex, 0, 0, 34); } catch (ArgumentOutOfRangeException) { rejected = true; }
        Check(rejected, "unknown stadium enum rejected");
        var donor = profiles.First(p => p.Team.UniformId != first.Team.UniformId && p.Team.LogoId != first.Team.LogoId && p.Team.StadiumId != first.Team.StadiumId && p.Assets.Any(a => a.Category == "Uniforms/Player textures") && p.Assets.Any(a => a.Category == "Logos"));
        var want = donor.Team;
        var edited = TeamAssignmentEditor.Patch(original, first.Team.RosterIndex, want.LogoId, want.UniformId, want.StadiumId);
        var session = new EditSession(root, output);
        session.Commit(roster, edited, "Assignment round trip check");
        var browser = TeamAssetCatalog.Build(nodes).Single(p => p.Team.RosterIndex == first.Team.RosterIndex);
        Check(browser.Team.UniformId == want.UniformId && browser.Team.LogoId == want.LogoId && browser.Team.StadiumId == want.StadiumId, "team browser decodes edited IDs");
        Check(browser.Assets.Where(a => a.Category.StartsWith("Uniforms") || a.Category == "Logos").Select(a => a.Asset.Key).Order().SequenceEqual(donor.Assets.Where(a => a.Category.StartsWith("Uniforms") || a.Category == "Logos").Select(a => a.Asset.Key).Order()), "team browser resolves selected families");
        Check(browser.StadiumCode == donor.StadiumCode, "team browser resolves selected stadium enum");
        session.Undo(); Check(roster.ReadAll().AsSpan().SequenceEqual(original), "undo restores original bytes");
        session.Redo(); Check(roster.ReadAll().AsSpan().SequenceEqual(edited), "redo restores reviewed assignments");
        Directory.CreateDirectory(output);
        var project = Path.Combine(output, "assignments.mvpwproj");
        ProjectFile.Save(session, project);
        var loaded = ProjectFile.Load(project, root, out var warnings);
        Check(warnings.Count == 0 && loaded.GetBytes(roster).AsSpan().SequenceEqual(edited), "project reopens with exact assignment bytes");
        var exported = ModExporter.Export(loaded, output, verify: true);
        Check(exported.Ok, "export reopens archives and verifies all logical entries: " + string.Join("; ", exported.Problems));
        Check(exported.Files.Count == 1, "only roster container exported; executable registry untouched");
        var exportedRoot = GameFileSystem.OpenFolder(output);
        var exportedRoster = exportedRoot.Resolve(roster.Key) ?? throw new InvalidDataException("Exported roster missing.");
        var actual = exportedRoster.ReadAll();
        TeamAssignmentEditor.Validate(original, actual, first.Team.RosterIndex, want.LogoId, want.UniformId, want.StadiumId);
        Check(actual.AsSpan().SequenceEqual(edited), "exported roster reopens with only intended assignment fields changed");
        Console.WriteLine($"{checks} checks passed. Output: {output}");
        return 0;
    }
}
