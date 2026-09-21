using System.Collections.Concurrent;

namespace HearSay.Dsp;

/// <summary>Periodic Hann window with its sums, cached per length and immutable after construction.</summary>
public sealed class HannWindow
{
    private static readonly ConcurrentDictionary<int, HannWindow> Cache = new();

    private readonly float[] coefficients;

    private HannWindow(int length)
    {
        coefficients = new float[length];
        double sum = 0, sumSquares = 0;
        for (int i = 0; i < length; i++)
        {
            double w = 0.5 - (0.5 * Math.Cos(2.0 * Math.PI * i / length));
            coefficients[i] = (float)w;
            sum += w;
            sumSquares += w * w;
        }

        Sum = sum;
        SumOfSquares = sumSquares;
    }

    /// <summary>Window coefficients.</summary>
    public ReadOnlySpan<float> Coefficients => coefficients;

    /// <summary>Window length in samples.</summary>
    public int Length => coefficients.Length;

    /// <summary>Sum of the coefficients (coherent gain times length).</summary>
    public double Sum { get; }

    /// <summary>Sum of the squared coefficients (incoherent power gain times length).</summary>
    public double SumOfSquares { get; }

    /// <summary>Returns the shared window of the given length.</summary>
    /// <param name="length">Window length in samples.</param>
    public static HannWindow Get(int length)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(length, 2);
        return Cache.GetOrAdd(length, static l => new HannWindow(l));
    }
}
