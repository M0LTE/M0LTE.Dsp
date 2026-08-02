# Provenance

`M0LTE.Dsp` is a set of digital-signal-processing primitives extracted from the
[pdn-soundmodem](https://github.com/packet-net/pdn-soundmodem) packet-radio modem. Every
component here is a standard-API or textbook DSP building block. The design choices mirror
[QtSoundModem](http://uz7.ho.ua/packetradio.htm) (a GPL project) in *approach* — windowed-sinc
FIR design, a fixed-size radix-2 FFT for the waterfall — but **no code was transcribed**: the
implementations are this project's own. That makes the package permissively relicensable; it
is shipped **AGPL-3.0-or-later by choice** (matching the `M0LTE.*` family) rather than by any
copyleft obligation.

| File | Provenance |
| --- | --- |
| `Fft.cs` | Textbook iterative in-place radix-2 Cooley-Tukey FFT (bit-reversal permutation + butterflies). Independent implementation; kept native C# to avoid an FFTW dependency. |
| `FilterDesign.cs` | The windowed-sinc FIR method with a Hann window (the same *approach* as QtSoundModem's `init_LPF`/`make_core_BPF`), plus the standard closed-form root-raised-cosine pulse (with its removable singularities evaluated as limits). Independent implementation of published formulae. |
| `FirFilter.cs` | A direct-form FIR filter over a circular history — the textbook streaming MAC. |
| `Decimator.cs` | FIR anti-aliased decimator that evaluates the FIR only at output instants (polyphase-equivalent cost). Standard construction; own implementation. |
| `Upsampler.cs` | FIR interpolating upsampler (zero-stuff + image-reject low-pass, gain-compensated) — the transmit-side mirror of `Decimator`. Standard construction; own implementation. |
| `FrequencyShifter.cs` | Analytic-signal (Hilbert) single-sideband frequency translator: a windowed-sinc Type III Hilbert FIR forms the analytic signal, mixed by an NCO with the real part taken. Textbook SSB up/down-conversion; independent implementation. |
| `SpectrumSource.cs` | Hann-windowed FFT framing into dB-scaled waterfall bytes. Own implementation over `Fft`. |
| `OccupiedBandwidth.cs` | The ITU-R SM.443 occupied-bandwidth definition (power fraction with equal tails), estimated by Welch averaging (Hann, 50 % overlap). Own implementation of a published measurement definition. |
| `SsbDemodulator.cs` | Textbook SSB demodulation from complex baseband: NCO shift to put the suppressed carrier at DC, a complex bandpass keeping one sideband, the real part, then decimation. Independent implementation; the readable reference the streaming form is tested against. |
| `StreamingSsbDemodulator.cs` | The same linear filter rearranged for constant memory: the SSB bandpass and the anti-alias low-pass collapsed into one complex kernel evaluated only at output instants (valid because the low-pass is real, so `g * Re{y} = Re{g * y}`). Own work; pinned sample-for-sample against the reference by test. |

No GPL-licensed source code (e.g. QtSoundModem, Dire Wolf) is copied here — only standard
algorithms and published measurement definitions. The modem, framing and codec layers of
pdn-soundmodem (some of which *are* derived from copyleft sources) are deliberately **not**
part of this package.
