using M0LTE.Dsp;

namespace M0LTE.Dsp.Tests;

public class UpsamplerTests
{
    [Fact]
    public void A_Tone_Survives_With_Images_Rejected()
    {
        var upsampler = new Upsampler(48000, 4);
        var input = new float[12000];
        for (int i = 0; i < input.Length; i++)
        {
            input[i] = 0.8f * (float)Math.Sin(2 * Math.PI * 1700 * i / 12000);
        }

        var output = new float[upsampler.OutputLength(input.Length)];
        upsampler.Process(input, output);

        // Peak lands at 1700 Hz in the 48 kHz spectrum; the first image (10300 Hz) is gone.
        int peakBin = -1;
        byte level10300 = 255;
        var source = new SpectrumSource(48000, line =>
        {
            var copy = line.ToArray();
            peakBin = Array.IndexOf(copy, copy.Max());
            level10300 = copy[(int)(10300 / (48000.0 / 4096))];
        }, fftSize: 4096);
        source.Process(output.AsSpan(8192, 4096));

        (peakBin * 48000.0 / 4096).Should().BeApproximately(1700, 24);
        level10300.Should().BeLessThan(60, "the zero-stuffing image must be filtered out");
    }
}
