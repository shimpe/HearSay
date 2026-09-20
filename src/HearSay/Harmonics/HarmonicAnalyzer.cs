using HearSay.Dsp;
using HearSay.Spectral;

namespace HearSay.Harmonics;

/// <summary>Measures the harmonic structure of a note from its time-averaged power spectrum.</summary>
public interface IHarmonicAnalyzer
{
    /// <summary>FFT size that resolves the harmonics of a note well enough for <see cref="Analyze"/>.</summary>
    /// <param name="sampleRate">Sample rate in Hz.</param>
    /// <param name="fundamentalHz">Fundamental of the note in Hz.</param>
    /// <param name="options">Analysis configuration.</param>
    int ChooseFftSize(int sampleRate, double fundamentalHz, HarmonicOptions options);

    /// <summary>Measures the harmonics of a note.</summary>
    /// <param name="spectrum">Time-averaged power spectrum of the note.</param>
    /// <param name="fundamentalHz">Expected fundamental in Hz.</param>
    /// <param name="options">Analysis configuration.</param>
    HarmonicProfile Analyze(AveragePowerSpectrum spectrum, double fundamentalHz, HarmonicOptions options);
}

/// <summary>
/// Default harmonic analyzer. The level of a harmonic is the power summed over the main lobe of its spectral peak,
/// which makes the measurement independent of phase and of where the partial falls between FFT bins.
/// Stateless and thread-safe.
/// </summary>
public sealed class HarmonicAnalyzer : IHarmonicAnalyzer
{
    private const int LobeHalfWidth = 3;

    /// <inheritdoc />
    public int ChooseFftSize(int sampleRate, double fundamentalHz, HarmonicOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        int needed = fundamentalHz > 0 ? (int)Math.Ceiling(8.0 * sampleRate / fundamentalHz) : options.MinimumFftSize;
        int size = MathUtil.NextPowerOfTwo(Math.Max(needed, options.MinimumFftSize));
        return Math.Min(size, MathUtil.NextPowerOfTwo(options.MaximumFftSize));
    }

    /// <inheritdoc />
    public HarmonicProfile Analyze(AveragePowerSpectrum spectrum, double fundamentalHz, HarmonicOptions options)
    {
        ArgumentNullException.ThrowIfNull(spectrum);
        ArgumentNullException.ThrowIfNull(options);
        if (fundamentalHz <= 0)
        {
            return new HarmonicProfile { HnrDb = -20.0, LowHarmonicsDb = options.LevelFloorDb, HighHarmonicsDb = options.LevelFloorDb };
        }

        double binHz = spectrum.BinHz;
        double nyquist = spectrum.SampleRate / 2.0;
        double maxHz = Math.Min(options.MaxFrequencyHz ?? double.MaxValue, nyquist * 0.95);
        int count = (int)Math.Min(options.MaxHarmonics, Math.Floor(maxHz / fundamentalHz));
        if (count < 1)
        {
            return new HarmonicProfile { NominalFundamentalHz = fundamentalHz, FundamentalHz = fundamentalHz, HnrDb = -20.0 };
        }

        double f0Bins = fundamentalHz / binHz;
        int lobe = (int)Math.Clamp(Math.Floor((f0Bins - 1.0) / 2.0), 1, LobeHalfWidth);
        double centsFactor = Math.Pow(2.0, options.SearchCents / 1200.0) - 1.0;

        // Pass 1: refine the fundamental from the strongest of the first partials.
        double refined = fundamentalHz;
        double weightSum = 0, weightedF0 = 0;
        for (int k = 1; k <= Math.Min(count, 8); k++)
        {
            (double hz, double power) = MeasurePeak(spectrum, k * refined, SearchBins(k * refined, centsFactor, binHz, f0Bins), lobe);
            if (power > 0)
            {
                weightSum += power;
                weightedF0 += power * hz / k;
                refined = weightedF0 / weightSum;
            }
        }

        // Pass 2: measure every harmonic around the refined fundamental.
        var measuredHz = new double[count];
        var powers = new double[count];
        double harmonicPower = 0;
        for (int k = 1; k <= count; k++)
        {
            double nominal = k * refined;
            (measuredHz[k - 1], powers[k - 1]) = MeasurePeak(spectrum, nominal, SearchBins(nominal, centsFactor, binHz, f0Bins), lobe);
            harmonicPower += powers[k - 1];
        }

        double floorDb = options.LevelFloorDb;
        var peaks = new HarmonicPeak[count];
        double even = 0, odd = 0, low = 0, high = 0;
        double inharmonicWeight = 0, inharmonicSum = 0;
        for (int k = 1; k <= count; k++)
        {
            double p = powers[k - 1];
            double relative = harmonicPower > 0 && p > 0 ? Math.Max(floorDb, 10.0 * Math.Log10(p / harmonicPower)) : floorDb;
            peaks[k - 1] = new HarmonicPeak(k, k * refined, measuredHz[k - 1], p, MathUtil.PowerToDb(p), relative);
            if ((k & 1) == 0)
            {
                even += p;
            }
            else
            {
                odd += p;
            }

            if (k is >= 2 and <= 5)
            {
                low += p;
            }
            else if (k >= 6)
            {
                high += p;
            }

            if (k >= 2 && relative > -50.0)
            {
                double w = Math.Sqrt(p);
                inharmonicWeight += w;
                inharmonicSum += w * Math.Abs(MathUtil.Cents(measuredHz[k - 1], k * refined));
            }
        }

        double noise = Math.Max(0.0, spectrum.TotalPower - harmonicPower);
        double hnr = harmonicPower <= 0 ? -20.0 : noise <= 0 ? 50.0 : Math.Clamp(10.0 * Math.Log10(harmonicPower / noise), -20.0, 50.0);

        return new HarmonicProfile
        {
            NominalFundamentalHz = fundamentalHz,
            FundamentalHz = refined,
            Harmonics = peaks,
            TotalHarmonicPower = harmonicPower,
            HnrDb = hnr,
            InharmonicityCents = inharmonicWeight > 0 ? inharmonicSum / inharmonicWeight : 0.0,
            SlopeDbPerOctave = Slope(peaks),
            EvenOddRatioDb = RatioDb(even, odd, 40.0),
            LowHarmonicsDb = GroupDb(low, harmonicPower, floorDb),
            HighHarmonicsDb = GroupDb(high, harmonicPower, floorDb),
        };
    }

    private static int SearchBins(double nominalHz, double centsFactor, double binHz, double f0Bins) =>
        (int)Math.Max(1, Math.Min(Math.Ceiling(nominalHz * centsFactor / binHz) + 1, Math.Floor(f0Bins * 0.45)));

    private static (double Hz, double Power) MeasurePeak(AveragePowerSpectrum spectrum, double nominalHz, int searchBins, int lobe)
    {
        double[] power = spectrum.Power;
        int centre = (int)Math.Round(nominalHz / spectrum.BinHz);
        int first = Math.Max(1, centre - searchBins);
        int last = Math.Min(power.Length - 2, centre + searchBins);
        if (last < first)
        {
            return (nominalHz, 0.0);
        }

        int peak = first;
        for (int k = first + 1; k <= last; k++)
        {
            if (power[k] > power[peak])
            {
                peak = k;
            }
        }

        double sum = 0;
        for (int k = Math.Max(0, peak - lobe); k <= Math.Min(power.Length - 1, peak + lobe); k++)
        {
            sum += power[k];
        }

        double offset = 0;
        if (power[peak] > 0 && power[peak - 1] > 0 && power[peak + 1] > 0)
        {
            offset = MathUtil.ParabolicOffset(Math.Log(power[peak - 1]), Math.Log(power[peak]), Math.Log(power[peak + 1]));
        }

        return ((peak + offset) * spectrum.BinHz, sum);
    }

    private static double Slope(HarmonicPeak[] peaks)
    {
        int n = Math.Min(peaks.Length, 16);
        if (n < 2)
        {
            return 0.0;
        }

        double sx = 0, sy = 0, sxx = 0, sxy = 0;
        for (int i = 0; i < n; i++)
        {
            double x = Math.Log2(peaks[i].Number);
            double y = peaks[i].RelativeDb;
            sx += x;
            sy += y;
            sxx += x * x;
            sxy += x * y;
        }

        double denominator = (n * sxx) - (sx * sx);
        return denominator > 1e-12 ? ((n * sxy) - (sx * sy)) / denominator : 0.0;
    }

    private static double RatioDb(double numerator, double denominator, double limit)
    {
        if (numerator <= 0 && denominator <= 0)
        {
            return 0.0;
        }

        if (denominator <= 0)
        {
            return limit;
        }

        return numerator <= 0 ? -limit : Math.Clamp(10.0 * Math.Log10(numerator / denominator), -limit, limit);
    }

    private static double GroupDb(double power, double total, double floorDb) =>
        power > 0 && total > 0 ? Math.Max(floorDb, 10.0 * Math.Log10(power / total)) : floorDb;
}
