using System.Diagnostics;
using System.Runtime.InteropServices;
using M0LTE.Dsp;

// Cost per output sample of the rational resampler, for the rate pairs an SDR front end meets.
// The headline case is a 2.048 MS/s source (an RTL dongle or an OpenWebRX profile) brought to
// 48 kS/s: after a coarse integer decimation (/16 to 128 kS/s, or /32 to 64 kS/s) and, for
// comparison, all in one step. Run with: dotnet run -c Release --project bench/M0LTE.Dsp.Bench

(int In, int Out, string Note)[] cases =
[
    (128000, 48000, "2.048 MS/s after a coarse /16"),
    (64000, 48000, "2.048 MS/s after a coarse /32"),
    (2048000, 48000, "2.048 MS/s in one step"),
    (250000, 48000, "2 MS/s after a coarse /8"),
    (44100, 48000, "sound card 44.1 kHz"),
];

Console.WriteLine($"{RuntimeInformation.ProcessArchitecture}, {Environment.ProcessorCount} cores, {RuntimeInformation.FrameworkDescription}, Vector<float> x{System.Numerics.Vector<float>.Count}");
Console.WriteLine("ns per output: real (one stream), IQ (one complex frame). One thread, best of 5.");
Console.WriteLine("x RT: seconds of IQ converted per second of one core.");
Console.WriteLine();
Console.WriteLine($"{"in -> out",-18} {"L/M",-8} {"taps/ph",7} {"real ns",8} {"IQ ns",8} {"x RT",8}  note");

foreach ((int inputRate, int outputRate, string note) in cases)
{
    var options = new RationalResamplerOptions { InputRate = inputRate, OutputRate = outputRate };
    var real = new RationalResampler(options);
    var complex = new ComplexRationalResampler(options);

    // 20 ms blocks, as a live source would deliver them.
    int block = inputRate / 50;
    var input = new float[2 * block];
    var random = new Random(1);
    for (int n = 0; n < input.Length; n++)
    {
        input[n] = (float)(random.NextDouble() - 0.5);
    }

    var realOut = new float[real.MaxOutput(block)];
    var complexOut = new float[2 * complex.MaxOutput(block)];

    double realNs = Best(() => real.Process(input.AsSpan(0, block), realOut));
    double complexNs = Best(() => complex.Process(input, complexOut));
    double realTime = 1e9 / (complexNs * outputRate);
    Console.WriteLine($"{inputRate + " -> " + outputRate,-18} {real.Interpolation + "/" + real.Decimation,-8} {real.TapsPerPhase,7} {realNs,8:0.0} {complexNs,8:0.0} {realTime,8:0}  {note}");
}

// Cost per output sample of the streaming SSB demodulator, for the arrangements a receiver
// meets: audio delivered at the IQ rate (nothing to decimate, every output a full kernel), and
// the decimating cases the class was first written for.
(int In, int Out, double Dial, string Note)[] ssbCases =
[
    (48000, 48000, 0, "IQ rate = DSP rate, tuned to the dial"),
    (48000, 48000, 1500, "IQ rate = DSP rate, dial 1500 Hz off"),
    (48000, 12000, 0, "decimate /4"),
    (48000, 9600, 0, "decimate /5"),
];

Console.WriteLine();
Console.WriteLine("StreamingSsbDemodulator, ns per audio output. One thread, best of 5.");
Console.WriteLine("x RT: seconds of IQ demodulated per second of one core.");
Console.WriteLine();
Console.WriteLine($"{"in -> out",-18} {"dial",6} {"int16 ns",9} {"double ns",9} {"x RT",7}  note");

foreach ((int inputRate, int outputRate, double dial, string note) in ssbCases)
{
    var options = new SsbDemodulatorOptions { InputRate = inputRate, OutputRate = outputRate, DialHz = dial };
    var demodulator = new StreamingSsbDemodulator(options);
    int block = inputRate / 50;
    var random = new Random(2);
    var iq = new float[2 * block];
    var shorts = new short[2 * block];
    var i = new double[block];
    var q = new double[block];
    for (int n = 0; n < block; n++)
    {
        shorts[2 * n] = (short)random.Next(-12000, 12000);
        shorts[(2 * n) + 1] = (short)random.Next(-12000, 12000);
        iq[2 * n] = shorts[2 * n] / 32768f;
        iq[(2 * n) + 1] = shorts[(2 * n) + 1] / 32768f;
        i[n] = iq[2 * n];
        q[n] = iq[(2 * n) + 1];
    }

    var audio = new float[demodulator.MaxOutputFor(block)];
    double shortNs = Best(() => demodulator.Process(shorts, audio));
    double doubleNs = Best(() => demodulator.Process(i, q, audio));
    double realTime = 1e9 / (doubleNs * outputRate);
    Console.WriteLine($"{inputRate + " -> " + outputRate,-18} {dial,6} {shortNs,9:0.0} {doubleNs,9:0.0} {realTime,7:0}  {note}");
}

static double Best(Func<int> processBlock)
{
    // Warm up for a full second, then time 5 runs of 50 blocks and keep the fastest. A count of
    // blocks is not enough: tiered compilation promotes the hot loop on a background thread, and
    // on a busy machine 200 blocks finished before it had, which made the first cases measured
    // look three times dearer than they are.
    var warm = Stopwatch.StartNew();
    while (warm.ElapsedMilliseconds < 1000)
    {
        processBlock();
    }

    double best = double.MaxValue;
    for (int run = 0; run < 5; run++)
    {
        long outputs = 0;
        var watch = Stopwatch.StartNew();
        for (int i = 0; i < 50; i++)
        {
            outputs += processBlock();
        }

        watch.Stop();
        best = Math.Min(best, watch.Elapsed.TotalNanoseconds / outputs);
    }

    return best;
}
