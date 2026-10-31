using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using MVPWorkshop.Core.Editing;
using MVPWorkshop.Core.Vfs;

var folder = Path.Combine(Path.GetTempPath(), "mvp-roster-csv-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(Path.Combine(folder, "database_big"));
int failures = 0;
void Check(string label, bool value) { Console.WriteLine($"{(value ? "PASS" : "FAIL")} {label}"); if (!value) failures++; }
try
{
    File.WriteAllText(Path.Combine(folder, "database_big", "_big.json"), "{}"); File.WriteAllBytes(Path.Combine(folder, "default.xbe"), [0, 1, 2, 3]);
    var roster = new byte[994]; BinaryPrimitives.WriteInt32LittleEndian(roster, 2);
    for (int i = 0; i < 2; i++)
    {
        int at = 4 + i * 495; Encoding.ASCII.GetBytes("School " + i).CopyTo(roster, at + 8); Encoding.ASCII.GetBytes("T" + i).CopyTo(roster, at + 56);
        BinaryPrimitives.WriteInt32LittleEndian(roster.AsSpan(at + 192), 1); BinaryPrimitives.WriteInt32LittleEndian(roster.AsSpan(at + 196), i + 1);
    }
    File.WriteAllBytes(Path.Combine(folder, "database_big", "roster.bin"), roster);
    const string attributes = "0 first_name,1 last_name,2 playerattrib_jerseynum,5 playerattrib_primaryposition\r\n00000001,0 Alpha  ,1 Player,2 7,5 1,90 opaque payload,;\r\n00000002,0 Beta,1 Player,2 9,5 1,90 other opaque bytes,;\r\n# tail\r\n";
    File.WriteAllText(Path.Combine(folder, "database_big", "attrib.dat"), attributes);
    File.WriteAllText(Path.Combine(folder, "database_big", "pitcher.dat"), "2 pitchattrib_speed\n00000001,2 55\n00000002,2 56\n");
    var originals = Directory.GetFiles(folder, "*", SearchOption.AllDirectories).ToDictionary(p => p, p => SHA256.HashData(File.ReadAllBytes(p)));
    var root = GameFileSystem.OpenGame(folder); var session = new EditSession(root, folder + "-output");
    var schools = TeamAssetCatalog.Build(root.EnumerateDeep().ToList());
    schools[0].Team = schools[0].Team with { Name = "School, \"Zero\"" };
    var csv = RosterCsv.Export(schools);
    Check("CSV contains readable names and stable source identities", csv.Contains("Jersey number [2:playerattrib_jerseynum]") && csv.Contains("database.big/attrib.dat", StringComparison.OrdinalIgnoreCase));
    Check("hexadecimal player keys remain text in spreadsheet imports", csv.Contains(",0x00000001,"));
    Check("school names with commas and quotes are escaped", csv.Contains("\"T0 · School, \"\"Zero\"\"\""));
    var unchanged = RosterCsv.Plan("\uFEFF" + csv, schools);
    Check("all-school export/import is a no-op", unchanged.Problems.Count == 0 && unchanged.Changes.Count == 0 && unchanged.Apply(session) == 0 && !session.IsDirty);
    var schoolCsv = RosterCsv.Export([schools[0]]);
    Check("single-school export excludes other players", !schoolCsv.Contains("00000002") && schoolCsv.Contains("00000001"));
    var editedCsv = csv.Replace(",Alpha,", ",Edited,").Replace(",7,", ",42,");
    var edited = RosterCsv.Plan(editedCsv, schools);
    Check("preview lists changed fields with original values", edited.Problems.Count == 0 && edited.ChangedRows == 1 && edited.Changes.Count == 2 && edited.Changes.Any(c => c.Before == "7" && c.After == "42"));
    Check("CSV applies through the DAT span editor", edited.Apply(session) == 1 && Encoding.Latin1.GetString(root.Resolve("database.big/attrib.dat")!.ReadAll()) == attributes.Replace("Alpha", "Edited").Replace("2 7,", "2 42,"));
    Check("CSV export reads current project overrides", RosterCsv.Export(schools).Contains(",Edited,") && RosterCsv.Export(schools).Contains(",42,"));
    session.Undo(); Check("undo restores the exact original DAT", Encoding.Latin1.GetString(root.Resolve("database.big/attrib.dat")!.ReadAll()) == attributes);
    var invalid = RosterCsv.Plan(csv.Replace(",7,", ",100,"), schools);
    Check("out-of-range jersey numbers are rejected", invalid.Problems.Any(p => p.Contains("0 to 99")));
    bool blocked = false; try { invalid.Apply(session); } catch (InvalidDataException) { blocked = true; }
    Check("invalid CSV cannot partially apply", blocked && session.Overrides.Count == 0);
    Check("integer fields reject fractional input", RosterCsv.Plan(csv.Replace(",7,", ",1.5,"), schools).Problems.Count > 0);
    Check("unencodable player names are rejected", RosterCsv.Plan(csv.Replace(",Alpha,", ",😀,"), schools).Problems.Count > 0);
    Check("source identity cannot select an arbitrary target", RosterCsv.Plan(csv.Replace("attrib.dat", "other.dat"), schools).Problems.Any(p => p.Contains("Identity")));
    Check("school-scoped import rejects out-of-scope rows", RosterCsv.Plan(csv, [schools[0]]).Problems.Any(p => p.Contains("outside")));
    Check("renamed columns are rejected", RosterCsv.Plan(csv.Replace("Jersey number", "Unrecognized"), schools).Problems.Any(p => p.Contains("Unknown or renamed")));
    Check("malformed quoted CSV is rejected", RosterCsv.Plan("\"unclosed", schools).Problems.Count > 0);
    var duplicate = csv + csv.Split('\n')[1] + "\n";
    Check("duplicate source rows are rejected", RosterCsv.Plan(duplicate, schools).Problems.Any(p => p.Contains("Duplicate school")));
    var stale = RosterCsv.Plan(editedCsv, schools);
    var source = root.Resolve("database.big/attrib.dat")!; session.Commit(source, Encoding.Latin1.GetBytes(attributes.Replace("2 9,", "2 8,")), "later edit");
    blocked = false; try { stale.Apply(session); } catch (InvalidOperationException) { blocked = true; }
    Check("reviewed imports cannot overwrite a later edit", blocked && Encoding.Latin1.GetString(source.ReadAll()).Contains("2 8,"));
    session.Undo();
    var rosterReview = RosterCsv.Plan(editedCsv, schools);
    var changedRoster = roster.ToArray(); changedRoster[4 + 196] ^= 1;
    session.Commit(schools[0].Roster, changedRoster, "later roster identity edit");
    blocked = false; try { rosterReview.Apply(session); } catch (InvalidOperationException) { blocked = true; }
    Check("roster identity changes invalidate a reviewed import", blocked && session.Overrides.Count == 1);
    session.Undo();
    var secondPlayer = schools[1].Players[0];
    schools[1].Players[0] = schools[0].Players[0];
    var shared = RosterCsv.Export(schools); var atChange = shared.IndexOf(",Alpha,", StringComparison.Ordinal);
    shared = shared.Remove(atChange + 1, 5).Insert(atChange + 1, "Changed");
    Check("contradictory shared-player rows are rejected", RosterCsv.Plan(shared, schools).Problems.Any(p => p.Contains("Conflicting values")));
    schools[1].Players[0] = secondPlayer with { Sources = Array.Empty<PlayerSource>() };
    var unresolvedCsv = RosterCsv.Export(schools);
    Check("unresolved roster players are retained as read-only CSV rows", unresolvedCsv.Contains("00000002,(unresolved)") && RosterCsv.Plan(unresolvedCsv, schools).Problems.Count == 0);
    Check("source game files remain unchanged", originals.All(p => SHA256.HashData(File.ReadAllBytes(p.Key)).SequenceEqual(p.Value)));
}
catch (Exception ex) { Console.WriteLine("FAIL " + ex); failures++; }
finally { AssetNode.Overlay = null; Directory.Delete(folder, true); }
return failures == 0 ? 0 : 1;
