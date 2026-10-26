namespace MVPWorkshop.Core.IO;

/// <summary>EA RefPack (QFS / "10 FB") LZ77 decompressor.</summary>
public static class RefPack
{
    public static bool IsCompressed(ReadOnlySpan<byte> d) =>
        d.Length >= 5 && (d[0] & 0x3E) == 0x10 && d[1] == 0xFB;

    public static int HeaderSize(ReadOnlySpan<byte> d)
    {
        var sizeBytes = (d[0] & 0x80) != 0 ? 4 : 3;
        return 2 + sizeBytes * ((d[0] & 0x01) != 0 ? 2 : 1);
    }

    public static int GetDecompressedSize(ReadOnlySpan<byte> d)
    {
        var sizeBytes = (d[0] & 0x80) != 0 ? 4 : 3;
        var p = 2 + ((d[0] & 0x01) != 0 ? sizeBytes : 0);
        var n = 0;
        for (var i = 0; i < sizeBytes; i++) n = n << 8 | d[p + i];
        return n;
    }


    /// <summary>Header layout of an existing RefPack stream, reused when re-encoding so the container stays in the same style.</summary>
    public readonly record struct Style(byte Flags, bool HasCompressedSize)
    {
        public static Style Default => new(0x10, false);
        public int SizeBytes => (Flags & 0x80) != 0 ? 4 : 3;
    }

    public static Style StyleOf(ReadOnlySpan<byte> d) => new((byte)(d[0] & 0xC1 | 0x10), (d[0] & 1) != 0);

    /// <summary>
    /// RefPack (QFS) compressor producing the same opcode set <see cref="Decompress"/> reads: 2/3/4-byte copy commands with 0–3
    /// trailing literals, 0xE0–0xFB literal runs and a 0xFC–0xFF stop code. Greedy hash-chain matcher over the full 128 KB window.
    /// </summary>
    public static byte[] Compress(ReadOnlySpan<byte> data, Style? style = null) => Compress(data.ToArray(), style);

    public static byte[] Compress(byte[] src, Style? style = null)
    {
        var st = style ?? Style.Default;
        var sizeBytes = st.SizeBytes;
        if (sizeBytes == 3 && src.Length > 0xFFFFFF) st = new Style((byte)(st.Flags | 0x80), st.HasCompressedSize);
        sizeBytes = st.SizeBytes;
        var body = new List<byte>(src.Length / 2 + 16);

        const int HashBits = 16, MaxChain = 48, MaxDist = 131072;
        var head = new int[1 << HashBits]; Array.Fill(head, -1);
        var prev = new int[src.Length];
        int Hash(int i) => (int)((uint)(src[i] | src[i + 1] << 8 | src[i + 2] << 16) * 2654435761u >> (32 - HashBits));
        void Insert(int i) { if (i + 2 < src.Length) { var h = Hash(i); prev[i] = head[h]; head[h] = i; } }

        var litStart = 0;
        var pos = 0;
        void FlushLiterals(int upTo, out int remainder)
        {
            // whole 4-byte groups (4..112 per command) go out as 0xE0 runs; 0–3 leftover literals ride on the next copy / stop code
            var n = upTo - litStart;
            var whole = n & ~3;
            var p = litStart;
            while (whole > 0)
            {
                var chunk = Math.Min(whole, 112);
                body.Add((byte)(0xE0 | (chunk - 4) >> 2));
                for (var k = 0; k < chunk; k++) body.Add(src[p++]);
                whole -= chunk;
            }
            litStart = p;
            remainder = n & 3;
        }

        while (pos < src.Length)
        {
            int bestLen = 0, bestDist = 0;
            if (pos + 2 < src.Length)
            {
                var cand = head[Hash(pos)];
                for (var chain = 0; cand >= 0 && chain < MaxChain; chain++, cand = prev[cand])
                {
                    var dist = pos - cand;
                    if (dist > MaxDist) break;
                    var max = Math.Min(1028, src.Length - pos);
                    var len = 0;
                    while (len < max && src[cand + len] == src[pos + len]) len++;
                    if (len > bestLen && Usable(len, dist)) { bestLen = len; bestDist = dist; if (len >= 1028) break; }
                }
            }
            if (bestLen < 3) { Insert(pos); pos++; continue; }

            FlushLiterals(pos, out var lit);
            var len2 = bestLen;
            if (bestDist <= 1024 && len2 <= 10)
            {
                var d = bestDist - 1;
                body.Add((byte)((d >> 3 & 0x60) | (len2 - 3) << 2 | lit)); body.Add((byte)d);
            }
            else if (bestDist <= 16384 && len2 >= 4)
            {
                len2 = Math.Min(len2, 67); var d = bestDist - 1;
                body.Add((byte)(0x80 | len2 - 4)); body.Add((byte)(lit << 6 | d >> 8)); body.Add((byte)d);
            }
            else
            {
                len2 = Math.Min(len2, 1028); var d = bestDist - 1; var l = len2 - 5;
                body.Add((byte)(0xC0 | (d >> 16) << 4 | (l >> 8) << 2 | lit)); body.Add((byte)(d >> 8)); body.Add((byte)d); body.Add((byte)l);
            }
            for (var k = 0; k < lit; k++) body.Add(src[litStart + k]);
            for (var k = 0; k < len2; k++) Insert(pos + k);
            pos += len2;
            litStart = pos;
        }
        FlushLiterals(src.Length, out var tail);
        body.Add((byte)(0xFC | tail));
        for (var k = 0; k < tail; k++) body.Add(src[litStart + k]);

        var headLen = 2 + sizeBytes * (st.HasCompressedSize ? 2 : 1);
        var o = new byte[headLen + body.Count];
        o[0] = (byte)(st.Flags & 0xFE | (st.HasCompressedSize ? 1 : 0)); o[1] = 0xFB;
        var w = 2;
        void Size(int v) { for (var i = sizeBytes - 1; i >= 0; i--) o[w++] = (byte)(v >> 8 * i); }
        if (st.HasCompressedSize) Size(o.Length);
        Size(src.Length);
        body.CopyTo(o, headLen);
        return o;

        static bool Usable(int len, int dist) => dist <= 1024 ? len >= 3 : dist <= 16384 ? len >= 4 : len >= 5;
    }

    public static byte[] Decompress(ReadOnlySpan<byte> s)
    {
        if (!IsCompressed(s)) throw new InvalidDataException("Not RefPack data.");
        var outLen = GetDecompressedSize(s);
        var o = new byte[outLen];
        var i = HeaderSize(s);
        var w = 0;

        while (i < s.Length)
        {
            int b0 = s[i], lit, len, dist;
            if (b0 < 0x80)
            {
                int b1 = s[i + 1]; i += 2;
                lit = b0 & 3; len = ((b0 & 0x1C) >> 2) + 3; dist = ((b0 & 0x60) << 3) + b1 + 1;
            }
            else if (b0 < 0xC0)
            {
                int b1 = s[i + 1], b2 = s[i + 2]; i += 3;
                lit = b1 >> 6; len = (b0 & 0x3F) + 4; dist = ((b1 & 0x3F) << 8) + b2 + 1;
            }
            else if (b0 < 0xE0)
            {
                int b1 = s[i + 1], b2 = s[i + 2], b3 = s[i + 3]; i += 4;
                lit = b0 & 3; len = ((b0 & 0x0C) << 6) + b3 + 5; dist = ((b0 & 0x10) << 12) + (b1 << 8) + b2 + 1;
            }
            else if (b0 < 0xFC)
            {
                lit = ((b0 & 0x1F) << 2) + 4; i++;
                Copy(s, ref i, o, ref w, lit);
                continue;
            }
            else
            {
                lit = b0 & 3; i++;
                Copy(s, ref i, o, ref w, lit);
                break;
            }

            Copy(s, ref i, o, ref w, lit);
            if (dist > w) throw new InvalidDataException($"RefPack back-reference out of range at output 0x{w:X}.");
            for (var k = 0; k < len && w < o.Length; k++, w++) o[w] = o[w - dist];
        }

        if (w != outLen) Array.Resize(ref o, w);
        return o;
    }

    private static void Copy(ReadOnlySpan<byte> s, ref int i, byte[] o, ref int w, int n)
    {
        n = Math.Min(n, Math.Min(s.Length - i, o.Length - w));
        s.Slice(i, n).CopyTo(o.AsSpan(w));
        i += n; w += n;
    }
}
