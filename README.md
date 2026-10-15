# M0LTE.Dsp

Dependency-free **digital-signal-processing primitives** for .NET, extracted from the
[pdn-soundmodem](https://github.com/packet-net/pdn-soundmodem) packet-radio modem. Nothing
here is radio-specific — it's general-purpose DSP (standard-API / textbook building blocks).

| Type | What it is |
| --- | --- |
| `Fft` | Iterative in-place radix-2 complex FFT (power-of-2 lengths). |
| `FilterDesign` | Windowed-sinc (Hann) FIR design: `LowPass`, `BandPass`, and a `RootRaisedCosine` pulse. |
| `FirFilter` | Streaming FIR filter — per-sample MAC over a circular history. |
| `Decimator` | Anti-aliased FIR decimator (computes the FIR only at output instants). |
| `Upsampler` | Image-rejecting FIR interpolating upsampler — the transmit-side mirror of `Decimator`. |
| `RationalResampler`, `ComplexRationalResampler` | Streaming L/M polyphase rate converter for real audio or interleaved IQ, between any two integer rates (2.048 MS/s to 48 kS/s, 44.1 to 48 kHz). Stated passband ripple and stopband attenuation, reported delay, no drift. |
| `FrequencyShifter` | Analytic-signal (Hilbert) SSB frequency translator — moves a *real* signal by N Hz with no mirror image, at the same rate. |
| `SpectrumSource` | Turns an audio stream into dB-scaled waterfall lines (Hann-windowed FFT, one byte per bin). |
| `OccupiedBandwidth` | ITU-R SM.443 occupied-bandwidth meter (Welch-averaged, Hann, 50 % overlap). |
| `SsbDemodulator` | Single sideband from complex baseband: IQ in, real audio out. The readable reference form — holds the whole signal. |
| `StreamingSsbDemodulator` | The same filter block-at-a-time, in constant memory and ~4× less arithmetic, which is what an unbounded stream or a live SDR needs. Single-precision SIMD inner loop; takes int16, float or double IQ. |

- **Targets** `net10.0`. No third-party package dependencies — just the BCL.
- Public API is **locked by a build-time test** and the package follows
  [Semantic Versioning](https://semver.org/); see [`docs/versioning.md`](docs/versioning.md).

## Install

```sh
dotnet add package M0LTE.Dsp
```

## Quick start

```csharp
using M0LTE.Dsp;

// Radix-2 FFT (in place, real/imaginary spans of equal power-of-2 length):
float[] re = new float[1024];
float[] im = new float[1024];
for (int i = 0; i < re.Length; i++)
    re[i] = MathF.Sin(2 * MathF.PI * 100 * i / re.Length); // a tone at bin 100
Fft.Forward(re, im);

// Anti-aliased ÷4 decimation, 48 kHz → 12 kHz:
var decimator = new Decimator(inputRate: 48000, factor: 4);
float[] input = new float[48000];
float[] output = new float[decimator.MaxOutput(input.Length)];
int produced = decimator.Process(input, output);

// A rate that does not divide: 128 kS/s IQ (2.048 MS/s after a coarse /16) to 48 kS/s.
// Default filter: flat to 0.4 of the lower rate, 80 dB down from its Nyquist.
var resampler = new ComplexRationalResampler(new RationalResamplerOptions
{
    InputRate = 128000,
    OutputRate = 48000,
});
float[] iqIn = new float[2 * 2560];                          // interleaved I,Q: 20 ms
float[] iqOut = new float[2 * resampler.MaxOutput(2560)];
int frames = resampler.Process(iqIn, iqOut);                 // 960 frames
// resampler.DelayOutputSamples is the filter's delay; Reset() after a gap or a retune.

// Design a 1500 Hz low-pass FIR and stream samples through it:
var fir = new FirFilter(FilterDesign.LowPass(cutoffHz: 1500, sampleRate: 12000, taps: 96));
float y = fir.Next(0.5f);

// Occupied bandwidth (ITU-R SM.443), e.g. the 99 % OBW of a captured signal:
var (lowHz, highHz, widthHz, peakHz) = OccupiedBandwidth.Measure(output, sampleRate: 12000);

// SSB from an SDR's complex baseband: 48 kHz IQ in, 12 kHz USB audio out. Because you hold
// the baseband, the receive filter is yours to choose rather than a rig's to impose.
var ssb = new StreamingSsbDemodulator(new SsbDemodulatorOptions
{
    InputRate = 48000,
    OutputRate = 12000,
    Sideband = Sideband.Upper,
    DialHz = 0,               // the receiver is tuned to the dial, so the carrier is at DC
    SsbLowHz = 150, SsbHighHz = 3450,
});
short[] iq = new short[9600];                     // interleaved I,Q,I,Q…
float[] audio = new float[ssb.MaxOutputFor(iq.Length / 2)];
int wrote = ssb.Process(iq, audio);
// Float IQ from an SDR stream goes in as it is, with no copy: interleaved I,Q, counts in frames.
float[] iqFloat = new float[9600];
wrote = ssb.Process(iqFloat, audio);
```

(Exact constructor/method signatures are in the XML docs shipped with the package.)

## Licence

AGPL-3.0-or-later (see [`LICENSE`](LICENSE)). The implementations are standard-API /
textbook DSP — the design choices mirror QtSoundModem but no code was transcribed, so the
package is permissively relicensable and is shipped AGPL-3.0-or-later **by choice** to match
the `M0LTE.*` family. See [`PROVENANCE.md`](PROVENANCE.md).
