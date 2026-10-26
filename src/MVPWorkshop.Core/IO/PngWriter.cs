using System.Buffers.Binary;
using System.IO.Compression;

namespace MVPWorkshop.Core.IO;

/// <summary>Minimal RGBA PNG encoder (no WPF dependency, usable from the CLI).</summary>
public static class PngWriter
{
    public static byte[] Encode(int w, int h, byte[] bgra)
    {
        using var raw = new MemoryStream();
        using (var z = new ZLibStream(raw, CompressionLevel.Fastest, leaveOpen: true))
        {
            var row = new byte[w * 4 + 1];
            for (var y = 0; y < h; y++)
            {
                row[0] = 0;
                for (var x = 0; x < w; x++)
                {
                    var s = (y * w + x) * 4;
                    row[1 + x * 4] = bgra[s + 2]; row[2 + x * 4] = bgra[s + 1]; row[3 + x * 4] = bgra[s]; row[4 + x * 4] = bgra[s + 3];
                }
                z.Write(row);
            }
        }
        using var ms = new MemoryStream();
        ms.Write([0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]);
        var ihdr = new byte[13];
        BinaryPrimitives.WriteInt32BigEndian(ihdr, w); BinaryPrimitives.WriteInt32BigEndian(ihdr.AsSpan(4), h);
        ihdr[8] = 8; ihdr[9] = 6;
        Chunk(ms, "IHDR", ihdr);
        Chunk(ms, "IDAT", raw.ToArray());
        Chunk(ms, "IEND", []);
        return ms.ToArray();
    }

    private static void Chunk(Stream s, string type, byte[] data)
    {
        Span<byte> b = stackalloc byte[4];
        BinaryPrimitives.WriteInt32BigEndian(b, data.Length); s.Write(b);
        var t = System.Text.Encoding.ASCII.GetBytes(type);
        s.Write(t); s.Write(data);
        var crc = Crc.Compute(t, data);
        BinaryPrimitives.WriteUInt32BigEndian(b, crc); s.Write(b);
    }


    private static class Crc
    {
        private static readonly uint[] Table = Build();
        private static uint[] Build()
        {
            var t = new uint[256];
            for (uint n = 0; n < 256; n++) { var c = n; for (var k = 0; k < 8; k++) c = (c & 1) != 0 ? 0xEDB88320 ^ (c >> 1) : c >> 1; t[n] = c; }
            return t;
        }
        public static uint Compute(byte[] a, byte[] b)
        {
            var c = 0xFFFFFFFFu;
            foreach (var x in a) c = Table[(c ^ x) & 0xFF] ^ (c >> 8);
            foreach (var x in b) c = Table[(c ^ x) & 0xFF] ^ (c >> 8);
            return c ^ 0xFFFFFFFFu;
        }
    }

}

