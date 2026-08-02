namespace M0LTE.Dsp;

/// <summary>Which side of the suppressed carrier an SSB demodulation keeps.</summary>
public enum Sideband
{
    /// <summary>RF = dial + audio — the data-mode norm.</summary>
    Upper,

    /// <summary>RF = dial − audio.</summary>
    Lower,
}

/// <summary>Knobs for <see cref="SsbDemodulator"/>.</summary>
public sealed class SsbDemodulatorOptions
{
    /// <summary>Complex sample rate of the IQ fed in, Hz (48000 for a 48 kHz SDR stream).</summary>
    public int InputRate { get; init; } = 48000;

    /// <summary>Real sample rate of the audio produced, Hz. Must divide
    /// <see cref="InputRate"/> exactly — this demodulates and decimates, it does not resample by
    /// a fraction.</summary>
    public int OutputRate { get; init; } = 9600;

    /// <summary>Where the SSB suppressed carrier — the dial — sits within the IQ baseband, in
    /// Hz relative to the IQ centre. The wanted band then lives at DialHz + [SsbLowHz, SsbHighHz]
    /// on USB, or DialHz − that on LSB. 0 when the receiver is tuned exactly to the dial, which
    /// is the usual arrangement; give it (dial − tune frequency) when it is not.</summary>
    public double DialHz { get; init; }

    /// <summary>
    /// Which sideband to demodulate. USB (the default) keeps the band <em>above</em> the
    /// suppressed carrier; LSB keeps the band below it, which is the same filter applied to the
    /// conjugated baseband — mirroring the spectrum about the dial turns a lower sideband into an
    /// upper one, so exactly one piece of arithmetic differs between the two.
    /// </summary>
    public Sideband Sideband { get; init; } = Sideband.Upper;

    /// <summary>Lower edge of the SSB passband, in Hz from the suppressed carrier. The default
    /// 150–3450 Hz is a generous data passband — wider than the nominal 300–2700 Hz a
    /// transceiver's filter gives, because holding the complex baseband means nothing forces us
    /// to inherit one. Narrow it (300–2700, say) to emulate a real rig for comparison.</summary>
    public double SsbLowHz { get; init; } = 150;

    /// <summary>Upper edge of the SSB passband, in Hz above the suppressed carrier. See
    /// <see cref="SsbLowHz"/>.</summary>
    public double SsbHighHz { get; init; } = 3450;

    /// <summary>Taps in the complex SSB bandpass (odd → linear phase).</summary>
    public int BandpassTaps { get; init; } = 301;

    /// <summary>Peak-normalise the output to this magnitude; 0 disables it and delivers the
    /// input's own scaling. Cosmetic, or clipping insurance — a demodulator downstream will
    /// equalise level itself, and normalising needs the whole signal in hand, which is why
    /// <see cref="StreamingSsbDemodulator"/> does not honour it.</summary>
    public float NormalisePeak { get; init; } = 0.7f;
}

/// <summary>
/// Demodulates single sideband from complex baseband: IQ in — from an SDR, a capture file, or a
/// receiver's network stream — real audio out at a submultiple of the IQ rate. Because the caller
/// holds the complex baseband, <em>the caller</em> chooses the receive filter, so a hardware
/// SSB filter's truncation is never in the path and cannot confound a measurement made through it.
///
/// <para>Chain: shift by −DialHz (suppressed carrier → 0 Hz) → conjugate for LSB → complex
/// bandpass keeping only the upper sideband +[SsbLow,SsbHigh] (rejecting the opposite sideband,
/// adjacent signals and the noise on the negative side) → take the real part → decimate to the
/// output rate.</para>
///
/// <para><b>This is the reference implementation, not the working one.</b> It holds the whole
/// signal in memory several times over and runs its filters at the <em>input</em> rate, so an
/// hour of 48 kHz IQ — 172.8 M complex samples, several 1.4 GB <c>double[]</c> — exhausts memory
/// long before it becomes slow. Use <see cref="StreamingSsbDemodulator"/> for anything
/// unbounded. This one stays because it is the obvious, auditable statement of what the
/// operation <em>is</em>, and the streaming form is tested sample-for-sample against it.</para>
/// </summary>
public sealed class SsbDemodulator
{
    private readonly SsbDemodulatorOptions _opt;

    /// <summary>Creates the converter; null takes every default.</summary>
    public SsbDemodulator(SsbDemodulatorOptions? options = null) => _opt = options ?? new SsbDemodulatorOptions();

    /// <summary>Convert interleaved 16-bit stereo IQ (I,Q,I,Q…) as read from a capture WAV.</summary>
    public float[] Convert(ReadOnlySpan<short> interleaved)
    {
        int n = interleaved.Length / 2;
        var i = new double[n];
        var q = new double[n];
        for (int k = 0; k < n; k++)
        {
            i[k] = interleaved[2 * k] / 32768.0;
            q[k] = interleaved[2 * k + 1] / 32768.0;
        }
        return Convert(i, q);
    }

    /// <summary>Convert separate I/Q sample arrays (normalised, ~[−1,1]).</summary>
    public float[] Convert(ReadOnlySpan<double> iIn, ReadOnlySpan<double> qIn)
    {
        int n = iIn.Length;
        if (qIn.Length != n)
        {
            throw new ArgumentException("I and Q must be the same length");
        }
        if (_opt.InputRate % _opt.OutputRate != 0)
        {
            throw new ArgumentException($"InputRate {_opt.InputRate} must be a multiple of OutputRate {_opt.OutputRate}");
        }

        // 1. NCO: shift by −DialHz so the suppressed carrier sits at 0 Hz (incremental phasor).
        //    On LSB the shifted baseband is then conjugated, which mirrors the spectrum about the
        //    dial and turns the wanted lower sideband into an upper one — so everything below is
        //    the single USB path, and this sign is the only place the two sidebands differ.
        var reS = new double[n];
        var imS = new double[n];
        double mirror = _opt.Sideband == Sideband.Lower ? -1.0 : 1.0;
        double dPhi = -2.0 * Math.PI * _opt.DialHz / _opt.InputRate;
        double wr = Math.Cos(dPhi), wi = Math.Sin(dPhi);
        double pr = 1.0, pi = 0.0;
        for (int k = 0; k < n; k++)
        {
            reS[k] = iIn[k] * pr - qIn[k] * pi;
            imS[k] = mirror * (iIn[k] * pi + qIn[k] * pr);
            double npr = pr * wr - pi * wi;
            pi = pr * wi + pi * wr;
            pr = npr;
            if ((k & 0x3FFF) == 0x3FFF) // renormalise the phasor periodically against drift
            {
                double m = Math.Sqrt(pr * pr + pi * pi);
                pr /= m;
                pi /= m;
            }
        }

        // 2. Complex SSB bandpass = real low-pass prototype (half the passband width) modulated up
        //    to the passband centre, so it keeps ONLY the upper sideband.
        double centre = 0.5 * (_opt.SsbHighHz + _opt.SsbLowHz);
        double halfWidth = 0.5 * (_opt.SsbHighHz - _opt.SsbLowHz);
        float[] proto = FilterDesign.LowPass(halfWidth, _opt.InputRate, _opt.BandpassTaps);
        int taps = proto.Length;
        int mid = (taps - 1) / 2;
        var hre = new double[taps];
        var him = new double[taps];
        for (int m = 0; m < taps; m++)
        {
            double ph = 2.0 * Math.PI * centre * (m - mid) / _opt.InputRate;
            hre[m] = proto[m] * Math.Cos(ph);

            // Negated: the convolution below is written y[k] = sum_m h[m]*x[k-mid+m], which
            // reverses the kernel and so realises a response centred at MINUS the modulation
            // frequency. Without this the filter selects the LOWER sideband while claiming to
            // keep the upper one. It went unnoticed because the loopback test synthesised its
            // input with the same convention, and the one real capture it was tried on was
            // only checked for "energy in the SSB band" — which an inverted selection can
            // still satisfy — it took a test asserting an absolute output frequency against a
            // bare complex exponential, which has no convention to share, to expose it. Do not
            // "tidy" this sign; SsbDemodulatorTests will fail if you do, and that is the point.
            him[m] = -proto[m] * Math.Sin(ph);
        }

        // 3. Anti-alias low-pass then decimate to the output rate. At factor 1 there is no
        //    decimation — "keep 1 in 1" discards nothing — so there is nothing to guard against
        //    aliasing and the low-pass is omitted rather than left to narrow, by a few hundred
        //    Hz of skirt, a passband the SSB bandpass above has already chosen deliberately.
        int factor = _opt.InputRate / _opt.OutputRate;
        FirFilter? lp = factor == 1
            ? null
            : new FirFilter(FilterDesign.LowPass(0.45 * _opt.OutputRate, _opt.InputRate, 8 * factor + 1));

        // The bandpass output is computed from k = −(lpTaps−1), not from 0, so that the low-pass
        // starts with real history rather than cold. At those negative instants the bandpass
        // window still overlaps the start of the capture, so its output is genuinely non-zero
        // and dropping it would truncate the cascade. Priming makes the whole chain exactly the
        // zero-padded convolution of the two filters, which is what lets
        // StreamingSsbDemodulator collapse them into one kernel and still agree
        // sample-for-sample. Without it the two differ over the first few milliseconds.
        int pre = lp is null ? 0 : 8 * factor; // the low-pass has 8*factor + 1 taps, so this is its memory

        // Convolve (complex), keep the real part only. Linear-phase filter → compensate its
        // group delay of `mid` samples so output aligns with input time.
        var real = new double[n + pre];
        for (int r = 0; r < real.Length; r++)
        {
            double acc = 0.0; // real part of sum_m h[m] * x[k - mid + m]
            int baseIdx = r - pre - mid;
            int mLo = Math.Max(0, -baseIdx);
            int mHi = Math.Min(taps, n - baseIdx);
            for (int m = mLo; m < mHi; m++)
            {
                int xi = baseIdx + m;
                acc += hre[m] * reS[xi] - him[m] * imS[xi];
            }
            real[r] = acc;
        }

        int outN = n / factor;
        var outp = new float[outN];
        int oi = 0;
        for (int r = 0; r < real.Length; r++)
        {
            float y = lp is null ? (float)real[r] : lp.Next((float)real[r]);
            int k = r - pre;
            if (k >= 0 && k % factor == 0 && oi < outN)
            {
                outp[oi++] = y;
            }
        }

        if (_opt.NormalisePeak > 0)
        {
            float peak = 0f;
            for (int k = 0; k < oi; k++)
            {
                peak = Math.Max(peak, Math.Abs(outp[k]));
            }
            if (peak > 1e-9f)
            {
                float g = _opt.NormalisePeak / peak;
                for (int k = 0; k < oi; k++)
                {
                    outp[k] *= g;
                }
            }
        }

        return outp;
    }
}
