namespace M0LTE.Dsp;

/// <summary>
/// Occupied-bandwidth meter, ITU-R SM.443 definition: the band leaving 0.5 % of total
/// mean power below it and 0.5 % above. This is the figure Nino publishes per NinoTNC
/// mode, so it is the one we have to be measured against.
/// </summary>
/// <remarks>
/// Welch-averaged (Hann, 50 % overlap) rather than a single transform — one window of a
/// modulated carrier is a noisy spectral estimate, and an OBW read off it moves by
/// hundreds of Hz depending which symbols it happened to land on.
/// </remarks>
public static class OccupiedBandwidth
{
    /// <summary>Measures occupied bandwidth.</summary>
    /// <param name="samples">Signal — pass steady-state modulation, not the preamble.</param>
    /// <param name="sampleRate">Sample rate of <paramref name="samples"/>.</param>
    /// <param name="fraction">Power fraction to contain (0.99 = the 99 % OBW).</param>
    /// <param name="fftSize">Transform size; sets the resolution (rate/fftSize per bin).</param>
    /// <returns>Low edge, high edge and width in Hz, plus the peak bin's frequency.</returns>
    public static (double LowHz, double HighHz, double WidthHz, double PeakHz) Measure(
        ReadOnlySpan<float> samples, int sampleRate, double fraction = 0.99, int fftSize = 4096)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(samples.Length, fftSize);

        var window = new float[fftSize];
        for (int i = 0; i < fftSize; i++)
        {
            window[i] = 0.5f - 0.5f * (float)Math.Cos(2 * Math.PI * i / (fftSize - 1));
        }

        var power = new double[fftSize / 2];
        var re = new float[fftSize];
        var im = new float[fftSize];
        int windows = 0;
        for (int offset = 0; offset + fftSize <= samples.Length; offset += fftSize / 2)
        {
            for (int i = 0; i < fftSize; i++)
            {
                re[i] = samples[offset + i] * window[i];
                im[i] = 0;
            }

            Fft.Forward(re, im);
            for (int k = 0; k < power.Length; k++)
            {
                power[k] += (re[k] * (double)re[k]) + (im[k] * (double)im[k]);
            }

            windows++;
        }

        if (windows == 0)
        {
            throw new ArgumentException("not enough samples for one window", nameof(samples));
        }

        double total = power.Sum();
        double tail = (1 - fraction) / 2 * total;
        double binHz = sampleRate / (double)fftSize;

        double acc = 0;
        int lo = 0;
        while (lo < power.Length - 1 && acc + power[lo] < tail)
        {
            acc += power[lo++];
        }

        acc = 0;
        int hi = power.Length - 1;
        while (hi > 0 && acc + power[hi] < tail)
        {
            acc += power[hi--];
        }

        int peak = 0;
        for (int k = 1; k < power.Length; k++)
        {
            if (power[k] > power[peak])
            {
                peak = k;
            }
        }

        return (lo * binHz, hi * binHz, (hi - lo) * binHz, peak * binHz);
    }
}
