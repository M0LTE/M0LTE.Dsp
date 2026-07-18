using M0LTE.Dsp;

namespace M0LTE.Dsp.Tests;

public class FrequencyShifterTests
{
    private const int SampleRate = 48000;

    private static float[] Tone(double hz, int length = SampleRate)
    {
        var s = new float[length];
        for (int i = 0; i < length; i++)
        {
            s[i] = 0.8f * (float)Math.Sin(2 * Math.PI * hz * i / SampleRate);
        }

        return s;
    }

    // Goertzel power at one frequency, skipping the FIR settling transient.
    private static double PowerAt(ReadOnlySpan<float> x, double hz)
    {
        double w = 2 * Math.PI * hz / SampleRate, c = 2 * Math.Cos(w);
        double s1 = 0, s2 = 0;
        for (int i = 2000; i < x.Length; i++)
        {
            double s0 = x[i] + c * s1 - s2;
            s2 = s1;
            s1 = s0;
        }

        return s1 * s1 + s2 * s2 - c * s1 * s2;
    }

    [Theory]
    [InlineData(1000, 650, 1650)]   // up
    [InlineData(1500, -650, 850)]   // down
    [InlineData(850, 650, 1500)]    // the ARDOP-slot case: 850 → ardopcf's 1500 Hz centre
    public void Shifts_A_Tone_To_The_Target_Frequency(double toneHz, double shiftHz, double expectedHz)
    {
        var shifter = new FrequencyShifter(SampleRate, shiftHz);
        var input = Tone(toneHz);
        var output = new float[input.Length];

        shifter.Process(input, output);

        double atTarget = PowerAt(output, expectedHz);
        double atOriginal = PowerAt(output, toneHz);
        double atImage = PowerAt(output, toneHz - shiftHz); // the wrong-sideband image

        atTarget.Should().BeGreaterThan(100 * atOriginal, "the tone must move to the target frequency");
        atTarget.Should().BeGreaterThan(100 * atImage, "the opposite sideband must be suppressed (no image)");
    }

    [Fact]
    public void Preserves_Amplitude_Within_The_Passband()
    {
        var shifter = new FrequencyShifter(SampleRate, 650);
        var input = Tone(1000);
        var output = new float[input.Length];

        shifter.Process(input, output);

        // RMS of a 0.8-amplitude sine is 0.8/√2 ≈ 0.566; the SSB shift is unity-gain in-band.
        double sum = 0;
        for (int i = 2000; i < output.Length; i++)
        {
            sum += output[i] * (double)output[i];
        }

        double rms = Math.Sqrt(sum / (output.Length - 2000));
        rms.Should().BeApproximately(0.8 / Math.Sqrt(2), 0.08);
    }

    [Fact]
    public void Rejects_A_Shift_At_Or_Above_Nyquist()
    {
        var act = () => new FrequencyShifter(SampleRate, SampleRate / 2.0);
        act.Should().Throw<ArgumentOutOfRangeException>();
    }
}
