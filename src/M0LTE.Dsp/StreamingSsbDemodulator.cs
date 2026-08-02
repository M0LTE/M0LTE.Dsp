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
/// <para>The output is sample-for-sample the reference's, to within double-versus-float rounding,
/// and a test asserts exactly that — the two implementations are not allowed to drift apart.
/// <see cref="SsbDemodulatorOptions.NormalisePeak"/> is deliberately <em>not</em> honoured here:
/// peak-normalising needs the whole signal in hand, and on a stream that means scaling everything
/// to whichever burst happened to be loudest so far, which silently moves the level under
/// whatever is measuring it. Level is the caller's to choose.</para>
/// </remarks>
public sealed class StreamingSsbDemodulator
{
    private readonly int _factor;
    private readonly double[] _kre;   // composite kernel = SSB bandpass * anti-alias low-pass
    private readonly double[] _kim;
    private readonly int _len;
    private readonly double[] _ringRe;
    private readonly double[] _ringIm;
    private readonly int _mask;
    private readonly double _wr;      // NCO step phasor
    private readonly double _wi;
    private readonly double _mirror;  // −1 on LSB: conjugate the shifted baseband
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
        _wr = Math.Cos(dPhi);
        _wi = Math.Sin(dPhi);
        _mirror = opt.Sideband == Sideband.Lower ? -1.0 : 1.0;

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
        // g causally and does not compensate its delay, so neither does this.
        _len = taps + gt - 1;
        _kre = new double[_len];
        _kim = new double[_len];
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
            _kre[p] = re;
            _kim[p] = im;
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

        _ringRe = new double[size];
        _ringIm = new double[size];
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
            Push(interleaved[2 * k] / 32768.0, interleaved[2 * k + 1] / 32768.0);
            Emit(output, ref written);
        }

        return written;
    }

    /// <summary>Converts a block of separate I/Q samples (normalised, ~[−1,1]).</summary>
    public int Process(ReadOnlySpan<double> i, ReadOnlySpan<double> q, Span<float> output)
    {
        if (i.Length != q.Length)
        {
            throw new ArgumentException("I and Q must be the same length");
        }

        int written = 0;
        for (int k = 0; k < i.Length; k++)
        {
            Push(i[k], q[k]);
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

    private void Push(double i, double q)
    {
        if (_flushed)
        {
            throw new InvalidOperationException("Process cannot follow Flush");
        }

        // NCO: shift by −DialHz so the suppressed carrier sits at 0 Hz. Incremental phasor,
        // renormalised on the same schedule as the reference so the two do not diverge. On LSB
        // the result is conjugated, mirroring the spectrum about the dial so that the wanted
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
        _realFed++;
        PushRaw(re, im);
    }

    private void PushRaw(double re, double im)
    {
        int slot = (int)(_fed & _mask);
        _ringRe[slot] = re;
        _ringIm[slot] = im;
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

            int s0 = (int)(start & _mask);
            int run = Math.Min(_len, _ringRe.Length - s0);
            double acc = 0;
            for (int p = 0; p < run; p++)
            {
                acc += (_kre[p] * _ringRe[s0 + p]) - (_kim[p] * _ringIm[s0 + p]);
            }

            for (int p = run; p < _len; p++)
            {
                acc += (_kre[p] * _ringRe[p - run]) - (_kim[p] * _ringIm[p - run]);
            }

            output[written++] = (float)acc;
            _produced++;
        }
    }
}
