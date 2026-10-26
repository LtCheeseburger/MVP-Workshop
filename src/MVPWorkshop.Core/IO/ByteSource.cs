using System.Buffers.Binary;

namespace MVPWorkshop.Core.IO;

/// <summary>Random-access, read-only view over bytes (file, file range, or memory).</summary>
public interface IByteSource
{
    long Length { get; }
    int Read(long offset, Span<byte> buffer);
}

public sealed class MemoryByteSource(byte[] data) : IByteSource
{
    public byte[] Data { get; } = data;
    public long Length => Data.Length;

    public int Read(long offset, Span<byte> buffer)
    {
        if (offset >= Data.Length || offset < 0) return 0;
        var n = (int)Math.Min(buffer.Length, Data.Length - offset);
        Data.AsSpan((int)offset, n).CopyTo(buffer);
        return n;
    }
}

/// <summary>
/// A range of a file on disk. Opens the file briefly per uncached read so nothing stays locked;
/// small reads are served from a 64 KB read-ahead window.
/// </summary>
public sealed class FileRangeByteSource(string path, long start, long length) : IByteSource
{
    private const int Window = 64 * 1024;
    private readonly object _lock = new();
    private byte[]? _buf;
    private long _bufStart = -1;
    private int _bufLen;

    public string Path { get; } = path;
    public long Start { get; } = start;
    public long Length { get; } = length;

    public int Read(long offset, Span<byte> buffer)
    {
        if (offset >= Length || offset < 0) return 0;
        var n = (int)Math.Min(buffer.Length, Length - offset);
        if (n > Window / 4) return ReadDirect(offset, buffer[..n]);
        lock (_lock)
        {
            if (_buf is null || offset < _bufStart || offset + n > _bufStart + _bufLen)
            {
                _buf ??= new byte[Window];
                _bufStart = offset;
                _bufLen = ReadDirect(offset, _buf.AsSpan(0, (int)Math.Min(Window, Length - offset)));
            }
            var avail = (int)Math.Min(n, _bufStart + _bufLen - offset);
            _buf.AsSpan((int)(offset - _bufStart), avail).CopyTo(buffer);
            return avail;
        }
    }

    private int ReadDirect(long offset, Span<byte> buffer)
    {
        using var fs = new FileStream(Path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 1, FileOptions.RandomAccess);
        fs.Position = Start + offset;
        return fs.ReadAtLeast(buffer, buffer.Length, throwOnEndOfStream: false);
    }
}

public static class ByteSourceExtensions
{
    public static byte[] ReadBytes(this IByteSource src, long offset, int count)
    {
        count = (int)Math.Max(0, Math.Min(count, src.Length - offset));
        var buf = new byte[count];
        src.Read(offset, buf);
        return buf;
    }

    public static byte[] ReadAll(this IByteSource src)
    {
        if (src is MemoryByteSource m) return m.Data;
        if (src.Length > int.MaxValue) throw new InvalidOperationException("File too large to load.");
        return src.ReadBytes(0, (int)src.Length);
    }

    public static uint U32LE(this IByteSource s, long o) { Span<byte> b = stackalloc byte[4]; s.Read(o, b); return BinaryPrimitives.ReadUInt32LittleEndian(b); }
    public static uint U32BE(this IByteSource s, long o) { Span<byte> b = stackalloc byte[4]; s.Read(o, b); return BinaryPrimitives.ReadUInt32BigEndian(b); }
}

public static class Bin
{
    public static ushort U16LE(ReadOnlySpan<byte> d, int o) => BinaryPrimitives.ReadUInt16LittleEndian(d[o..]);
    public static short S16LE(ReadOnlySpan<byte> d, int o) => BinaryPrimitives.ReadInt16LittleEndian(d[o..]);
    public static short S16BE(ReadOnlySpan<byte> d, int o) => BinaryPrimitives.ReadInt16BigEndian(d[o..]);
    public static uint U32LE(ReadOnlySpan<byte> d, int o) => BinaryPrimitives.ReadUInt32LittleEndian(d[o..]);
    public static uint U32BE(ReadOnlySpan<byte> d, int o) => BinaryPrimitives.ReadUInt32BigEndian(d[o..]);
    public static uint U24LE(ReadOnlySpan<byte> d, int o) => (uint)(d[o] | d[o + 1] << 8 | d[o + 2] << 16);

    public static string FourCC(ReadOnlySpan<byte> d, int o = 0)
    {
        if (d.Length < o + 4) return "";
        Span<char> c = stackalloc char[4];
        for (var i = 0; i < 4; i++) c[i] = d[o + i] is >= 0x20 and < 0x7F ? (char)d[o + i] : '.';
        return new string(c);
    }

    public static bool IsPrintable4CC(ReadOnlySpan<byte> d, int o = 0)
    {
        if (d.Length < o + 4) return false;
        for (var i = 0; i < 4; i++) if (d[o + i] is < 0x20 or >= 0x7F) return false;
        return true;
    }

    public static string CString(ReadOnlySpan<byte> d, int o, int max = int.MaxValue)
    {
        var end = o;
        while (end < d.Length && end - o < max && d[end] != 0) end++;
        return System.Text.Encoding.Latin1.GetString(d[o..end]);
    }

    public static string Size(long bytes) => bytes switch
    {
        < 1024 => $"{bytes} B",
        < 1024 * 1024 => $"{bytes / 1024.0:0.0} KB",
        < 1024L * 1024 * 1024 => $"{bytes / 1048576.0:0.00} MB",
        _ => $"{bytes / 1073741824.0:0.00} GB"
    };
}
