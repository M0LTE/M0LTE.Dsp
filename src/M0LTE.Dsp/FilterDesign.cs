namespace M0LTE.Dsp;

/// <summary>
/// Runtime FIR design by the windowed-sinc method with a Hann window — the same approach
/// QtSoundModem uses (its init_LPF/make_core_BPF); coefficients are designed at startup,
/// not baked into tables.
/// </summary>
public static class FilterDesign
{
    /// <summary>Designs a low-pass filter with unity DC gain.</summary>
    public static float[] LowPass(double cutoffHz, double sampleRate, int taps)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(taps, 3);
        var h = new double[taps];
        double fc = cutoffHz / sampleRate;
        double centre = (taps - 1) / 2.0;
        double sum = 0;
        for (int i = 0; i < taps; i++)
        {
            double x = i - centre;
            double sinc = x == 0 ? 2 * fc : Math.Sin(2 * Math.PI * fc * x) / (Math.PI * x);
            double window = 0.5 - 0.5 * Math.Cos(2 * Math.PI * i / (taps - 1));
            h[i] = sinc * window;
            sum += h[i];
        }

        var result = new float[taps];
        for (int i = 0; i < taps; i++)
        {
            result[i] = (float)(h[i] / sum);
        }

        return result;
    }

    /// <summary>Designs a band-pass filter (difference of two low-passes), gain ≈ 1 in band.</summary>
    public static float[] BandPass(double lowHz, double highHz, double sampleRate, int taps)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(highHz, lowHz);
        float[] upper = LowPass(highHz, sampleRate, taps);
        float[] lower = LowPass(lowHz, sampleRate, taps);
        var result = new float[taps];
        for (int i = 0; i < taps; i++)
        {
            result[i] = upper[i] - lower[i];
        }

        return result;
    }

    /// <summary>
    /// Root-raised-cosine pulse, evaluated in symbol units (<paramref name="t"/> = 0 is
    /// the symbol centre, ±1 the neighbouring symbols).
    /// </summary>
    /// <remarks>
    /// This is the shaping a band-limited PSK transmitter needs. Synthesising phase
    /// directly at constant envelope — which is the intuitive way to build PSK, and what
    /// this project did first — produces sidebands roughly twice as wide as the symbol
    /// rate warrants: measured 5344 Hz of 99 % OBW on 1200 sym/s QPSK against a NinoTNC's
    /// 1887 Hz for the same mode. An RRC-shaped signal occupies about
    /// symbolRate·(1 + <paramref name="beta"/>).
    /// </remarks>
    /// <param name="t">Time in symbol periods from the pulse centre.</param>
    /// <param name="beta">Roll-off, 0…1. 0.35 is the usual compromise between occupied
    /// bandwidth and time-domain tail length.</param>
    public static double RootRaisedCosine(double t, double beta)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(beta);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(beta, 1);

        // Both closed-form singularities are removable; evaluate the limits directly
        // rather than let 0/0 through.
        if (Math.Abs(t) < 1e-8)
        {
            return 1 - beta + (4 * beta / Math.PI);
        }

        if (beta > 0 && Math.Abs(Math.Abs(t) - (1 / (4 * beta))) < 1e-8)
        {
            return beta / Math.Sqrt(2) *
                (((1 + (2 / Math.PI)) * Math.Sin(Math.PI / (4 * beta))) +
                 ((1 - (2 / Math.PI)) * Math.Cos(Math.PI / (4 * beta))));
        }

        double numerator = Math.Sin(Math.PI * t * (1 - beta)) +
                           (4 * beta * t * Math.Cos(Math.PI * t * (1 + beta)));
        double denominator = Math.PI * t * (1 - (16 * beta * beta * t * t));
        return numerator / denominator;
    }
}
