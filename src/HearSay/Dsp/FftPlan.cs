using System.Collections.Concurrent;

namespace HearSay.Dsp;

/// <summary>
/// Precomputed tables for an in-place radix-2 complex FFT of one size. Plans are immutable after
/// construction and cached per size, so they can be shared freely between threads.
/// </summary>
public sealed class FftPlan
{
    private static readonly ConcurrentDictionary<int, FftPlan> Cache = new();

    private readonly int[] bitReversal;
    private readonly float[] cosTable;
    private readonly float[] sinTable;

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

        cosTable = new float[size / 2];
        sinTable = new float[size / 2];
        for (int i = 0; i < size / 2; i++)
        {
            double angle = 2.0 * Math.PI * i / size;
            cosTable[i] = (float)Math.Cos(angle);
            sinTable[i] = (float)Math.Sin(angle);
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

        for (int length = 2; length <= n; length <<= 1)
        {
            int half = length >> 1;
            int step = n / length;
            for (int start = 0; start < n; start += length)
            {
                for (int k = 0, tableIndex = 0; k < half; k++, tableIndex += step)
                {
                    float wr = cosTable[tableIndex];
                    float wi = -sinTable[tableIndex];
                    int a = start + k;
                    int b = a + half;
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
