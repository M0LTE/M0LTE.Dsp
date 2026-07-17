namespace M0LTE.Dsp;

/// <summary>
/// FIR anti-aliased decimator. The intended use is capturing at a card-native 48 kHz and
/// decimating ÷4 to the 12 kHz DSP rate — with a real filter, unlike QtSoundModem's
/// keep-1-in-4 (no anti-aliasing) and unlike the ALSA plug layer's linear resampler.
/// Computes the FIR only at output instants (polyphase-equivalent cost).
/// </summary>
public sealed class Decimator
{
    private readonly float[] _coefficients;
    private readonly float[] _history;
    private readonly int _factor;
    private int _position;
    private int _sinceLastOutput;

    /// <summary>Creates a ÷<paramref name="factor"/> decimator for
    /// <paramref name="inputRate"/> input.</summary>
    public Decimator(int inputRate, int factor, int taps = 96)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(factor, 2);
        _factor = factor;
        // Cut off at ~0.44 of the output Nyquist-defining band edge.
        _coefficients = FilterDesign.LowPass(0.44 * inputRate / factor, inputRate, taps);
        _history = new float[taps];
    }

    /// <summary>Maximum output count for an input of length <paramref name="inputLength"/>.</summary>
    public int MaxOutput(int inputLength) => inputLength / _factor + 1;

    /// <summary>Decimates a block; returns the number of output samples written.</summary>
    public int Process(ReadOnlySpan<float> input, Span<float> output)
    {
        int produced = 0;
        foreach (float sample in input)
        {
            _history[_position] = sample;
            if (++_sinceLastOutput == _factor)
            {
                _sinceLastOutput = 0;
                float acc = 0;
                int index = _position;
                foreach (float coefficient in _coefficients)
                {
                    acc += coefficient * _history[index];
                    if (--index < 0)
                    {
                        index = _history.Length - 1;
                    }
                }

                output[produced++] = acc;
            }

            if (++_position == _history.Length)
            {
                _position = 0;
            }
        }

        return produced;
    }
}
