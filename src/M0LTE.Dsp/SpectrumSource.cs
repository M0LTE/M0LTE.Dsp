namespace M0LTE.Dsp;

/// <summary>
/// Produces waterfall lines from an audio stream: batches samples to the FFT size, applies
/// a Hann window, and emits one byte per bin (0–255, dB-scaled) covering 0 Hz to half the
/// sample rate. Sized for a browser waterfall: at 12 kHz with the default 4096-point FFT a
/// line is 2048 bytes roughly 3 times a second — a few kB/s per channel before transport
/// encoding, matching the founding research's §7 estimate.
/// </summary>
public sealed class SpectrumSource
{
    private readonly int _fftSize;
    private readonly float[] _window;
    private readonly float[] _buffer;
    private readonly float[] _re;
    private readonly float[] _im;
    private readonly float _floorDb;
    private readonly float _rangeDb;
    private readonly Action<ReadOnlyMemory<byte>> _lineSink;
    private int _filled;

    /// <summary>Creates a source delivering waterfall lines to <paramref name="lineSink"/>.</summary>
    /// <param name="sampleRate">Input sample rate (fixes <see cref="BinWidthHz"/>).</param>
    /// <param name="lineSink">Receives one <see cref="LineLength"/>-byte line per FFT frame.
    /// The memory is reused; consumers must copy if they keep it.</param>
    /// <param name="fftSize">Power-of-2 FFT length. 4096 at 12 kHz ≈ 2.93 Hz bins.</param>
    /// <param name="floorDb">Power level (dBFS) mapped to byte 0.</param>
    /// <param name="rangeDb">Dynamic range mapped onto 0…255.</param>
    public SpectrumSource(
        int sampleRate,
        Action<ReadOnlyMemory<byte>> lineSink,
        int fftSize = 4096,
        float floorDb = -100,
        float rangeDb = 70)
    {
        ArgumentNullException.ThrowIfNull(lineSink);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(sampleRate, 0);
        if (fftSize < 64 || (fftSize & (fftSize - 1)) != 0)
        {
            throw new ArgumentException("fftSize must be a power of two ≥ 64", nameof(fftSize));
        }

        _fftSize = fftSize;
        SampleRate = sampleRate;
        _lineSink = lineSink;
        _floorDb = floorDb;
        _rangeDb = rangeDb;
        _buffer = new float[fftSize];
        _re = new float[fftSize];
        _im = new float[fftSize];
        _line = new byte[fftSize / 2];
        _window = new float[fftSize];
        for (int i = 0; i < fftSize; i++)
        {
            _window[i] = 0.5f - 0.5f * (float)Math.Cos(2 * Math.PI * i / (fftSize - 1));
        }
    }

    private readonly byte[] _line;

    /// <summary>The sample rate the source was created for.</summary>
    public int SampleRate { get; }

    /// <summary>Bytes per emitted line (half the FFT size; bin 0 = DC).</summary>
    public int LineLength => _fftSize / 2;

    /// <summary>Width of one bin in hertz.</summary>
    public double BinWidthHz => (double)SampleRate / _fftSize;

    /// <summary>Feeds audio samples; emits a line every <c>fftSize</c> samples.</summary>
    public void Process(ReadOnlySpan<float> samples)
    {
        foreach (float sample in samples)
        {
            _buffer[_filled++] = sample;
            if (_filled < _fftSize)
            {
                continue;
            }

            _filled = 0;
            for (int i = 0; i < _fftSize; i++)
            {
                _re[i] = _buffer[i] * _window[i];
                _im[i] = 0;
            }

            Fft.Forward(_re, _im);

            // Normalise so a full-scale sine reads ~0 dBFS: Hann coherent gain is 0.5,
            // and a real sine's energy splits between the ± frequency bins.
            float scale = 4f / _fftSize;
            for (int bin = 0; bin < _line.Length; bin++)
            {
                float magnitude = MathF.Sqrt(_re[bin] * _re[bin] + _im[bin] * _im[bin]) * scale;
                float db = 20f * MathF.Log10(magnitude + 1e-12f);
                float scaled = (db - _floorDb) / _rangeDb * 255f;
                _line[bin] = (byte)Math.Clamp((int)scaled, 0, 255);
            }

            _lineSink(_line);
        }
    }
}
