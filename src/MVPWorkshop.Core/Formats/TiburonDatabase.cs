using System.Buffers.Binary;
using System.Globalization;
using System.Text;
using MVPWorkshop.Core.IO;

namespace MVPWorkshop.Core.Formats;

/// <summary>EA DB v8 with fixed records, packed fields and plain/Huffman variable strings. Edits preserve row counts and unknown bytes.</summary>
public sealed class TiburonDatabase
{
    private static readonly Encoding TextEncoding = CreateTextEncoding();
    private static Encoding CreateTextEncoding() { Encoding.RegisterProvider(CodePagesEncodingProvider.Instance); return Encoding.GetEncoding(1252, EncoderFallback.ExceptionFallback, DecoderFallback.ExceptionFallback); }
    public sealed record Field(string Name, uint Type, int BitOffset, int Bits);
    public sealed record Table(int Index, string Name, int Offset, int End, uint Allocation, int Stride, int Capacity, int Rows, IReadOnlyList<Field> Fields)
    {
        public int DataOffset => Offset + 40 + Fields.Count * 16;
        public int ExtraOffset => DataOffset + Stride * Capacity;
        public string Label => $"{Name} · {Rows:N0} records · {Fields.Count} fields";
    }
    private sealed class Huffman
    {
        public Huffman? Left, Right;
        public byte Value;
        public bool Leaf;
    }
    public byte[] Data { get; private init; } = [];
    public List<Table> Tables { get; } = [];

    public static TiburonDatabase Read(byte[] data)
    {
        void Bounds(long p, long n) { if (p < 0 || n < 0 || p > data.Length || n > data.Length - p) throw new InvalidDataException("DB table or record exceeds file bounds."); }
        Bounds(0, 24);
        if (data[0] != 'D' || data[1] != 'B' || data[2] != 0 || data[3] != 8 || data[4] != 0) throw new NotSupportedException("Only little-endian EA DB v8 is supported.");
        if (Bin.U32LE(data, 8) != data.Length) throw new InvalidDataException("DB declared size differs from file size.");
        int count = checked((int)Bin.U32LE(data, 16));
        if (count > 4096) throw new InvalidDataException("Invalid DB table count.");
        Bounds(24, count * 8L);
        int start = 24 + count * 8;
        var result = new TiburonDatabase { Data = data };
        for (int i = 0; i < count; i++)
        {
            int p = checked(start + (int)Bin.U32LE(data, 28 + i * 8));
            int end = i + 1 < count ? checked(start + (int)Bin.U32LE(data, 36 + i * 8)) : data.Length - 4;
            Bounds(p, end - (long)p); Bounds(p, 40);
            if (end < p + 40) throw new InvalidDataException("Truncated DB table header.");
            int stride = checked((int)Bin.U32LE(data, p + 8)), capacity = Bin.U16LE(data, p + 20), rows = Bin.U16LE(data, p + 22), fields = data[p + 28];
            if (rows > capacity || stride > 1024 * 1024) throw new InvalidDataException("Invalid DB record allocation.");
            long length = 40L + fields * 16L + stride * (long)capacity;
            if (length + data[p + 29] * 16L > end - p) throw new InvalidDataException("DB records/indexes exceed table bounds.");
            var definitions = new List<Field>();
            for (int f = 0; f < fields; f++)
            {
                int at = p + 40 + f * 16;
                uint type = Bin.U32LE(data, at); int offset = checked((int)Bin.U32LE(data, at + 4)), bits = checked((int)Bin.U32LE(data, at + 12));
                int storageBits = type is 13 or 14 ? 32 : bits;
                if (offset < 0 || storageBits <= 0 || offset + (long)storageBits > stride * 8L) throw new InvalidDataException("DB field exceeds record bounds.");
                definitions.Add(new(Bin.FourCC(data, at + 8), type, offset, bits));
            }
            result.Tables.Add(new(i, Bin.FourCC(data, 24 + i * 8), p, end, Bin.U32LE(data, p + 4), stride, capacity, rows, definitions));
        }
        return result;
    }

    private uint Bits(Table table, int row, Field field)
    {
        if (row < 0 || row >= table.Rows) throw new ArgumentOutOfRangeException(nameof(row));
        int length = field.Type is 13 or 14 ? 32 : field.Bits;
        if (length > 32) throw new NotSupportedException("Numeric DB fields wider than 32 bits are not supported.");
        long first = (table.DataOffset + row * (long)table.Stride) * 8 + field.BitOffset;
        uint value = 0;
        for (int i = 0; i < length; i++) value |= (uint)((Data[(first + i) / 8] >> (int)((first + i) % 8)) & 1) << i;
        return value;
    }

    private void SetBits(byte[] data, Table table, int row, Field field, uint value)
    {
        int length = field.Type is 13 or 14 ? 32 : field.Bits;
        long first = (table.DataOffset + row * (long)table.Stride) * 8 + field.BitOffset;
        for (int i = 0; i < length; i++)
        {
            int index = checked((int)((first + i) / 8)), shift = (int)((first + i) % 8);
            data[index] = (byte)((data[index] & ~(1 << shift)) | (int)((value >> i) & 1) << shift);
        }
    }

    private Huffman Tree(Table table)
    {
        var root = new Huffman(); var queue = new Queue<Huffman>(); queue.Enqueue(root);
        int offset = table.ExtraOffset, nodes = 0;
        while (queue.Count > 0)
        {
            var node = queue.Dequeue();
            if (offset + 4 > table.End || ++nodes > 512) throw new InvalidDataException("Invalid DB Huffman tree.");
            node.Left = new() { Leaf = Data[offset] == 0, Value = Data[offset + 1] };
            node.Right = new() { Leaf = Data[offset + 2] == 0, Value = Data[offset + 3] };
            offset += 4;
            // Serialized breadth first, visiting right branches before left branches on each level.
            if (!node.Right.Leaf) queue.Enqueue(node.Right);
            if (!node.Left.Leaf) queue.Enqueue(node.Left);
        }
        return root;
    }

    public string Get(Table table, int row, Field field)
    {
        if (field.Type == 0)
        {
            if (field.BitOffset % 8 != 0 || field.Bits % 8 != 0) throw new NotSupportedException("Unaligned DB text field.");
            var bytes = Data.AsSpan(table.DataOffset + row * table.Stride + field.BitOffset / 8, field.Bits / 8);
            int terminator = bytes.IndexOf((byte)0);
            return TextEncoding.GetString(terminator < 0 ? bytes : bytes[..terminator]);
        }
        uint raw = Bits(table, row, field);
        if (field.Type is 13 or 14)
        {
            if (raw == uint.MaxValue) return "";
            long pos = table.ExtraOffset + (long)raw;
            int prefix = field.Type == 13 ? 1 : 2;
            if (pos < table.ExtraOffset || pos + prefix > table.End) throw new InvalidDataException("DB text pointer exceeds table bounds.");
            int length = prefix == 1 ? Data[pos] : BinaryPrimitives.ReadUInt16BigEndian(Data.AsSpan((int)pos));
            pos += prefix;
            if ((table.Allocation & 64) == 0)
            {
                if (pos + length > table.End) throw new InvalidDataException("Truncated DB variable text.");
                return TextEncoding.GetString(Data, (int)pos, length);
            }
            var root = Tree(table); var current = root; var bytes = new List<byte>(length);
            long bit = pos * 8;
            while (bytes.Count < length)
            {
                if (bit / 8 >= table.End) throw new InvalidDataException("Truncated DB Huffman text.");
                bool right = (Data[bit / 8] & (128 >> (int)(bit % 8))) != 0; bit++;
                current = (right ? current.Right : current.Left)!;
                if (current.Leaf) { bytes.Add(current.Value); current = root; }
            }
            return TextEncoding.GetString(bytes.ToArray());
        }
        if (field.Type == 4 && field.Bits == 32) return BitConverter.UInt32BitsToSingle(raw).ToString("R", CultureInfo.InvariantCulture);
        if (field.Type == 2) { long signed = raw; if ((raw & (1U << (field.Bits - 1))) != 0) signed -= 1L << field.Bits; return signed.ToString(CultureInfo.InvariantCulture); }
        return raw.ToString(CultureInfo.InvariantCulture);
    }

    public byte[] Set(Table table, int row, Field field, string value)
    {
        // Validate the address before copying or updating anything.
        _ = Get(table, row, field);
        var result = Data.ToArray();
        if (field.Type == 0)
        {
            var encoded = TextEncoding.GetBytes(value);
            if (value.Contains('\0') || encoded.Length > field.Bits / 8) throw new InvalidDataException($"Text must fit in {field.Bits / 8} single-byte characters.");
            var span = result.AsSpan(table.DataOffset + row * table.Stride + field.BitOffset / 8, field.Bits / 8); span.Clear(); encoded.CopyTo(span);
        }
        else if (field.Type is 13 or 14)
        {
            int limit = Math.Min(field.Type == 13 ? 255 : 65535, field.Bits / 8);
            byte[] text = TextEncoding.GetBytes(value);
            if (text.Length > limit) throw new InvalidDataException("Variable text exceeds its encoding limits.");
            if ((table.Allocation & 64) != 0)
            {
                var codes = new Dictionary<byte, string>();
                void Codes(Huffman node, string path) { if (node.Leaf) codes[node.Value] = path; else { Codes(node.Left!, path + "0"); Codes(node.Right!, path + "1"); } }
                Codes(Tree(table), "");
                var bits = new StringBuilder();
                foreach (byte c in text) { if (!codes.TryGetValue(c, out var code)) throw new InvalidDataException($"Character '{(char)c}' is absent from this table's Huffman alphabet."); bits.Append(code); }
                text = new byte[(bits.Length + 7) / 8]; for (int i = 0; i < bits.Length; i++) if (bits[i] == '1') text[i / 8] |= (byte)(128 >> (i % 8));
            }
            int prefix = field.Type == 13 ? 1 : 2, added = (prefix + text.Length + 3) & ~3;
            int insertion = table.End - Data[table.Offset + 29] * 16;
            result = new byte[checked(Data.Length + added)]; Data.AsSpan(0, insertion).CopyTo(result); Data.AsSpan(insertion).CopyTo(result.AsSpan(insertion + added));
            if (prefix == 1) result[insertion] = (byte)value.Length; else BinaryPrimitives.WriteUInt16BigEndian(result.AsSpan(insertion), (ushort)value.Length);
            text.CopyTo(result, insertion + prefix);
            SetBits(result, table, row, field, checked((uint)(insertion - table.ExtraOffset)));
            for (int i = table.Index + 1; i < Tables.Count; i++) WriteWord(result, 28 + i * 8, checked(Bin.U32LE(result, 28 + i * 8) + (uint)added));
            WriteWord(result, 8, (uint)result.Length);
        }
        else
        {
            uint raw;
            if (field.Type == 4 && field.Bits == 32)
            { float f = float.Parse(value, CultureInfo.InvariantCulture); if (!float.IsFinite(f)) throw new InvalidDataException("Float must be finite."); raw = BitConverter.SingleToUInt32Bits(f); }
            else if (field.Type == 2)
            { long v = long.Parse(value, CultureInfo.InvariantCulture), min = -(1L << (field.Bits - 1)), max = (1L << (field.Bits - 1)) - 1; if (v < min || v > max) throw new InvalidDataException($"Value must be {min} through {max}."); raw = unchecked((uint)v); }
            else if (field.Type == 3)
            { ulong v = ulong.Parse(value, CultureInfo.InvariantCulture), max = field.Bits == 32 ? uint.MaxValue : (1UL << field.Bits) - 1; if (v > max) throw new InvalidDataException($"Value must be 0 through {max}."); raw = (uint)v; }
            else throw new NotSupportedException($"DB field type {field.Type} is read-only.");
            SetBits(result, table, row, field, raw);
        }
        var rebuilt = Read(result); rebuilt.UpdateChecksums();
        return result;
    }

    private static void WriteWord(byte[] bytes, int offset, uint value) => BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(offset), value);
    // EA uses a non-reflected CRC-32 polynomial, initial value FFFFFFFF and no final complement.
    private static uint Crc(ReadOnlySpan<byte> bytes)
    {
        uint crc = uint.MaxValue;
        foreach (byte b in bytes) { crc ^= (uint)b << 24; for (int i = 0; i < 8; i++) crc = (crc << 1) ^ ((crc & 0x80000000) != 0 ? 0x04C11DB7U : 0); }
        return crc;
    }
    public IReadOnlyList<string> CheckChecksums()
    {
        var problems = new List<string>();
        if (Bin.U32LE(Data, 20) != Crc(Data.AsSpan(0, 20))) problems.Add("DB header checksum");
        if (Tables.Count == 0) return problems;
        if (Bin.U32LE(Data, Tables[0].Offset) != Crc(Data.AsSpan(24, Tables.Count * 8))) problems.Add("DB directory checksum");
        foreach (var table in Tables)
        {
            if (Bin.U32LE(Data, table.Offset + 36) != Crc(Data.AsSpan(table.Offset + 4, 32))) problems.Add(table.Name + " header checksum");
            if (Bin.U32LE(Data, table.End) != Crc(Data.AsSpan(table.Offset + 40, table.End - table.Offset - 40))) problems.Add(table.Name + " body checksum");
        }
        return problems;
    }
    private void UpdateChecksums()
    {
        WriteWord(Data, 20, Crc(Data.AsSpan(0, 20)));
        if (Tables.Count == 0) return;
        WriteWord(Data, Tables[0].Offset, Crc(Data.AsSpan(24, Tables.Count * 8)));
        foreach (var table in Tables)
        {
            WriteWord(Data, table.Offset + 36, Crc(Data.AsSpan(table.Offset + 4, 32)));
            WriteWord(Data, table.End, Crc(Data.AsSpan(table.Offset + 40, table.End - table.Offset - 40)));
        }
    }
}
