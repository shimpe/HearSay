using System.Collections.Concurrent;

namespace HearSay.Spectral;

/// <summary>Configuration of the log-Mel (perceptual) comparison.</summary>
public sealed record MelOptions
{
    /// <summary>FFT size of the underlying spectrogram.</summary>
    public int FftSize { get; init; } = 2048;

    /// <summary>Hop size as a fraction of the FFT size.</summary>
    public double HopFraction { get; init; } = 0.25;

    /// <summary>Number of Mel bands.</summary>
    public int BandCount { get; init; } = 40;

    /// <summary>Lowest frequency covered, in Hz.</summary>
    public double MinimumHz { get; init; } = 30.0;

    /// <summary>Highest frequency covered, in Hz; limited to the Nyquist frequency.</summary>
    public double MaximumHz { get; init; } = 16000.0;

    /// <summary>Dynamic range, in dB below the loudest target band, that is compared.</summary>
    public double DynamicRangeDb { get; init; } = 80.0;

    /// <summary>Time-averaged band difference, in dB, above which a band is reported as having too much or too little energy.</summary>
    public double ReportThresholdDb { get; init; } = 3.0;
}

/// <summary>
/// Triangular Mel filterbank (HTK Mel scale, unnormalized triangles applied to power). Instances are immutable
/// and cached per configuration.
/// </summary>
public sealed class MelFilterbank
{
    private static readonly ConcurrentDictionary<(int SampleRate, int FftSize, int Bands, double MinHz, double MaxHz), MelFilterbank> Cache = new();

    private readonly int[] firstBins;
    private readonly float[][] weights;

    private MelFilterbank(int sampleRate, int fftSize, int bands, double minHz, double maxHz)
    {
        BandCount = bands;
        double nyquist = sampleRate / 2.0;
        maxHz = Math.Min(maxHz, nyquist);
        minHz = Math.Clamp(minHz, 0.0, maxHz * 0.5);
        double melMin = HzToMel(minHz), melMax = HzToMel(maxHz);
        var edges = new double[bands + 2];
        for (int i = 0; i < edges.Length; i++)
        {
            edges[i] = MelToHz(melMin + ((melMax - melMin) * i / (bands + 1)));
        }

        EdgesHz = edges;
        double binHz = (double)sampleRate / fftSize;
        int binCount = (fftSize / 2) + 1;
        firstBins = new int[bands];
        weights = new float[bands][];
        for (int b = 0; b < bands; b++)
        {
            double lo = edges[b], centre = edges[b + 1], hi = edges[b + 2];
            int first = Math.Clamp((int)Math.Ceiling(lo / binHz), 0, binCount - 1);
            int last = Math.Clamp((int)Math.Floor(hi / binHz), 0, binCount - 1);
            if (last < first)
            {
                // The triangle is narrower than one bin: fall back to the bin nearest its centre.
                first = last = Math.Clamp((int)Math.Round(centre / binHz), 0, binCount - 1);
                firstBins[b] = first;
                weights[b] = [1f];
                continue;
            }

            var w = new float[last - first + 1];
            float sum = 0f;
            for (int k = first; k <= last; k++)
            {
                double f = k * binHz;
                double value = f <= centre ? (f - lo) / Math.Max(1e-9, centre - lo) : (hi - f) / Math.Max(1e-9, hi - centre);
                w[k - first] = (float)Math.Max(0.0, value);
                sum += w[k - first];
            }

            if (sum <= 0f)
            {
                w[(int)Math.Clamp(Math.Round(centre / binHz) - first, 0, w.Length - 1)] = 1f;
            }

            firstBins[b] = first;
            weights[b] = w;
        }
    }

    /// <summary>Number of Mel bands.</summary>
    public int BandCount { get; }

    /// <summary>Band edges in Hz: band <c>b</c> spans <c>EdgesHz[b]</c>..<c>EdgesHz[b + 2]</c> and is centred on <c>EdgesHz[b + 1]</c>.</summary>
    public IReadOnlyList<double> EdgesHz { get; }

    /// <summary>Returns the shared filterbank for a configuration.</summary>
    /// <param name="sampleRate">Sample rate in Hz.</param>
    /// <param name="fftSize">FFT size of the spectrogram the filterbank is applied to.</param>
    /// <param name="options">Mel configuration.</param>
    public static MelFilterbank Get(int sampleRate, int fftSize, MelOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        return Cache.GetOrAdd(
            (sampleRate, fftSize, options.BandCount, options.MinimumHz, options.MaximumHz),
            static key => new MelFilterbank(key.SampleRate, key.FftSize, key.Bands, key.MinHz, key.MaxHz));
    }

    /// <summary>Applies the filterbank to every frame of a magnitude spectrogram and returns band powers (frame-major).</summary>
    /// <param name="spectrogram">Magnitude spectrogram with the FFT size this filterbank was built for.</param>
    public float[] Apply(Spectrogram spectrogram)
    {
        ArgumentNullException.ThrowIfNull(spectrogram);
        int bands = BandCount;
        var result = new float[spectrogram.FrameCount * bands];
        for (int f = 0; f < spectrogram.FrameCount; f++)
        {
            ReadOnlySpan<float> row = spectrogram.Frame(f);
            for (int b = 0; b < bands; b++)
            {
                float[] w = weights[b];
                int first = firstBins[b];
                float sum = 0f;
                for (int i = 0; i < w.Length; i++)
                {
                    float m = row[first + i];
                    sum += w[i] * m * m;
                }

                result[(f * bands) + b] = sum;
            }
        }

        return result;
    }

    private static double HzToMel(double hz) => 2595.0 * Math.Log10(1.0 + (hz / 700.0));

    private static double MelToHz(double mel) => 700.0 * (Math.Pow(10.0, mel / 2595.0) - 1.0);
}
