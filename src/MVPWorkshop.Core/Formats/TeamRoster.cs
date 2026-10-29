using System.Text;
using MVPWorkshop.Core.IO;

namespace MVPWorkshop.Core.Formats;

public sealed record GameTeam(int RosterIndex, int Offset, string ShortName, string Name, string Abbreviation, string City, string Nickname,
    int LogoId, int UniformId, int ThirdAssetId, int PlayerCount, int StadiumId, int AssetShift = 1)
{
    /// <summary>"bits 1–9" for the logo, "bits 10–18" for the uniform, as they sit in the word at +164 on this platform.</summary>
    public string LogoBits => $"bits {AssetShift}–{AssetShift + 8}";
    public string UniformBits => $"bits {AssetShift + 9}–{AssetShift + 17}";
}

/// <summary>
/// roster.bin: team count followed by variable records (487 bytes + 8 bytes per roster slot). The word at +164 packs three
/// 9-bit asset families (logo, uniform, third) from bit 1 on PS2 and from bit 0 on Xbox (checked against all 128 Logo IDs
/// in the Xbox dbmisc.big/teaminfo.csv).
/// </summary>
public static class TeamRoster
{
    /// <summary>First bit of the three asset families in the word at +164.</summary>
    public static int AssetShift(bool xbox) => xbox ? 0 : 1;

    /// <summary>Whether a roster node belongs to the Xbox game (its root holds default.xbe).</summary>
    public static bool IsXbox(Vfs.AssetNode roster) => roster.Root.Children.Any(c => c.Name.Equals("default.xbe", StringComparison.OrdinalIgnoreCase));

    public static List<GameTeam> Read(byte[] data, bool xbox = false)
    {
        var s = AssetShift(xbox);
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
                    (int)((assets >> s) & 511), (int)((assets >> (s + 9)) & 511), (int)((assets >> (s + 18)) & 511), players,
                    (int)((Bin.U32LE(data, offset + 168) >> 4) & 63), s));
            offset += size;
        }
        if (offset != data.Length) throw new InvalidDataException($"Unexpected trailing roster bytes ({data.Length - offset}).");
        return teams;
    }
}
