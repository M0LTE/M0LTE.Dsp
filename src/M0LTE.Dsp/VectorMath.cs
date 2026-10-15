using System.Numerics;
using System.Runtime.InteropServices;

namespace M0LTE.Dsp;

/// <summary>The inner loops the FIR classes share.</summary>
internal static class VectorMath
{
    /// <summary>
    /// The dot product of <paramref name="a"/> with the same length of <paramref name="b"/>, in
    /// single precision, <see cref="Vector{T}"/> wide where the hardware has it.
    /// </summary>
    public static float Dot(ReadOnlySpan<float> a, ReadOnlySpan<float> b)
    {
        float result = 0;
        int i = 0;
        if (Vector.IsHardwareAccelerated && a.Length >= Vector<float>.Count)
        {
            ReadOnlySpan<Vector<float>> va = MemoryMarshal.Cast<float, Vector<float>>(a);
            ReadOnlySpan<Vector<float>> vb = MemoryMarshal.Cast<float, Vector<float>>(b[..a.Length]);
            // Four independent accumulators, so consecutive adds do not wait on each other.
            var acc0 = Vector<float>.Zero;
            var acc1 = Vector<float>.Zero;
            var acc2 = Vector<float>.Zero;
            var acc3 = Vector<float>.Zero;
            int v = 0;
            for (; v + 3 < va.Length; v += 4)
            {
                acc0 += va[v] * vb[v];
                acc1 += va[v + 1] * vb[v + 1];
                acc2 += va[v + 2] * vb[v + 2];
                acc3 += va[v + 3] * vb[v + 3];
            }

            for (; v < va.Length; v++)
            {
                acc0 += va[v] * vb[v];
            }

            result = Vector.Sum((acc0 + acc1) + (acc2 + acc3));
            i = va.Length * Vector<float>.Count;
        }

        for (; i < a.Length; i++)
        {
            result += a[i] * b[i];
        }

        return result;
    }
}
