using M0LTE.Dsp;

namespace M0LTE.Dsp.Tests;

/// <summary>
/// Guards the occupied-bandwidth meter itself against known signals: a meter that reads low
/// would silently pass a splattering transmitter. A pure tone occupies ~nothing; two tones
/// 400 Hz apart occupy ~400 Hz. (In pdn-soundmodem this same method reads a real NinoTNC's
/// mode-11 transmission at 1887 Hz against its published 2400 Hz.)
/// </summary>
public class OccupiedBandwidthTests
{
    private const int SampleRate = 12000;

    [Fact]
    public void The_Meter_Agrees_With_A_Known_Signal()
    {
        var tone = new float[SampleRate];
        var pair = new float[SampleRate];
        for (int i = 0; i < tone.Length; i++)
        {
            tone[i] = 0.8f * (float)Math.Sin(2 * Math.PI * 1500 * i / SampleRate);
            pair[i] = 0.4f * ((float)Math.Sin(2 * Math.PI * 1300 * i / SampleRate)
                            + (float)Math.Sin(2 * Math.PI * 1700 * i / SampleRate));
        }

        OccupiedBandwidth.Measure(tone, SampleRate).WidthHz.Should().BeLessThan(30);

        var (lo, hi, width, _) = OccupiedBandwidth.Measure(pair, SampleRate);
        width.Should().BeInRange(380, 420);
        lo.Should().BeApproximately(1300, 20);
        hi.Should().BeApproximately(1700, 20);
    }
}
