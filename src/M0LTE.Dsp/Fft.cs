namespace M0LTE.Dsp;

/// <summary>
/// Iterative in-place radix-2 complex FFT. Power-of-2 lengths only — the waterfall uses a
/// fixed size, so there is no need for arbitrary-length transforms (and keeping it native
/// C# avoids an FFTW dependency).
/// </summary>
public static class Fft
{
    /// <summary>Forward transform, in place. Lengths must be equal powers of two.</summary>
    public static void Forward(Span<float> real, Span<float> imaginary)
    {
        int n = real.Length;
        if (n != imaginary.Length)
        {
            throw new ArgumentException("real and imaginary must be the same length");
        }

        if (n < 2 || (n & (n - 1)) != 0)
        {
            throw new ArgumentException("length must be a power of two", nameof(real));
        }

        // Bit-reversal permutation.
        for (int i = 1, j = 0; i < n; i++)
        {
            int bit = n >> 1;
            for (; (j & bit) != 0; bit >>= 1)
            {
                j ^= bit;
            }

            j |= bit;
            if (i < j)
            {
                (real[i], real[j]) = (real[j], real[i]);
                (imaginary[i], imaginary[j]) = (imaginary[j], imaginary[i]);
            }
        }

        for (int length = 2; length <= n; length <<= 1)
        {
            double angle = -2 * Math.PI / length;
            float wRe = (float)Math.Cos(angle);
            float wIm = (float)Math.Sin(angle);
            for (int start = 0; start < n; start += length)
            {
                float curRe = 1;
                float curIm = 0;
                int half = length >> 1;
                for (int k = 0; k < half; k++)
                {
                    int even = start + k;
                    int odd = even + half;
                    float tRe = real[odd] * curRe - imaginary[odd] * curIm;
                    float tIm = real[odd] * curIm + imaginary[odd] * curRe;
                    real[odd] = real[even] - tRe;
                    imaginary[odd] = imaginary[even] - tIm;
                    real[even] += tRe;
                    imaginary[even] += tIm;

                    float nextRe = curRe * wRe - curIm * wIm;
                    curIm = curRe * wIm + curIm * wRe;
                    curRe = nextRe;
                }
            }
        }
    }
}
