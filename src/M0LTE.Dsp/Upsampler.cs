namespace M0LTE.Dsp;

/// <summary>
/// FIR interpolating upsampler — the transmit-side mirror of <see cref="Decimator"/>:
/// zero-stuff by the factor, then image-reject low-pass with the same windowed-sinc
/// design (gain compensated). Exists because consumer cards commonly refuse low direct
/// rates: the modems synthesise at the 12 kHz DSP rate and playback opens card-native
/// 48 kHz.
/// </summary>
public sealed class Upsampler
{
    private readonly FirFilter _filter;
    private readonly int _factor;
    private readonly float _gain;

    /// <summary>Creates a ×<paramref name="factor"/> upsampler producing
    /// <paramref name="outputRate"/> output.</summary>
    public Upsampler(int outputRate, int factor, int taps = 96)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(factor, 2);
        _factor = factor;
        // Image rejection: keep the original band (below inputNyquist ≈ outputRate/2/factor).
        _filter = new FirFilter(FilterDesign.LowPass(0.44 * outputRate / factor, outputRate, taps));
        _gain = factor;
    }

    /// <summary>Output samples produced for <paramref name="inputLength"/> input samples.</summary>
    public int OutputLength(int inputLength) => inputLength * _factor;

    /// <summary>Upsamples a block; writes exactly <see cref="OutputLength"/> samples.</summary>
    public void Process(ReadOnlySpan<float> input, Span<float> output)
    {
        int position = 0;
        foreach (float sample in input)
        {
            output[position++] = _gain * _filter.Next(sample);
            for (int i = 1; i < _factor; i++)
            {
                output[position++] = _gain * _filter.Next(0f);
            }
        }
    }
}
