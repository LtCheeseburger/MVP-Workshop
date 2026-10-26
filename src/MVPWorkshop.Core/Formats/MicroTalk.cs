namespace MVPWorkshop.Core.Formats;

/// <summary>
/// EA MicroTalk (MT10:1 / MT5:1, "UTK") speech decoder — CELP-style: 12 reflection coefficients per frame,
/// four 108-sample subframes of pitch + fixed excitation, 12th-order LPC synthesis, 432 samples per frame.
///
/// Verified against the game's own decoder in SLUS_215.82 (recompiled MIPS in config/output):
/// header parse 0x6824F0 (1-bit reduced bandwidth, 4-bit threshold → 32−t, 4-bit gain → 8·(g+1),
/// 6-bit multiplier → 1.04 + 0.001·m), frame decode 0x682678 (6/6/6/6 + 8×5-bit RC indices into the
/// table at 0x880EB0, ×0.25 interpolation; per subframe 8-bit pitch lag at 108·(i+2) − lag, 4-bit pitch
/// gain /15, 6-bit fixed-gain index), excitation 0x6820A8, bit reader 0x681FF0 (LSB-first).
/// Algorithm structure and tables follow vgmstream's utkdec.c (public domain/ISC), which matches the above.
/// </summary>
public sealed class MicroTalkDecoder
{
    private static readonly float[] RcTable =
    [
        +0.000000f, -0.996776f, -0.990327f, -0.983879f, -0.977431f, -0.970982f, -0.964534f, -0.958085f,
        -0.951637f, -0.930754f, -0.904960f, -0.879167f, -0.853373f, -0.827579f, -0.801786f, -0.775992f,
        -0.750198f, -0.724405f, -0.698611f, -0.670635f, -0.619048f, -0.567460f, -0.515873f, -0.464286f,
        -0.412698f, -0.361111f, -0.309524f, -0.257937f, -0.206349f, -0.154762f, -0.103175f, -0.051587f,
        +0.000000f, +0.051587f, +0.103175f, +0.154762f, +0.206349f, +0.257937f, +0.309524f, +0.361111f,
        +0.412698f, +0.464286f, +0.515873f, +0.567460f, +0.619048f, +0.670635f, +0.698611f, +0.724405f,
        +0.750198f, +0.775992f, +0.801786f, +0.827579f, +0.853373f, +0.879167f, +0.904960f, +0.930754f,
        +0.951637f, +0.958085f, +0.964534f, +0.970982f, +0.977431f, +0.983879f, +0.990327f, +0.996776f,
    ];

    private static readonly byte[][] Codebooks =
    [
        [
            4, 6, 5, 9, 4, 6, 5, 13, 4, 6, 5, 10, 4, 6, 5, 17, 4, 6, 5, 9, 4, 6, 5, 14, 4, 6, 5, 10, 4, 6, 5, 21,
            4, 6, 5, 9, 4, 6, 5, 13, 4, 6, 5, 10, 4, 6, 5, 18, 4, 6, 5, 9, 4, 6, 5, 14, 4, 6, 5, 10, 4, 6, 5, 25,
            4, 6, 5, 9, 4, 6, 5, 13, 4, 6, 5, 10, 4, 6, 5, 17, 4, 6, 5, 9, 4, 6, 5, 14, 4, 6, 5, 10, 4, 6, 5, 22,
            4, 6, 5, 9, 4, 6, 5, 13, 4, 6, 5, 10, 4, 6, 5, 18, 4, 6, 5, 9, 4, 6, 5, 14, 4, 6, 5, 10, 4, 6, 5, 0,
            4, 6, 5, 9, 4, 6, 5, 13, 4, 6, 5, 10, 4, 6, 5, 17, 4, 6, 5, 9, 4, 6, 5, 14, 4, 6, 5, 10, 4, 6, 5, 21,
            4, 6, 5, 9, 4, 6, 5, 13, 4, 6, 5, 10, 4, 6, 5, 18, 4, 6, 5, 9, 4, 6, 5, 14, 4, 6, 5, 10, 4, 6, 5, 26,
            4, 6, 5, 9, 4, 6, 5, 13, 4, 6, 5, 10, 4, 6, 5, 17, 4, 6, 5, 9, 4, 6, 5, 14, 4, 6, 5, 10, 4, 6, 5, 22,
            4, 6, 5, 9, 4, 6, 5, 13, 4, 6, 5, 10, 4, 6, 5, 18, 4, 6, 5, 9, 4, 6, 5, 14, 4, 6, 5, 10, 4, 6, 5, 2,
        ],
        [
            4, 11, 7, 15, 4, 12, 8, 19, 4, 11, 7, 16, 4, 12, 8, 23, 4, 11, 7, 15, 4, 12, 8, 20, 4, 11, 7, 16, 4, 12, 8, 27,
            4, 11, 7, 15, 4, 12, 8, 19, 4, 11, 7, 16, 4, 12, 8, 24, 4, 11, 7, 15, 4, 12, 8, 20, 4, 11, 7, 16, 4, 12, 8, 1,
            4, 11, 7, 15, 4, 12, 8, 19, 4, 11, 7, 16, 4, 12, 8, 23, 4, 11, 7, 15, 4, 12, 8, 20, 4, 11, 7, 16, 4, 12, 8, 28,
            4, 11, 7, 15, 4, 12, 8, 19, 4, 11, 7, 16, 4, 12, 8, 24, 4, 11, 7, 15, 4, 12, 8, 20, 4, 11, 7, 16, 4, 12, 8, 3,
            4, 11, 7, 15, 4, 12, 8, 19, 4, 11, 7, 16, 4, 12, 8, 23, 4, 11, 7, 15, 4, 12, 8, 20, 4, 11, 7, 16, 4, 12, 8, 27,
            4, 11, 7, 15, 4, 12, 8, 19, 4, 11, 7, 16, 4, 12, 8, 24, 4, 11, 7, 15, 4, 12, 8, 20, 4, 11, 7, 16, 4, 12, 8, 1,
            4, 11, 7, 15, 4, 12, 8, 19, 4, 11, 7, 16, 4, 12, 8, 23, 4, 11, 7, 15, 4, 12, 8, 20, 4, 11, 7, 16, 4, 12, 8, 28,
            4, 11, 7, 15, 4, 12, 8, 19, 4, 11, 7, 16, 4, 12, 8, 24, 4, 11, 7, 15, 4, 12, 8, 20, 4, 11, 7, 16, 4, 12, 8, 3,
        ],
    ];

    // (next model, code size, pulse value)
    private static readonly (int Next, int Size, float Pulse)[] Commands =
    [
        (1, 8, 0f), (1, 7, 0f), (0, 8, 0f), (0, 7, 0f), (0, 2, 0f), (0, 2, -1f), (0, 2, +1f), (0, 3, -1f), (0, 3, +1f),
        (1, 4, -2f), (1, 4, +2f), (1, 3, -2f), (1, 3, +2f), (1, 5, -3f), (1, 5, +3f), (1, 4, -3f), (1, 4, +3f),
        (1, 6, -4f), (1, 6, +4f), (1, 5, -4f), (1, 5, +4f), (1, 7, -5f), (1, 7, +5f), (1, 6, -5f), (1, 6, +5f),
        (1, 8, -6f), (1, 8, +6f), (1, 7, -6f), (1, 7, +6f),
    ];

    public const int FrameSamples = 432;

    private readonly bool _pcmFrames;
    private bool _parsedHeader, _reducedBandwidth;
    private int _threshold;
    private readonly float[] _gains = new float[64];
    private readonly float[] _rc = new float[12];
    private readonly float[] _history = new float[12];
    private readonly float[] _sub = new float[324 + 432]; // adaptive codebook (324) + samples (432)
    private const int Samples = 324;

    // bit reader
    private byte[] _buf = [];
    private int _pos, _end;
    private uint _bits;
    private int _bitCount;

    /// <param name="pcmFrames">Version-3 streams: each frame starts with a byte; 0xEE = raw PCM patch follows.</param>
    public MicroTalkDecoder(bool pcmFrames) => _pcmFrames = pcmFrames;

    /// <summary>Point the bit reader at new data (a new SCDl block); decoder state is kept.</summary>
    public void SetBuffer(byte[] buf, int start, int end)
    {
        _buf = buf; _pos = start; _end = Math.Min(end, buf.Length); _bitCount = 0;
    }

    public ReadOnlySpan<float> Output => _sub.AsSpan(Samples, FrameSamples);

    private byte ReadByte() => _pos < _end ? _buf[_pos++] : (byte)0;

    private int ReadS16() { var x = ReadByte() << 8 | ReadByte(); return (short)x; }

    private void InitBits() { if (_bitCount == 0) { _bits = ReadByte(); _bitCount = 8; } }

    private int Peek(int n) => (int)(_bits & ((1u << n) - 1));

    private int Read(int n)
    {
        var r = (int)(_bits & ((1u << n) - 1));
        _bits >>= n; _bitCount -= n;
        if (_bitCount < 8) { _bits |= (uint)ReadByte() << _bitCount; _bitCount += 8; }
        return r;
    }

    private void ParseHeader()
    {
        _reducedBandwidth = Read(1) == 1;
        var thre = Read(4); var gain = Read(4); var mult = Read(6);
        _threshold = 32 - thre;
        _gains[0] = 8f * (1 + gain);
        var m = 1.04f + mult * 0.001f;
        for (var i = 1; i < 64; i++) _gains[i] = _gains[i - 1] * m;
    }

    /// <summary>Decode one frame into <see cref="Output"/> (432 samples, float PCM scale).</summary>
    public void DecodeFrame()
    {
        if (!_pcmFrames) { DecodeMain(); return; }
        var pcm = ReadByte() == 0xEE;
        DecodeMain();
        // un-read the bit reader's look-ahead byte; frames are byte-aligned in this mode
        _pos--; _bitCount = 0;
        if (!pcm) return;
        int offset = ReadS16(), count = ReadS16();
        if (offset is < 0 or > FrameSamples || count < 0 || count > FrameSamples - offset) throw new InvalidDataException("Bad MicroTalk PCM patch.");
        for (var i = 0; i < count; i++) _sub[Samples + offset + i] = ReadS16();
    }

    private void DecodeMain()
    {
        InitBits();
        if (!_parsedHeader) { ParseHeader(); _parsedHeader = true; }

        var multipulse = false;
        Span<float> rcDelta = stackalloc float[12];
        for (var i = 0; i < 12; i++)
        {
            int idx;
            if (i == 0) { idx = Read(6); multipulse = idx < _threshold; }
            else if (i < 4) idx = Read(6);
            else idx = 16 + Read(5);
            rcDelta[i] = (RcTable[idx] - _rc[i]) * 0.25f;
        }

        Span<float> exc = stackalloc float[5 + 108 + 5];
        for (var i = 0; i < 4; i++)
        {
            var lag = Read(8);
            var pitchGain = Read(4) / 15f;
            var fixedGain = _gains[Read(6)];
            if (!_reducedBandwidth)
                Excitation(multipulse, exc, 5, 1);
            else
            {
                var align = Read(1);
                var zero = Read(1);
                Excitation(multipulse, exc, 5 + align, 2);
                if (zero == 1)
                    for (var j = 0; j < 54; j++) exc[5 + (1 - align) + 2 * j] = 0;
                else
                {
                    exc[..5].Clear();
                    exc[(5 + 108)..].Clear();
                    // interpolate the skipped samples (low-pass), then halve the gain
                    var b = 5 + (1 - align);
                    for (var k = 0; k < 108; k += 2)
                        exc[b + k] = (exc[b + k - 5] + exc[b + k + 5]) * 0.01803268f
                                   - (exc[b + k - 3] + exc[b + k + 3]) * 0.11459156f
                                   + (exc[b + k - 1] + exc[b + k + 1]) * 0.59738597f;
                    fixedGain *= 0.5f;
                }
            }
            for (var j = 0; j < 108; j++)
            {
                var idx = Math.Max(0, 108 * i + 216 - lag + j);
                _sub[Samples + 108 * i + j] = fixedGain * exc[5 + j] + pitchGain * _sub[idx];
            }
        }

        Array.Copy(_sub, Samples + 108, _sub, 0, 324);

        Span<float> lpc = stackalloc float[12];
        for (var i = 0; i < 4; i++)
        {
            for (var j = 0; j < 12; j++) _rc[j] += rcDelta[j];
            RcToLpc(_rc, lpc);
            var blocks = i < 3 ? 1 : 33;
            var p = Samples + 12 * i;
            for (var b = 0; b < blocks; b++)
                for (var j = 0; j < 12; j++)
                {
                    var x = _sub[p];
                    int k;
                    for (k = 0; k < j; k++) x += lpc[k] * _history[k - j + 12];
                    for (; k < 12; k++) x += lpc[k] * _history[k - j];
                    _history[11 - j] = x;
                    _sub[p++] = x;
                }
        }
    }

    private void Excitation(bool multipulse, Span<float> outp, int start, int stride)
    {
        var i = 0;
        if (multipulse)
        {
            var model = 0;
            while (i < 108)
            {
                var cmd = Codebooks[model][Peek(8)];
                model = Commands[cmd].Next;
                Read(Commands[cmd].Size);
                if (cmd > 3) { outp[start + i] = Commands[cmd].Pulse; i += stride; }
                else if (cmd > 1)
                {
                    var count = 7 + Read(6);
                    if (i + count * stride > 108) count = (108 - i) / stride;
                    while (count-- > 0) { outp[start + i] = 0; i += stride; }
                }
                else
                {
                    var x = 7;
                    while (Read(1) == 1) x++;
                    if (Read(1) == 0) x = -x;
                    outp[start + i] = x; i += stride;
                }
            }
        }
        else
        {
            while (i < 108)
            {
                var code = Peek(2);
                (float v, int bits) = code switch { 1 => (-2f, 2), 3 => (2f, 2), _ => (0f, 1) };
                Read(bits);
                outp[start + i] = v; i += stride;
            }
        }
    }

    private static void RcToLpc(float[] rc, Span<float> lpc)
    {
        Span<float> t1 = stackalloc float[12];
        Span<float> t2 = stackalloc float[12];
        for (var i = 10; i >= 0; i--) t2[i + 1] = rc[i];
        t2[0] = 1;
        for (var i = 0; i < 12; i++)
        {
            var x = -(rc[11] * t2[11]);
            for (var j = 10; j >= 0; j--)
            {
                x -= rc[j] * t2[j];
                t2[j + 1] = x * rc[j] + t2[j];
            }
            t2[0] = x;
            t1[i] = x;
            for (var j = 0; j < i; j++) x -= t1[i - 1 - j] * lpc[j];
            lpc[i] = x;
        }
    }

    public static short ToPcm(float f)
    {
        var v = (int)MathF.Round(f);
        return (short)Math.Clamp(v, short.MinValue, short.MaxValue);
    }
}
