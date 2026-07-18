namespace M0LTE.Dsp;

/// <summary>
/// Translates a real signal up or down in frequency by an arbitrary number of Hz, cleanly (no
/// mirror image), via the analytic-signal single-sideband method: form the analytic signal
/// x + j·H{x} with a Hilbert FIR, mix it by e^{j·2π·shift·t}, and take the real part. A plain
/// real multiply by a cosine would produce both sum and difference images; forming the analytic
/// signal first suppresses one sideband, so the whole spectrum moves in a single direction and no
/// post-filter is needed. Works on a signal of any bandwidth without knowing its band.
/// </summary>
/// <remarks>
/// The in-phase path is delayed by the Hilbert FIR's group delay ((taps−1)/2 samples) so I and Q
/// stay time-aligned; the first few output samples are a settling transient. The rate is
/// unchanged (1:1) — pair with <see cref="Decimator"/>/<see cref="Upsampler"/> to also change rate.
/// A Type III Hilbert FIR has nulls at DC and Nyquist, so keep the signal clear of both.
/// </remarks>
public sealed class FrequencyShifter
{
    private readonly FirFilter _hilbert;
    private readonly float[] _delay;   // group-delay line for the in-phase path
    private readonly int _groupDelay;
    private readonly double _phaseStep;
    private double _phase;
    private int _delayPosition;

    /// <summary>Creates a shifter for <paramref name="sampleRate"/> translating by
    /// <paramref name="shiftHz"/> (positive = up, negative = down).</summary>
    /// <param name="sampleRate">Sample rate of the input and output, Hz.</param>
    /// <param name="shiftHz">Frequency translation, Hz; |shiftHz| must be below Nyquist.</param>
    /// <param name="taps">Hilbert FIR length; forced odd. More taps flatten the response nearer
    /// DC and Nyquist at higher cost.</param>
    public FrequencyShifter(int sampleRate, double shiftHz, int taps = 129)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(sampleRate, 0);
        ArgumentOutOfRangeException.ThrowIfLessThan(taps, 3);
        if (Math.Abs(shiftHz) >= sampleRate / 2.0)
        {
            throw new ArgumentOutOfRangeException(nameof(shiftHz), shiftHz, "|shift| must be below Nyquist");
        }

        if (taps % 2 == 0)
        {
            taps++; // odd length ⇒ integer group delay, so I and Q align exactly
        }

        _groupDelay = (taps - 1) / 2;
        _hilbert = new FirFilter(HilbertTaps(taps));
        _delay = new float[_groupDelay];
        _phaseStep = 2.0 * Math.PI * shiftHz / sampleRate;
    }

    /// <summary>Shifts a block; writes exactly <paramref name="input"/>.Length samples to
    /// <paramref name="output"/>. Output is delayed by the FIR group delay (irrelevant to
    /// decoding).</summary>
    public void Process(ReadOnlySpan<float> input, Span<float> output)
    {
        for (int k = 0; k < input.Length; k++)
        {
            float x = input[k];
            float q = _hilbert.Next(x);              // Hilbert output: x, +90°, delayed by groupDelay

            // In-phase path: x delayed by the same group delay so it aligns with q.
            float i = _delay[_delayPosition];
            _delay[_delayPosition] = x;
            if (++_delayPosition == _delay.Length)
            {
                _delayPosition = 0;
            }

            // Re{(i + jq)·e^{jθ}} = i·cosθ − q·sinθ.
            output[k] = (float)(i * Math.Cos(_phase) - q * Math.Sin(_phase));

            _phase += _phaseStep;
            if (_phase > Math.PI)
            {
                _phase -= 2.0 * Math.PI;
            }
            else if (_phase < -Math.PI)
            {
                _phase += 2.0 * Math.PI;
            }
        }
    }

    // Windowed-sinc Hilbert transformer (Type III): h[n] = 2/(πn) for odd n about the centre,
    // 0 for even n and at the centre, Hann-tapered to control ripple. Standard construction.
    private static float[] HilbertTaps(int taps)
    {
        int centre = (taps - 1) / 2;
        var h = new float[taps];
        for (int i = 0; i < taps; i++)
        {
            int n = i - centre;
            double ideal = n == 0 || n % 2 == 0 ? 0.0 : 2.0 / (Math.PI * n);
            double window = 0.5 - 0.5 * Math.Cos(2.0 * Math.PI * i / (taps - 1));
            h[i] = (float)(ideal * window);
        }

        return h;
    }
}
