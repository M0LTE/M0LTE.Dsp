using System.Numerics;
using System.Runtime.InteropServices;

namespace M0LTE.Dsp;

/// <summary>Settings for <see cref="RationalResampler"/> and <see cref="ComplexRationalResampler"/>.
/// Only the two rates are required; the filter defaults keep everything that survives the lower
/// rate's Nyquist and reject what would alias or image by <see cref="StopbandAttenuationDb"/>.</summary>
public sealed class RationalResamplerOptions
{
    /// <summary>Sample rate fed in, Hz (complex samples per second for IQ).</summary>
    public int InputRate { get; init; } = 48000;

    /// <summary>Sample rate produced, Hz. Any positive integer: the ratio need not be whole.</summary>
    public int OutputRate { get; init; } = 12000;

    /// <summary>Edge of the passband, Hz. Below it the response is flat to within
    /// <see cref="RationalResampler.PassbandRippleDb"/>. Null takes 0.4 of the lower of the two
    /// rates, which is 80 % of its Nyquist (19.2 kHz either side of DC for 48 kS/s IQ).</summary>
    public double? PassbandHz { get; init; }

    /// <summary>Start of the stopband, Hz. Null takes 0.5 of the lower rate, its Nyquist, so that
    /// everything which would alias on decimation (or image on interpolation) is attenuated by the
    /// full <see cref="StopbandAttenuationDb"/> wherever it lands. Moving it above Nyquist, up to
    /// <c>lower rate - PassbandHz</c>, shortens the filter and lets aliases fold into the transition
    /// band but never into the passband.</summary>
    public double? StopbandHz { get; init; }

    /// <summary>Attenuation at and beyond <see cref="StopbandHz"/>, dB. Default 80, which is below a
    /// 12-bit converter's quantisation floor and well inside float arithmetic's reach. Each extra
    /// 10 dB costs about 14 % more taps. Accepted from 20 to 110 dB: beyond that the float
    /// arithmetic, not the filter, sets the floor.</summary>
    public double StopbandAttenuationDb { get; init; } = 80;

    /// <summary>Refuse to design a prototype filter longer than this. Only a rate pair with a
    /// large reduced numerator gets near it (44100 to 48000 is 160/147 and about 8000 taps; two
    /// rates whose ratio does not reduce at all could ask for millions). Default 2^20.</summary>
    public int MaxPrototypeTaps { get; init; } = 1 << 20;
}

/// <summary>
/// Streaming rational (L/M) sample-rate converter for a real signal: polyphase, block in and
/// block out, with a stated passband ripple and stopband attenuation. The fractional sibling of
/// <see cref="Decimator"/> and <see cref="Upsampler"/>, for a source whose rate is not a whole
/// multiple of the one wanted (an SDR stream at 2.048 MS/s, a sound card at 44.1 kHz).
/// </summary>
/// <remarks>
/// <para><b>Method.</b> Conceptually: insert L - 1 zeros between input samples, low-pass at the
/// lower of the two Nyquist frequencies, keep one sample in M. The low-pass is evaluated only
/// where an output falls, and only over the input samples that are not inserted zeros, so each
/// output costs <see cref="TapsPerPhase"/> multiply-accumulates whatever L is. L and M are the
/// two rates divided by their greatest common divisor.</para>
/// <para><b>Filter.</b> A Kaiser-windowed sinc, sized by Kaiser's formula for the requested
/// attenuation and transition width, with a little margin so the stated figures hold as
/// measured rather than as estimated. Unlike <see cref="FilterDesign.LowPass"/>'s periodic
/// Hann, the window is symmetric, so the phase is exactly linear and
/// <see cref="DelayInputSamples"/> is exact.</para>
/// <para><b>Timing.</b> Output sample m sits at input time <c>m * M / L - DelayInputSamples</c>.
/// Phase is carried as an integer remainder, so there is no drift however long the stream runs:
/// after n input samples exactly <c>ceil(n * L / M)</c> outputs have been produced.</para>
/// <para><b>Cost.</b> Zero allocation after construction; the dot products use
/// <see cref="Vector{T}"/>. Every output costs the same, so a coarse integer decimation ahead of
/// a large ratio (2.048 MS/s by 16 to 128 kS/s, then 3/8 to 48 kS/s) is far cheaper than doing
/// it all here.</para>
/// </remarks>
public sealed class RationalResampler
{
    private readonly PolyphaseBank _bank;
    private readonly float[] _history;
    private int _write;
    private int _need;
    private int _phase;

    /// <summary>Creates the converter for the rates and filter in <paramref name="options"/>.</summary>
    public RationalResampler(RationalResamplerOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        _bank = new PolyphaseBank(options);
        _history = new float[2 * _bank.TapsPerPhase];
        _need = 1;
    }

    /// <summary>Input sample rate, Hz.</summary>
    public int InputRate => _bank.InputRate;

    /// <summary>Output sample rate, Hz.</summary>
    public int OutputRate => _bank.OutputRate;

    /// <summary>L: the interpolation factor, OutputRate over the rates' common divisor.</summary>
    public int Interpolation => _bank.Interpolation;

    /// <summary>M: the decimation factor, InputRate over the rates' common divisor.</summary>
    public int Decimation => _bank.Decimation;

    /// <summary>Multiply-accumulates per output sample.</summary>
    public int TapsPerPhase => _bank.TapsPerPhase;

    /// <summary>Length of the prototype low-pass at the interpolated rate (L x TapsPerPhase).</summary>
    public int PrototypeTaps => _bank.PrototypeTaps;

    /// <summary>Passband edge the filter was designed for, Hz.</summary>
    public double PassbandHz => _bank.PassbandHz;

    /// <summary>Stopband edge the filter was designed for, Hz.</summary>
    public double StopbandHz => _bank.StopbandHz;

    /// <summary>Stated attenuation at and beyond <see cref="StopbandHz"/>, dB.</summary>
    public double StopbandAttenuationDb => _bank.StopbandAttenuationDb;

    /// <summary>Stated peak deviation from unity gain below <see cref="PassbandHz"/>, dB (plus or
    /// minus). A Kaiser design ripples by the same fraction in both bands, so this follows from
    /// the stopband figure: about 0.0009 dB at 80 dB.</summary>
    public double PassbandRippleDb => _bank.PassbandRippleDb;

    /// <summary>Group delay in input samples. Constant across frequency (linear phase).</summary>
    public double DelayInputSamples => _bank.DelayInputSamples;

    /// <summary>Group delay in output samples.</summary>
    public double DelayOutputSamples => _bank.DelayOutputSamples;

    /// <summary>Input samples accepted since construction or <see cref="Reset"/>.</summary>
    public long InputCount { get; private set; }

    /// <summary>Output samples produced since construction or <see cref="Reset"/>.</summary>
    public long OutputCount { get; private set; }

    /// <summary>Most outputs <see cref="Process"/> can write for <paramref name="inputLength"/>
    /// input samples, whatever state the converter is in: <c>ceil(inputLength * L / M)</c>.</summary>
    public int MaxOutput(int inputLength) => _bank.MaxOutput(inputLength);

    /// <summary>Converts a block; returns the number of output samples written. Blocks may be any
    /// length, and splitting a stream into blocks differently does not change the output.</summary>
    public int Process(ReadOnlySpan<float> input, Span<float> output)
    {
        int k = _bank.TapsPerPhase;
        int written = 0;
        int index = 0;
        while (index < input.Length)
        {
            int take = Math.Min(_need, input.Length - index);
            for (int j = 0; j < take; j++)
            {
                float x = input[index + j];
                _history[_write] = x;
                _history[_write + k] = x;
                if (++_write == k)
                {
                    _write = 0;
                }
            }

            index += take;
            _need -= take;
            InputCount += take;
            while (_need == 0)
            {
                output[written++] = PolyphaseBank.Dot(_bank.Phase(_phase), _history.AsSpan(_write, k));
                _need = _bank.Advance(ref _phase);
                OutputCount++;
            }
        }

        return written;
    }

    /// <summary>Forgets all history, as if newly constructed: for a discontinuity in the input
    /// (a gap or a retune), so that what follows is not filtered together with what came
    /// before.</summary>
    public void Reset()
    {
        Array.Clear(_history);
        _write = 0;
        _need = 1;
        _phase = 0;
        InputCount = 0;
        OutputCount = 0;
    }
}

/// <summary>The design shared by the real and complex converters: the polyphase coefficient bank,
/// the phase bookkeeping and the dot product.</summary>
internal sealed class PolyphaseBank
{
    /// <summary>Kaiser's length formula is an estimate; designing a little beyond the stated
    /// attenuation keeps the measured figure on the right side of it.</summary>
    private const double DesignMarginDb = 3;

    private const double MinAttenuationDb = 20;

    private const double MaxAttenuationDb = 110;

    private readonly float[] _coefficients;

    public PolyphaseBank(RationalResamplerOptions options)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(options.InputRate);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(options.OutputRate);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(options.MaxPrototypeTaps);

        // Float coefficients and float accumulation bottom out somewhere between 115 and 140 dB,
        // so a figure past MaxAttenuationDb could be stated but not delivered.
        if (!(options.StopbandAttenuationDb >= MinAttenuationDb && options.StopbandAttenuationDb <= MaxAttenuationDb))
        {
            throw new ArgumentOutOfRangeException(
                nameof(options),
                $"StopbandAttenuationDb ({options.StopbandAttenuationDb}) must be from {MinAttenuationDb} to {MaxAttenuationDb} dB");
        }

        InputRate = options.InputRate;
        OutputRate = options.OutputRate;
        int divisor = Gcd(InputRate, OutputRate);
        Interpolation = OutputRate / divisor;
        Decimation = InputRate / divisor;

        double lower = Math.Min(InputRate, OutputRate);
        PassbandHz = options.PassbandHz ?? 0.4 * lower;
        StopbandHz = options.StopbandHz ?? 0.5 * lower;
        // Written so that NaN, which fails every comparison, fails the check too.
        if (!(PassbandHz > 0 && StopbandHz > PassbandHz && StopbandHz <= lower - PassbandHz))
        {
            throw new ArgumentException(
                $"Need 0 < PassbandHz ({PassbandHz}) < StopbandHz ({StopbandHz}) <= lower rate - PassbandHz ({lower - PassbandHz})",
                nameof(options));
        }

        StopbandAttenuationDb = options.StopbandAttenuationDb;
        double delta = Math.Pow(10, -StopbandAttenuationDb / 20);
        PassbandRippleDb = 20 * Math.Log10(1 + delta);

        // Kaiser: length and shape from the attenuation and the transition width, both measured
        // at the interpolated rate the prototype runs at.
        double prototypeRate = (double)InputRate * Interpolation;
        double design = StopbandAttenuationDb + DesignMarginDb;
        double transition = (StopbandHz - PassbandHz) / prototypeRate;
        double estimate = Math.Ceiling((design - 7.95) / (14.36 * transition)) + 1;
        if (!(estimate <= options.MaxPrototypeTaps))
        {
            throw new ArgumentException(
                $"{InputRate} Hz to {OutputRate} Hz reduces to {Interpolation}/{Decimation} and needs a {estimate:0}-tap prototype, over MaxPrototypeTaps ({options.MaxPrototypeTaps}). Choose rates with a larger common divisor, or relax the filter.",
                nameof(options));
        }

        TapsPerPhase = Math.Max(1, (int)Math.Ceiling(estimate / Interpolation));
        PrototypeTaps = checked(TapsPerPhase * Interpolation);
        double beta = KaiserBeta(design);
        double cutoff = 0.5 * (PassbandHz + StopbandHz) / prototypeRate;
        double centre = (PrototypeTaps - 1) / 2.0;
        double i0Beta = BesselI0(beta);

        var h = new double[PrototypeTaps];
        double sum = 0;
        for (int n = 0; n < PrototypeTaps; n++)
        {
            double x = n - centre;
            double sinc = x == 0 ? 2 * cutoff : Math.Sin(2 * Math.PI * cutoff * x) / (Math.PI * x);
            double r = PrototypeTaps == 1 ? 0 : 2 * n / (double)(PrototypeTaps - 1) - 1;
            h[n] = sinc * BesselI0(beta * Math.Sqrt(Math.Max(0, 1 - r * r))) / i0Beta;
            sum += h[n];
        }

        // Gain L, so that each phase (one in L of the taps) sums to about one: zero-stuffing
        // divides the signal's level by L and this puts it back.
        double gain = Interpolation / sum;

        // Phase p holds prototype taps p, p + L, p + 2L, ..., reversed so the dot product runs
        // over the history oldest-first.
        _coefficients = new float[PrototypeTaps];
        for (int p = 0; p < Interpolation; p++)
        {
            for (int k = 0; k < TapsPerPhase; k++)
            {
                _coefficients[(p * TapsPerPhase) + (TapsPerPhase - 1 - k)] = (float)(gain * h[(k * Interpolation) + p]);
            }
        }

        DelayInputSamples = centre / Interpolation;
        DelayOutputSamples = centre / Decimation;
    }

    public int InputRate { get; }

    public int OutputRate { get; }

    public int Interpolation { get; }

    public int Decimation { get; }

    public int TapsPerPhase { get; }

    public int PrototypeTaps { get; }

    public double PassbandHz { get; }

    public double StopbandHz { get; }

    public double StopbandAttenuationDb { get; }

    public double PassbandRippleDb { get; }

    public double DelayInputSamples { get; }

    public double DelayOutputSamples { get; }

    public int MaxOutput(int inputLength) =>
        (int)((((long)inputLength * Interpolation) + Decimation - 1) / Decimation);

    public ReadOnlySpan<float> Phase(int phase) => _coefficients.AsSpan(phase * TapsPerPhase, TapsPerPhase);

    /// <summary>Steps to the next output instant: M further along the interpolated grid. Returns how
    /// many new input samples it needs first and leaves the remainder in
    /// <paramref name="phase"/>; integer arithmetic throughout, so nothing accumulates.</summary>
    public int Advance(ref int phase)
    {
        long next = (long)phase + Decimation;
        int need = (int)(next / Interpolation);
        phase = (int)(next - ((long)need * Interpolation));
        return need;
    }

    public static float Dot(ReadOnlySpan<float> a, ReadOnlySpan<float> b)
    {
        float result = 0;
        int i = 0;
        if (Vector.IsHardwareAccelerated && a.Length >= Vector<float>.Count)
        {
            ReadOnlySpan<Vector<float>> va = MemoryMarshal.Cast<float, Vector<float>>(a);
            ReadOnlySpan<Vector<float>> vb = MemoryMarshal.Cast<float, Vector<float>>(b[..a.Length]);
            var acc = Vector<float>.Zero;
            for (int v = 0; v < va.Length; v++)
            {
                acc += va[v] * vb[v];
            }

            result = Vector.Sum(acc);
            i = va.Length * Vector<float>.Count;
        }

        for (; i < a.Length; i++)
        {
            result += a[i] * b[i];
        }

        return result;
    }

    private static double KaiserBeta(double attenuationDb) => attenuationDb switch
    {
        > 50 => 0.1102 * (attenuationDb - 8.7),
        >= 21 => (0.5842 * Math.Pow(attenuationDb - 21, 0.4)) + (0.07886 * (attenuationDb - 21)),
        _ => 0,
    };

    /// <summary>Modified Bessel function of the first kind, order zero, by its power series.</summary>
    private static double BesselI0(double x)
    {
        double sum = 1;
        double term = 1;
        double half = x / 2;
        for (int k = 1; k < 200; k++)
        {
            term *= half / k;
            double add = term * term;
            sum += add;
            if (add < sum * 1e-17)
            {
                break;
            }
        }

        return sum;
    }

    private static int Gcd(int a, int b)
    {
        while (b != 0)
        {
            (a, b) = (b, a % b);
        }

        return a;
    }
}
