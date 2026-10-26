using MVPWorkshop.Core.IO;

namespace MVPWorkshop.Core.Formats.Eagl;

/// <summary>One decoded PS2 VIF command.</summary>
public sealed record VifCommand(int Offset, int Cmd, int Num, int Imm, int PayloadOffset, int PayloadSize)
{
    public bool IsUnpack => Cmd >= 0x60;
    public int Vn => (Cmd >> 2) & 3;
    public int Vl => Cmd & 3;
    public int Format => Cmd & 0x0F;
    public int Count => Num == 0 ? 256 : Num;
    public int Address => Imm & 0x3FF;
    public bool Unsigned => (Imm & 0x4000) != 0;
    public bool TopsRelative => (Imm & 0x8000) != 0;
    public bool Masked => (Cmd & 0x10) != 0;
    public bool IsKick => Cmd is 0x14 or 0x15 or 0x17;

    public static string FormatName(int fmt) => fmt switch
    {
        0x0 => "S-32", 0x1 => "S-16", 0x2 => "S-8", 0x4 => "V2-32", 0x5 => "V2-16", 0x6 => "V2-8",
        0x8 => "V3-32", 0x9 => "V3-16", 0xA => "V3-8", 0xC => "V4-32", 0xD => "V4-16", 0xE => "V4-8", 0xF => "V4-5", _ => $"?{fmt:X}"
    };

    public override string ToString() => IsUnpack
        ? $"{Offset:X6}  UNPACK {FormatName(Format),-6} n={Count,-3} addr={Address,-4}{(Unsigned ? " usn" : "")}{(TopsRelative ? " tops" : "")}{(Masked ? " mask" : "")}  ({PayloadSize} bytes)"
        : $"{Offset:X6}  {Name(Cmd)}{(Imm != 0 || Num != 0 ? $" imm=0x{Imm:X} num={Num}" : "")}";

    public static string Name(int cmd) => cmd switch
    {
        0x00 => "NOP", 0x01 => "STCYCL", 0x02 => "OFFSET", 0x03 => "BASE", 0x04 => "ITOP", 0x05 => "STMOD", 0x06 => "MSKPATH3",
        0x07 => "MARK", 0x10 => "FLUSHE", 0x11 => "FLUSH", 0x13 => "FLUSHA", 0x14 => "MSCAL", 0x15 => "MSCALF", 0x17 => "MSCNT",
        0x20 => "STMASK", 0x30 => "STROW", 0x31 => "STCOL", 0x4A => "MPG", 0x50 => "DIRECT", 0x51 => "DIRECTHL", _ => $"?{cmd:X2}"
    };
}

public static class Vif
{
    public static int UnpackSize(int cmd, int count)
    {
        int vn = (cmd >> 2) & 3, vl = cmd & 3;
        var bits = vl == 3 ? 16 : (32 >> vl) * (vn + 1);
        return (bits * count + 31) / 32 * 4;
    }

    public static IEnumerable<VifCommand> Parse(byte[] vif)
    {
        var p = 0;
        while (p + 4 <= vif.Length)
        {
            var at = p;
            var w = Bin.U32LE(vif, p); p += 4;
            var cmd = (int)(w >> 24) & 0x7F; var num = (int)(w >> 16) & 0xFF; var imm = (int)(w & 0xFFFF);
            var size = cmd switch
            {
                >= 0x60 => UnpackSize(cmd, num == 0 ? 256 : num),
                0x20 => 4,
                0x30 or 0x31 => 16,
                0x4A => (num == 0 ? 256 : num) * 8,
                0x50 or 0x51 => (imm == 0 ? 65536 : imm) * 16,
                _ => 0,
            };
            if (p + size > vif.Length) size = vif.Length - p;
            yield return new VifCommand(at, cmd, num, imm, p, size);
            p += size;
        }
    }
}
