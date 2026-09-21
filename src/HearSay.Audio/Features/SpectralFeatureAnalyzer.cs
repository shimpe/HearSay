using HearSay.Dsp;
using HearSay.Spectral;

namespace HearSay.Features;

/// <summary>Scalar descriptors of one note.</summary>
public sealed record SpectralDescriptors
{
    /// <summary>Power-weighted mean frequency in Hz.</summary>
    public double CentroidHz { get; init; }

    /// <summary>Frequency below which 85% of the power lies, in Hz.</summary>
    public double RolloffHz { get; init; }

    /// <summary>Power-weighted standard deviation of frequency around the centroid, in Hz.</summary>
    public double BandwidthHz { get; init; }

    /// <summary>Spectral flatness (geometric over arithmetic mean of the power spectrum) in dB; 0 dB is white noise, tonal sounds are strongly negative.</summary>
    public double FlatnessDb { get; init; }

    /// <summary>Slope of power density over octave bands anchored on the fundamental, in dB per octave.</summary>
    public double SlopeDbPerOctave { get; init; }

    /// <summary>Mean frame-to-frame change of the normalized magnitude spectrum (0 = static, up to about 1.4).</summary>
    public double Flux { get; init; }

    /// <summary>Zero crossings per second.</summary>
    public double ZeroCrossingRateHz { get; init; }

    /// <summary>RMS level of the whole trimmed note in dB.</summary>
    public double RmsDb { get; init; }

    /// <summary>Peak-to-RMS ratio in dB.</summary>
    public double CrestFactorDb { get; init; }
}

/// <summary>Computes scalar spectral and temporal descriptors.</summary>
public interface ISpectralFeatureAnalyzer
{
    /// <summary>Computes the descriptors of one note.</summary>
    /// <param name="samples">Trimmed note.</param>
    /// <param name="sampleRate">Sample rate in Hz.</param>
    /// <param name="spectrum">Time-averaged power spectrum of the note.</param>
    /// <param name="spectrogram">Spectrogram used for spectral flux.</param>
    /// <param name="fundamentalHz">Fundamental of the note in Hz; anchors the octave bands of the slope estimate.</param>
    SpectralDescriptors Analyze(ReadOnlySpan<float> samples, int sampleRate, AveragePowerSpectrum spectrum, Spectrogram spectrogram, double fundamentalHz);

    /// <summary>Power-weighted mean frequency of a power spectrum, in Hz.</summary>
    /// <param name="spectrum">Power spectrum.</param>
    double Centroid(AveragePowerSpectrum spectrum);
}

/// <summary>Default descriptor implementation. Stateless and thread-safe.</summary>
public sealed class SpectralFeatureAnalyzer : ISpectralFeatureAnalyzer
{
    private const double MinimumHz = 20.0;
    private const double FlatnessRangeDb = 90.0;

    /// <inheritdoc />
    public SpectralDescriptors Analyze(ReadOnlySpan<float> samples, int sampleRate, AveragePowerSpectrum spectrum, Spectrogram spectrogram, double fundamentalHz)
    {
        ArgumentNullException.ThrowIfNull(spectrum);
        ArgumentNullException.ThrowIfNull(spectrogram);

        double[] power = spectrum.Power;
        double binHz = spectrum.BinHz;
        int firstBin = Math.Max(1, (int)Math.Ceiling(MinimumHz / binHz));
        double total = 0, weighted = 0, maxBin = 0;
        for (int k = firstBin; k < power.Length; k++)
        {
            total += power[k];
            weighted += power[k] * k * binHz;
            maxBin = Math.Max(maxBin, power[k]);
        }

        double centroid = total > 0 ? weighted / total : 0.0;
        double spread = 0, cumulative = 0, rolloff = 0;
        bool rolloffFound = false;
        double logSum = 0;
        double binFloor = maxBin * MathUtil.DbToPower(-FlatnessRangeDb);
        double flooredTotal = 0;
        int binCount = 0;
        for (int k = firstBin; k < power.Length; k++)
        {
            double f = k * binHz;
            spread += power[k] * (f - centroid) * (f - centroid);
            cumulative += power[k];
            if (!rolloffFound && cumulative >= 0.85 * total)
            {
                rolloff = f;
                rolloffFound = true;
            }

            double floored = Math.Max(power[k], binFloor);
            if (floored > 0)
            {
                logSum += Math.Log(floored);
                flooredTotal += floored;
                binCount++;
            }
        }

        double flatnessDb = -FlatnessRangeDb;
        if (binCount > 0 && flooredTotal > 0)
        {
            double geometric = Math.Exp(logSum / binCount);
            flatnessDb = Math.Clamp(10.0 * Math.Log10(geometric / (flooredTotal / binCount)), -FlatnessRangeDb, 0.0);
        }

        // Temporal descriptors.
        int crossings = 0;
        double sumSquares = 0;
        float peak = 0f;
        for (int i = 0; i < samples.Length; i++)
        {
            float s = samples[i];
            sumSquares += (double)s * s;
            peak = Math.Max(peak, Math.Abs(s));
            if (i > 0 && (s >= 0f) != (samples[i - 1] >= 0f))
            {
                crossings++;
            }
        }

        double rms = samples.Length > 0 ? Math.Sqrt(sumSquares / samples.Length) : 0.0;
        double duration = (double)samples.Length / sampleRate;

        return new SpectralDescriptors
        {
            CentroidHz = centroid,
            RolloffHz = rolloff,
            BandwidthHz = total > 0 ? Math.Sqrt(spread / total) : 0.0,
            FlatnessDb = flatnessDb,
            SlopeDbPerOctave = OctaveSlope(spectrum, fundamentalHz),
            Flux = Flux(spectrogram),
            ZeroCrossingRateHz = duration > 0 ? crossings / duration : 0.0,
            RmsDb = MathUtil.AmplitudeToDb(rms),
            CrestFactorDb = rms > 0 ? MathUtil.AmplitudeToDb(peak / rms) : 0.0,
        };
    }

    /// <inheritdoc />
    public double Centroid(AveragePowerSpectrum spectrum)
    {
        ArgumentNullException.ThrowIfNull(spectrum);
        double[] power = spectrum.Power;
        double binHz = spectrum.BinHz;
        double total = 0, weighted = 0;
        for (int k = Math.Max(1, (int)Math.Ceiling(MinimumHz / binHz)); k < power.Length; k++)
        {
            total += power[k];
            weighted += power[k] * k * binHz;
        }

        return total > 0 ? weighted / total : 0.0;
    }

    /// <summary>
    /// Regression of power density (dB) over octave bands centred on f0 * 2^j. Anchoring the bands on the fundamental
    /// guarantees that every band of a harmonic sound contains at least one partial.
    /// </summary>
    private static double OctaveSlope(AveragePowerSpectrum spectrum, double fundamentalHz)
    {
        double baseHz = fundamentalHz > 0 ? fundamentalHz : 55.0;
        double limit = Math.Min(16000.0, spectrum.SampleRate * 0.475);
        Span<double> density = stackalloc double[16];
        int bands = 0;
        double maxDensity = 0;
        for (int j = 0; j < density.Length; j++)
        {
            double lo = baseHz * Math.Pow(2.0, j - 0.5);
            double hi = Math.Min(limit, lo * 2.0);
            if (hi - lo < lo * 0.5)
            {
                break;
            }

            density[j] = spectrum.BandPower(lo, hi) / (hi - lo);
            maxDensity = Math.Max(maxDensity, density[j]);
            bands++;
        }

        if (bands < 2 || maxDensity <= 0)
        {
            return 0.0;
        }

        double floor = maxDensity * 1e-9;
        double sx = 0, sy = 0, sxx = 0, sxy = 0;
        for (int j = 0; j < bands; j++)
        {
            double y = 10.0 * Math.Log10(Math.Max(density[j], floor));
            sx += j;
            sy += y;
            sxx += (double)j * j;
            sxy += j * y;
        }

        double denominator = (bands * sxx) - (sx * sx);
        return denominator > 0 ? ((bands * sxy) - (sx * sy)) / denominator : 0.0;
    }

    private static double Flux(Spectrogram spectrogram)
    {
        int frames = spectrogram.FrameCount, bins = spectrogram.BinCount;
        if (frames < 2)
        {
            return 0.0;
        }

        var norms = new double[frames];
        double maxNorm = 0;
        for (int f = 0; f < frames; f++)
        {
            ReadOnlySpan<float> row = spectrogram.Frame(f);
            double sum = 0;
            for (int k = 0; k < bins; k++)
            {
                sum += (double)row[k] * row[k];
            }

            norms[f] = Math.Sqrt(sum);
            maxNorm = Math.Max(maxNorm, norms[f]);
        }

        double threshold = maxNorm * 0.01;
        double total = 0;
        int count = 0;
        for (int f = 1; f < frames; f++)
        {
            if (norms[f] < threshold || norms[f - 1] < threshold)
            {
                continue;
            }

            ReadOnlySpan<float> a = spectrogram.Frame(f - 1), b = spectrogram.Frame(f);
            double invA = 1.0 / norms[f - 1], invB = 1.0 / norms[f];
            double sum = 0;
            for (int k = 0; k < bins; k++)
            {
                double d = (b[k] * invB) - (a[k] * invA);
                sum += d * d;
            }

            total += Math.Sqrt(sum);
            count++;
        }

        return count > 0 ? total / count : 0.0;
    }
}
