namespace M0LTE.Dsp.Tests;

/// <summary>
/// The SSB demodulators: that the streaming form is the reference form rearranged and not merely
/// something similar, that block boundaries are invisible, and — the assertion that matters most —
/// that "upper sideband" means the upper sideband of the real spectrum.
/// </summary>
public class SsbDemodulatorTests
{
    private const int Fs = 48000;

    /// <summary>A complex exponential at an exact baseband frequency, as int16 stereo IQ.</summary>
    private static short[] ComplexTone(double hz, int samples, double amplitude = 0.5)
    {
        var iq = new short[samples * 2];
        for (int k = 0; k < samples; k++)
        {
            double ph = 2.0 * Math.PI * hz * k / Fs;
            iq[2 * k] = (short)Math.Round(amplitude * Math.Cos(ph) * 32767.0);
            iq[(2 * k) + 1] = (short)Math.Round(amplitude * Math.Sin(ph) * 32767.0);
        }

        return iq;
    }

    private static short[] NoisyIq(int samples, int seed)
    {
        var rng = new Random(seed);
        var iq = new short[samples * 2];
        for (int k = 0; k < iq.Length; k++)
        {
            iq[k] = (short)rng.Next(-12000, 12000);
        }

        return iq;
    }

    /// <summary>Runs the streaming demodulator over a signal in blocks of the given size.</summary>
    private static float[] Stream(short[] interleaved, SsbDemodulatorOptions options, int blockFrames)
    {
        var demodulator = new StreamingSsbDemodulator(options);
        var output = new List<float>();
        var buffer = new float[demodulator.MaxOutputFor(blockFrames) + demodulator.MaxFlushOutput];

        int frames = interleaved.Length / 2;
        for (int start = 0; start < frames; start += blockFrames)
        {
            int n = Math.Min(blockFrames, frames - start);
            int wrote = demodulator.Process(interleaved.AsSpan(start * 2, n * 2), buffer);
            output.AddRange(buffer.AsSpan(0, wrote).ToArray());
        }

        output.AddRange(buffer.AsSpan(0, demodulator.Flush(buffer)).ToArray());
        return [.. output];
    }

    private static double Rms(ReadOnlySpan<float> x)
    {
        double sum = 0;
        foreach (float v in x)
        {
            sum += v * (double)v;
        }

        return Math.Sqrt(sum / Math.Max(1, x.Length));
    }

    /// <summary>Amplitude of the component at <paramref name="hz"/> in real audio, by direct
    /// correlation — no FFT, no windowing convention to get wrong.</summary>
    private static double ToneAmplitude(ReadOnlySpan<float> audio, double hz, int rate, int skip)
    {
        double re = 0, im = 0;
        int n = audio.Length - skip;
        for (int k = 0; k < n; k++)
        {
            double ph = 2.0 * Math.PI * hz * k / rate;
            re += audio[skip + k] * Math.Cos(ph);
            im += audio[skip + k] * Math.Sin(ph);
        }

        return 2.0 * Math.Sqrt((re * re) + (im * im)) / n;
    }

    private static float[] Demodulate(Type form, short[] iq, SsbDemodulatorOptions options) =>
        form == typeof(SsbDemodulator)
            ? new SsbDemodulator(options).Convert(iq)
            : Stream(iq, options, blockFrames: 8192);

    [Theory]
    [InlineData(0.0)]
    [InlineData(1200.0)]
    [InlineData(-900.0)]
    public void The_streaming_form_matches_the_reference_sample_for_sample(double dialHz)
    {
        // The reference is the readable statement of what the operation is; the streaming one is
        // the fast, constant-memory rearrangement of the same filter. If they ever disagree, the
        // rearrangement is wrong — so the test is equivalence, not plausibility.
        short[] iq = NoisyIq(48000 + 137, seed: 3);
        var options = new SsbDemodulatorOptions { DialHz = dialHz, NormalisePeak = 0f };

        float[] reference = new SsbDemodulator(options).Convert(iq);
        float[] streamed = Stream(iq, options, blockFrames: 4096);

        streamed.Length.Should().Be(reference.Length);

        var error = new float[reference.Length];
        for (int k = 0; k < reference.Length; k++)
        {
            error[k] = streamed[k] - reference[k];
        }

        // Where the error lives is the diagnosis, so report it: a difference confined to the
        // first few samples is a filter-startup convention, one spread throughout is a wrong
        // kernel, and one at the end is the flush.
        int first = -1, last = -1;
        for (int k = 0; k < error.Length; k++)
        {
            if (Math.Abs(error[k]) > 1e-5)
            {
                first = first < 0 ? k : first;
                last = k;
            }
        }

        Rms(error).Should().BeLessThan(1e-6,
            "the streaming form is the same linear filter evaluated at output instants, so only "
            + "double-versus-float rounding may separate them (of {0} samples, the first differing "
            + "is {1} and the last {2}; signal RMS {3:F4})",
            error.Length, first, last, Rms(reference));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(3)]        // not a multiple of the decimation factor
    [InlineData(5)]
    [InlineData(997)]
    [InlineData(1 << 15)]
    public void The_result_does_not_depend_on_how_the_input_is_blocked(int blockFrames)
    {
        // A stream arrives in whatever chunks the reader hands out, and a signal will straddle
        // them. Any filter state that reset at a block boundary would show up here.
        short[] iq = NoisyIq(20011, seed: 11);
        var options = new SsbDemodulatorOptions { DialHz = 300, NormalisePeak = 0f };

        float[] whole = Stream(iq, options, blockFrames: iq.Length / 2);
        float[] blocked = Stream(iq, options, blockFrames);

        blocked.Should().Equal(whole);
    }

    /// <summary>
    /// That "upper sideband" means the upper sideband of the real spectrum, not whichever one the
    /// test synthesised.
    /// </summary>
    /// <remarks>
    /// Both demodulators once carried a reversed-kernel sideband inversion, and it survived
    /// because every test synthesised its input with the same convention it decoded with — the two
    /// errors cancelled and payloads came back bit-exact. The input here is a bare complex
    /// exponential at an exact frequency, which has no convention to share: at +2000 Hz it is
    /// inside the SSB passband and must appear in the audio at 2000 Hz; at −2000 Hz it is the
    /// image the bandpass exists to reject and must not appear at all.
    /// </remarks>
    [Theory]
    [InlineData(typeof(SsbDemodulator))]
    [InlineData(typeof(StreamingSsbDemodulator))]
    public void On_the_upper_sideband_it_keeps_the_upper_half_and_rejects_the_lower(Type form)
    {
        var options = new SsbDemodulatorOptions { NormalisePeak = 0f };
        const double tone = 2000.0;
        const double amplitude = 0.5;
        int samples = Fs * 2;

        float[] wanted = Demodulate(form, ComplexTone(+tone, samples, amplitude), options);
        float[] image = Demodulate(form, ComplexTone(-tone, samples, amplitude), options);

        // Settle past the filter's transient before measuring.
        int skip = 9600 / 4;
        ToneAmplitude(wanted, tone, options.OutputRate, skip).Should().BeApproximately(amplitude, 0.02,
            "a +2000 Hz baseband component is inside the 150-3450 Hz USB passband and must come "
            + "through at 2000 Hz in the audio");

        Rms(image.AsSpan(skip)).Should().BeLessThan(amplitude / 100.0,
            "a −2000 Hz baseband component is the sideband the complex bandpass exists to reject; "
            + "if it survives, the filter is selecting the wrong sideband");
    }

    /// <summary>
    /// LSB is the same filter looking the other way. A receiver on the lower sideband must hear a
    /// −2000 Hz baseband component at 2000 Hz of audio, and must not hear the +2000 Hz one — the
    /// exact mirror of the USB case, which is the only thing that makes "sideband" mean anything.
    /// </summary>
    [Theory]
    [InlineData(typeof(SsbDemodulator))]
    [InlineData(typeof(StreamingSsbDemodulator))]
    public void On_the_lower_sideband_it_keeps_the_lower_half_and_rejects_the_upper(Type form)
    {
        var options = new SsbDemodulatorOptions { Sideband = Sideband.Lower, NormalisePeak = 0f };
        const double tone = 2000.0;
        const double amplitude = 0.5;
        int samples = Fs * 2;

        float[] wanted = Demodulate(form, ComplexTone(-tone, samples, amplitude), options);
        float[] image = Demodulate(form, ComplexTone(+tone, samples, amplitude), options);

        int skip = 9600 / 4;
        ToneAmplitude(wanted, tone, options.OutputRate, skip).Should().BeApproximately(amplitude, 0.02,
            "on LSB the RF is dial − audio, so a −2000 Hz baseband component is 2000 Hz of audio");

        Rms(image.AsSpan(skip)).Should().BeLessThan(amplitude / 100.0,
            "the +2000 Hz component is the image LSB rejects; if it survives, \"sideband\" is "
            + "being ignored rather than acted on");
    }

    /// <summary>
    /// A caller whose DSP runs at the IQ rate asks for decimation by one. That path skips the
    /// anti-alias low-pass — there is nothing to alias — and this pins that both forms skip it
    /// together, because their agreement is what the equivalence test above rests on.
    /// </summary>
    [Fact]
    public void Delivering_audio_at_the_input_rate_needs_no_decimation_and_still_matches_the_reference()
    {
        short[] iq = NoisyIq(24000 + 61, seed: 21);
        var options = new SsbDemodulatorOptions { OutputRate = Fs, NormalisePeak = 0f };

        float[] reference = new SsbDemodulator(options).Convert(iq);
        float[] streamed = Stream(iq, options, blockFrames: 4096);

        streamed.Length.Should().Be(reference.Length);
        var error = new float[reference.Length];
        for (int k = 0; k < reference.Length; k++)
        {
            error[k] = streamed[k] - reference[k];
        }

        Rms(error).Should().BeLessThan(1e-6);
    }

    /// <summary>At factor 1 the passband must still be the one that was asked for — an anti-alias
    /// filter left in the chain would round the top of it off by several hundred Hz.</summary>
    [Theory]
    [InlineData(500.0)]
    [InlineData(3000.0)]
    public void At_the_input_rate_the_passband_is_the_one_that_was_asked_for(double tone)
    {
        var options = new SsbDemodulatorOptions { OutputRate = Fs, NormalisePeak = 0f };
        const double amplitude = 0.5;

        float[] audio = Stream(ComplexTone(tone, Fs * 2, amplitude), options, blockFrames: 8192);

        ToneAmplitude(audio, tone, options.OutputRate, skip: Fs / 4)
            .Should().BeApproximately(amplitude, 0.02,
                "{0} Hz is inside the 150-3450 Hz passband and nothing else should be attenuating it",
                tone);
    }

    [Fact]
    public void Demodulating_an_hour_costs_no_more_memory_than_a_second()
    {
        // The whole point of the streaming form. An hour of 48 kHz IQ is 172.8 M frames; the
        // reference would ask for several 1.4 GB double[] and be killed. Here the steady-state
        // allocation is asserted directly: after warm-up, further blocks must allocate nothing.
        var demodulator = new StreamingSsbDemodulator(new SsbDemodulatorOptions { NormalisePeak = 0f });
        const int blockFrames = 1 << 16;
        short[] input = NoisyIq(blockFrames, seed: 5);
        var output = new float[demodulator.MaxOutputFor(blockFrames) + demodulator.MaxFlushOutput];

        demodulator.Process(input, output); // warm up: JIT, first-touch
        long before = GC.GetAllocatedBytesForCurrentThread();
        int produced = 0;
        for (int block = 0; block < 16; block++)
        {
            produced += demodulator.Process(input, output);
        }

        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        produced.Should().BeGreaterThan(0);
        allocated.Should().Be(0, "the steady-state path must not allocate, or an hour is not affordable");
    }

    [Fact]
    public void An_output_rate_that_does_not_divide_the_input_is_refused()
    {
        var options = new SsbDemodulatorOptions { InputRate = 48000, OutputRate = 11025 };

        Action reference = () => new SsbDemodulator(options).Convert(new short[64]);
        Action streaming = () => new StreamingSsbDemodulator(options);

        reference.Should().Throw<ArgumentException>();
        streaming.Should().Throw<ArgumentException>();
    }
}
