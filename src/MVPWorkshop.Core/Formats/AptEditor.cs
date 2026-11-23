using System.Buffers.Binary;
using System.Numerics;
using System.Text;
using MVPWorkshop.Core.IO;

namespace MVPWorkshop.Core.Formats;

/// <summary>Values for an EditText field; null members are left as they are.</summary>
public sealed record EditTextChange
{
    public Vector4? Bounds { get; init; }
    public int? FontId { get; init; }
    public int? Align { get; init; }
    public uint? Color { get; init; }
    public float? FontHeight { get; init; }
    public bool? ReadOnly { get; init; }
    public bool? Multiline { get; init; }
    public bool? WordWrap { get; init; }
    public string? Text { get; init; }
    public string? Variable { get; init; }
}

/// <summary>Values for a PlaceObject display-list item; null members are left as they are.</summary>
public sealed record PlacementChange
{
    public float? X { get; init; }
    public float? Y { get; init; }
    public float? ScaleX { get; init; }
    public float? ScaleY { get; init; }
    public float? SkewX { get; init; }
    public float? SkewY { get; init; }
    /// <summary>Colour multiplier as the file stores it (0xAABBGGRR order as shown by the viewer).</summary>
    public uint? Tint { get; init; }
    public uint? Additive { get; init; }
    public string? Name { get; init; }
    public int? Depth { get; init; }
    public int? Character { get; init; }
    public int? ClipDepth { get; init; }
}

/// <summary>
/// In-place edits of APT movies (.apt) and constant files (.const). APT data is a block of structs whose pointers are file offsets, so
/// numeric fields are patched where they sit and nothing else moves. A new string overwrites the old one only when it fits and nothing else
/// points at it; otherwise it is appended to the end of the file and just that one pointer is redirected, so shared strings stay intact.
/// Each edit returns a new array; the input is never modified.
/// </summary>
public static class AptEditor
{
    public static byte[] ButtonRecord(byte[] apt, AptCharacter character, int index, int? flags = null, int? target = null, int? depth = null, Matrix3x2? matrix = null, Vector4? tint = null)
    {
        if (character.Type != AptCharacterType.Button || index < 0 || index >= character.ButtonRecords.Count) throw new ArgumentException("Invalid button state record.");
        int table = checked((int)Bin.U32LE(apt, character.Offset + 48)), at = checked(table + index * 68);
        if (at < 0 || at > apt.Length - 68) throw new InvalidDataException("Truncated button state record.");
        var result = apt.ToArray();
        if (flags is { } f) { if (f < 0 || f > 15) throw new InvalidDataException("Button states use Up=1, Over=2, Down=4, Hit=8."); result[at] = (byte)f; }
        if (target is { } c) { if (c < 0) throw new InvalidDataException("Character must be nonnegative."); PutU(result, at + 4, (uint)c); }
        if (depth is { } d) { if (d < 0) throw new InvalidDataException("Depth must be nonnegative."); PutU(result, at + 8, (uint)d); }
        if (matrix is { } m)
        {
            var values = new[] { m.M11, m.M12, m.M21, m.M22, m.M31, m.M32 };
            for (int i = 0; i < values.Length; i++) PutF(result, at + 12 + i * 4, values[i]);
        }
        if (tint is { } color) { PutF(result, at + 36, color.X); PutF(result, at + 40, color.Y); PutF(result, at + 44, color.Z); PutF(result, at + 48, color.W); }
        return result;
    }
    public static byte[] EditText(byte[] apt, AptCharacter c, EditTextChange e, out string notes)
    {
        if (c.Type != AptCharacterType.EditText) throw new ArgumentException("Not an EditText character.");
        var d = (byte[])apt.Clone();
        var b = c.Offset + 8;
        if (b + 52 > d.Length) throw new InvalidDataException("EditText record is truncated.");
        var log = new List<string>();
        if (e.Bounds is { } r) { PutF(d, b, r.X); PutF(d, b + 4, r.Y); PutF(d, b + 8, r.Z); PutF(d, b + 12, r.W); }
        if (e.FontId is { } f) PutU(d, b + 16, (uint)f);
        if (e.Align is { } a) PutU(d, b + 20, (uint)a);
        if (e.Color is { } col) PutU(d, b + 24, col);
        if (e.FontHeight is { } h) PutF(d, b + 28, h);
        if (e.ReadOnly is { } ro) PutU(d, b + 32, ro ? 1u : 0);
        if (e.Multiline is { } ml) PutU(d, b + 36, ml ? 1u : 0);
        if (e.WordWrap is { } ww) PutU(d, b + 40, ww ? 1u : 0);
        if (e.Text is { } t) { d = SetString(d, b + 44, t, out var how); log.Add($"text {how}"); }
        if (e.Variable is { } v) { d = SetString(d, b + 48, v, out var how); log.Add($"variable {how}"); }
        notes = string.Join(", ", log);
        return d;
    }

    public static byte[] Placement(byte[] apt, AptFrameItem item, PlacementChange e)
    {
        if (item.Type != 3) throw new ArgumentException("Not a PlaceObject item.");
        var d = (byte[])apt.Clone();
        var o = item.Offset;
        if (o + 0x40 > d.Length) throw new InvalidDataException("PlaceObject record is truncated.");
        var flags = Bin.U32LE(d, o + 4);
        if (e.X is not null || e.Y is not null || e.ScaleX is not null || e.ScaleY is not null || e.SkewX is not null || e.SkewY is not null)
        {
            // when the matrix flag was clear its fields are unused: start from identity so the unspecified parts stay neutral
            var m = item.Matrix;
            PutF(d, o + 0x10, e.ScaleX ?? m.M11); PutF(d, o + 0x14, e.SkewX ?? m.M12); PutF(d, o + 0x18, e.SkewY ?? m.M21); PutF(d, o + 0x1C, e.ScaleY ?? m.M22);
            PutF(d, o + 0x20, e.X ?? m.M31); PutF(d, o + 0x24, e.Y ?? m.M32);
            flags |= 4;
        }
        if (e.Tint is { } tint) { PutU(d, o + 0x28, tint); if ((flags & 8) == 0) PutU(d, o + 0x2C, 0); flags |= 8; }
        if (e.Additive is { } additive) { if ((flags & 8) == 0) PutU(d, o + 0x28, uint.MaxValue); PutU(d, o + 0x2C, additive); flags |= 8; }
        if (e.Depth is { } depth) PutU(d, o + 8, (uint)depth);
        if (e.Character is { } ch) { PutU(d, o + 12, (uint)ch); flags |= 2; }
        if (e.ClipDepth is { } clip) { PutU(d, o + 0x38, (uint)clip); flags |= 64; }
        PutU(d, o + 4, flags);
        if (e.Name is { } name) { d = SetString(d, o + 0x34, name, out _); PutU(d, o + 4, flags | 32); }
        return d;
    }

    /// <summary>Stage size and frame time of the movie character.</summary>
    public static byte[] Stage(byte[] apt, AptFile file, int? width, int? height, int? frameTime)
    {
        var d = (byte[])apt.Clone();
        var m = file.Movie.Offset;
        if (width is { } w) PutU(d, m + 0x1C, (uint)w);
        if (height is { } h) PutU(d, m + 0x20, (uint)h);
        if (frameTime is { } t) PutU(d, m + 0x24, (uint)t);
        return d;
    }

    /// <summary>Change a string constant (type 1) in a .const file.</summary>
    public static byte[] ConstantString(byte[] constFile, AptConstant item, string text, out string how)
    {
        if (item.Type != 1) throw new ArgumentException("Not a string constant.");
        var items = (int)Bin.U32LE(constFile, 0x1C);
        return SetString(constFile, items + item.Index * 8 + 4, text, out how);
    }

    /// <summary>Change the numeric value of an integer/float/boolean constant.</summary>
    public static byte[] ConstantValue(byte[] constFile, AptConstant item, uint raw)
    {
        if (item.Type is 1 or 8) throw new ArgumentException("Use ConstantString for strings.");
        var d = (byte[])constFile.Clone();
        PutU(d, (int)Bin.U32LE(d, 0x1C) + item.Index * 8 + 4, raw);
        return d;
    }

    /// <summary>
    /// Point the 32-bit pointer at <paramref name="field"/> to <paramref name="text"/> (Latin-1, NUL-terminated).
    /// Reuses the old string's space only if the text fits and the old string is referenced exactly once and begins its own string.
    /// </summary>
    public static byte[] SetString(byte[] d, int field, string text, out string how)
    {
        if (text.Contains('\0')) throw new InvalidDataException("UI strings cannot contain a NUL character.");
        var bytes = Encoding.Latin1.GetBytes(text);
        if (Encoding.Latin1.GetString(bytes) != text) throw new InvalidDataException("Only Latin-1 characters can be stored in this file's strings.");
        var payload = bytes.Append((byte)0).ToArray();
        var old = (int)Bin.U32LE(d, field);
        var result = (byte[])d.Clone();

        if (old > 0 && old < d.Length)
        {
            var oldLen = 0; while (old + oldLen < d.Length && d[old + oldLen] != 0) oldLen++;
            oldLen++;   // terminator
            var ownString = d[old - 1] == 0;   // a non-zero byte before it means it is the tail of a longer string or part of a struct
            if (payload.Length <= oldLen && ownString && PointerCount(d, old) <= 1)
            {
                payload.CopyTo(result, old);
                for (var i = payload.Length; i < oldLen; i++) result[old + i] = 0;
                how = "rewritten in place";
                return result;
            }
        }

        var at = (d.Length + 3) & ~3;
        Array.Resize(ref result, at + payload.Length);
        payload.CopyTo(result, at);
        // keep the file a multiple of 4 so later appends stay aligned
        if (result.Length % 4 != 0) Array.Resize(ref result, (result.Length + 3) & ~3);
        BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(field), (uint)at);
        how = $"appended at 0x{at:X} (old string left untouched)";
        return result;
    }

    private static int PointerCount(byte[] d, int target)
    {
        var n = 0;
        for (var i = 0; i + 4 <= d.Length; i += 4) if (BinaryPrimitives.ReadUInt32LittleEndian(d.AsSpan(i)) == (uint)target) n++;
        return n;
    }

    private static void PutU(byte[] d, int o, uint v) => BinaryPrimitives.WriteUInt32LittleEndian(d.AsSpan(o), v);
    private static void PutF(byte[] d, int o, float v)
    {
        if (!float.IsFinite(v)) throw new InvalidDataException("UI values must be finite numbers.");
        BinaryPrimitives.WriteSingleLittleEndian(d.AsSpan(o), v);
    }
}
