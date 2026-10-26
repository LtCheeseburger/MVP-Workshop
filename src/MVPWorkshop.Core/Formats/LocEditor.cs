using System.Buffers.Binary;
using System.Text;
using MVPWorkshop.Core.IO;

namespace MVPWorkshop.Core.Formats;

/// <summary>
/// Rewrites strings of a LOCH table (<c>FEENG.LOC</c> etc.). The header and the LOCI id table are kept byte for byte; the LOCL string area is
/// rebuilt in index order with fresh offsets (unedited strings keep their exact original UTF-16 bytes), and anything after the LOCL chunk is appended unchanged.
/// </summary>
public static class LocEditor
{
    public static byte[] Write(byte[] original, IReadOnlyDictionary<int, string> edits)
    {
        if (edits.Count == 0) return original;
        if (Bin.FourCC(original) != "LOCH") throw new InvalidDataException("Not a LOCH table.");
        int locl = -1, lochEnd = 0;
        for (var o = 0; o + 8 <= original.Length;)
        {
            var size = (int)Bin.U32LE(original, o + 4);
            if (Bin.FourCC(original, o) == "LOCL") { locl = o; lochEnd = size; break; }
            if (size < 8) break;
            o += size;
        }
        if (locl < 0) throw new InvalidDataException("LOCL chunk missing.");
        var count = (int)Bin.U32LE(original, locl + 12);
        var tableEnd = locl + 16 + count * 4;
        var chunkEnd = locl + (lochEnd >= 16 + count * 4 ? lochEnd : original.Length - locl);
        chunkEnd = Math.Min(chunkEnd, original.Length);

        var pieces = new List<byte[]>(count);
        for (var i = 0; i < count; i++)
        {
            if (edits.TryGetValue(i, out var text)) { pieces.Add(Encoding.Unicode.GetBytes(text + "\0")); continue; }
            var so = locl + (int)Bin.U32LE(original, locl + 16 + i * 4);
            var end = so;
            while (end + 1 < original.Length && (original[end] | original[end + 1]) != 0) end += 2;
            pieces.Add(original.AsSpan(so, Math.Min(end + 2, original.Length) - so).ToArray());
        }
        foreach (var k in edits.Keys) if (k < 0 || k >= count) throw new ArgumentOutOfRangeException(nameof(edits), $"String index {k} does not exist.");

        using var ms = new MemoryStream();
        ms.Write(original, 0, tableEnd);                           // everything up to the string area, offsets patched below
        var offsets = new int[count];
        for (var i = 0; i < count; i++) { offsets[i] = (int)ms.Length - locl; ms.Write(pieces[i]); }
        while (ms.Length % 4 != 0) ms.WriteByte(0);
        var chunkLen = (int)ms.Length - locl;
        if (chunkEnd < original.Length) ms.Write(original, chunkEnd, original.Length - chunkEnd);
        var result = ms.ToArray();
        for (var i = 0; i < count; i++) BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(locl + 16 + i * 4), (uint)offsets[i]);
        BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(locl + 4), (uint)chunkLen);
        return result;
    }
}

/// <summary>Reads and writes plain-text assets without disturbing their encoding: BOM, line endings and Latin-1/UTF-8 choice are preserved.</summary>
public sealed class TextCodec
{
    public required Encoding Encoding { get; init; }
    public bool Bom { get; init; }
    public string Name => Bom ? Encoding.WebName + " (BOM)" : Encoding.WebName;

    public static (string Text, TextCodec Codec) Decode(byte[] d)
    {
        if (d.Length >= 3 && d[0] == 0xEF && d[1] == 0xBB && d[2] == 0xBF) return (Encoding.UTF8.GetString(d, 3, d.Length - 3), new TextCodec { Encoding = new UTF8Encoding(false), Bom = true });
        if (d.Length >= 2 && d[0] == 0xFF && d[1] == 0xFE) return (Encoding.Unicode.GetString(d, 2, d.Length - 2), new TextCodec { Encoding = new UnicodeEncoding(false, false), Bom = true });
        try { return (new UTF8Encoding(false, true).GetString(d), new TextCodec { Encoding = new UTF8Encoding(false) }); }
        catch (DecoderFallbackException) { return (Encoding.Latin1.GetString(d), new TextCodec { Encoding = Encoding.Latin1 }); }
    }

    public byte[] Encode(string text)
    {
        var body = Encoding.GetBytes(text);
        if (Encoding.Equals(Encoding.Latin1) && Encoding.Latin1.GetString(body) != text) throw new InvalidDataException("The text contains characters this file's Latin-1 encoding cannot store.");
        if (!Bom) return body;
        var bom = Encoding is UnicodeEncoding ? new byte[] { 0xFF, 0xFE } : [0xEF, 0xBB, 0xBF];
        return [.. bom, .. body];
    }
}
