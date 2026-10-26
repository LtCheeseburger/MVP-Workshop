using System.Globalization;
using System.Text;

namespace MVPWorkshop.Core.Formats;

public sealed record PlayerAttributeRecord(uint Id, int Line, IReadOnlyDictionary<int, string> Fields)
{
    public string Field(int index) => Fields.GetValueOrDefault(index, "?");
}

/// <summary>Indexed DAT fields retain their source labels and values; IDs are hexadecimal, not row indices.</summary>
public sealed class PlayerAttributes
{
    public Dictionary<int, string> Labels { get; } = [];
    public Dictionary<uint, PlayerAttributeRecord> Records { get; } = [];

    public static PlayerAttributes Read(byte[] bytes)
    {
        var result = new PlayerAttributes();
        var lines = Encoding.Latin1.GetString(bytes).Split('\n');
        Dictionary<int, string> Fields(IEnumerable<string> cells)
        {
            var fields = new Dictionary<int, string>();
            foreach (var cell in cells)
            {
                var value = cell.Trim();
                int space = value.IndexOf(' ');
                if (space > 0 && int.TryParse(value[..space], out var index)) fields[index] = value[(space + 1)..];
            }
            return fields;
        }
        foreach (var field in Fields(lines[0].Split(','))) result.Labels.Add(field.Key, field.Value);
        for (int line = 1; line < lines.Length; line++)
        {
            var cells = lines[line].Trim().Split(',');
            if (cells.Length < 2 || !uint.TryParse(cells[0], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var id)) continue;
            if (!result.Records.TryAdd(id, new(id, line + 1, Fields(cells.Skip(1)))))
                throw new InvalidDataException($"Duplicate player key {id:X8} at line {line + 1}.");
        }
        return result;
    }
}
