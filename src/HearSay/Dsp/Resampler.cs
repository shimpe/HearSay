namespace HearSay.Dsp;

/// <summary>
/// Band-limited sample-rate converter using a Kaiser-windowed sinc kernel (16 zero crossings per side,
/// stop-band attenuation of roughly 80 dB). Deterministic; intended for offline use.
/// </summary>
public static class Resampler
{
    private const int ZeroCrossings = 16;
    private const int TableResolution = 512;
    private const double KaiserBeta = 8.0;

    private static readonly float[] KernelTable = BuildKernelTable();

    /// <summary>Resamples a signal to a new sample rate.</summary>
    /// <param name="input">Input samples.</param>
    /// <param name="sourceRate">Sample rate of <paramref name="input"/> in Hz.</param>
    /// <param name="targetRate">Desired sample rate in Hz.</param>
    public static float[] Resample(ReadOnlySpan<float> input, int sourceRate, int targetRate)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(sourceRate);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(targetRate);
        if (sourceRate == targetRate || input.IsEmpty)
        {
            return input.ToArray();
        }

        double ratio = (double)targetRate / sourceRate;
        int outputLength = (int)Math.Max(1, Math.Round(input.Length * ratio));
        var output = new float[outputLength];

        // When downsampling, the kernel is stretched so that its cutoff follows the new Nyquist frequency.
        double cutoff = Math.Min(1.0, ratio) * 0.97;
        double halfWidth = ZeroCrossings / cutoff;

        for (int n = 0; n < outputLength; n++)
        {
            double position = n / ratio;
            int first = Math.Max(0, (int)Math.Ceiling(position - halfWidth));
            int last = Math.Min(input.Length - 1, (int)Math.Floor(position + halfWidth));
            double sum = 0.0;
            for (int i = first; i <= last; i++)
            {
                double x = Math.Abs(i - position) * cutoff * TableResolution;
                int index = (int)x;
                double fraction = x - index;
                double kernel = KernelTable[index] + (fraction * (KernelTable[index + 1] - KernelTable[index]));
                sum += input[i] * kernel;
            }

            output[n] = (float)(sum * cutoff);
        }

        return output;
    }

    private static float[] BuildKernelTable()
    {
        int length = (ZeroCrossings * TableResolution) + 2;
        var table = new float[length];
        double i0Beta = BesselI0(KaiserBeta);
        for (int i = 0; i < length; i++)
        {
            double x = (double)i / TableResolution;
            if (x >= ZeroCrossings)
            {
                table[i] = 0f;
                continue;
            }

            double sinc = x < 1e-9 ? 1.0 : Math.Sin(Math.PI * x) / (Math.PI * x);
            double r = x / ZeroCrossings;
            double window = BesselI0(KaiserBeta * Math.Sqrt(1.0 - (r * r))) / i0Beta;
            table[i] = (float)(sinc * window);
        }

        return table;
    }

    private static double BesselI0(double x)
    {
        double sum = 1.0, term = 1.0;
        double halfSquared = x * x / 4.0;
        for (int k = 1; k < 50; k++)
        {
            term *= halfSquared / ((double)k * k);
            sum += term;
            if (term < 1e-12 * sum)
            {
                break;
            }
        }

        return sum;
    }
}
