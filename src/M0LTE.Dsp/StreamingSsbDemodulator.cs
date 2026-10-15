namespace M0LTE.Dsp;

/// <summary>
/// The block-at-a-time form of <see cref="SsbDemodulator"/>: complex baseband in, real SSB
/// audio out at the requested rate, in constant memory and at a fraction of the arithmetic. This
/// is what an unbounded stream needs — an hour-long capture being scored offline, or a live SDR
/// feeding a demodulator for as long as the process runs.
/// </summary>
/// <remarks>
/// <para><b>Why this exists.</b> <see cref="SsbDemodulator"/> allocates six arrays the length of
/// the whole signal and runs a 301-tap complex FIR at the <em>input</em> rate. An hour of 48 kHz
/// IQ is 172.8 M complex samples — several 1.4 GB <c>double[]</c> and 14 M complex MACs per
/// second of signal. It exhausts memory well before it becomes slow, so it cannot be the form a
/// long capture or a live receiver runs through.</para>
/// <para><b>What makes it cheap.</b> The reference chain is: NCO → complex SSB bandpass (<c>h</c>,
/// 301 taps) → take the real part → real anti-alias low-pass (<c>g</c>, 41 taps) → keep 1 in 5.
/// Because <c>g</c> is real, <c>g * Re{y} = Re{g * y}</c>, so the two filters can be collapsed
/// into a single complex kernel <c>e = h * reverse(g)</c> of 341 taps whose real part is taken at
/// the very end. That kernel is then evaluated <em>only at output instants</em> — decimating
/// filters cost taps-per-output, not taps-per-input — which is 341 complex MACs per 9600 Hz
/// output against 301 per 48000 Hz input plus the low-pass, a little over 4× less work. Nothing
/// is approximated: this is the same linear filter, evaluated where its result is wanted.</para>
/// <para><b>Single precision.</b> The kernel is designed in double and then held, with the
/// sample history, in float, and each output is one <see cref="System.Numerics.Vector{T}"/> dot
/// product: the history is stored as interleaved (re, im) pairs and the kernel as (Re e, -Im e),
/// so the real part of the complex product is a plain sum of products, and every sample is
/// written twice, one ring apart, so that any window is one contiguous run. The NCO, where there
/// is one, still steps its phasor in double. Against the double-precision reference the worst
/// output sample differs by about 3e-7 of the signal's peak, measured on x64 and arm64, and a
/// test holds it under 2e-6 (-114 dB): a rounding change, below the quantisation of 16-bit IQ.
/// Against the same loop in scalar double it is roughly 11 times faster on an x64 with AVX2 and
/// 6 times on a Raspberry Pi 4, where audio at a 48 kS/s IQ rate now costs about 2%
/// of a core rather than 11%.</para>
/// <para>The output is sample-for-sample the reference's, to within that rounding, and a test
/// asserts exactly that, so the two implementations are not allowed to drift apart.
/// <see cref="SsbDemodulatorOptions.NormalisePeak"/> is deliberately <em>not</em> honoured here:
/// peak-normalising needs the whole signal in hand, and on a stream that means scaling everything
/// to whichever burst happened to be loudest so far, which silently moves the level under
/// whatever is measuring it. Level is the caller's to choose.</para>
/// </remarks>
public sealed class StreamingSsbDemodulator
{
    private readonly int _factor;
    private readonly float[] _kernel; // composite kernel, interleaved (Re e[p], -Im e[p])
    private readonly int _len;        // kernel length in complex taps
    private readonly float[] _ring;   // interleaved (re, im) history, written twice over: see PushRaw
    private readonly int _size;       // ring length in frames, a power of two
    private readonly int _mask;
    private readonly bool _tuned;     // DialHz != 0: the NCO has work to do
    private readonly double _wr;      // NCO step phasor
    private readonly double _wi;
    private readonly float _mirror;   // -1 on LSB: conjugate the shifted baseband
    private readonly int _pad;

    private double _pr = 1.0;
    private double _pi;
    private long _ncoIndex;
    private long _fed;                // ring-relative index of the next write
    private long _realFed;            // input samples accepted (excludes the flush tail)
    private long _produced;
    private bool _flushed;

    /// <summary>Creates the converter; null takes every default.</summary>
    public StreamingSsbDemodulator(SsbDemodulatorOptions? options = null)
    {
        SsbDemodulatorOptions opt = options ?? new SsbDemodulatorOptions();
        if (opt.InputRate % opt.OutputRate != 0)
        {
            throw new ArgumentException(
                $"InputRate {opt.InputRate} must be a multiple of OutputRate {opt.OutputRate}");
        }

        InputRate = opt.InputRate;
        OutputRate = opt.OutputRate;
        _factor = opt.InputRate / opt.OutputRate;

        double dPhi = -2.0 * Math.PI * opt.DialHz / opt.InputRate;
        _tuned = opt.DialHz != 0;
        _wr = Math.Cos(dPhi);
        _wi = Math.Sin(dPhi);
        _mirror = opt.Sideband == Sideband.Lower ? -1f : 1f;

        // The SSB bandpass, exactly as the reference builds it — including the sign of the
        // imaginary half, which selects the UPPER sideband only because the convolution below
        // reverses the kernel. See SsbDemodulator for the story of getting that wrong.
        double centre = 0.5 * (opt.SsbHighHz + opt.SsbLowHz);
        double halfWidth = 0.5 * (opt.SsbHighHz - opt.SsbLowHz);
        float[] proto = FilterDesign.LowPass(halfWidth, opt.InputRate, opt.BandpassTaps);
        int taps = proto.Length;
        int mid = (taps - 1) / 2;
        var hre = new double[taps];
        var him = new double[taps];
        for (int m = 0; m < taps; m++)
        {
            double ph = 2.0 * Math.PI * centre * (m - mid) / opt.InputRate;
            hre[m] = proto[m] * Math.Cos(ph);
            him[m] = -proto[m] * Math.Sin(ph);
        }

        // The decimation anti-alias low-pass, as the reference builds it — but measured rather
        // than assumed. FilterDesign's Hann window is the periodic form, so its coefficients are
        // NOT symmetric, and a cascade is only reproducible if the order FirFilter applies them
        // in is known. Reading it out of the filter as an impulse response settles that by
        // observation instead of by guessing which end is which: gImpulse is by definition the
        // g[t] in y[k] = sum_t g[t]·x[k−t], whatever the class does internally.
        // At factor 1 there is nothing to decimate and so nothing to anti-alias: g is the unit
        // impulse, the composite kernel collapses to the SSB bandpass alone, and the reference
        // omits its low-pass on the same condition so the two still agree sample-for-sample.
        double[] g;
        if (_factor == 1)
        {
            g = [1.0];
        }
        else
        {
            float[] design = FilterDesign.LowPass(0.45 * opt.OutputRate, opt.InputRate, 8 * _factor + 1);
            var probe = new FirFilter(design);
            g = new double[design.Length];
            for (int t = 0; t < g.Length; t++)
            {
                g[t] = probe.Next(t == 0 ? 1f : 0f);
            }
        }

        int gt = g.Length;

        // Composite kernel: e[p] = sum_s g[gt-1-s] * h[p-s]. Cascading the two filters and taking
        // the real part at the end is the same operation as the reference's filter → real → filter,
        // because g is real; the reversal is what lines the group delays up. The reference applies
        // g causally and does not compensate its delay, so neither does this. It is designed in
        // double and stored in float, interleaved to match the history and with the imaginary
        // half negated, so that Re{e x} = Re e Re x - Im e Im x is one plain dot product.
        _len = taps + gt - 1;
        _kernel = new float[2 * _len];
        for (int p = 0; p < _len; p++)
        {
            double re = 0, im = 0;
            for (int s = 0; s < gt; s++)
            {
                int hi = p - s;
                if (hi >= 0 && hi < taps)
                {
                    double gc = g[gt - 1 - s];
                    re += gc * hre[hi];
                    im += gc * him[hi];
                }
            }
            _kernel[2 * p] = (float)re;
            _kernel[(2 * p) + 1] = (float)-im;
        }

        // Output instant k draws on input samples k − _pad … k + LookAheadSamples. Shifting every
        // ring index up by _pad keeps it non-negative for the first outputs, whose window reaches
        // back before the start of the capture — where the reference sees zeros, which is exactly
        // what the untouched head of a zero-initialised ring provides.
        _pad = mid + gt - 1;
        LookAheadSamples = mid;
        int span = _len + _factor + 8;
        int size = 1;
        while (size < span)
        {
            size <<= 1;
        }

        _size = size;
        _ring = new float[2 * 2 * size];
        _mask = size - 1;
        _fed = _pad;
    }

    /// <summary>Complex sample rate of the IQ fed in, Hz.</summary>
    public int InputRate { get; }

    /// <summary>Real sample rate of the audio produced, Hz.</summary>
    public int OutputRate { get; }

    /// <summary>Input samples of look-ahead the filter needs before an output instant can be
    /// evaluated — the reason <see cref="Flush"/> exists.</summary>
    public int LookAheadSamples { get; }

    /// <summary>Output samples produced so far.</summary>
    public long OutputCount => _produced;

    /// <summary>Largest number of outputs <see cref="Process(ReadOnlySpan{short}, Span{float})"/>
    /// can return for a block of <paramref name="inputFrames"/> IQ frames.</summary>
    public int MaxOutputFor(int inputFrames) => (inputFrames / _factor) + 1;

    /// <summary>Largest number of outputs <see cref="Flush"/> can return.</summary>
    public int MaxFlushOutput => (_len / _factor) + 2;

    /// <summary>Converts a block of interleaved 16-bit stereo IQ (I,Q,I,Q…) as read from a
    /// capture. Returns the number of audio samples written to <paramref name="output"/>.</summary>
    public int Process(ReadOnlySpan<short> interleaved, Span<float> output)
    {
        int n = interleaved.Length / 2;
        int written = 0;
        for (int k = 0; k < n; k++)
        {
            Push(interleaved[2 * k] / 32768f, interleaved[(2 * k) + 1] / 32768f);
            Emit(output, ref written);
        }

        return written;
    }

    /// <summary>
    /// Converts a block of interleaved single-precision IQ (I,Q,I,Q..., normalised to about -1..1), the
    /// layout an SDR stream or <see cref="ComplexRationalResampler"/> delivers. Counts are frames:
    /// <paramref name="interleaved"/> holds twice as many floats, and an odd trailing one is
    /// ignored. Returns the number of audio samples written to <paramref name="output"/>.
    /// </summary>
    /// <remarks>The samples go into the filter as they are, with no conversion or copy, so this is
    /// the overload to use when the IQ is already float.</remarks>
    public int Process(ReadOnlySpan<float> interleaved, Span<float> output)
    {
        int n = interleaved.Length / 2;
        int written = 0;
        for (int k = 0; k < n; k++)
        {
            Push(interleaved[2 * k], interleaved[(2 * k) + 1]);
            Emit(output, ref written);
        }

        return written;
    }

    /// <summary>Converts a block of separate I/Q samples (normalised, about -1..1). The filter runs in
    /// single precision, so each sample is rounded to float on the way in.</summary>
    public int Process(ReadOnlySpan<double> i, ReadOnlySpan<double> q, Span<float> output)
    {
        if (i.Length != q.Length)
        {
            throw new ArgumentException("I and Q must be the same length");
        }

        int written = 0;
        for (int k = 0; k < i.Length; k++)
        {
            Push((float)i[k], (float)q[k]);
            Emit(output, ref written);
        }

        return written;
    }

    /// <summary>
    /// Emits the outputs still held up by the filter's look-ahead, then refuses further input.
    /// Call once, at end of capture.
    /// </summary>
    public int Flush(Span<float> output)
    {
        if (_flushed)
        {
            return 0;
        }

        _flushed = true;

        // Feeding zeros past the end is what the reference does implicitly by clamping its
        // convolution to the array bounds.
        int written = 0;
        for (int k = 0; k < _len; k++)
        {
            PushRaw(0, 0);
            Emit(output, ref written);
        }

        return written;
    }

    private void Push(float i, float q)
    {
        if (_flushed)
        {
            throw new InvalidOperationException("Process cannot follow Flush");
        }

        _realFed++;
        if (!_tuned)
        {
            // Tuned to the dial, the NCO's phasor stays at exactly 1 + 0j, so its product is the
            // sample itself: skip the arithmetic, keep only the LSB mirror.
            PushRaw(i, _mirror * q);
            return;
        }

        // NCO: shift by -DialHz so the suppressed carrier sits at 0 Hz. Incremental phasor in
        // double, renormalised on the same schedule as the reference so the two do not diverge. On
        // LSB the result is conjugated, mirroring the spectrum about the dial so that the wanted
        // lower sideband becomes an upper one and the single USB kernel below serves both.
        double re = (i * _pr) - (q * _pi);
        double im = _mirror * ((i * _pi) + (q * _pr));
        double npr = (_pr * _wr) - (_pi * _wi);
        _pi = (_pr * _wi) + (_pi * _wr);
        _pr = npr;
        if ((_ncoIndex & 0x3FFF) == 0x3FFF)
        {
            double m = Math.Sqrt((_pr * _pr) + (_pi * _pi));
            _pr /= m;
            _pi /= m;
        }

        _ncoIndex++;
        PushRaw((float)re, (float)im);
    }

    /// <summary>Writes one frame at its ring slot and again one ring further on, so that every
    /// window of <c>_len</c> frames starting inside the first ring is one contiguous run of floats
    /// for the vector dot product, wherever the ring has wrapped.</summary>
    private void PushRaw(float re, float im)
    {
        int slot = 2 * (int)(_fed & _mask);
        int copy = slot + (2 * _size);
        _ring[slot] = re;
        _ring[slot + 1] = im;
        _ring[copy] = re;
        _ring[copy + 1] = im;
        _fed++;
    }

    /// <summary>Evaluates the composite kernel at every output instant whose whole window has
    /// arrived. The output count is capped at <c>realInput / factor</c> to match the reference
    /// exactly at the tail.</summary>
    private void Emit(Span<float> output, ref int written)
    {
        long limit = _realFed / _factor;
        while (_produced < limit)
        {
            long start = _factor * _produced;
            if (_fed < start + _len)
            {
                return;
            }

            int s0 = 2 * (int)(start & _mask);
            output[written++] = VectorMath.Dot(_kernel, _ring.AsSpan(s0, 2 * _len));
            _produced++;
        }
    }
}
