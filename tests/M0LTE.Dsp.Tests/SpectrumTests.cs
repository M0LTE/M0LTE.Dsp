using M0LTE.Dsp;

namespace M0LTE.Dsp.Tests;

public class SpectrumTests
{
    [Fact]
    public void Fft_Locates_A_Pure_Tone()
    {
        const int n = 1024;
        var re = new float[n];
        var im = new float[n];
        for (int i = 0; i < n; i++)
        {
            re[i] = MathF.Sin(2 * MathF.PI * 100 * i / n); // exactly bin 100
        }

        Fft.Forward(re, im);

        int peak = 0;
        float best = 0;
        for (int bin = 0; bin < n / 2; bin++)
        {
            float magnitude = re[bin] * re[bin] + im[bin] * im[bin];
            if (magnitude > best)
            {
                best = magnitude;
                peak = bin;
            }
        }

        peak.Should().Be(100);
        MathF.Sqrt(best).Should().BeApproximately(n / 2f, n / 200f); // sine amplitude 1 → N/2
    }

    [Fact]
    public void Fft_Of_An_Impulse_Is_Flat()
    {
        var re = new float[256];
        var im = new float[256];
        re[0] = 1;

        Fft.Forward(re, im);

        for (int bin = 0; bin < 256; bin++)
        {
            re[bin].Should().BeApproximately(1f, 1e-4f, $"bin {bin}");
            im[bin].Should().BeApproximately(0f, 1e-4f, $"bin {bin}");
        }
    }

    [Fact]
    public void Non_Power_Of_Two_Lengths_Are_Rejected()
    {
        var act = () => Fft.Forward(new float[100], new float[100]);
        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Spectrum_Lines_Show_A_Tone_At_The_Right_Bin()
    {
        const int sampleRate = 12000;
        const double tone = 1700;
        var lines = new List<byte[]>();
        var source = new SpectrumSource(sampleRate, line => lines.Add(line.ToArray()), fftSize: 4096);

        var samples = new float[3 * 4096];
        for (int i = 0; i < samples.Length; i++)
        {
            samples[i] = 0.8f * (float)Math.Sin(2 * Math.PI * tone * i / sampleRate);
        }

        source.Process(samples);

        lines.Should().HaveCount(3);
        source.LineLength.Should().Be(2048);
        source.BinWidthHz.Should().BeApproximately(2.93, 0.01);

        byte[] line = lines[^1];
        int peak = Array.IndexOf(line, line.Max());
        (peak * source.BinWidthHz).Should().BeApproximately(tone, source.BinWidthHz * 2);
        line[peak].Should().BeGreaterThan(200, "a near-full-scale tone sits near the top of the range");

        // Bins far from the tone are near the floor.
        line[100].Should().BeLessThan(60);
        line[1800].Should().BeLessThan(60);
    }

    [Fact]
    public void Silence_Produces_A_Floor_Line()
    {
        var lines = new List<byte[]>();
        var source = new SpectrumSource(12000, line => lines.Add(line.ToArray()), fftSize: 1024);
        source.Process(new float[1024]);

        lines.Should().ContainSingle().Which.Should().OnlyContain(value => value == 0);
    }

    [Fact]
    public void Lines_Are_Emitted_Once_Per_Fft_Frame_Across_Split_Blocks()
    {
        int count = 0;
        var source = new SpectrumSource(12000, _ => count++, fftSize: 1024);
        for (int i = 0; i < 10; i++)
        {
            source.Process(new float[400]);
        }

        count.Should().Be(3); // 4000 samples → 3 complete 1024-sample frames
    }
}
