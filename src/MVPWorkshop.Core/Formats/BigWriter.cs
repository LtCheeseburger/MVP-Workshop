using System.Buffers.Binary;

namespace MVPWorkshop.Core.Formats;

/// <summary>
/// Rebuilds an EA BIGF archive from the original bytes plus replacement entry payloads. Everything that is not an edited entry is
/// carried over verbatim: the whole header (including bytes after the directory), the order of entries, the padding between them,
/// and any trailing data. Only entry offsets/sizes and the declared archive size are patched. With no replacements the output is
/// byte-identical to the input.
/// </summary>
public static class BigWriter
{
    private sealed record Placed(uint Offset, uint Size, uint NewOffset, bool Changed);

    /// <param name="original">Complete bytes of the archive being rebuilt.</param>
    /// <param name="replacements">Entry index → new stored bytes (already RefPack-compressed when the entry should be).</param>
    public static byte[] Write(byte[] original, BigArchive big, IReadOnlyDictionary<int, byte[]> replacements)
    {
        if (replacements.Count == 0) return original;
        var entries = big.Entries;
        var header = (int)big.HeaderSize;
        if (header < 16 || header > original.Length) throw new InvalidDataException("BIG header size is outside the archive.");

        // entries sharing an identical byte range share one blob
        var blobs = entries.GroupBy(e => (e.Offset, e.Size)).Select(g => (Key: g.Key, Members: g.ToList())).OrderBy(b => b.Key.Offset).ThenBy(b => b.Key.Size).ToList();
        var fill = FillByte(original, blobs.Select(b => b.Key).ToList(), header);
        var align = InferAlignment(entries);

        var output = new MemoryStream(original.Length + 4096);
        output.Write(original, 0, header);
        var newLocation = new Dictionary<(uint, uint), (uint Offset, uint Size)>();
        var placed = new List<Placed>();
        long origCursor = header;
        var shifted = false;   // once an earlier blob changed size, padding is recomputed instead of copied

        foreach (var (key, members) in blobs)
        {
            var (off, size) = key;
            var edited = members.Where(m => replacements.ContainsKey(m.Index)).ToList();
            if (edited.Count > 0 && edited.Count < members.Count)
                throw new NotSupportedException($"Entries '{edited[0].Name}' and '{members.First(m => !replacements.ContainsKey(m.Index)).Name}' share the same data but only one is edited.");
            var replacement = edited.Count > 0 ? replacements[edited[0].Index] : null;

            // empty placeholder entries (size 0 at offset 0, e.g. folder markers) occupy no space; their directory values stay as they are
            if (size == 0 && off < header && (replacement is null || replacement.Length == 0)) { newLocation[key] = (off, 0); continue; }

            if (off < origCursor)
            {
                // lies inside an earlier blob: it keeps its relative position, which is only possible while that blob is intact
                var host = placed.LastOrDefault(p => p.Offset <= off && off + size <= p.Offset + p.Size);
                if (replacement is not null || host is null || host.Changed)
                    throw new NotSupportedException($"Entry '{members[0].Name}' overlaps another entry; overlapping entries cannot be re-laid-out after an edit.");
                newLocation[key] = (host.NewOffset + (off - host.Offset), size);
                continue;
            }

            var gapLen = (int)(off - origCursor);
            if (!shifted) output.Write(original, (int)origCursor, gapLen);
            else
            {
                // same slack beyond the minimal alignment padding as the original had, re-aligned for the new position
                var origMin = align <= 1 ? 0 : (int)((align - origCursor % align) % align);
                var extra = Math.Max(0, gapLen - origMin);
                var pos = output.Length + extra;
                var pad = align <= 1 ? 0 : (int)((align - pos % align) % align);
                for (var i = 0; i < extra + pad; i++) output.WriteByte(i < gapLen ? original[origCursor + i] : fill);
            }

            var newOff = (uint)output.Length;
            var payload = replacement ?? original.AsSpan((int)off, (int)size).ToArray();
            output.Write(payload, 0, payload.Length);
            if (payload.Length != size) shifted = true;
            newLocation[key] = (newOff, (uint)payload.Length);
            placed.Add(new Placed(off, size, newOff, replacement is not null));
            origCursor = off + size;
        }

        // bytes after the last entry (footer, alignment slack)
        if (origCursor < original.Length)
        {
            var tailLen = original.Length - (int)origCursor;
            if (!shifted) output.Write(original, (int)origCursor, tailLen);
            else
            {
                var origPad = align <= 1 ? 0 : (int)((align - origCursor % align) % align);
                if (tailLen == origPad)   // pure alignment slack: recompute for the new end
                {
                    var pad = align <= 1 ? 0 : (int)((align - output.Length % align) % align);
                    for (var i = 0; i < pad; i++) output.WriteByte(original[origCursor + Math.Min(i, tailLen - 1)]);
                }
                else output.Write(original, (int)origCursor, tailLen);
            }
        }

        var result = output.ToArray();
        foreach (var e in entries)
        {
            var (o, s) = newLocation[(e.Offset, e.Size)];
            BinaryPrimitives.WriteUInt32BigEndian(result.AsSpan(e.DirPos), o);
            BinaryPrimitives.WriteUInt32BigEndian(result.AsSpan(e.DirPos + 4), s);
        }
        BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(4), (uint)result.Length);
        return result;
    }

    /// <summary>Largest power of two (≤ 4096) that every entry offset is a multiple of; archives pad their entries to this.</summary>
    public static int InferAlignment(IReadOnlyList<BigEntry> entries)
    {
        foreach (var a in new[] { 4096, 2048, 1024, 512, 256, 128, 64, 32, 16, 8, 4, 2 })
            if (entries.All(e => e.Offset % a == 0)) return a;
        return 1;
    }

    private static byte FillByte(byte[] d, List<(uint Offset, uint Size)> blobs, int header)
    {
        long cursor = header;
        var counts = new int[256];
        foreach (var (o, s) in blobs)
        {
            for (var i = cursor; i < o && i < d.Length; i++) counts[d[i]]++;
            cursor = Math.Max(cursor, o + (long)s);
        }
        var best = 0;
        for (var i = 1; i < 256; i++) if (counts[i] > counts[best]) best = i;
        return (byte)best;
    }
}
