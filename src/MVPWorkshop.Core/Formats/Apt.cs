using System.Text;
using System.Numerics;
using MVPWorkshop.Core.IO;

namespace MVPWorkshop.Core.Formats;

public sealed record AptConstant(int Index, int Type, uint Value, string? Text)
{
    public string TypeName => Type switch { 0 => "undefined", 1 => "string", 2 => "property", 3 => "none", 4 => "register", 5 => "boolean", 6 => "float", 7 => "integer", 8 => "lookup", _ => $"type {Type}" };
    public string Display => Type switch
    {
        1 => $"\"{Text}\"",
        6 => BitConverter.Int32BitsToSingle((int)Value).ToString("0.###"),
        4 => $"r{Value}",
        5 => Value != 0 ? "true" : "false",
        _ => Value.ToString(),
    };
}

/// <summary>
/// APT constant file: <c>"Apt constant file\x1A\0\0"</c>, u32 →movie character in the .apt, u32 count, u32 →items;
/// items are {u32 type, u32 value} (type 1 = string at offset value).
/// </summary>
public sealed class AptConstants
{
    public int MovieOffset { get; private init; }
    public List<AptConstant> Items { get; } = [];

    public static AptConstants Read(byte[] d)
    {
        if (!Encoding.ASCII.GetString(d, 0, Math.Min(17, d.Length)).StartsWith("Apt constant file", StringComparison.Ordinal)) throw new InvalidDataException("Not an APT constant file.");
        var c = new AptConstants { MovieOffset = (int)Bin.U32LE(d, 0x14) };
        int count = (int)Bin.U32LE(d, 0x18), items = (int)Bin.U32LE(d, 0x1C);
        for (var i = 0; i < count && items + i * 8 + 8 <= d.Length; i++)
        {
            var o = items + i * 8;
            var type = (int)Bin.U32LE(d, o); var v = Bin.U32LE(d, o + 4);
            c.Items.Add(new AptConstant(i, type, v, type == 1 && v < d.Length ? Bin.CString(d, (int)v) : null));
        }
        return c;
    }
}

public enum AptCharacterType { Shape = 1, EditText, Font, Button, Sprite, Sound, Image, Morph, Movie, StaticText, None, Video }

public sealed class AptCharacter
{
    public int Id { get; init; }
    public int Offset { get; init; }
    public AptCharacterType Type { get; init; }
    public List<AptFrame> Frames { get; } = [];
    public List<(string Key, string Value)> Fields { get; } = [];
    public Vector4? Bounds { get; set; }
    public int? GeometryId { get; set; }
    public int? TextureId { get; set; }
    public string? Text { get; set; }
    public int FontId { get; set; }
    /// <summary>SWF alignment: 0 left, 1 right, 2 centre, 3 justify (EditText +0x14).</summary>
    public int TextAlign { get; set; }
    public bool ReadOnly { get; set; }
    public bool Multiline { get; set; }
    public bool WordWrap { get; set; }
    /// <summary>Name of the ActionScript variable bound to the field's text.</summary>
    public string? Variable { get; set; }
    public string? FontName { get; set; }
    public float FontHeight { get; set; } = 16;
    public uint TextColor { get; set; } = uint.MaxValue;
    public List<AptButtonRecord> ButtonRecords { get; } = [];
    public string Summary => Type switch
    {
        AptCharacterType.Sprite or AptCharacterType.Movie => $"{Frames.Count} frame(s)",
        _ => string.Join(", ", Fields.Select(f => $"{f.Key} {f.Value}")),
    };
}

public sealed class AptFrame
{
    public int Index { get; init; }
    public List<AptFrameItem> Items { get; } = [];
}

public sealed class AptFrameItem
{
    public uint Flags { get; init; }
    public Matrix3x2 Matrix { get; init; } = Matrix3x2.Identity;
    public uint Tint { get; init; } = uint.MaxValue;
    public uint Additive { get; init; }
    public int ClipDepth { get; init; }
    public uint? Background { get; init; }
    public int Offset { get; init; }
    public int Type { get; init; }
    public string TypeName => Type switch { 1 => "Action", 2 => "FrameLabel", 3 => "PlaceObject", 4 => "RemoveObject", 5 => "BackgroundColor", 8 => "InitAction", _ => $"Item {Type}" };
    public string Detail { get; init; } = "";
    /// <summary>Bytecode offset in the .apt for Action / InitAction items.</summary>
    public int? Code { get; init; }
    public int? Depth { get; init; }
    /// <summary>For FrameLabel items: the frame the label names (zero-based timeline index).</summary>
    public int LabelFrame { get; init; }
    public int? Character { get; init; }
    public string? Name { get; init; }
}

public sealed record AptButtonRecord(int Flags, int Character, int Depth, Matrix3x2 Matrix, Vector4 Tint);

public sealed record AptImport(string Movie, string Name, int Character);
public sealed record AptExport(string Name, int Character);

/// <summary>
/// EA APT movie (<c>"Apt Data:6\x1A"</c>): a relocated block of structs whose pointers are file offsets. The movie
/// character (offset from the .const) lists every character; sprites/movies hold frames of display-list items.
/// Layout follows the OpenSAGE reverse engineering of the PC format and was checked against this game's files.
/// </summary>
public sealed class AptFile
{
    public const uint Signature = 0x09876543;
    public string Version { get; private init; } = "";
    public AptCharacter Movie { get; private set; } = null!;
    public List<AptCharacter?> Characters { get; } = [];
    public int Width { get; private set; }
    public int Height { get; private set; }
    public int FrameTime { get; private set; }
    public List<AptImport> Imports { get; } = [];
    public List<AptExport> Exports { get; } = [];
    public byte[] Data { get; private init; } = [];

    public static AptFile Read(byte[] d, int movieOffset)
    {
        var head = Bin.CString(d, 0, 32);
        // "Apt Data:6\x1A" in the game's movies; the EA online module (EACN) writes "Apt Data\x1A" with no version
        if (!head.StartsWith("Apt Data", StringComparison.Ordinal)) throw new InvalidDataException("Not an APT movie.");
        var f = new AptFile { Version = head[8..].Trim(':', '\x1A') is { Length: > 0 } v ? v : "-", Data = d };
        if (movieOffset <= 0 || movieOffset + 0x3C > d.Length || Bin.U32LE(d, movieOffset) != 9)
            movieOffset = Enumerable.Range(3, 16).Select(i => i * 4).FirstOrDefault(o => o + 8 <= d.Length && Bin.U32LE(d, o) == 9 && Bin.U32LE(d, o + 4) == Signature, 0x1C);
        var m = movieOffset;
        if (Bin.U32LE(d, m) != 9 || Bin.U32LE(d, m + 4) != Signature) throw new InvalidDataException("Movie character not found.");
        int charCount = (int)Bin.U32LE(d, m + 0x14), chars = (int)Bin.U32LE(d, m + 0x18);
        f.Width = (int)Bin.U32LE(d, m + 0x1C); f.Height = (int)Bin.U32LE(d, m + 0x20); f.FrameTime = (int)Bin.U32LE(d, m + 0x24);
        int imports = (int)Bin.U32LE(d, m + 0x28), importsAt = (int)Bin.U32LE(d, m + 0x2C);
        int exports = (int)Bin.U32LE(d, m + 0x30), exportsAt = (int)Bin.U32LE(d, m + 0x34);
        for (var i = 0; i < imports && importsAt + i * 16 + 16 <= d.Length; i++)
        {
            var o = importsAt + i * 16;
            f.Imports.Add(new AptImport(Str(d, Bin.U32LE(d, o)), Str(d, Bin.U32LE(d, o + 4)), (int)Bin.U32LE(d, o + 8)));
        }
        for (var i = 0; i < exports && exportsAt + i * 8 + 8 <= d.Length; i++)
        {
            var o = exportsAt + i * 8;
            f.Exports.Add(new AptExport(Str(d, Bin.U32LE(d, o)), (int)Bin.U32LE(d, o + 4)));
        }
        for (var i = 0; i < charCount && chars + i * 4 + 4 <= d.Length; i++)
        {
            var co = (int)Bin.U32LE(d, chars + i * 4);
            f.Characters.Add(co > 0 && co + 8 <= d.Length && Bin.U32LE(d, co + 4) == Signature ? ReadCharacter(d, i, co) : null);
        }
        f.Movie = f.Characters.FirstOrDefault(c => c?.Offset == m) ?? ReadCharacter(d, 0, m);
        return f;
    }

    private static string Str(byte[] d, uint p) => p > 0 && p < d.Length ? Bin.CString(d, (int)p, 256) : "";
    private static float F(byte[] d, int o) => BitConverter.ToSingle(d, o);

    private static AptCharacter ReadCharacter(byte[] d, int id, int o)
    {
        var c = new AptCharacter { Id = id, Offset = o, Type = (AptCharacterType)Bin.U32LE(d, o) };
        var b = o + 8;
        switch (c.Type)
        {
            case AptCharacterType.Movie or AptCharacterType.Sprite:
                ReadFrames(d, c, (int)Bin.U32LE(d, b), (int)Bin.U32LE(d, b + 4));
                break;
            case AptCharacterType.Shape:
                c.Bounds = new Vector4(F(d, b), F(d, b + 4), F(d, b + 8), F(d, b + 12));
                c.GeometryId = (int)Bin.U32LE(d, b + 16);
                c.Fields.Add(("bounds", $"({F(d, b):0.#}, {F(d, b + 4):0.#})–({F(d, b + 8):0.#}, {F(d, b + 12):0.#})"));
                c.Fields.Add(("geometry", Bin.U32LE(d, b + 16).ToString()));
                break;
            case AptCharacterType.Image:
                c.TextureId = (int)Bin.U32LE(d, b);
                c.Fields.Add(("texture", Bin.U32LE(d, b).ToString()));
                break;
            case AptCharacterType.Font:
                c.FontName = Str(d, Bin.U32LE(d, b));
                c.Fields.Add(("name", Str(d, Bin.U32LE(d, b))));
                c.Fields.Add(("glyphs", Bin.U32LE(d, b + 4).ToString()));
                break;
            case AptCharacterType.EditText:
                c.Bounds = new Vector4(F(d, b), F(d, b + 4), F(d, b + 8), F(d, b + 12));
                if (b + 52 <= d.Length)
                {
                    // font id, alignment, colour, height, read-only, multiline, word-wrap, →initial text, →variable
                    c.FontId = (int)Bin.U32LE(d, b + 16); c.TextAlign = (int)Bin.U32LE(d, b + 20);
                    c.TextColor = Bin.U32LE(d, b + 24); c.FontHeight = F(d, b + 28);
                    c.ReadOnly = Bin.U32LE(d, b + 32) != 0; c.Multiline = Bin.U32LE(d, b + 36) != 0; c.WordWrap = Bin.U32LE(d, b + 40) != 0;
                    c.Text = Str(d, Bin.U32LE(d, b + 44)); c.Variable = Str(d, Bin.U32LE(d, b + 48));
                }
                c.Fields.Add(("bounds", $"({F(d, b):0.#}, {F(d, b + 4):0.#})–({F(d, b + 8):0.#}, {F(d, b + 12):0.#})"));
                c.Fields.Add(("font", Bin.U32LE(d, b + 16).ToString()));
                break;
            case AptCharacterType.Button:
                if (b + 44 <= d.Length)
                {
                    c.Bounds = new Vector4(F(d, b + 4), F(d, b + 8), F(d, b + 12), F(d, b + 16));
                    int count = (int)Bin.U32LE(d, b + 36), at = (int)Bin.U32LE(d, b + 40);
                    for (int i = 0; i < Math.Clamp(count, 0, 10000) && at > 0 && (long)at + (i + 1L) * 68 <= d.Length; i++)
                    {
                        var r = at + i * 68;
                        c.ButtonRecords.Add(new AptButtonRecord(d[r], (int)Bin.U32LE(d, r + 4), (int)Bin.U32LE(d, r + 8),
                            new Matrix3x2(F(d, r + 12), F(d, r + 16), F(d, r + 20), F(d, r + 24), F(d, r + 28), F(d, r + 32)),
                            new Vector4(F(d, r + 36), F(d, r + 40), F(d, r + 44), F(d, r + 48))));
                    }
                }
                break;
        }
        return c;
    }

    private static void ReadFrames(byte[] d, AptCharacter c, int count, int at)
    {
        for (var i = 0; i < count && i < 100000 && at + i * 8 + 8 <= d.Length; i++)
        {
            var fr = new AptFrame { Index = i };
            int n = (int)Bin.U32LE(d, at + i * 8), items = (int)Bin.U32LE(d, at + i * 8 + 4);
            for (var k = 0; k < n && items + k * 4 + 4 <= d.Length; k++)
            {
                var io = (int)Bin.U32LE(d, items + k * 4);
                if (io <= 0 || io + 8 > d.Length) continue;
                fr.Items.Add(ReadItem(d, io));
            }
            c.Frames.Add(fr);
        }
    }

    private static AptFrameItem ReadItem(byte[] d, int o)
    {
        var type = (int)Bin.U32LE(d, o);
        switch (type)
        {
            case 1: return new AptFrameItem { Offset = o, Type = 1, Code = (int)Bin.U32LE(d, o + 4), Detail = $"bytecode @0x{Bin.U32LE(d, o + 4):X}" };
            case 2:
            {
                var name = Str(d, Bin.U32LE(d, o + 4));
                return new AptFrameItem { Offset = o, Type = 2, Name = name, LabelFrame = (int)Bin.U32LE(d, o + 12), Detail = $"\"{name}\" frame {Bin.U32LE(d, o + 12)}" };
            }
            case 3:
            {
                // flags, depth, character, rotscale[4], translate[2], colour transform, ?, ratio, →name, clip depth, →clip actions
                if (o + 0x40 > d.Length) break;
                var flags = Bin.U32LE(d, o + 4); var depth = (int)Bin.U32LE(d, o + 8); var ch = (int)Bin.U32LE(d, o + 12);
                var name = Str(d, Bin.U32LE(d, o + 0x34));
                var hasChar = (flags & 2) != 0;
                var detail = new StringBuilder($"depth {depth}");
                if (hasChar) detail.Append($" · char {ch}");
                if ((flags & 4) != 0) detail.Append($" · at ({F(d, o + 0x20):0.##}, {F(d, o + 0x24):0.##}) scale ({F(d, o + 0x10):0.##}, {F(d, o + 0x1C):0.##})");
                if ((flags & 8) != 0) detail.Append($" · colour {Bin.U32LE(d, o + 0x28):X8}");
                if (name.Length > 0) detail.Append($" · \"{name}\"");
                if ((flags & 1) != 0) detail.Append(" · move");
                return new AptFrameItem { Offset = o, Type = 3, Flags = flags, Depth = depth, Character = hasChar ? ch : null, Name = (flags & 32) != 0 && name.Length > 0 ? name : null, Detail = detail.ToString(),
                    Matrix = (flags & 4) != 0 ? new Matrix3x2(F(d, o + 0x10), F(d, o + 0x14), F(d, o + 0x18), F(d, o + 0x1C), F(d, o + 0x20), F(d, o + 0x24)) : Matrix3x2.Identity,
                    Tint = (flags & 8) != 0 ? Bin.U32LE(d, o + 0x28) : uint.MaxValue, Additive = (flags & 8) != 0 ? Bin.U32LE(d, o + 0x2C) : 0, ClipDepth = (flags & 64) != 0 ? (int)Bin.U32LE(d, o + 0x38) : 0 };
            }
            case 4: return new AptFrameItem { Offset = o, Type = 4, Depth = (int)Bin.U32LE(d, o + 4), Detail = $"depth {Bin.U32LE(d, o + 4)}" };
            case 5: return new AptFrameItem { Offset = o, Type = 5, Background = Bin.U32LE(d, o + 4), Detail = $"#{Bin.U32LE(d, o + 4):X8}" };
            case 8: return new AptFrameItem { Offset = o, Type = 8, Character = (int)Bin.U32LE(d, o + 4), Code = (int)Bin.U32LE(d, o + 8), Detail = $"sprite {Bin.U32LE(d, o + 4)} · bytecode @0x{Bin.U32LE(d, o + 8):X}" };
        }
        return new AptFrameItem { Offset = o, Type = type, Detail = $"@0x{o:X}" };
    }

    public IEnumerable<AptFrameItem> AllItems() =>
        Characters.OfType<AptCharacter>().SelectMany(c => c.Frames).SelectMany(f => f.Items);
}

public sealed record AptInstruction(int Offset, byte Op, string Mnemonic, string Operands, int Depth);

/// <summary>
/// Disassembler for APT action bytecode (SWF ActionScript 1 plus EA's compact opcodes). Operands of opcodes ≥ 0x80
/// are 4-byte aligned (EA's compact push/call opcodes are not). Opcodes whose operand layout is not known stop the listing rather than guess.
/// </summary>
public static class AptBytecode
{
    private static readonly Dictionary<byte, string> Simple = new()
    {
        [0x00] = "End", [0x04] = "NextFrame", [0x05] = "PrevFrame", [0x06] = "Play", [0x07] = "Stop", [0x08] = "ToggleQuality", [0x09] = "StopSounds",
        [0x0A] = "Add", [0x0B] = "Subtract", [0x0C] = "Multiply", [0x0D] = "Divide", [0x0E] = "Equals", [0x0F] = "Less", [0x10] = "And", [0x11] = "Or",
        [0x12] = "Not", [0x13] = "StringEquals", [0x14] = "StringLength", [0x15] = "StringExtract", [0x17] = "Pop", [0x18] = "ToInteger",
        [0x1C] = "GetVariable", [0x1D] = "SetVariable", [0x20] = "SetTarget2", [0x21] = "StringAdd", [0x22] = "GetProperty", [0x23] = "SetProperty",
        [0x24] = "CloneSprite", [0x25] = "RemoveSprite", [0x26] = "Trace", [0x27] = "StartDrag", [0x28] = "EndDrag", [0x29] = "StringLess",
        [0x2A] = "Throw", [0x2B] = "CastOp", [0x2C] = "ImplementsOp", [0x30] = "RandomNumber", [0x31] = "MBStringLength", [0x32] = "CharToAscii",
        [0x33] = "AsciiToChar", [0x34] = "GetTime", [0x35] = "MBStringExtract", [0x36] = "MBCharToAscii", [0x37] = "MBAsciiToChar",
        [0x3A] = "Delete", [0x3B] = "Delete2", [0x3C] = "DefineLocal", [0x3D] = "CallFunction", [0x3E] = "Return", [0x3F] = "Modulo",
        [0x40] = "NewObject", [0x41] = "DefineLocal2", [0x42] = "InitArray", [0x43] = "InitObject", [0x44] = "TypeOf", [0x45] = "TargetPath",
        [0x46] = "Enumerate", [0x47] = "Add2", [0x48] = "Less2", [0x49] = "Equals2", [0x4A] = "ToNumber", [0x4B] = "ToString",
        [0x4C] = "PushDuplicate", [0x4D] = "StackSwap", [0x4E] = "GetMember", [0x4F] = "SetMember", [0x50] = "Increment", [0x51] = "Decrement",
        [0x52] = "CallMethod", [0x53] = "NewMethod", [0x54] = "InstanceOf", [0x55] = "Enumerate2",
        [0x59] = "EA_PushZero", [0x5A] = "EA_PushOne", [0x5B] = "EA_CallFunctionPop", [0x5C] = "EA_CallFunction", [0x5D] = "EA_CallMethodPop", [0x5E] = "EA_CallMethod",
        [0x70] = "EA_PushThis", [0x71] = "EA_PushGlobal", [0x72] = "EA_Op72", [0x73] = "EA_PushTrue", [0x74] = "EA_PushFalse", [0x75] = "EA_PushNull", [0x76] = "EA_PushUndefined",
        [0x60] = "BitAnd", [0x61] = "BitOr", [0x62] = "BitXor", [0x63] = "BitLShift", [0x64] = "BitRShift", [0x65] = "BitURShift",
        [0x66] = "StrictEquals", [0x67] = "Greater", [0x68] = "StringGreater", [0x69] = "Extends",
    };

    public static List<AptInstruction> Disassemble(byte[] d, int start, AptConstants? consts, int maxBytes = 1 << 20)
    {
        var list = new List<AptInstruction>();
        var pool = new List<uint>();
        Walk(d, start, Math.Min(d.Length, start + maxBytes), 0, consts, pool, list);
        return list;
    }

    private static string Const(AptConstants? c, List<uint> pool, int index)
    {
        if (c is null) return $"#{index}";
        var ci = index < pool.Count ? (int)pool[index] : index;
        return ci >= 0 && ci < c.Items.Count ? c.Items[ci].Display : $"#{index}";
    }

    private static void Walk(byte[] d, int pc, int end, int depth, AptConstants? c, List<uint> pool, List<AptInstruction> outp)
    {
        int Al(int p) => (p + 3) & ~3;
        while (pc < end && outp.Count < 200000)
        {
            var at = pc; var op = d[pc++];
            void Add(string m, string a = "") => outp.Add(new AptInstruction(at, op, m, a, depth));
            if (op < 0x80 && Simple.TryGetValue(op, out var name))
            {
                Add(name);
                if (op == 0x00) return;
                continue;
            }
            switch (op)
            {
                case 0xA2: Add("EA_PushConstant", Const(c, pool, d[pc++])); continue;
                case 0xA3: Add("EA_PushWordConstant", Const(c, pool, Bin.U16LE(d, pc))); pc += 2; continue;
                case 0xAE: Add("EA_PushValueOfVar", Const(c, pool, d[pc++])); continue;
                case 0xAF: Add("EA_GetNamedMember", Const(c, pool, d[pc++])); continue;
                case 0xB0: Add("EA_CallNamedFunctionPop", Const(c, pool, d[pc++])); continue;
                case 0xB1: Add("EA_CallNamedFunction", Const(c, pool, d[pc++])); continue;
                case 0xB2: Add("EA_CallNamedMethodPop", Const(c, pool, d[pc++])); continue;
                case 0xB3: Add("EA_CallNamedMethod", Const(c, pool, d[pc++])); continue;
                case 0xB5: Add("EA_PushByte", ((sbyte)d[pc++]).ToString()); continue;
                case 0xB6: Add("EA_PushShort", Bin.S16LE(d, pc).ToString()); pc += 2; continue;
                case 0xB9: Add("EA_PushRegister", $"r{d[pc++]}"); continue;
                case 0xB4: Add("EA_PushFloat", BitConverter.ToSingle(d, pc).ToString("0.###")); pc += 4; continue;
                case 0xB7: Add("EA_PushLong", $"{(int)Bin.U32LE(d, pc)} (0x{Bin.U32LE(d, pc):X})"); pc += 4; continue;
                case 0x9F: Add("GotoFrame2", (d[pc++] & 1) != 0 ? "play" : "stop"); continue;
                case 0xA1 or 0xA4 or 0xA5 or 0xA6 or 0xA7:
                    pc = Al(pc);
                    Add(op switch { 0xA1 => "EA_PushString", 0xA4 => "EA_GetStringVar", 0xA5 => "EA_GetStringMember", 0xA6 => "EA_SetStringVar", _ => "EA_SetStringMember" }, $"\"{Bin.CString(d, (int)Bin.U32LE(d, pc), 256)}\"");
                    pc += 4;
                    continue;
                case 0x9A: Add("GetURL2"); continue;
                case 0x88:
                {
                    pc = Al(pc);
                    int n = (int)Bin.U32LE(d, pc), p = (int)Bin.U32LE(d, pc + 4);
                    pc += 8;
                    pool.Clear();
                    for (var i = 0; i < n && p + i * 4 + 4 <= d.Length; i++) pool.Add(Bin.U32LE(d, p + i * 4));
                    Add("ConstantPool", string.Join(", ", pool.Select((_, i) => Const(c, pool, i))));
                    continue;
                }
                case 0x96:
                {
                    pc = Al(pc);
                    int n = (int)Bin.U32LE(d, pc), p = (int)Bin.U32LE(d, pc + 4);
                    pc += 8;
                    var vals = Enumerable.Range(0, Math.Clamp(n, 0, 256)).Where(i => p + i * 4 + 4 <= d.Length).Select(i => c is null ? $"#{Bin.U32LE(d, p + i * 4)}" : Const(c, [], (int)Bin.U32LE(d, p + i * 4)));
                    Add("PushData", string.Join(", ", vals));
                    continue;
                }
                case 0x99 or 0x9D or 0xB8:
                    pc = Al(pc);
                    Add(op switch { 0x99 => "BranchAlways", 0x9D => "BranchIfTrue", _ => "EA_BranchIfFalse" }, $"{(int)Bin.U32LE(d, pc):+#;-#;0} → 0x{pc + 4 + (int)Bin.U32LE(d, pc):X}");
                    pc += 4;
                    continue;
                case 0x81: pc = Al(pc); Add("GotoFrame", Bin.U32LE(d, pc).ToString()); pc += 4; continue;
                case 0x87: pc = Al(pc); Add("StoreRegister", $"r{Bin.U32LE(d, pc)}"); pc += 4; continue;
                case 0x8B: pc = Al(pc); Add("SetTarget", $"\"{Bin.CString(d, (int)Bin.U32LE(d, pc), 256)}\""); pc += 4; continue;
                case 0x8C: pc = Al(pc); Add("GotoLabel", $"\"{Bin.CString(d, (int)Bin.U32LE(d, pc), 256)}\""); pc += 4; continue;
                case 0x83:
                    pc = Al(pc);
                    Add("GetURL", $"\"{Bin.CString(d, (int)Bin.U32LE(d, pc), 256)}\", \"{Bin.CString(d, (int)Bin.U32LE(d, pc + 4), 256)}\"");
                    pc += 8;
                    continue;
                case 0x9B:
                {
                    // DefineFunction: →name, u32 argc, →args (u32 → name each), u32 body size, 8-byte signature, body inline
                    pc = Al(pc);
                    var fname = Bin.CString(d, (int)Bin.U32LE(d, pc), 256);
                    int argc = (int)Bin.U32LE(d, pc + 4), args = (int)Bin.U32LE(d, pc + 8), size = (int)Bin.U32LE(d, pc + 12);
                    var names = Enumerable.Range(0, Math.Clamp(argc, 0, 64)).Where(i => args + i * 4 + 4 <= d.Length).Select(i => Bin.CString(d, (int)Bin.U32LE(d, args + i * 4), 64));
                    pc += 24;
                    Add("DefineFunction", $"{(fname.Length > 0 ? fname : "<anonymous>")}({string.Join(", ", names)}) · {size} bytes");
                    if (size > 0 && pc + size <= d.Length) Walk(d, pc, pc + size, depth + 1, c, pool, outp);
                    pc += Math.Max(0, size);
                    continue;
                }
                case 0x8E:
                {
                    // DefineFunction2: →name, u32 argc, u8 registers, u8 0, u16 flags, →args ({u32 register, →name} each), u32 body size, 8-byte signature
                    pc = Al(pc);
                    var fname = Bin.CString(d, (int)Bin.U32LE(d, pc), 256);
                    int argc = (int)Bin.U32LE(d, pc + 4), regs = d[pc + 8], flags = Bin.U16LE(d, pc + 10), args = (int)Bin.U32LE(d, pc + 12), size = (int)Bin.U32LE(d, pc + 16);
                    var names = Enumerable.Range(0, Math.Clamp(argc, 0, 64)).Where(i => args + i * 8 + 8 <= d.Length)
                        .Select(i => $"r{Bin.U32LE(d, args + i * 8)}:{Bin.CString(d, (int)Bin.U32LE(d, args + i * 8 + 4), 64)}");
                    pc += 28;
                    Add("DefineFunction2", $"{(fname.Length > 0 ? fname : "<anonymous>")}({string.Join(", ", names)}) · {regs} registers · flags 0x{flags:X} · {size} bytes");
                    if (size > 0 && pc + size <= d.Length) Walk(d, pc, pc + size, depth + 1, c, pool, outp);
                    pc += Math.Max(0, size);
                    continue;
                }
            }
            Add($"op_{op:X2}", "(operand layout unknown — listing stops)");
            return;
        }
    }
}


