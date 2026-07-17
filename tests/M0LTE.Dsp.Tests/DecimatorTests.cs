using M0LTE.Dsp;

namespace M0LTE.Dsp.Tests;

public class DecimatorTests
{
    [Fact]
    public void A_Tone_Survives_Decimation_At_The_Right_Frequency()
    {
        var decimator = new Decimator(48000, 4);
        var input = new float[48000];
        for (int i = 0; i < input.Length; i++)
        {
            input[i] = 0.8f * (float)Math.Sin(2 * Math.PI * 1700 * i / 48000);
        }

        var output = new float[decimator.MaxOutput(input.Length)];
        int produced = decimator.Process(input, output);

        produced.Should().Be(12000);

        // Confirm the tone lands at 1700 Hz in the 12 kHz output via the spectrum.
        int peakBin = -1;
        var source = new SpectrumSource(12000, line =>
        {
            var copy = line.ToArray();
            peakBin = Array.IndexOf(copy, copy.Max());
        }, fftSize: 4096);
        source.Process(output.AsSpan(4096, 4096));

        (peakBin * source.BinWidthHz).Should().BeApproximately(1700, 6);
    }

    [Fact]
    public void Out_Of_Band_Energy_Is_Suppressed_Not_Aliased()
    {
        var decimator = new Decimator(48000, 4);
        var input = new float[48000];
        for (int i = 0; i < input.Length; i++)
        {
            // 10.3 kHz would alias to 1700 Hz in a 12 kHz output without anti-aliasing.
            input[i] = 0.8f * (float)Math.Sin(2 * Math.PI * 10300 * i / 48000);
        }

        var output = new float[decimator.MaxOutput(input.Length)];
        decimator.Process(input, output);

        float peak = output.Skip(2000).Max(MathF.Abs);
        peak.Should().BeLessThan(0.01f, "the anti-alias filter kills what QtSM's skip-3-in-4 would fold in-band");
    }

    [Fact]
    public void Decimation_Across_Split_Blocks_Matches_One_Shot()
    {
        var random = new Random(1);
        var input = new float[9600];
        for (int i = 0; i < input.Length; i++)
        {
            input[i] = (float)(random.NextDouble() - 0.5);
        }

        var oneShot = new Decimator(48000, 4);
        var expected = new float[oneShot.MaxOutput(input.Length)];
        int expectedCount = oneShot.Process(input, expected);

        var split = new Decimator(48000, 4);
        var actual = new List<float>();
        var buffer = new float[split.MaxOutput(input.Length)];
        for (int position = 0; position < input.Length; position += 700)
        {
            int length = Math.Min(700, input.Length - position);
            int produced = split.Process(input.AsSpan(position, length), buffer);
            actual.AddRange(buffer.Take(produced));
        }

        actual.Should().HaveCount(expectedCount);
        for (int i = 0; i < expectedCount; i++)
        {
            actual[i].Should().BeApproximately(expected[i], 1e-6f, $"sample {i}");
        }
    }
}
