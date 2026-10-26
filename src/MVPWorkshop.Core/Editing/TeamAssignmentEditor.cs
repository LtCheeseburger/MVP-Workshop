using System.Buffers.Binary;
using MVPWorkshop.Core.Formats;

namespace MVPWorkshop.Core.Editing;

/// <summary>Changes only decoded assignment bits; all remaining roster bytes are retained.</summary>
public static class TeamAssignmentEditor
{
    public static byte[] Patch(byte[] source, int rosterIndex, int logo, int uniform, int stadium)
    {
        if (logo is < 0 or > 511 || uniform is < 0 or > 511 || stadium is < 0 or > 33)
            throw new ArgumentOutOfRangeException(nameof(logo), "Expected 9-bit asset families and a decoded stadium enum (0–33).");
        var team = TeamRoster.Read(source).Single(t => t.RosterIndex == rosterIndex);
        var result = (byte[])source.Clone();
        void Set(int offset, uint mask, uint value)
        {
            var word = BinaryPrimitives.ReadUInt32LittleEndian(result.AsSpan(offset, 4));
            BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(offset, 4), (word & ~mask) | (value & mask));
        }
        const uint assetsMask = (511u << 1) | (511u << 10);
        Set(team.Offset + 164, assetsMask, ((uint)logo << 1) | ((uint)uniform << 10));
        Set(team.Offset + 168, 63u << 4, (uint)stadium << 4);
        Validate(source, result, rosterIndex, logo, uniform, stadium);
        return result;
    }

    public static void Validate(byte[] before, byte[] after, int index, int logo, int uniform, int stadium)
    {
        var team = TeamRoster.Read(before).Single(t => t.RosterIndex == index);
        var reopened = TeamRoster.Read(after).Single(t => t.RosterIndex == index);
        if (before.Length != after.Length || reopened.LogoId != logo || reopened.UniformId != uniform || reopened.StadiumId != stadium)
            throw new InvalidDataException("Assignment round trip failed.");
        for (int i = 0; i < before.Length; i++)
        {
            uint mask = i >= team.Offset + 164 && i < team.Offset + 168 ?
                (((511u << 1) | (511u << 10)) >> ((i - team.Offset - 164) * 8)) & 255 :
                i >= team.Offset + 168 && i < team.Offset + 172 ? ((63u << 4) >> ((i - team.Offset - 168) * 8)) & 255 : 0;
            if (((before[i] ^ after[i]) & ~mask) != 0) throw new InvalidDataException($"Unexpected roster change at 0x{i:X}.");
        }
    }
}
