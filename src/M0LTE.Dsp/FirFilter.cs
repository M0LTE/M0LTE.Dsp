namespace M0LTE.Dsp;

/// <summary>
/// Streaming FIR filter with per-sample processing over a circular history. Plain scalar
/// MACs — at packet-modem rates (12–48 kHz, ≤256 taps) this is far below one percent of a
/// core; vectorise only if profiling ever says otherwise.
/// </summary>
public sealed class FirFilter
{
    private readonly float[] _coefficients;
    private readonly float[] _history;
    private int _position;

    /// <summary>Creates a filter from design output (see <see cref="FilterDesign"/>).</summary>
    public FirFilter(float[] coefficients)
    {
        ArgumentNullException.ThrowIfNull(coefficients);
        ArgumentOutOfRangeException.ThrowIfZero(coefficients.Length);
        _coefficients = coefficients;
        _history = new float[coefficients.Length];
    }

    /// <summary>Filters one sample.</summary>
    public float Next(float sample)
    {
        _history[_position] = sample;
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

        if (++_position == _history.Length)
        {
            _position = 0;
        }

        return acc;
    }
}
