using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;

namespace MVPWorkshop.Core.IO;

public static class PngValidation
{
    /// <summary>Checks icon PNG integrity, including chunk CRCs and the complete bounded image stream.</summary>
    public static string? IconProblem(byte[] data)
    {
        try
        {
            if (data.Length < 33 || !data.AsSpan(0, 8).SequenceEqual(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 })) return "The icon isn't a complete PNG.";
            using var pixels = new MemoryStream();
            int offset = 8, width = 0, height = 0, bits = 0, channels = 0, interlace = 0, color = 0;
            bool header = false, end = false, palette = false;
            while (offset + 12 <= data.Length)
            {
                var size = BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(offset));
                if (size > data.Length - offset - 12) return "The PNG has a truncated chunk.";
                var length = (int)size;
                var type = Encoding.ASCII.GetString(data, offset + 4, 4);
                var crc = 0xffffffffu;
                foreach (var b in data.AsSpan(offset + 4, length + 4))
                {
                    crc ^= b;
                    for (int i = 0; i < 8; i++) crc = (crc & 1) != 0 ? 0xedb88320u ^ (crc >> 1) : crc >> 1;
                }
                if ((crc ^ 0xffffffffu) != BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(offset + 8 + length))) return "The PNG has a damaged chunk checksum.";
                if (!header && type != "IHDR") return "The PNG is missing its image header.";
                if (type == "IHDR")
                {
                    if (header || length != 13) return "The PNG image header is invalid.";
                    width = BinaryPrimitives.ReadInt32BigEndian(data.AsSpan(offset + 8)); height = BinaryPrimitives.ReadInt32BigEndian(data.AsSpan(offset + 12));
                    if (width != height || width is < 64 or > 256) return "The icon must be square and 64 to 256 pixels.";
                    bits = data[offset + 16]; color = data[offset + 17]; interlace = data[offset + 20];
                    channels = color switch { 0 => 1, 2 => 3, 3 => 1, 4 => 2, 6 => 4, _ => 0 };
                    if (channels == 0 || !(bits is 8 or 16 || color is 0 or 3 && bits is 1 or 2 or 4) || color == 3 && bits == 16 || data[offset + 18] != 0 || data[offset + 19] != 0 || interlace > 1) return "The PNG uses an invalid image format.";
                    header = true;
                }
                else if (type == "PLTE") { if (length is < 3 or > 768 || length % 3 != 0) return "The PNG palette is invalid."; palette = true; }
                else if (type == "IDAT") pixels.Write(data, offset + 8, length);
                else if (type == "IEND") { if (length != 0) return "The PNG end marker is invalid."; end = true; break; }
                else if (type[0] is >= 'A' and <= 'Z') return "The PNG contains an unsupported critical chunk.";
                offset += length + 12;
            }
            if (!end || pixels.Length == 0 || color == 3 && !palette) return "The PNG image data is incomplete.";
            pixels.Position = 0;
            using var decoded = new ZLibStream(pixels, CompressionMode.Decompress);
            var passes = interlace == 0 ? new[] { (0, 0, 1, 1) } : new[] { (0, 0, 8, 8), (4, 0, 8, 8), (0, 4, 4, 8), (2, 0, 4, 4), (0, 2, 2, 4), (1, 0, 2, 2), (0, 1, 1, 2) };
            foreach (var (x, y, dx, dy) in passes)
            {
                int w = Math.Max(0, (width - x + dx - 1) / dx), h = Math.Max(0, (height - y + dy - 1) / dy);
                if (w == 0 || h == 0) continue;
                var row = new byte[(w * channels * bits + 7) / 8];
                for (int line = 0; line < h; line++)
                {
                    if (decoded.ReadByte() is < 0 or > 4) return "The PNG has an invalid or truncated image row.";
                    decoded.ReadExactly(row);
                }
            }
            if (decoded.ReadByte() != -1) return "The PNG image stream has an invalid length.";
            return null;
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or ArgumentException) { return "The PNG is damaged: " + ex.Message; }
    }
}
