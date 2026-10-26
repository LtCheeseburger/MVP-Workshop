using System.Collections.Concurrent;

namespace MVPWorkshop.Core.Formats;

/// <summary>
/// Forward (linear → GS memory order) swizzles. Rather than re-deriving each layout by hand they invert the existing, verified
/// un-swizzlers: both are pure permutations of pixels, so feeding an un-swizzler position-coded probe images reveals the permutation exactly.
/// A layout that is not a clean bijection for the requested size is rejected instead of producing damaged data.
/// </summary>
public static partial class Ps2Swizzle
{
    private static readonly ConcurrentDictionary<(int Bpp, int W, int H), int[]> Permutations = new();

    /// <summary>Linear 8bpp indices → swizzled bytes (inverse of <see cref="Unswizzle8"/>).</summary>
    public static byte[] Swizzle8(ReadOnlySpan<byte> linear, int w, int h)
    {
        var perm = Permutation(8, w, h);
        var o = new byte[w * h];
        for (var k = 0; k < perm.Length; k++) o[perm[k]] = k < linear.Length ? linear[k] : (byte)0;
        return o;
    }

    /// <summary>Linear packed 4bpp (low nibble first) → swizzled packed bytes (inverse of <see cref="Unswizzle4"/>).</summary>
    public static byte[] Swizzle4(ReadOnlySpan<byte> linear, int w, int h)
    {
        var perm = Permutation(4, w, h);
        var o = new byte[(w * h + 1) / 2];
        for (var k = 0; k < perm.Length; k++)
        {
            var v = k / 2 < linear.Length ? (k & 1) == 0 ? linear[k / 2] & 0x0F : linear[k / 2] >> 4 : 0;
            var d = perm[k];
            if ((d & 1) == 0) o[d / 2] = (byte)(o[d / 2] & 0xF0 | v); else o[d / 2] = (byte)(o[d / 2] & 0x0F | v << 4);
        }
        return o;
    }

    /// <summary>perm[k] = position in swizzled storage (in pixels) that holds linear pixel k.</summary>
    private static int[] Permutation(int bpp, int w, int h) => Permutations.GetOrAdd((bpp, w, h), key =>
    {
        var n = w * h;
        if (n <= 0) throw new NotSupportedException("Empty image.");
        var perm = new int[n];
        if (bpp == 8)
        {
            for (var t = 0; t < 4 && n > 1L << 8 * t; t++)
            {
                var probe = new byte[n];
                for (var i = 0; i < n; i++) probe[i] = (byte)(i >> 8 * t);
                var un = Unswizzle8(probe, w, h);
                for (var k = 0; k < n; k++) perm[k] |= un[k] << 8 * t;
            }
        }
        else
        {
            var bytes = (n + 1) / 2;
            for (var t = 0; t < 8 && n > 1L << 4 * t; t++)
            {
                var probe = new byte[bytes];
                for (var i = 0; i < n; i++)
                {
                    var v = (i >> 4 * t) & 0xF;
                    probe[i / 2] = (i & 1) == 0 ? (byte)(probe[i / 2] & 0xF0 | v) : (byte)(probe[i / 2] & 0x0F | v << 4);
                }
                var un = Unswizzle4(probe, w, h);
                for (var k = 0; k < n; k++)
                {
                    var v = (k & 1) == 0 ? un[k / 2] & 0xF : un[k / 2] >> 4;
                    perm[k] |= v << 4 * t;
                }
            }
        }
        var seen = new bool[n];
        foreach (var p in perm)
        {
            if (p < 0 || p >= n || seen[p]) throw new NotSupportedException($"The {bpp}bpp swizzle for {w}×{h} is not a clean permutation, so it cannot be re-applied.");
            seen[p] = true;
        }
        return perm;
    });
}
