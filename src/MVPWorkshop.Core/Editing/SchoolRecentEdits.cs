using MVPWorkshop.Core.Formats;
using MVPWorkshop.Core.Vfs;

namespace MVPWorkshop.Core.Editing;

public static class SchoolRecentEdits
{
    /// <summary>Match confirmed assets and the school's actual roster/player records, rather than every edit to shared databases.</summary>
    public static IReadOnlyList<Change> ForSchool(TeamProfile profile, EditSession session)
    {
        bool Matches(Change change)
        {
            if (change.Key == profile.Roster.Key && change.Kind == ChangeKind.Saved)
            {
                var original = profile.Roster.ReadOriginal();
                var before = change.Before ?? original; var after = change.After ?? original;
                int offset = profile.Team.Offset, length = 487 + profile.Team.PlayerCount * 8;
                return offset + length > before.Length || offset + length > after.Length || !before.AsSpan(offset, length).SequenceEqual(after.AsSpan(offset, length));
            }
            var playerSources = profile.Players.SelectMany(p => p.Sources).Where(s => s.Asset.Key == change.Key).ToList();
            if (playerSources.Count > 0 && change.Kind == ChangeKind.Saved)
            {
                try
                {
                    var original = playerSources[0].Asset.ReadOriginal();
                    var before = PlayerAttributes.Read(change.Before ?? original);
                    var after = PlayerAttributes.Read(change.After ?? original);
                    return playerSources.Any(s =>
                    {
                        var a = before.Records.GetValueOrDefault(s.Record.Id); var b = after.Records.GetValueOrDefault(s.Record.Id);
                        return a is null || b is null || a.Fields.Count != b.Fields.Count || a.Fields.Any(f => b.Fields.GetValueOrDefault(f.Key) != f.Value);
                    });
                }
                catch { return true; }
            }
            return profile.Assets.Any(a => change.Key == a.Asset.Key || change.Key.StartsWith(a.Asset.Key + "/", StringComparison.OrdinalIgnoreCase) || a.Asset.Key.StartsWith(change.Key + "/", StringComparison.OrdinalIgnoreCase));
        }
        return session.History.Where(Matches).OrderByDescending(c => c.Id).ToList();
    }
}
