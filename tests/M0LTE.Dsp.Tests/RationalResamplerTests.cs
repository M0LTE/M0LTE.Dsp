namespace M0LTE.Dsp.Tests;

public class RationalResamplerTests
{
    /// <summary>Rate pairs the SDR front end meets: sources whose rate we do not choose, after
    /// and without a coarse integer decimation, both directions of the sound-card 44.1/48 pair, and
    /// a plain integer up and down for comparison with <see cref="Decimator"/> and
    /// <see cref="Upsampler"/>.</summary>
    public static TheoryData<int, int> RatePairs => new()
    {
        { 128000, 48000 },   // 2.048 MS/s after a coarse /16
        { 64000, 48000 },    // 2.048 MS/s after a coarse /32
        { 250000, 48000 },   // 2 MS/s (SDRplay) after a coarse /8
        { 44100, 48000 },
        { 48000, 44100 },
        { 48000, 12000 },
        { 12000, 48000 },
        { 37500, 48000 },
    };

    [Theory]
    [MemberData(nameof(RatePairs))]
    public void A_tone_comes_out_at_the_right_frequency_and_level_across_the_passband(int inputRate, int outputRate)
    {
        var resampler = new RationalResampler(new RationalResamplerOptions { InputRate = inputRate, OutputRate = outputRate });
        double ripple = Ratio(resampler.PassbandRippleDb);
        double floor = Math.Pow(10, -resampler.StopbandAttenuationDb / 20);

        foreach (double fraction in new[] { 0.0, 0.01, 0.1, 0.2, 0.3, 0.37, 0.4 })
        {
            double frequency = fraction / 0.4 * resampler.PassbandHz;
            float[] output = Run(resampler, Tone(frequency, inputRate, inputRate / 5, 0.5), inputRate / 5);
            resampler.Reset();

            int skip = (int)Math.Ceiling(2 * resampler.DelayOutputSamples) + 1;
            Fit fit = FitTone(output.AsSpan(skip), frequency, outputRate, skip);

            (fit.Amplitude / 0.5).Should().BeInRange(1 / ripple, ripple,
                "{0} Hz is in the passband, so its level is held to the stated ripple ({1}/{2})",
                frequency, inputRate, outputRate);
            (fit.ResidualRms / (0.5 / Math.Sqrt(2))).Should().BeLessThan(floor,
                "what is left after removing a tone at exactly {0} Hz is images and aliases, and they are held to the stopband figure ({1}/{2})",
                frequency, inputRate, outputRate);
        }
    }

    [Theory]
    [MemberData(nameof(RatePairs))]
    public void Content_that_would_alias_or_image_is_rejected_by_the_stated_figure(int inputRate, int outputRate)
    {
        var resampler = new RationalResampler(new RationalResamplerOptions { InputRate = inputRate, OutputRate = outputRate });
        double floor = Math.Pow(10, -resampler.StopbandAttenuationDb / 20);

        // Everything from the stopband edge up to the input's own Nyquist must not come out.
        double top = inputRate / 2.0;
        int skip = (int)Math.Ceiling(2 * resampler.DelayOutputSamples) + 1;
        for (int step = 0; step <= 8 && resampler.StopbandHz < top; step++)
        {
            double frequency = Math.Min(resampler.StopbandHz + ((top - resampler.StopbandHz) * step / 8.0), top - 1);
            float[] output = Run(resampler, Tone(frequency, inputRate, inputRate / 5, 0.5), inputRate / 5);
            resampler.Reset();

            (Rms(output.AsSpan(skip)) / (0.5 / Math.Sqrt(2))).Should().BeLessThan(floor,
                "{0} Hz is beyond the stopband edge, so whatever it folds to is held to {1} dB ({2}/{3})",
                frequency, resampler.StopbandAttenuationDb, inputRate, outputRate);
        }

        // On an upsampling pair the stopband edge is the input's Nyquist, so every input tone is
        // wanted and the figure applies to its images instead, which land at least as far above
        // the edge as the tone is below it. The passband test covers the passband; this covers
        // the transition band, where a tone and its first image are closest.
        if (outputRate > inputRate)
        {
            for (int step = 0; step <= 8; step++)
            {
                double frequency = Math.Min(resampler.PassbandHz + ((top - resampler.PassbandHz) * step / 8.0), top - 50);
                float[] output = Run(resampler, Tone(frequency, inputRate, inputRate / 5, 0.5), inputRate / 5);
                resampler.Reset();

                Fit fit = FitTone(output.AsSpan(skip), frequency, outputRate, skip);
                (fit.ResidualRms / (0.5 / Math.Sqrt(2))).Should().BeLessThan(floor,
                    "the images of {0} Hz are beyond the stopband edge ({1}/{2})",
                    frequency, inputRate, outputRate);
            }
        }
    }

    [Theory]
    [InlineData(60)]
    [InlineData(100)]
    public void The_stated_figure_follows_the_requested_attenuation(double attenuationDb)
    {
        var resampler = new RationalResampler(new RationalResamplerOptions
        {
            InputRate = 128000,
            OutputRate = 48000,
            StopbandAttenuationDb = attenuationDb,
        });
        double floor = Math.Pow(10, -attenuationDb / 20);

        foreach (double frequency in new[] { 24000.0, 30000.0, 45000.0, 63000.0 })
        {
            float[] output = Run(resampler, Tone(frequency, 128000, 25600, 0.5), 25600);
            resampler.Reset();
            int skip = (int)Math.Ceiling(2 * resampler.DelayOutputSamples) + 1;
            (Rms(output.AsSpan(skip)) / (0.5 / Math.Sqrt(2))).Should().BeLessThan(floor);
        }
    }

    [Fact]
    public void A_complex_resampler_keeps_positive_and_negative_frequencies_apart()
    {
        // 2.048 MS/s IQ after a coarse /16: tones either side of DC must come out where they went
        // in, at level, with no mirror image; and content beyond the output's +-24 kHz must not
        // fold back.
        const int inputRate = 128000;
        const int outputRate = 48000;
        var resampler = new ComplexRationalResampler(new RationalResamplerOptions { InputRate = inputRate, OutputRate = outputRate });
        double ripple = Ratio(resampler.PassbandRippleDb);
        double floor = Math.Pow(10, -resampler.StopbandAttenuationDb / 20);
        const int frames = inputRate / 5;
        int skip = (int)Math.Ceiling(2 * resampler.DelayOutputSamples) + 1;

        foreach (double frequency in new[] { -19200.0, -7000.0, -1.0, 0.0, 2500.0, 12000.0, 19200.0 })
        {
            float[] output = RunComplex(resampler, ComplexTone(frequency, inputRate, frames, 0.5), frames);
            resampler.Reset();
            (double amplitude, double residual) = FitComplexTone(output, frequency, outputRate, skip);
            (amplitude / 0.5).Should().BeInRange(1 / ripple, ripple, "{0} Hz is in the passband", frequency);
            (residual / 0.5).Should().BeLessThan(floor, "nothing but the tone at {0} Hz should come out, its mirror included", frequency);
        }

        foreach (double frequency in new[] { -63000.0, -40000.0, -24000.0, 24000.0, 33000.0, 63000.0 })
        {
            float[] output = RunComplex(resampler, ComplexTone(frequency, inputRate, frames, 0.5), frames);
            resampler.Reset();
            double power = 0;
            int count = 0;
            for (int m = skip; m < output.Length / 2; m++)
            {
                power += (output[2 * m] * output[2 * m]) + (output[(2 * m) + 1] * output[(2 * m) + 1]);
                count++;
            }

            (Math.Sqrt(power / count) / 0.5).Should().BeLessThan(floor, "{0} Hz is beyond the stopband", frequency);
        }
    }

    [Fact]
    public void A_complex_resampler_is_two_real_ones()
    {
        var options = new RationalResamplerOptions { InputRate = 44100, OutputRate = 48000 };
        var complex = new ComplexRationalResampler(options);
        var realI = new RationalResampler(options);
        var realQ = new RationalResampler(options);
        var random = new Random(3);
        const int frames = 20000;
        var iq = new float[2 * frames];
        var i = new float[frames];
        var q = new float[frames];
        for (int n = 0; n < frames; n++)
        {
            i[n] = iq[2 * n] = (float)(random.NextDouble() - 0.5);
            q[n] = iq[(2 * n) + 1] = (float)(random.NextDouble() - 0.5);
        }

        var outIq = new float[2 * complex.MaxOutput(frames)];
        var outI = new float[realI.MaxOutput(frames)];
        var outQ = new float[realQ.MaxOutput(frames)];
        int produced = complex.Process(iq, outIq);
        realI.Process(i, outI).Should().Be(produced);
        realQ.Process(q, outQ).Should().Be(produced);

        for (int m = 0; m < produced; m++)
        {
            outIq[2 * m].Should().Be(outI[m]);
            outIq[(2 * m) + 1].Should().Be(outQ[m]);
        }
    }

    [Fact]
    public void An_hour_of_input_gives_exactly_the_right_count_and_no_timing_drift()
    {
        // An hour of 2.048 MS/s after a coarse /16: 460.8 M samples in, which must be exactly
        // 172.8 M out, with a tone at the end still where the delay figure says it should be.
        // Drift would show as a phase error growing through the hour; there is no accumulator
        // to drift, and this proves it.
        const int inputRate = 128000;
        const int outputRate = 48000;
        const int frequency = 1013;
        const long totalInput = 3600L * inputRate;
        var resampler = new RationalResampler(new RationalResamplerOptions
        {
            InputRate = inputRate,
            OutputRate = outputRate,
            // A cheap filter: what is under test here is the bookkeeping, which is the same for
            // any filter. 40 dB and a wide transition (nine taps a phase) keep the hour to
            // seconds; the tone at 1013 Hz is well inside the passband either way.
            StopbandAttenuationDb = 40,
            PassbandHz = 5000,
            StopbandHz = 43000,
        });

        // 1013 Hz at 128 kHz repeats exactly every 128000 samples; a table keeps 460.8 M cosines
        // from being most of the test's run time.
        var period = new float[inputRate];
        for (int n = 0; n < inputRate; n++)
        {
            period[n] = (float)Math.Cos(2 * Math.PI * ((long)frequency * n % inputRate) / inputRate);
        }

        const int block = 65536;
        var input = new float[block];
        var output = new float[resampler.MaxOutput(block)];
        long fed = 0;
        long produced = 0;
        double worst = 0;
        double worstStart = 0;
        int settle = (int)Math.Ceiling(2 * resampler.DelayOutputSamples) + 1;
        while (fed < totalInput)
        {
            int count = (int)Math.Min(block, totalInput - fed);
            for (int n = 0; n < count;)
            {
                int at = (int)((fed + n) % inputRate);
                int run = Math.Min(count - n, inputRate - at);
                period.AsSpan(at, run).CopyTo(input.AsSpan(n));
                n += run;
            }

            int wrote = resampler.Process(input.AsSpan(0, count), output);
            bool first = produced < 48000;
            bool last = fed + count >= totalInput - inputRate;
            if (first || last)
            {
                for (int m = 0; m < wrote; m++)
                {
                    long index = produced + m;
                    if (index < settle)
                    {
                        continue;
                    }

                    double expected = ExpectedCosine(frequency, inputRate, resampler, index);
                    double error = Math.Abs(output[m] - expected);
                    if (first)
                    {
                        worstStart = Math.Max(worstStart, error);
                    }
                    else
                    {
                        worst = Math.Max(worst, error);
                    }
                }
            }

            fed += count;
            produced += wrote;
        }

        resampler.InputCount.Should().Be(totalInput);
        produced.Should().Be(3600L * outputRate);
        resampler.OutputCount.Should().Be(produced);
        worstStart.Should().BeLessThan(0.02, "the delay figure places the output on the input's time line");
        worst.Should().BeLessThan(0.02, "an hour later the output is on the same time line: no drift");
    }

    [Fact]
    public void The_output_count_is_exact_and_within_MaxOutput_for_any_blocking()
    {
        foreach ((int inputRate, int outputRate) in new[] { (128000, 48000), (44100, 48000), (12000, 48000), (2048000, 48000) })
        {
            var resampler = new RationalResampler(new RationalResamplerOptions
            {
                InputRate = inputRate,
                OutputRate = outputRate,
                StopbandAttenuationDb = 40,
            });
            var random = new Random(inputRate);
            var input = new float[5000];
            var output = new float[resampler.MaxOutput(input.Length)];
            long fed = 0;
            long produced = 0;
            for (int block = 0; block < 2000; block++)
            {
                int length = random.Next(0, input.Length + 1);
                int wrote = resampler.Process(input.AsSpan(0, length), output);
                wrote.Should().BeLessThanOrEqualTo(resampler.MaxOutput(length));
                fed += length;
                produced += wrote;
                long l = resampler.Interpolation;
                long m = resampler.Decimation;
                produced.Should().Be(((fed * l) + m - 1) / m, "after n inputs exactly ceil(n L / M) outputs exist");
            }
        }
    }

    [Fact]
    public void Splitting_the_input_into_blocks_does_not_change_the_output()
    {
        var options = new RationalResamplerOptions { InputRate = 128000, OutputRate = 48000 };
        var random = new Random(7);
        var input = new float[50000];
        for (int n = 0; n < input.Length; n++)
        {
            input[n] = (float)(random.NextDouble() - 0.5);
        }

        var oneShot = new RationalResampler(options);
        var expected = new float[oneShot.MaxOutput(input.Length)];
        int expectedCount = oneShot.Process(input, expected);

        var split = new RationalResampler(options);
        var actual = new List<float>();
        var buffer = new float[split.MaxOutput(input.Length)];
        int position = 0;
        while (position < input.Length)
        {
            int length = Math.Min(random.Next(0, 300), input.Length - position);
            int wrote = split.Process(input.AsSpan(position, length), buffer);
            actual.AddRange(buffer.Take(wrote));
            position += length;
        }

        actual.Should().Equal(expected.Take(expectedCount));
    }

    [Fact]
    public void Reset_starts_again_from_scratch()
    {
        var options = new RationalResamplerOptions { InputRate = 44100, OutputRate = 48000 };
        var random = new Random(9);
        var input = new float[3000];
        for (int n = 0; n < input.Length; n++)
        {
            input[n] = (float)(random.NextDouble() - 0.5);
        }

        var fresh = new RationalResampler(options);
        var expected = new float[fresh.MaxOutput(input.Length)];
        int expectedCount = fresh.Process(input, expected);

        var reused = new RationalResampler(options);
        var scratch = new float[reused.MaxOutput(1234)];
        reused.Process(input.AsSpan(0, 1234), scratch);
        reused.Reset();
        reused.InputCount.Should().Be(0);
        reused.OutputCount.Should().Be(0);
        var actual = new float[reused.MaxOutput(input.Length)];
        reused.Process(input, actual).Should().Be(expectedCount);
        actual.Should().Equal(expected);
    }

    [Fact]
    public void The_steady_state_allocates_nothing()
    {
        var real = new RationalResampler(new RationalResamplerOptions { InputRate = 128000, OutputRate = 48000 });
        var complex = new ComplexRationalResampler(new RationalResamplerOptions { InputRate = 128000, OutputRate = 48000 });
        const int frames = 8192;
        var input = new float[2 * frames];
        for (int n = 0; n < input.Length; n++)
        {
            input[n] = MathF.Sin(n * 0.01f);
        }

        var realOut = new float[real.MaxOutput(frames)];
        var complexOut = new float[2 * complex.MaxOutput(frames)];
        real.Process(input.AsSpan(0, frames), realOut);
        complex.Process(input, complexOut);

        long before = GC.GetAllocatedBytesForCurrentThread();
        int produced = 0;
        for (int block = 0; block < 32; block++)
        {
            produced += real.Process(input.AsSpan(0, frames), realOut);
            produced += complex.Process(input, complexOut);
        }

        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        produced.Should().BeGreaterThan(0);
        allocated.Should().Be(0);
    }

    [Fact]
    public void The_ratio_is_reduced_and_reported()
    {
        var resampler = new RationalResampler(new RationalResamplerOptions { InputRate = 2048000, OutputRate = 48000 });
        resampler.Interpolation.Should().Be(3);
        resampler.Decimation.Should().Be(128);
        resampler.PrototypeTaps.Should().Be(resampler.Interpolation * resampler.TapsPerPhase);
        resampler.DelayInputSamples.Should().BeApproximately((resampler.PrototypeTaps - 1) / 2.0 / 3, 1e-9);
        resampler.DelayOutputSamples.Should().BeApproximately((resampler.PrototypeTaps - 1) / 2.0 / 128, 1e-9);
        resampler.PassbandHz.Should().Be(19200);
        resampler.StopbandHz.Should().Be(24000);
        resampler.PassbandRippleDb.Should().BeLessThan(0.001);
    }

    [Fact]
    public void Bad_options_are_refused()
    {
        Action stopBelowPass = () => new RationalResampler(new RationalResamplerOptions { PassbandHz = 4000, StopbandHz = 3000 });
        Action aliasIntoPassband = () => new RationalResampler(new RationalResamplerOptions { InputRate = 48000, OutputRate = 12000, PassbandHz = 4000, StopbandHz = 9000 });
        Action zeroRate = () => new RationalResampler(new RationalResamplerOptions { InputRate = 0 });
        Action hopelessRatio = () => new RationalResampler(new RationalResamplerOptions { InputRate = 2048123, OutputRate = 48000 });

        stopBelowPass.Should().Throw<ArgumentException>();
        aliasIntoPassband.Should().Throw<ArgumentException>();
        zeroRate.Should().Throw<ArgumentException>();
        hopelessRatio.Should().Throw<ArgumentException>().WithMessage("*MaxPrototypeTaps*");
    }

    [Fact]
    public void Equal_rates_are_a_linear_phase_low_pass()
    {
        var resampler = new RationalResampler(new RationalResamplerOptions { InputRate = 48000, OutputRate = 48000 });
        resampler.Interpolation.Should().Be(1);
        resampler.Decimation.Should().Be(1);
        resampler.MaxOutput(1000).Should().Be(1000);
        float[] output = Run(resampler, Tone(1000, 48000, 9600, 0.5), 9600);
        output.Should().HaveCount(9600);
    }

    private static double Ratio(double db) => Math.Pow(10, db / 20);

    private static double ExpectedCosine(int frequency, int inputRate, RationalResampler resampler, long outputIndex)
    {
        // Output m sits at input time m * M / L - delay. Cycles = f * t / fs, reduced exactly in
        // integers before the fractional delay term is added, so an hour in does not lose
        // precision to a large phase.
        long l = resampler.Interpolation;
        long m = resampler.Decimation;
        long numerator = frequency * outputIndex * m;
        long denominator = (long)inputRate * l;
        double cycles = (double)(numerator % denominator) / denominator
            - (frequency * resampler.DelayInputSamples / inputRate);
        return Math.Cos(2 * Math.PI * cycles);
    }

    private static float[] Tone(double frequency, int rate, int count, double amplitude)
    {
        var samples = new float[count];
        for (int n = 0; n < count; n++)
        {
            samples[n] = (float)(amplitude * Math.Cos((2 * Math.PI * frequency * n / rate) + 0.3));
        }

        return samples;
    }

    private static float[] ComplexTone(double frequency, int rate, int frames, double amplitude)
    {
        var samples = new float[2 * frames];
        for (int n = 0; n < frames; n++)
        {
            double phase = (2 * Math.PI * frequency * n / rate) + 0.3;
            samples[2 * n] = (float)(amplitude * Math.Cos(phase));
            samples[(2 * n) + 1] = (float)(amplitude * Math.Sin(phase));
        }

        return samples;
    }

    private static float[] Run(RationalResampler resampler, float[] input, int count)
    {
        var output = new float[resampler.MaxOutput(count)];
        int wrote = resampler.Process(input.AsSpan(0, count), output);
        return output[..wrote];
    }

    private static float[] RunComplex(ComplexRationalResampler resampler, float[] input, int frames)
    {
        var output = new float[2 * resampler.MaxOutput(frames)];
        int wrote = resampler.Process(input.AsSpan(0, 2 * frames), output);
        return output[..(2 * wrote)];
    }

    private static double Rms(ReadOnlySpan<float> samples)
    {
        double sum = 0;
        foreach (float s in samples)
        {
            sum += s * s;
        }

        return Math.Sqrt(sum / samples.Length);
    }

    private readonly record struct Fit(double Amplitude, double ResidualRms);

    /// <summary>Least-squares fit of a*cos + b*sin at a known frequency (with a DC term, which a
    /// 0 Hz tone needs), solved exactly rather than assuming the bases are orthogonal over the
    /// window. Returns the tone's amplitude and the RMS of what the fit does not explain.</summary>
    private static Fit FitTone(ReadOnlySpan<float> y, double frequency, int rate, int offset)
    {
        if (frequency == 0)
        {
            double mean = 0;
            foreach (float s in y)
            {
                mean += s;
            }

            mean /= y.Length;
            double res = 0;
            foreach (float s in y)
            {
                res += (s - mean) * (s - mean);
            }

            // A "tone" at 0 Hz with phase 0.3 is the constant amplitude * cos(0.3).
            return new Fit(Math.Abs(mean / Math.Cos(0.3)), Math.Sqrt(res / y.Length));
        }

        double scc = 0, sss = 0, scs = 0, syc = 0, sys = 0;
        for (int n = 0; n < y.Length; n++)
        {
            double w = 2 * Math.PI * frequency * (n + offset) / rate;
            double c = Math.Cos(w);
            double s = Math.Sin(w);
            scc += c * c;
            sss += s * s;
            scs += c * s;
            syc += y[n] * c;
            sys += y[n] * s;
        }

        double det = (scc * sss) - (scs * scs);
        double a = ((syc * sss) - (sys * scs)) / det;
        double b = ((sys * scc) - (syc * scs)) / det;
        double residual = 0;
        for (int n = 0; n < y.Length; n++)
        {
            double w = 2 * Math.PI * frequency * (n + offset) / rate;
            double e = y[n] - (a * Math.Cos(w)) - (b * Math.Sin(w));
            residual += e * e;
        }

        return new Fit(Math.Sqrt((a * a) + (b * b)), Math.Sqrt(residual / y.Length));
    }

    /// <summary>Projects interleaved IQ onto exp(j w n): the complex amplitude, and the RMS
    /// magnitude of everything else.</summary>
    private static (double Amplitude, double Residual) FitComplexTone(float[] iq, double frequency, int rate, int skip)
    {
        int frames = iq.Length / 2;
        double re = 0, im = 0;
        int count = 0;
        for (int n = skip; n < frames; n++)
        {
            double w = 2 * Math.PI * frequency * n / rate;
            double c = Math.Cos(w);
            double s = Math.Sin(w);

            // (i + jq) * exp(-jw)
            re += (iq[2 * n] * c) + (iq[(2 * n) + 1] * s);
            im += (iq[(2 * n) + 1] * c) - (iq[2 * n] * s);
            count++;
        }

        re /= count;
        im /= count;
        double residual = 0;
        for (int n = skip; n < frames; n++)
        {
            double w = 2 * Math.PI * frequency * n / rate;
            double c = Math.Cos(w);
            double s = Math.Sin(w);
            double er = iq[2 * n] - ((re * c) - (im * s));
            double ei = iq[(2 * n) + 1] - ((re * s) + (im * c));
            residual += (er * er) + (ei * ei);
        }

        return (Math.Sqrt((re * re) + (im * im)), Math.Sqrt(residual / count));
    }
}
