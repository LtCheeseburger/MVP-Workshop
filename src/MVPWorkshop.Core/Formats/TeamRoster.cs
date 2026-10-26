using System.Text;
using MVPWorkshop.Core.IO;

namespace MVPWorkshop.Core.Formats;

public sealed record GameTeam(int RosterIndex, int Offset, string ShortName, string Name, string Abbreviation, string City, string Nickname,
    int LogoId, int UniformId, int ThirdAssetId, int PlayerCount, int StadiumId);

/// <summary>PS2 roster.bin: team count followed by variable records (487 bytes + 8 bytes per roster slot).</summary>
public static class TeamRoster
{
    public static List<GameTeam> Read(byte[] data)
    {
        if (data.Length < 4) throw new InvalidDataException("Truncated roster header.");
        var count = (int)Bin.U32LE(data, 0);
        if (count is < 1 or > 1024) throw new InvalidDataException("Invalid team count.");
        var teams = new List<GameTeam>();
        int offset = 4;
        for (int i = 0; i < count; i++)
        {
            if (offset + 487 > data.Length) throw new InvalidDataException($"Truncated team record {i}.");
            var players = (int)Bin.U32LE(data, offset + 192);
            if (players is < 0 or > 100) throw new InvalidDataException($"Invalid roster size for team {i}.");
            var size = checked(487 + players * 8);
            if (offset + size > data.Length) throw new InvalidDataException($"Truncated player slots for team {i}.");
            string Text(int at, int length) => Encoding.Latin1.GetString(data, offset + at, length).Split('\0')[0].Trim();
            var assets = Bin.U32LE(data, offset + 164);
            if (Text(8, 48).Length > 0)
                teams.Add(new GameTeam(i, offset, Text(0, 8), Text(8, 48), Text(56, 16), Text(72, 40), Text(112, 32),
                    (int)((assets >> 1) & 511), (int)((assets >> 10) & 511), (int)((assets >> 19) & 511), players,
                    (int)((Bin.U32LE(data, offset + 168) >> 4) & 63)));
            offset += size;
        }
        if (offset != data.Length) throw new InvalidDataException($"Unexpected trailing roster bytes ({data.Length - offset}).");
        return teams;
    }
}
