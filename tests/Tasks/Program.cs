using System.Buffers.Binary;
using System.Text;
using MVPWorkshop.Core.Editing;
using MVPWorkshop.Core.Vfs;

var folder = Path.Combine(Path.GetTempPath(), "mvp-school-tasks-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(Path.Combine(folder, "database_big"));
int failures = 0;
void Check(string label, bool value) { Console.WriteLine($"{(value ? "PASS" : "FAIL")} {label}"); if (!value) failures++; }
try
{
    File.WriteAllText(Path.Combine(folder, "database_big", "_big.json"), "{}");
    File.WriteAllBytes(Path.Combine(folder, "default.xbe"), [0, 1, 2, 3]);
    var roster = new byte[4 + 495 * 2]; BinaryPrimitives.WriteInt32LittleEndian(roster, 2);
    for (int team = 0; team < 2; team++)
    {
        int at = 4 + team * 495;
        Encoding.ASCII.GetBytes("School " + team).CopyTo(roster, at + 8);
        Encoding.ASCII.GetBytes("T" + team).CopyTo(roster, at + 56);
        BinaryPrimitives.WriteInt32LittleEndian(roster.AsSpan(at + 192), 1);
        BinaryPrimitives.WriteInt32LittleEndian(roster.AsSpan(at + 196), team + 1);
    }
    File.WriteAllBytes(Path.Combine(folder, "database_big", "roster.bin"), roster);
    const string attributes = ",0 firstname,1 lastname,2 playerattrib_jerseynum\n00000001,0 Alpha,1 Player,2 1\n00000002,0 Beta,1 Player,2 2\n";
    File.WriteAllText(Path.Combine(folder, "database_big", "attrib.dat"), attributes);
    var root = GameFileSystem.OpenGame(folder);
    var session = new EditSession(root, folder + "-output");
    var schools = TeamAssetCatalog.Build(root.EnumerateDeep().ToList());
    Check("synthetic schools resolve their player records", schools.Count == 2 && schools.All(s => s.Players.Single().Sources.Count == 1));
    var playerData = root.Resolve("database.big/attrib.dat")!;
    session.Commit(playerData, Encoding.ASCII.GetBytes(attributes.Replace("2 1\n", "2 9\n")), "Change Alpha jersey");
    Check("school recent edits include its player's changed DAT record", SchoolRecentEdits.ForSchool(schools[0], session).Count == 1);
    Check("shared player database does not attribute another player's edit", SchoolRecentEdits.ForSchool(schools[1], session).Count == 0);
    var changedRoster = roster.ToArray(); changedRoster[4 + 112] = 65;
    session.Commit(schools[0].Roster, changedRoster, "Change first school nickname");
    Check("school-specific roster and player edits combine", SchoolRecentEdits.ForSchool(schools[0], session).Count == 2 && SchoolRecentEdits.ForSchool(schools[1], session).Count == 0);
    var secondRoster = changedRoster.ToArray(); secondRoster[4 + 495 + 112] = 66;
    session.Commit(schools[1].Roster, secondRoster, "Change second school nickname");
    Check("later change to another record stays with that school", SchoolRecentEdits.ForSchool(schools[0], session).Count == 2 && SchoolRecentEdits.ForSchool(schools[1], session).Count == 1);
    session.Undo();
    Check("undo removes the second school's recent edit", SchoolRecentEdits.ForSchool(schools[1], session).Count == 0);
}
catch (Exception ex) { Console.WriteLine("FAIL " + ex); failures++; }
finally { AssetNode.Overlay = null; Directory.Delete(folder, true); }
return failures == 0 ? 0 : 1;
