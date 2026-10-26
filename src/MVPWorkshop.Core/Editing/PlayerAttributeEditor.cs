using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using MVPWorkshop.Core.Formats;

namespace MVPWorkshop.Core.Editing;

/// <summary>Splices DAT value spans only. Headers, keys, delimiters, unknown fields and line endings are opaque.</summary>
public static class PlayerAttributeEditor
{
    /// <summary>Rebase a draft over unrelated edits, rejecting changes to the same field or its definition.</summary>
    public static byte[] ApplyDraft(byte[] baseline, byte[] current, uint player, IReadOnlyDictionary<int, string> edits)
    {
        var before = PlayerAttributes.Read(baseline);
        var now = PlayerAttributes.Read(current);
        if (!before.Records.TryGetValue(player, out var old) || !now.Records.TryGetValue(player, out var latest))
            throw new InvalidDataException("Player record was removed. Reset this draft to reload the source.");
        foreach (var edit in edits)
            if (before.Labels.GetValueOrDefault(edit.Key) != now.Labels.GetValueOrDefault(edit.Key) || old.Field(edit.Key) != latest.Field(edit.Key))
                throw new InvalidDataException($"Field {edit.Key} changed elsewhere. Draft retained; reset it to reload the current values.");
        return Patch(current, player, edits);
    }

    public static byte[] Patch(byte[] source, uint player, IReadOnlyDictionary<int, string> edits)
    {
        var table = PlayerAttributes.Read(source);
        if (!table.Records.TryGetValue(player, out var record)) throw new InvalidDataException("Player key missing.");
        var text = Encoding.Latin1.GetString(source);
        // Locate the original physical line, preserving every byte outside the selected values.
        int start = 0;
        for (int line = 1; line < record.Line; line++) start = text.IndexOf('\n', start) + 1;
        int end = text.IndexOf('\n', start); if (end < 0) end = text.Length;
        var row = text[start..end];
        var changes = new List<(int Start, int Length, string Value)>();
        foreach (var edit in edits)
        {
            if (record.Fields.TryGetValue(edit.Key, out var existing) && existing == edit.Value) continue;
            if (!table.Labels.TryGetValue(edit.Key, out var label) || string.IsNullOrWhiteSpace(label) || !record.Fields.ContainsKey(edit.Key))
                throw new InvalidDataException($"Unknown field {edit.Key} is read-only.");
            var value = edit.Value;
            if (value.Length == 0 || value != value.Trim() || value.Any(c => c > 255 || char.IsControl(c) || c is ',' or ';'))
                throw new InvalidDataException("Values must be nonempty Latin-1 text without delimiters or control characters.");
            if (label is not ("first_name" or "last_name") && !decimal.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out _))
                throw new InvalidDataException($"{label} requires a raw number; enum meanings and ranges are unresolved.");
            if (label is not ("first_name" or "last_name") && int.TryParse(existing, NumberStyles.Integer, CultureInfo.InvariantCulture, out _) && !int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out _))
                throw new InvalidDataException($"{label} uses an integer in this record.");
            var matches = Regex.Matches(row, @"(?<=,)\s*" + edit.Key + @" +(?<value>[^,;\r\n]*?)(?=\s*[,;\r]|$)");
            if (matches.Count != 1) throw new InvalidDataException("Ambiguous or missing field span.");
            var span = matches[0].Groups["value"];
            if (record.Field(edit.Key) != value) changes.Add((start + span.Index, span.Length, value));
        }
        foreach (var change in changes.OrderByDescending(c => c.Start))
            text = text.Remove(change.Start, change.Length).Insert(change.Start, change.Value);
        var result = Encoding.Latin1.GetBytes(text);
        var reopened = PlayerAttributes.Read(result);
        if (!table.Labels.OrderBy(p => p.Key).SequenceEqual(reopened.Labels.OrderBy(p => p.Key)) || !table.Records.Keys.SequenceEqual(reopened.Records.Keys))
            throw new InvalidDataException("DAT relationships changed.");
        foreach (var old in table.Records.Values)
            foreach (var field in old.Fields)
                if (reopened.Records[old.Id].Field(field.Key) != (old.Id == player && edits.TryGetValue(field.Key, out var desired) ? desired : field.Value))
                    throw new InvalidDataException("DAT value round trip failed.");
        return result;
    }
}

