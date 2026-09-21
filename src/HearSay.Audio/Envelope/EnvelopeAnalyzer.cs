using HearSay.Dsp;

namespace HearSay.Envelope;

/// <summary>Extracts an amplitude envelope and robust attack/decay/sustain/release estimates from one note.</summary>
public interface IEnvelopeAnalyzer
{
    /// <summary>Analyses the envelope of a trimmed note.</summary>
    /// <param name="samples">Trimmed note; the onset is expected near the start.</param>
    /// <param name="sampleRate">Sample rate in Hz.</param>
    /// <param name="fundamentalHz">Fundamental of the note, used to pick a ripple-free RMS window; may be null.</param>
    /// <param name="options">Analysis configuration.</param>
    EnvelopeProfile Analyze(ReadOnlySpan<float> samples, int sampleRate, double? fundamentalHz, EnvelopeOptions options);
}

/// <summary>
/// RMS-envelope analyzer. It deliberately avoids fitting a full ADSR model: every stage is measured from simple,
/// robust features of the envelope (threshold crossings and the longest low-slope plateau) and carries a confidence.
/// Stateless and thread-safe.
/// </summary>
public sealed class EnvelopeAnalyzer : IEnvelopeAnalyzer
{
    private const double SmoothingMs = 10.0;
    private const double SlopeHalfSpanMs = 15.0;
    private const double PlateauMinimumLevelDb = -30.0;

    /// <inheritdoc />
    public EnvelopeProfile Analyze(ReadOnlySpan<float> samples, int sampleRate, double? fundamentalHz, EnvelopeOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(sampleRate);

        // Synth renders usually start exactly at the onset. Half a window of silence in front lets the envelope
        // start from zero, so that the beginning of a fast attack is observed instead of being cut off.
        int hop = Math.Max(1, (int)Math.Round(options.HopMs * sampleRate / 1000.0));
        int window = WindowLength(sampleRate, fundamentalHz, options.MinimumWindowMs);
        int leadIn = ((window / 2) + hop - 1) / hop * hop;
        double leadInMs = 1000.0 * leadIn / sampleRate;
        var padded = new float[samples.Length + leadIn];
        samples.CopyTo(padded.AsSpan(leadIn));

        EnvelopeProfile profile = AnalyzePadded(padded, sampleRate, fundamentalHz, options);
        return profile with
        {
            LeadInMs = leadInMs,
            DurationMs = 1000.0 * samples.Length / sampleRate,
            OnsetMs = Math.Max(0.0, profile.OnsetMs - leadInMs),
            PeakMs = Math.Max(0.0, profile.PeakMs - leadInMs),
            SustainStartMs = profile.SustainStartMs is double s ? Math.Max(0.0, s - leadInMs) : null,
            ReleaseStartMs = profile.ReleaseStartMs is double r ? Math.Max(0.0, r - leadInMs) : null,
        };
    }

    private static EnvelopeProfile AnalyzePadded(ReadOnlySpan<float> samples, int sampleRate, double? fundamentalHz, EnvelopeOptions options)
    {
        int hop = Math.Max(1, (int)Math.Round(options.HopMs * sampleRate / 1000.0));
        double hopMs = 1000.0 * hop / sampleRate;
        int window = WindowLength(sampleRate, fundamentalHz, options.MinimumWindowMs);
        double windowMs = 1000.0 * window / sampleRate;
        double durationMs = 1000.0 * samples.Length / sampleRate;

        if (samples.Length < 2)
        {
            return new EnvelopeProfile { HopMs = hopMs, WindowMs = windowMs, DurationMs = durationMs, PeakDb = MathUtil.FloorDb };
        }

        double[] linear = CentredRms(samples, window, hop);
        int n = linear.Length;
        double max = 0;
        foreach (double v in linear)
        {
            max = Math.Max(max, v);
        }

        if (max <= 0)
        {
            return new EnvelopeProfile { HopMs = hopMs, WindowMs = windowMs, DurationMs = durationMs, PeakDb = MathUtil.FloorDb };
        }

        var levels = new float[n];
        var relative = new double[n];
        for (int i = 0; i < n; i++)
        {
            relative[i] = linear[i] / max;
            levels[i] = (float)Math.Max(options.FloorDb, MathUtil.AmplitudeToDb(relative[i]));
        }

        // Attack: 10%..90% rise time.
        int peakIndex = FirstAtOrAbove(relative, 0, 0.98);
        int index90 = FirstAtOrAbove(relative, 0, 0.9);
        double t90 = RisingCrossing(relative, index90, 0.9) * hopMs;
        int index10 = index90;
        while (index10 > 0 && relative[index10 - 1] >= 0.1)
        {
            index10--;
        }

        bool startsHigh = index10 == 0 && relative[0] >= 0.1;
        double t10 = startsHigh ? 0.0 : RisingCrossing(relative, index10, 0.1) * hopMs;
        double attackMs = Math.Max(0.0, (t90 - t10) / 0.8);
        double attackConfidence = startsHigh ? (relative[0] >= 0.9 ? 0.5 : 0.4) : 0.9;
        if (attackMs <= 1.25 * windowMs)
        {
            // Resolution limited: the RMS window itself takes this long to rise.
            attackConfidence *= 0.5;
        }

        var profile = new EnvelopeProfile
        {
            HopMs = hopMs,
            WindowMs = windowMs,
            LevelsDb = levels,
            DurationMs = durationMs,
            OnsetMs = t10,
            PeakMs = peakIndex * hopMs,
            PeakDb = MathUtil.AmplitudeToDb(max),
            AttackMs = attackMs,
            AttackConfidence = attackConfidence,
        };

        // Sustain: the longest stretch after the peak where the smoothed envelope is nearly flat.
        double[] smoothed = MovingAverage(levels, Math.Max(1, (int)Math.Round(SmoothingMs / hopMs)));
        int halfSpan = Math.Max(1, (int)Math.Round(SlopeHalfSpanMs / hopMs));
        int bestStart = -1, bestLength = 0, runStart = -1;
        double bestSlopeSum = 0, runSlopeSum = 0;
        for (int i = peakIndex; i <= n; i++)
        {
            bool stable = false;
            double slope = 0;
            if (i < n)
            {
                int a = Math.Max(0, i - halfSpan);
                int b = Math.Min(n - 1, i + halfSpan);
                slope = b > a ? Math.Abs((smoothed[b] - smoothed[a]) / ((b - a) * hopMs / 1000.0)) : 0.0;
                stable = slope <= options.PlateauMaxSlopeDbPerSecond && smoothed[i] >= PlateauMinimumLevelDb;
            }

            if (stable)
            {
                if (runStart < 0)
                {
                    runStart = i;
                    runSlopeSum = 0;
                }

                runSlopeSum += slope;
            }
            else if (runStart >= 0)
            {
                int runLength = i - runStart;
                if (runLength > bestLength)
                {
                    bestStart = runStart;
                    bestLength = runLength;
                    bestSlopeSum = runSlopeSum;
                }

                runStart = -1;
            }
        }

        double drop = options.ReleaseDropDb;
        if (bestLength * hopMs >= options.MinimumPlateauMs)
        {
            return WithSustain(profile, options, levels, relative, peakIndex, bestStart, bestLength, bestSlopeSum / bestLength, hopMs, drop);
        }

        // No plateau: a continuously decaying (plucked or percussive) note.
        (double? decayMs, double decayConfidence) = FallTime(levels, peakIndex, levels[peakIndex] - drop, drop, hopMs, 0.7);
        return profile with { HasSustain = false, DecayMs = decayMs, DecayConfidence = decayConfidence };
    }

    private static EnvelopeProfile WithSustain(
        EnvelopeProfile profile,
        EnvelopeOptions options,
        float[] levels,
        double[] relative,
        int peakIndex,
        int runStart,
        int runLength,
        double meanSlope,
        double hopMs,
        double drop)
    {
        int n = levels.Length;
        var plateau = new double[runLength];
        for (int i = 0; i < runLength; i++)
        {
            plateau[i] = levels[runStart + i];
        }

        double sustainDb = Math.Min(0.0, MathUtil.MedianInPlace(plateau));
        double lengthFactor = MathUtil.Clamp01(runLength * hopMs / 200.0);
        double flatness = 1.0 - (0.5 * MathUtil.Clamp01(meanSlope / Math.Max(1e-9, options.PlateauMaxSlopeDbPerSecond)));
        double sustainConfidence = MathUtil.Clamp01((0.4 + (0.6 * lengthFactor)) * flatness);

        // Decay: from the peak to within 10% of the sustain level.
        double decayMs = 0.0;
        double decayConfidence = 0.6;
        double sustainLinear = MathUtil.DbToAmplitude(sustainDb);
        if (sustainDb < -1.0)
        {
            double threshold = sustainLinear + (0.1 * (1.0 - sustainLinear));
            int index = peakIndex;
            while (index < n - 1 && relative[index] > threshold)
            {
                index++;
            }

            decayMs = Math.Max(0.0, (FallingCrossing(relative, index, threshold) - peakIndex) * hopMs);
            decayConfidence = 0.85 * sustainConfidence;
        }

        double sustainStartMs = Math.Max(runStart * hopMs, profile.PeakMs + decayMs);

        // Release: starts where the envelope leaves the plateau for good.
        // Walk forward until the level drops clearly (1.5 dB) below the sustain level, which is robust against
        // ripple, then back along the fall to where it began, which removes most of the bias of that threshold.
        int plateauEnd = runStart + runLength - 1;
        int releaseStart = plateauEnd;
        while (releaseStart < n - 1 && levels[releaseStart + 1] >= sustainDb - 1.5)
        {
            releaseStart++;
        }

        while (releaseStart > plateauEnd && levels[releaseStart] < sustainDb - 0.3)
        {
            releaseStart--;
        }

        double? releaseMs;
        double releaseConfidence;
        if ((n - 1 - releaseStart) * hopMs < 5.0)
        {
            // The recording ends at the sustain level: either an instant release or a truncated recording.
            releaseMs = 0.0;
            releaseConfidence = 0.3;
        }
        else
        {
            (releaseMs, releaseConfidence) = FallTime(levels, releaseStart, sustainDb - drop, drop, hopMs, 0.85);
        }

        return profile with
        {
            HasSustain = true,
            SustainDb = sustainDb,
            SustainConfidence = sustainConfidence,
            SustainStartMs = sustainStartMs,
            DecayMs = decayMs,
            DecayConfidence = decayConfidence,
            ReleaseStartMs = releaseStart * hopMs,
            ReleaseMs = releaseMs,
            ReleaseConfidence = releaseConfidence,
        };
    }

    /// <summary>Time for the envelope to fall to <paramref name="targetDb"/> after <paramref name="startIndex"/>.</summary>
    private static (double? Ms, double Confidence) FallTime(float[] levels, int startIndex, double targetDb, double drop, double hopMs, double confidence)
    {
        int n = levels.Length;
        double startDb = targetDb + drop;
        double lowest = startDb;
        for (int i = startIndex + 1; i < n; i++)
        {
            if (levels[i] <= targetDb)
            {
                double previous = levels[i - 1];
                double fraction = previous > levels[i] ? (previous - targetDb) / (previous - levels[i]) : 0.0;
                return ((i - 1 + Math.Clamp(fraction, 0.0, 1.0) - startIndex) * hopMs, confidence);
            }

            lowest = Math.Min(lowest, levels[i]);
        }

        // The recording ends before the full drop: extrapolate when enough of the fall was captured.
        double dropped = startDb - lowest;
        if (dropped >= 6.0)
        {
            return ((n - 1 - startIndex) * hopMs * drop / dropped, 0.4);
        }

        return (null, 0.0);
    }

    private static int WindowLength(int sampleRate, double? fundamentalHz, double minimumWindowMs)
    {
        double minimum = Math.Max(2.0, minimumWindowMs * sampleRate / 1000.0);
        if (fundamentalHz is double f0 && f0 > 0)
        {
            double period = sampleRate / f0;
            double periods = Math.Max(1.0, Math.Ceiling(minimum / period));
            return (int)Math.Round(Math.Min(periods * period, 0.06 * sampleRate));
        }

        return (int)Math.Round(minimum * 2.0);
    }

    private static double[] CentredRms(ReadOnlySpan<float> samples, int window, int hop)
    {
        var prefix = new double[samples.Length + 1];
        for (int i = 0; i < samples.Length; i++)
        {
            prefix[i + 1] = prefix[i] + ((double)samples[i] * samples[i]);
        }

        int frames = ((samples.Length - 1) / hop) + 1;
        var result = new double[frames];
        int half = window / 2;
        for (int f = 0; f < frames; f++)
        {
            int centre = f * hop;
            int a = Math.Max(0, centre - half);
            int b = Math.Min(samples.Length, centre - half + window);
            // Dividing by the full window treats everything outside the recording as silence.
            result[f] = b > a ? Math.Sqrt((prefix[b] - prefix[a]) / window) : 0.0;
        }

        return result;
    }

    private static double[] MovingAverage(float[] values, int halfWidth)
    {
        int n = values.Length;
        var prefix = new double[n + 1];
        for (int i = 0; i < n; i++)
        {
            prefix[i + 1] = prefix[i] + values[i];
        }

        var result = new double[n];
        for (int i = 0; i < n; i++)
        {
            int a = Math.Max(0, i - halfWidth);
            int b = Math.Min(n, i + halfWidth + 1);
            result[i] = (prefix[b] - prefix[a]) / (b - a);
        }

        return result;
    }

    private static int FirstAtOrAbove(double[] values, int start, double threshold)
    {
        for (int i = start; i < values.Length; i++)
        {
            if (values[i] >= threshold)
            {
                return i;
            }
        }

        return values.Length - 1;
    }

    /// <summary>Fractional index at which a rising curve crosses the threshold, given the first index at or above it.</summary>
    private static double RisingCrossing(double[] values, int index, double threshold)
    {
        if (index <= 0)
        {
            return 0.0;
        }

        double a = values[index - 1], b = values[index];
        return b > a ? index - 1 + Math.Clamp((threshold - a) / (b - a), 0.0, 1.0) : index;
    }

    /// <summary>Fractional index at which a falling curve crosses the threshold, given the first index at or below it.</summary>
    private static double FallingCrossing(double[] values, int index, double threshold)
    {
        if (index <= 0)
        {
            return 0.0;
        }

        double a = values[index - 1], b = values[index];
        return a > b ? index - 1 + Math.Clamp((a - threshold) / (a - b), 0.0, 1.0) : index;
    }
}
