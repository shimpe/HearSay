using HearSay.Dsp;

namespace HearSay.Harmonics;

/// <summary>Result of a fundamental-frequency estimate.</summary>
/// <param name="FrequencyHz">Estimated fundamental in Hz; 0 when no estimate could be made.</param>
/// <param name="Confidence">Confidence 0..1 (one minus the normalized YIN difference at the chosen lag).</param>
public readonly record struct FundamentalEstimate(double FrequencyHz, double Confidence);

/// <summary>
/// Simple monophonic F0 estimator based on the YIN cumulative-mean-normalized difference function, evaluated on
/// one segment from the middle of the note. Deterministic, stateless and thread-safe.
/// </summary>
public static class FundamentalEstimator
{
    private const double YinThreshold = 0.15;

    /// <summary>Estimates the fundamental of a note.</summary>
    /// <param name="samples">Trimmed note.</param>
    /// <param name="sampleRate">Sample rate in Hz.</param>
    /// <param name="minimumHz">Lowest fundamental considered.</param>
    /// <param name="maximumHz">Highest fundamental considered.</param>
    public static FundamentalEstimate Estimate(ReadOnlySpan<float> samples, int sampleRate, double minimumHz = 30.0, double maximumHz = 5000.0)
    {
        int tauMax = (int)Math.Min(sampleRate / Math.Max(1.0, minimumHz), samples.Length / 2.0);
        int tauMin = Math.Max(2, (int)(sampleRate / Math.Max(minimumHz + 1.0, maximumHz)));
        if (tauMax <= tauMin + 2)
        {
            return new FundamentalEstimate(0.0, 0.0);
        }

        int window = Math.Min(samples.Length - tauMax, 2 * tauMax);
        int start = Math.Max(0, (samples.Length - (window + tauMax)) / 2);
        ReadOnlySpan<float> x = samples.Slice(start, window + tauMax);

        var difference = new double[tauMax + 1];
        for (int tau = 1; tau <= tauMax; tau++)
        {
            double sum = 0;
            for (int j = 0; j < window; j++)
            {
                double d = x[j] - x[j + tau];
                sum += d * d;
            }

            difference[tau] = sum;
        }

        // Cumulative mean normalization.
        var normalized = new double[tauMax + 1];
        normalized[0] = 1.0;
        double running = 0;
        for (int tau = 1; tau <= tauMax; tau++)
        {
            running += difference[tau];
            normalized[tau] = running > 0 ? difference[tau] * tau / running : 1.0;
        }

        int best = -1;
        for (int tau = tauMin; tau < tauMax; tau++)
        {
            if (normalized[tau] < YinThreshold)
            {
                while (tau + 1 < tauMax && normalized[tau + 1] < normalized[tau])
                {
                    tau++;
                }

                best = tau;
                break;
            }
        }

        if (best < 0)
        {
            best = tauMin;
            for (int tau = tauMin; tau < tauMax; tau++)
            {
                if (normalized[tau] < normalized[best])
                {
                    best = tau;
                }
            }
        }

        double offset = best > 1 && best < tauMax
            ? MathUtil.ParabolicOffset(normalized[best - 1], normalized[best], normalized[best + 1])
            : 0.0;
        double frequency = sampleRate / (best + offset);
        return new FundamentalEstimate(frequency, MathUtil.Clamp01(1.0 - normalized[best]));
    }
}
