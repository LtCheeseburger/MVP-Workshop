namespace MVPWorkshop.Core.Formats;

/// <summary>
/// EA MicroTalk 10:1 revision-3 encoder with per-block byte budgets.
///
/// Analysis follows the public-domain utkencode by Fatbag (daddesio/utkencode @ e0b13fac): 12 reflection
/// coefficients per 432-sample frame, four 108-sample subframes of pitch + Huffman/triangular innovation.
/// Revision-3 framing is native: each frame is a 0x00 marker byte followed by byte-aligned bits, and the
/// 15-bit stream header precedes the first frame only.
///
/// Rate control is per SCDl block rather than per utterance. Every frame of a block is encoded from a snapshot
/// of the encoder state at one innovation bit target; the largest target whose byte-aligned frames fit the
/// block's fixed capacity is committed. Because a block is encoded and committed as a unit, the adaptive
/// codebook and reflection-coefficient history always equal what the decoder reconstructs from the committed
/// bits — rate changes at block boundaries cannot desynchronise pitch/LPC/gain state.
/// </summary>
public sealed class MicroTalkEncoder
{
    public const int FrameSamples = 432;
    private const int HuffmanThreshold = 24, GainSignificand = 64, GainBaseMilli = 28; // utkencode defaults: base 1.068 = 1.04 + 0.028
    private const bool Halved = true;
    private const int ZeroInnovationTargetLimit = 32;

    private static readonly float[] RcTable =
    [
        0f,
        -.99677598476409912109375f, -.99032700061798095703125f, -.983879029750823974609375f, -.977430999279022216796875f,
        -.970982015132904052734375f, -.964533984661102294921875f, -.958085000514984130859375f, -.9516370296478271484375f,
        -.930754005908966064453125f, -.904959976673126220703125f, -.879167020320892333984375f, -.853372991085052490234375f,
        -.827579021453857421875f, -.801786005496978759765625f, -.775991976261138916015625f, -.75019800662994384765625f,
        -.724404990673065185546875f, -.6986110210418701171875f, -.6706349849700927734375f, -.61904799938201904296875f,
        -.567460000514984130859375f, -.515873014926910400390625f, -.4642859995365142822265625f, -.4126980006694793701171875f,
        -.361110985279083251953125f, -.309523999691009521484375f, -.257937014102935791015625f, -.20634900033473968505859375f,
        -.1547619998455047607421875f, -.10317499935626983642578125f, -.05158700048923492431640625f,
        0f,
        +.05158700048923492431640625f, +.10317499935626983642578125f, +.1547619998455047607421875f, +.20634900033473968505859375f,
        +.257937014102935791015625f, +.309523999691009521484375f, +.361110985279083251953125f, +.4126980006694793701171875f,
        +.4642859995365142822265625f, +.515873014926910400390625f, +.567460000514984130859375f, +.61904799938201904296875f,
        +.6706349849700927734375f, +.6986110210418701171875f, +.724404990673065185546875f, +.75019800662994384765625f,
        +.775991976261138916015625f, +.801786005496978759765625f, +.827579021453857421875f, +.853372991085052490234375f,
        +.879167020320892333984375f, +.904959976673126220703125f, +.930754005908966064453125f, +.9516370296478271484375f,
        +.958085000514984130859375f, +.964533984661102294921875f, +.970982015132904052734375f, +.977430999279022216796875f,
        +.983879029750823974609375f, +.99032700061798095703125f, +.99677598476409912109375f,
    ];

    // Huffman codes for values -13..+13, per model (bits value, bit count), LSB-first.
    private static readonly (int Value, int Count)[][] Huffman =
    [
        [
            (16255, 16), (8063, 15), (3967, 14), (1919, 13), (895, 12), (383, 11), (127, 10), (63, 8), (31, 7), (15, 6), (7, 5), (3, 4), (2, 2),
            (0, 2),
            (1, 2), (11, 4), (23, 5), (47, 6), (95, 7), (191, 8), (639, 10), (1407, 11), (2943, 12), (6015, 13), (12159, 14), (24447, 15), (49023, 16),
        ],
        [
            (8127, 15), (4031, 14), (1983, 13), (959, 12), (447, 11), (191, 10), (63, 9), (31, 7), (15, 6), (7, 5), (3, 4), (1, 3), (2, 3),
            (0, 2),
            (6, 3), (5, 3), (11, 4), (23, 5), (47, 6), (95, 7), (319, 9), (703, 10), (1471, 11), (3007, 12), (6079, 13), (12223, 14), (24511, 15),
        ],
    ];

    private static readonly float[] Gains = BuildGains();

    private static float[] BuildGains()
    {
        var g = new float[64];
        g[0] = GainSignificand;
        var b = 1.04f + GainBaseMilli * 0.001f;
        for (var i = 1; i < 64; i++) g[i] = g[i - 1] * b;
        return g;
    }

    /// <summary>State the decoder mirrors; snapshot/restored around every block trial.</summary>
    private sealed class State
    {
        public float[] InputHistory = new float[12];
        public float[] Adaptive = new float[324];
        public float[] PrevRc = new float[12];
        public bool HeaderWritten;
        public State Clone() => new() { InputHistory = (float[])InputHistory.Clone(), Adaptive = (float[])Adaptive.Clone(), PrevRc = (float[])PrevRc.Clone(), HeaderWritten = HeaderWritten };
    }

    private State _state = new();
    private MicroTalkDecoder _decoder = new(pcmFrames: true);

    // Reflection-index clamps tried in order when a block decodes unstably (|rc| up to 0.997, 0.971, 0.952, 0.828).
    // Open-loop analysis cannot see the decoder's synthesis gain: at onsets after silence, near-unity coefficients
    // amplify innovation error into clipping. Each block is therefore decoded through a mirror of the real decoder.
    private static readonly int[] RcLimits = [1, 5, 9, 13];

    /// <summary>Innovation bits per subframe at the top of the search (~64 kb/s at 22,050 Hz).</summary>
    public int MaxInnovationTarget { get; init; } = 300;

    /// <param name="ForcedHuffman">The block only fit after clamping the first reflection coefficient into the Huffman
    /// (rate-controllable) range; spectral tilt of noise-like frames in this block is approximated.</param>
    public sealed record BlockResult(byte[] Payload, int Frames, int Capacity, int InnovationTarget, bool ForcedHuffman, int RcLimit = 1, double SnrDb = double.NaN);

    /// <summary>
    /// Encode one native block: <paramref name="samples"/> are the block's PCM samples (the decoder emits exactly this
    /// many), <paramref name="lookahead"/> any following samples used only to fill the final partial frame, and
    /// <paramref name="capacity"/> the bytes available after the block's flag byte. Commits encoder state on success.
    /// </summary>
    public BlockResult EncodeBlock(ReadOnlySpan<short> samples, ReadOnlySpan<short> lookahead, int capacity)
    {
        if (samples.Length == 0) return new([], 0, capacity, 0, false);
        var frames = (samples.Length + FrameSamples - 1) / FrameSamples;
        var input = new float[frames * FrameSamples];
        for (var i = 0; i < input.Length; i++)
            input[i] = i < samples.Length ? samples[i] : i - samples.Length < lookahead.Length ? lookahead[i - samples.Length] : 0f;
        // The decoder resumes at the next block's first sample; keep LPC analysis history aligned to it.
        var tail = new float[12]; var count = samples.Length;
        for (var i = 0; i < 12; i++) { var at = samples.Length - 12 + i; tail[i] = at >= 0 ? samples[at] : 0f; }

        double signal = 0; for (var i = 0; i < count; i++) signal += (double)input[i] * input[i];

        (byte[] Bytes, State After)? Trial(int target, bool force, int rcLimit)
        {
            var s = _state.Clone();
            var bytes = new List<byte>(capacity + 64);
            for (var f = 0; f < frames; f++)
            {
                EncodeFrame(s, input.AsSpan(f * FrameSamples, FrameSamples), target, force, rcLimit, bytes);
                if (bytes.Count > capacity) return null;
            }
            if (count < input.Length) Array.Copy(tail, s.InputHistory, 12);
            return (bytes.ToArray(), s);
        }

        // Decode a candidate exactly as the game will (state carried from committed blocks) and measure its error.
        (double Error, MicroTalkDecoder After) Decode(byte[] bytes)
        {
            var d = _decoder.Clone(); d.SetBuffer(bytes, 0, bytes.Length); double error = 0;
            for (int f = 0, done = 0; f < frames; f++)
            {
                d.DecodeFrame(); var take = Math.Min(FrameSamples, count - done);
                for (var i = 0; i < take; i++) { double e = MicroTalkDecoder.ToPcm(d.Output[i]) - input[done + i]; error += e * e; }
                done += take;
            }
            return (error, d);
        }

        (byte[] Bytes, State After, int Target, bool Forced, int Limit, double Error, MicroTalkDecoder Decoder)? chosen = null;
        foreach (var limit in RcLimits)
        {
            var forced = false;
            var best = Trial(0, false, limit);
            if (best is null) { forced = true; best = Trial(0, true, limit); }
            if (best is null) continue;
            int lo = 0, hi = MaxInnovationTarget, bestTarget = 0;
            while (lo < hi)
            {
                var mid = (lo + hi + 1) / 2;
                if (Trial(mid, forced, limit) is { } ok) { best = ok; bestTarget = mid; lo = mid; }
                else hi = mid - 1;
            }
            var (error, decoder) = Decode(best.Value.Bytes);
            if (chosen is null || error < chosen.Value.Error) chosen = (best.Value.Bytes, best.Value.After, bestTarget, forced, limit, error, decoder);
            // Stable: decoded SNR of at least 3 dB, or inaudibly small error for near-silent blocks.
            if (error <= signal / 2 || error <= count * 64.0 * 64.0) break;
        }
        if (chosen is not { } c) throw new InvalidOperationException($"MicroTalk block of {count} samples cannot fit {capacity} bytes even at the minimum rate.");
        _state = c.After; _decoder = c.Decoder;
        return new(c.Bytes, frames, capacity, c.Target, c.Forced, c.Limit, signal <= count * 64.0 * 64.0 ? double.NaN : 10 * Math.Log10(signal / Math.Max(c.Error, 1))); // NaN: near-silent block
    }

    private static void EncodeFrame(State s, ReadOnlySpan<float> frame, int innovationTarget, bool forceHuffman, int rcLimit, List<byte> output)
    {
        var w = new BitWriter();
        if (!s.HeaderWritten)
        {
            w.Write(Halved ? 1 : 0, 1);
            w.Write(32 - HuffmanThreshold, 4);
            w.Write(GainSignificand / 8 - 1, 4);
            w.Write(GainBaseMilli, 6);
            s.HeaderWritten = true;
        }

        var input = new float[12 + FrameSamples];
        Array.Copy(s.InputHistory, input, 12);
        for (var i = 0; i < FrameSamples; i++) input[12 + i] = frame[i];

        var rc = FindRc(input.AsSpan(12));
        var useHuffman = false;
        for (var i = 0; i < 4; i++)
        {
            var idx = 1 + Quantize(rc[i], RcTable.AsSpan(1, 63));
            idx = Math.Clamp(idx, rcLimit, 64 - rcLimit);
            if (i == 0 && forceHuffman) idx = Math.Min(idx, HuffmanThreshold - 1);
            w.Write(idx, 6);
            rc[i] = RcTable[idx];
            if (i == 0 && idx < HuffmanThreshold) useHuffman = true;
        }
        for (var i = 4; i < 12; i++)
        {
            var idx = Quantize(rc[i], RcTable.AsSpan(16, 32));
            w.Write(idx, 5);
            rc[i] = RcTable[16 + idx];
        }

        var delta = new float[12];
        for (var i = 0; i < 12; i++) delta[i] = (rc[i] - s.PrevRc[i]) / 4f;
        var cur = (float[])s.PrevRc.Clone();

        var adaptive = new float[324 + FrameSamples];
        Array.Copy(s.Adaptive, adaptive, 324);
        var lpc = new float[12];
        for (var i = 0; i < 4; i++)
        {
            for (var j = 0; j < 12; j++) cur[j] += delta[j];
            RcToLpc(lpc, cur);
            FindExcitation(adaptive, 324 + 12 * i, input, 12 + 12 * i, i < 3 ? 12 : 396, lpc);
        }
        Array.Copy(input, FrameSamples, s.InputHistory, 0, 12);
        s.PrevRc = cur;

        var innovation = new float[5 + 108 + 5];
        for (var i = 0; i < 4; i++)
        {
            var ex = 324 + 108 * i;
            FindPitch(adaptive, ex, out var lag, out var pitchGain);
            w.Write(lag - 108, 8);
            var gi = Round(pitchGain * 15f);
            w.Write(gi, 4);
            pitchGain = gi / 15f;
            for (var j = 0; j < 108; j++) innovation[5 + j] = adaptive[ex + j] - pitchGain * adaptive[ex + j - lag];
            EncodeInnovation(w, innovation, useHuffman, innovationTarget);
            for (var j = 0; j < 108; j++) adaptive[ex + j] = innovation[5 + j] + pitchGain * adaptive[ex + j - lag];
        }
        Array.Copy(adaptive, FrameSamples, s.Adaptive, 0, 324);

        output.Add(0x00); // revision-3 marker: compressed frame, no PCM patch
        output.AddRange(w.ToArray());
    }

    private static void EncodeInnovation(BitWriter w, float[] inn, bool useHuffman, int target)
    {
        int a = 0, z = 1;
        if (Halved) { FindAzFlags(inn, out a, out z); LowPass(inn, a, z); }
        var interval = Halved ? 2 : 1;
        BitWriter? bestBits = null; float[]? bestOut = null;

        if (useHuffman)
        {
            var max = 0f;
            for (var i = a; i < 108; i += interval) max = Math.Max(max, Math.Abs(inn[5 + i]));
            int p;
            for (p = 62; p >= 0; p--) if (Gains[p] * (z == 0 ? 0.5f : 1f) * 13.5f < max) break;
            var minPow = p + 1;
            var bestDistance = 0;
            for (var pow = minPow; pow <= 63; pow++)
            {
                var cand = new BitWriter(); var o = new float[108];
                var used = EncodeHuffman(cand, o, inn, pow, a, z, out _);
                var d = Math.Abs(used - target);
                if (pow == minPow || d < bestDistance) { bestDistance = d; bestBits = cand; bestOut = o; }
            }
            // Starved budgets: an all-zero innovation (one zero run) is the cheapest legal subframe. Only offered at targets
            // far below any standard bitrate, so default-rate output stays identical to utkencode.
            if (target < ZeroInnovationTargetLimit)
            {
                var cand = new BitWriter(); var o = new float[108];
                // a=0, z=1: one 14-bit run covers every even position (a=1 costs an extra symbol at 107, see above).
                var used = Halved ? EncodeHuffman(cand, o, new float[inn.Length], 63, 0, 1, out _) : EncodeHuffman(cand, o, new float[inn.Length], 63, a, z, out _);
                if (Math.Abs(used - target) < bestDistance) { bestBits = cand; bestOut = o; if (Halved) { a = 0; z = 1; } }
            }
        }
        else
        {
            // Highest quality whose bits meet the target; otherwise the fewest bits.
            float bestError = float.MaxValue; var fewest = int.MaxValue; var anyFits = false;
            for (var pow = 0; pow <= 63; pow++)
            {
                var cand = new BitWriter(); var o = new float[108];
                var used = Triangular(cand, o, inn, pow, a, z, out var err);
                var fits = used <= target;
                if (fits && (!anyFits || err < bestError)) { anyFits = true; bestError = err; bestBits = cand; bestOut = o; }
                else if (!anyFits && used < fewest) { fewest = used; bestBits = cand; bestOut = o; }
            }
        }

        w.Append(bestBits!);
        Array.Copy(bestOut!, 0, inn, 5, 108);
        if (Halved) Interpolate(inn, a, z);
    }

    private static int EncodeHuffman(BitWriter w, float[] o, float[] inn, int pow, int a, int z, out float error)
    {
        var interval = Halved ? 2 : 1;
        var gain = Gains[pow] * (z == 0 ? 0.5f : 1f);
        var start = w.Bits;
        if (Halved) w.Write(pow | a << 6 | z << 7, 8); else w.Write(pow, 6);
        var values = new int[108]; var zeros = new int[108]; error = 0;
        for (var i = a; i < 108; i += interval)
        {
            values[i] = Round(Math.Clamp(inn[5 + i] / gain, -13f, 13f));
            o[i] = gain * values[i];
            var e = o[i] - inn[5 + i]; error += e * e;
        }
        // Zero runs scanned from 108 - interval - a, reproducing the decoder's halved off-by-one.
        var counter = 0;
        for (var i = 108 - interval - a; i >= 0; i -= interval) { counter = values[i] == 0 ? counter + 1 : 0; zeros[i] = counter; }
        int k = a, model = 0;
        while (k < 108)
        {
            if (zeros[k] >= 7)
            {
                var len = Math.Min(zeros[k], 70);
                if (model == 0) w.Write(255 | (len - 7) << 8, 14); else w.Write(127 | (len - 7) << 7, 13);
                model = 0; k += len * interval;
            }
            else
            {
                var v = values[k];
                var code = Huffman[model][13 + v];
                w.Write(code.Value, code.Count);
                model = v is < -1 or > 1 ? 1 : 0;
                k += interval;
            }
        }
        return w.Bits - start;
    }

    private static int Triangular(BitWriter w, float[] o, float[] inn, int pow, int a, int z, out float error)
    {
        var interval = Halved ? 2 : 1;
        var gain = 2f * Gains[pow] * (z == 0 ? 0.5f : 1f);
        var start = w.Bits;
        if (Halved) w.Write(pow | a << 6 | z << 7, 8); else w.Write(pow, 6);
        error = 0;
        for (var i = a; i < 108; i += interval)
        {
            var v = Round(Math.Clamp(inn[5 + i] / gain, -1f, 1f));
            if (v > 0) w.Write(3, 2); else if (v < 0) w.Write(1, 2); else w.Write(0, 1);
            o[i] = gain * v;
            var e = o[i] - inn[5 + i]; error += e * e;
        }
        return w.Bits - start;
    }

    private static void FindAzFlags(float[] x, out int a, out int z)
    {
        int ba = 0, bz = 1; var best = InterpolationError(0, 1, x);
        foreach (var (ta, tz) in new[] { (1, 1), (0, 0), (1, 0) })
        {
            var e = InterpolationError(ta, tz, x);
            if (e < best) { best = e; ba = ta; bz = tz; }
        }
        a = ba; z = bz;
    }

    private static float InterpolationError(int a, int z, float[] x)
    {
        var error = 0f;
        for (var i = 5 + (a == 0 ? 1 : 0); i < 5 + 108; i += 2)
        {
            if (z == 1) { error += x[i] * x[i]; continue; }
            var p = (x[i - 1] + x[i + 1]) * .5973859429f - (x[i - 3] + x[i + 3]) * .1145915613f + (x[i - 5] + x[i + 5]) * .0180326793f;
            error += (p - x[i]) * (p - x[i]);
        }
        return error;
    }

    private static void Interpolate(float[] x, int a, int z)
    {
        for (var i = 5 + (a == 0 ? 1 : 0); i < 5 + 108; i += 2)
            x[i] = z == 1 ? 0f : (x[i - 1] + x[i + 1]) * .5973859429f - (x[i - 3] + x[i + 3]) * .1145915613f + (x[i - 5] + x[i + 5]) * .0180326793f;
    }

    private static void LowPass(float[] x, int a, int z)
    {
        for (var i = 5 + a; i < 5 + 108; i += 2)
            x[i] = (z == 1 ? 1f : 0.5f) * (x[i] + (x[i - 1] + x[i + 1]) * 0.6189590521549956f + (x[i - 3] + x[i + 3]) * -0.1633990749076792f + (x[i - 5] + x[i + 5]) * 0.05858453198856907f);
    }

    private static float[] FindRc(ReadOnlySpan<float> samples)
    {
        var r = new float[13];
        for (var i = 0; i < 13; i++) { var acc = 0f; for (var j = 0; j < FrameSamples - i; j++) acc += samples[j] * samples[j + i]; r[i] = acc; }
        var k = new float[12];
        if (Math.Abs(r[0]) <= 1f / 32768f) return k;
        var aVec = new float[12]; var x = new float[12];
        aVec[0] = 1; var e = r[0]; x[0] = r[1] / r[0];
        for (var i = 1; i < 12; i++)
        {
            var u = 0f;
            for (var j = 0; j < i; j++) u += aVec[j] * r[i - j];
            k[i - 1] = -u / e;
            e += u * k[i - 1];
            if (Math.Abs(e) <= 1f / 32768f) return new float[12];
            var tmp = new float[i]; Array.Copy(aVec, tmp, i);
            aVec[i] = 0;
            for (var j = 1; j <= i; j++) aVec[j] += k[i - 1] * tmp[i - j];
            var m = r[1 + i];
            for (var j = 0; j < i; j++) m -= x[j] * r[i - j];
            m /= e;
            x[i] = 0;
            for (var j = 0; j <= i; j++) x[j] += m * aVec[i - j];
        }
        k[11] = -x[11];
        return k;
    }

    private static void RcToLpc(float[] lpc, float[] k)
    {
        var a = new float[13]; a[0] = 1;
        for (var i = 1; i < 13; i++)
        {
            var tmp = new float[i]; Array.Copy(a, tmp, i);
            a[i] = 0;
            for (var j = 1; j <= i; j++) a[j] += k[i - 1] * tmp[i - j];
        }
        for (var i = 1; i < 13; i++) lpc[i - 1] = -a[i];
    }

    private static void FindExcitation(float[] dst, int dstAt, float[] src, int srcAt, int length, float[] lpc)
    {
        for (var i = 0; i < length; i++)
        {
            var prediction = 0f;
            for (var j = 0; j < 12; j++) prediction += lpc[j] * src[srcAt + i - 1 - j];
            dst[dstAt + i] = src[srcAt + i] - prediction;
        }
    }

    private static void FindPitch(float[] x, int at, out int lag, out float gain)
    {
        int bestLag = 108; var bestCorr = 0f;
        for (var i = 108; i < 324; i++)
        {
            var c = 0f;
            for (var j = 0; j < 108; j++) c += x[at + j] * x[at + j - i];
            if (c > bestCorr) { bestLag = i; bestCorr = c; }
        }
        var energy = 0f;
        for (var i = 0; i < 108; i++) { var v = x[at + i - bestLag]; energy += v * v; }
        if (energy >= 1f / 32768f) { lag = bestLag; gain = Math.Clamp(bestCorr / energy, 0f, 1f); }
        else { lag = 108; gain = 0f; }
    }

    private static int Quantize(float value, ReadOnlySpan<float> alphabet)
    {
        var best = 0; var bestD = Math.Abs(value - alphabet[0]);
        for (var i = 1; i < alphabet.Length; i++) { var d = Math.Abs(value - alphabet[i]); if (d < bestD) { bestD = d; best = i; } }
        return best;
    }

    private static int Round(float x) => (int)(x >= 0 ? x + 0.5 : x - 0.5); // utkencode ROUND adds a double 0.5

    /// <summary>LSB-first bit writer matching the decoder's reader.</summary>
    private sealed class BitWriter
    {
        private readonly List<byte> _bytes = [0];
        private int _used; // bits used in the last byte
        public int Bits => (_bytes.Count - 1) * 8 + _used;

        public void Write(int value, int count)
        {
            for (var i = 0; i < count; i++)
            {
                if (_used == 8) { _bytes.Add(0); _used = 0; }
                if (((value >> i) & 1) != 0) _bytes[^1] |= (byte)(1 << _used);
                _used++;
            }
        }

        public void Append(BitWriter other)
        {
            var total = other.Bits;
            for (var i = 0; i < total; i++) Write((other._bytes[i >> 3] >> (i & 7)) & 1, 1);
        }

        public byte[] ToArray() => _used == 0 ? _bytes.Take(_bytes.Count - 1).ToArray() : _bytes.ToArray();
    }
}
