using System.Collections.Concurrent;

namespace HearSay.Spectral;

/// <summary>
/// Maps FFT bins onto fractional-octave bands. Cached per configuration and immutable, so instances can be shared
/// between threads.
/// </summary>
public sealed class LogBandLayout
{
    private static readonly ConcurrentDictionary<(int SampleRate, int FftSize, int BandsPerOctave), LogBandLayout> Cache = new();

    private LogBandLayout(int sampleRate, int fftSize, int bandsPerOctave)
    {
        const double lowestHz = 40.0;
        double binHz = (double)sampleRate / fftSize;
        double highestHz = Math.Min(18000.0, sampleRate / 2.0);
        int bands = (int)Math.Floor(Math.Log2(highestHz / lowestHz) * bandsPerOctave);
        var first = new List<int>();
        var last = new List<int>();
        var centers = new List<double>();
        for (int b = 0; b < bands; b++)
        {
            double lo = lowestHz * Math.Pow(2.0, (double)b / bandsPerOctave);
            double hi = lowestHz * Math.Pow(2.0, (double)(b + 1) / bandsPerOctave);
            int from = (int)Math.Ceiling(lo / binHz);
            int to = (int)Math.Ceiling(hi / binHz) - 1;
            if (to < from)
            {
                // Narrower than one bin at this resolution; the band is merged into the next one that has bins.
                continue;
            }

            if (first.Count > 0 && from <= last[^1])
            {
                from = last[^1] + 1;
                if (to < from)
                {
                    continue;
                }
            }

            first.Add(from);
            last.Add(Math.Min(to, fftSize / 2));
            centers.Add(Math.Sqrt(lo * hi));
        }

        FirstBin = [.. first];
        LastBin = [.. last];
        CenterHz = [.. centers];
        LogCenter = new double[CenterHz.Length];
        for (int b = 0; b < LogCenter.Length; b++)
        {
            LogCenter[b] = Math.Log2(CenterHz[b]);
        }
    }

    /// <summary>Number of bands.</summary>
    public int BandCount => CenterHz.Length;

    /// <summary>First bin of every band.</summary>
    public int[] FirstBin { get; }

    /// <summary>Last bin of every band, inclusive.</summary>
    public int[] LastBin { get; }

    /// <summary>Geometric centre of every band in hertz.</summary>
    public double[] CenterHz { get; }

    /// <summary>Base-2 logarithm of <see cref="CenterHz"/>.</summary>
    public double[] LogCenter { get; }

    /// <summary>Returns the shared layout for a configuration.</summary>
    /// <param name="sampleRate">Sample rate in hertz.</param>
    /// <param name="fftSize">FFT size.</param>
    /// <param name="bandsPerOctave">Bands per octave, 3 for third-octave bands.</param>
    public static LogBandLayout Get(int sampleRate, int fftSize, int bandsPerOctave = 3) =>
        Cache.GetOrAdd((sampleRate, fftSize, bandsPerOctave), static key => new LogBandLayout(key.SampleRate, key.FftSize, key.BandsPerOctave));

    /// <summary>Sums the power of a magnitude frame per band.</summary>
    /// <param name="magnitudes">Magnitudes of one frame.</param>
    /// <param name="bandPower">Receives the power per band.</param>
    public void Apply(ReadOnlySpan<float> magnitudes, Span<double> bandPower)
    {
        for (int b = 0; b < FirstBin.Length; b++)
        {
            double sum = 0;
            int to = Math.Min(LastBin[b], magnitudes.Length - 1);
            for (int k = FirstBin[b]; k <= to; k++)
            {
                sum += (double)magnitudes[k] * magnitudes[k];
            }

            bandPower[b] = sum;
        }
    }

    /// <summary>Sums a power spectrum per band.</summary>
    /// <param name="power">Power per bin.</param>
    /// <param name="bandPower">Receives the power per band.</param>
    public void Apply(ReadOnlySpan<double> power, Span<double> bandPower)
    {
        for (int b = 0; b < FirstBin.Length; b++)
        {
            double sum = 0;
            int to = Math.Min(LastBin[b], power.Length - 1);
            for (int k = FirstBin[b]; k <= to; k++)
            {
                sum += power[k];
            }

            bandPower[b] = sum;
        }
    }
}
