namespace M0LTE.Dsp;

/// <summary>
/// <see cref="RationalResampler"/> for complex baseband: interleaved I,Q in, interleaved I,Q out.
/// The same real low-pass runs over I and Q, so the passband is symmetric about DC and spans
/// plus and minus <see cref="PassbandHz"/>.
/// </summary>
/// <remarks>
/// The input and output are <c>float</c> spans of interleaved pairs, the layout an IQ source
/// delivers. A span of any two-float complex struct converts with
/// <c>MemoryMarshal.Cast</c>. Counts and lengths are in frames (complex samples), not floats.
/// Everything else (filter, timing, delay, cost per output, no allocation after construction)
/// is as <see cref="RationalResampler"/> describes, doubled for the two components.
/// </remarks>
public sealed class ComplexRationalResampler
{
    private readonly PolyphaseBank _bank;
    private readonly float[] _historyI;
    private readonly float[] _historyQ;
    private int _write;
    private int _need;
    private int _phase;

    /// <summary>Creates the converter for the rates and filter in <paramref name="options"/>.</summary>
    public ComplexRationalResampler(RationalResamplerOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        _bank = new PolyphaseBank(options);
        _historyI = new float[2 * _bank.TapsPerPhase];
        _historyQ = new float[2 * _bank.TapsPerPhase];
        _need = 1;
    }

    /// <inheritdoc cref="RationalResampler.InputRate"/>
    public int InputRate => _bank.InputRate;

    /// <inheritdoc cref="RationalResampler.OutputRate"/>
    public int OutputRate => _bank.OutputRate;

    /// <inheritdoc cref="RationalResampler.Interpolation"/>
    public int Interpolation => _bank.Interpolation;

    /// <inheritdoc cref="RationalResampler.Decimation"/>
    public int Decimation => _bank.Decimation;

    /// <summary>Multiply-accumulates per output component (twice this per complex output).</summary>
    public int TapsPerPhase => _bank.TapsPerPhase;

    /// <inheritdoc cref="RationalResampler.PrototypeTaps"/>
    public int PrototypeTaps => _bank.PrototypeTaps;

    /// <inheritdoc cref="RationalResampler.PassbandHz"/>
    public double PassbandHz => _bank.PassbandHz;

    /// <inheritdoc cref="RationalResampler.StopbandHz"/>
    public double StopbandHz => _bank.StopbandHz;

    /// <inheritdoc cref="RationalResampler.StopbandAttenuationDb"/>
    public double StopbandAttenuationDb => _bank.StopbandAttenuationDb;

    /// <inheritdoc cref="RationalResampler.PassbandRippleDb"/>
    public double PassbandRippleDb => _bank.PassbandRippleDb;

    /// <inheritdoc cref="RationalResampler.DelayInputSamples"/>
    public double DelayInputSamples => _bank.DelayInputSamples;

    /// <inheritdoc cref="RationalResampler.DelayOutputSamples"/>
    public double DelayOutputSamples => _bank.DelayOutputSamples;

    /// <summary>Input frames accepted since construction or <see cref="Reset"/>.</summary>
    public long InputCount { get; private set; }

    /// <summary>Output frames produced since construction or <see cref="Reset"/>.</summary>
    public long OutputCount { get; private set; }

    /// <summary>Most frames <see cref="Process"/> can write for <paramref name="inputFrames"/>
    /// input frames: <c>ceil(inputFrames * L / M)</c>. The output span needs twice this many
    /// floats.</summary>
    public int MaxOutput(int inputFrames) => _bank.MaxOutput(inputFrames);

    /// <summary>Converts a block of interleaved I,Q; returns the number of frames written to
    /// <paramref name="output"/> (twice that many floats).</summary>
    public int Process(ReadOnlySpan<float> interleaved, Span<float> output)
    {
        if ((interleaved.Length & 1) != 0)
        {
            throw new ArgumentException("Interleaved IQ must hold whole I,Q pairs", nameof(interleaved));
        }

        int k = _bank.TapsPerPhase;
        int frames = interleaved.Length / 2;
        int written = 0;
        int index = 0;
        while (index < frames)
        {
            int take = Math.Min(_need, frames - index);
            for (int j = 0; j < take; j++)
            {
                float i = interleaved[2 * (index + j)];
                float q = interleaved[(2 * (index + j)) + 1];
                _historyI[_write] = i;
                _historyI[_write + k] = i;
                _historyQ[_write] = q;
                _historyQ[_write + k] = q;
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
                ReadOnlySpan<float> taps = _bank.Phase(_phase);
                output[2 * written] = PolyphaseBank.Dot(taps, _historyI.AsSpan(_write, k));
                output[(2 * written) + 1] = PolyphaseBank.Dot(taps, _historyQ.AsSpan(_write, k));
                written++;
                _need = _bank.Advance(ref _phase);
                OutputCount++;
            }
        }

        return written;
    }

    /// <inheritdoc cref="RationalResampler.Reset"/>
    public void Reset()
    {
        Array.Clear(_historyI);
        Array.Clear(_historyQ);
        _write = 0;
        _need = 1;
        _phase = 0;
        InputCount = 0;
        OutputCount = 0;
    }
}
