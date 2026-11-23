using System.Text;
using MVPWorkshop.Core.IO;

namespace MVPWorkshop.Core.Formats;

public sealed record SpeechEventName(int Index, int RecordOffset, int NameOffset, string Name, uint RawValue, byte[] Unknown);

/// <summary>EA speech EVT directory. Script bytecode and the last 28 bytes of each record remain undecoded.</summary>
public sealed class SpeechEvents
{
    public const uint Signature = 0x073C1303;
    public int TableOffset { get; private init; }
    public int NamesOffset { get; private init; }
    public uint Project { get; private init; }
    public uint RawHeaderValue { get; private init; }
    public List<SpeechEventName> Events { get; } = [];
    public static SpeechEvents Read(byte[] data)
    {
        if (data.Length < 20 || Bin.U32LE(data, 0) != Signature) throw new InvalidDataException("Not an EA speech EVT directory.");
        uint table = Bin.U32LE(data, 4), count = Bin.U32LE(data, 16);
        if (table < 20 || table > data.Length || count > 65536 || count > (data.Length - table) / 32) throw new InvalidDataException("EVT record table exceeds the source.");
        int names = checked((int)(table + count * 32));
        var result = new SpeechEvents { TableOffset = (int)table, NamesOffset = names, Project = Bin.U32LE(data, 8), RawHeaderValue = Bin.U32LE(data, 12) };
        for (int i = 0; i < count; i++)
        {
            int record = (int)table + i * 32; uint relative = Bin.U32LE(data, record);
            if (relative >= data.Length - names) throw new InvalidDataException("EVT name offset exceeds the string table.");
            int start = names + (int)relative, end = Array.IndexOf(data, (byte)0, start);
            if (end < 0 || end - start > 4096 || data.AsSpan(start, end - start).ContainsAnyExceptInRange((byte)32, (byte)126)) throw new InvalidDataException("Invalid EVT name string.");
            result.Events.Add(new(i, record, start, Encoding.ASCII.GetString(data, start, end - start), Bin.U32LE(data, record + 4), data.AsSpan(record + 8, 24).ToArray()));
        }
        return result;
    }
}
