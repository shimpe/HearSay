using System.Collections.Concurrent;
using System.Numerics;

namespace HearSay.Dsp;

/// <summary>
/// Precomputed tables for an in-place radix-2 complex FFT of one size. Plans are immutable after
/// construction and cached per size, so they can be shared freely between threads. The butterflies are vectorized
/// with <see cref="Vector{T}"/>; only IEEE add, subtract and multiply are used, so the vectorized and the scalar path
/// produce bit-identical results.
/// </summary>
public sealed class FftPlan
{
    private static readonly ConcurrentDictionary<int, FftPlan> Cache = new();

    private readonly int[] bitReversal;

    // Twiddle factors per stage, stored contiguously: the stage with half-length h uses entries [h, 2h).
    private readonly float[] stageCos;
    private readonly float[] stageSin;

    private FftPlan(int size)
    {
        Size = size;
        bitReversal = new int[size];
        int bits = int.Log2(size);
        for (int i = 0; i < size; i++)
        {
            int reversed = 0;
            for (int b = 0; b < bits; b++)
            {
                if ((i & (1 << b)) != 0)
                {
                    reversed |= 1 << (bits - 1 - b);
                }
            }

            bitReversal[i] = reversed;
        }

        stageCos = new float[size];
        stageSin = new float[size];
        for (int half = 1; half < size; half <<= 1)
        {
            for (int k = 0; k < half; k++)
            {
                double angle = Math.PI * k / half;
                stageCos[half + k] = (float)Math.Cos(angle);
                stageSin[half + k] = (float)-Math.Sin(angle);
            }
        }
    }

    /// <summary>Transform length; always a power of two.</summary>
    public int Size { get; }

    /// <summary>Returns the shared plan for a transform size.</summary>
    /// <param name="size">Transform length; must be a power of two of at least 2.</param>
    public static FftPlan Get(int size)
    {
        if (size < 2 || (size & (size - 1)) != 0)
        {
            throw new ArgumentOutOfRangeException(nameof(size), "FFT size must be a power of two of at least 2.");
        }

        return Cache.GetOrAdd(size, static s => new FftPlan(s));
    }

    /// <summary>Computes the forward transform in place.</summary>
    /// <param name="real">Real parts; length must equal <see cref="Size"/>.</param>
    /// <param name="imaginary">Imaginary parts; length must equal <see cref="Size"/>.</param>
    public void Forward(Span<float> real, Span<float> imaginary)
    {
        int n = Size;
        if (real.Length != n || imaginary.Length != n)
        {
            throw new ArgumentException("Buffer length must equal the FFT size.");
        }

        for (int i = 0; i < n; i++)
        {
            int j = bitReversal[i];
            if (j > i)
            {
                (real[i], real[j]) = (real[j], real[i]);
                (imaginary[i], imaginary[j]) = (imaginary[j], imaginary[i]);
            }
        }

        int width = Vector.IsHardwareAccelerated ? Vector<float>.Count : int.MaxValue;
        ReadOnlySpan<float> cos = stageCos, sin = stageSin;
        for (int half = 1; half < n; half <<= 1)
        {
            int length = half << 1;
            if (half >= width)
            {
                for (int start = 0; start < n; start += length)
                {
                    for (int k = 0; k < half; k += width)
                    {
                        int a = start + k, b = a + half;
                        var wr = new Vector<float>(cos.Slice(half + k));
                        var wi = new Vector<float>(sin.Slice(half + k));
                        var ar = new Vector<float>(real.Slice(a));
                        var ai = new Vector<float>(imaginary.Slice(a));
                        var br = new Vector<float>(real.Slice(b));
                        var bi = new Vector<float>(imaginary.Slice(b));
                        Vector<float> tr = (br * wr) - (bi * wi);
                        Vector<float> ti = (br * wi) + (bi * wr);
                        (ar - tr).CopyTo(real.Slice(b));
                        (ai - ti).CopyTo(imaginary.Slice(b));
                        (ar + tr).CopyTo(real.Slice(a));
                        (ai + ti).CopyTo(imaginary.Slice(a));
                    }
                }

                continue;
            }

            for (int start = 0; start < n; start += length)
            {
                for (int k = 0; k < half; k++)
                {
                    float wr = cos[half + k], wi = sin[half + k];
                    int a = start + k, b = a + half;
                    float tr = (real[b] * wr) - (imaginary[b] * wi);
                    float ti = (real[b] * wi) + (imaginary[b] * wr);
                    real[b] = real[a] - tr;
                    imaginary[b] = imaginary[a] - ti;
                    real[a] += tr;
                    imaginary[a] += ti;
                }
            }
        }
    }
}
