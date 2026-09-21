using HearSay.Dsp;

namespace HearSay.Pitch;

/// <summary>Fundamental frequency over time and what a synthesis search needs to know about it.</summary>
public sealed record PitchTrajectory
{
    /// <summary>Seconds between two frames.</summary>
    public required double FrameSeconds { get; init; }

    /// <summary>Deviation from <see cref="ReferenceHz"/> per frame in cents; NaN where no pitch was found.</summary>
    public required float[] Cents { get; init; }

    /// <summary>Periodicity per frame, 0..1.</summary>
    public required float[] Confidence { get; init; }

    /// <summary>Median fundamental over the steady part of the note.</summary>
    public required double ReferenceHz { get; init; }

    /// <summary>Pitch at the start of the note relative to the reference, in cents.</summary>
    public double AttackOffsetCents { get; init; }

    /// <summary>Time after which the pitch stays within 5 cents of the reference; 0 without an attack offset.</summary>
    public double PitchDecaySeconds { get; init; }

    /// <summary>Vibrato rate in hertz; 0 without a vibrato.</summary>
    public double VibratoRateHz { get; init; }

    /// <summary>Peak vibrato deviation in cents.</summary>
    public double VibratoDepthCents { get; init; }

    /// <summary>0..1, how much of the pitch movement in the steady part the vibrato explains.</summary>
    public double VibratoConfidence { get; init; }

    /// <summary>Slow pitch drift over the steady part in cents per second.</summary>
    public double DriftCentsPerSecond { get; init; }

    /// <summary>Mean periodicity of the voiced frames, 0..1. Low for inharmonic or noisy notes, whose pitch features mean little.</summary>
    public double OverallConfidence { get; init; }

    /// <summary>True when the pitch moves enough to matter: an attack offset, a vibrato or drift.</summary>
    public bool HasMovement =>
        OverallConfidence > 0.5
        && (Math.Abs(AttackOffsetCents) > 8.0 || (VibratoDepthCents > 4.0 && VibratoConfidence > 0.4) || Math.Abs(DriftCentsPerSecond) > 8.0);

    /// <summary>An empty trajectory for signals without a usable pitch.</summary>
    /// <param name="referenceHz">Fundamental to report.</param>
    public static PitchTrajectory Empty(double referenceHz) =>
        new() { FrameSeconds = 0.01, Cents = [], Confidence = [], ReferenceHz = referenceHz };
}

/// <summary>
/// Tracks the fundamental with a normalized difference function (YIN) restricted to a few semitones around the
/// expected fundamental, which makes it fast and immune to octave errors. Stateless and thread-safe.
/// </summary>
public static class PitchTracker
{
    /// <summary>Tracks the pitch of a trimmed note.</summary>
    /// <param name="samples">Trimmed note.</param>
    /// <param name="sampleRate">Sample rate in hertz.</param>
    /// <param name="expectedHz">Expected fundamental.</param>
    /// <param name="rangeSemitones">Search range on both sides of the expected fundamental.</param>
    /// <param name="hopSeconds">Time between two frames.</param>
    public static PitchTrajectory Track(ReadOnlySpan<float> samples, int sampleRate, double expectedHz, double rangeSemitones = 4.0, double hopSeconds = 0.01)
    {
        if (!(expectedHz > 0) || samples.Length < 64)
        {
            return PitchTrajectory.Empty(expectedHz);
        }

        double factor = Math.Pow(2.0, rangeSemitones / 12.0);
        int tauMin = Math.Max(2, (int)Math.Floor(sampleRate / (expectedHz * factor)));
        int tauMax = (int)Math.Ceiling(sampleRate / (expectedHz / factor)) + 1;
        int window = Math.Max(2 * tauMax, (int)(0.02 * sampleRate));
        int hop = Math.Max(1, (int)Math.Round(hopSeconds * sampleRate));
        int frames = samples.Length > window + tauMax ? ((samples.Length - window - tauMax) / hop) + 1 : 0;
        if (frames < 3)
        {
            return PitchTrajectory.Empty(expectedHz);
        }

        var hz = new double[frames];
        var confidence = new float[frames];
        var difference = new double[tauMax + 2];
        double peakEnergy = 0;
        var energies = new double[frames];
        for (int f = 0; f < frames; f++)
        {
            ReadOnlySpan<float> x = samples.Slice(f * hop, window + tauMax);
            double energy = 0;
            for (int j = 0; j < window; j++)
            {
                energy += (double)x[j] * x[j];
            }

            energies[f] = energy;
            peakEnergy = Math.Max(peakEnergy, energy);
            if (energy <= 1e-12)
            {
                continue;
            }

            int best = -1;
            double bestValue = double.PositiveInfinity;
            for (int tau = tauMin - 1; tau <= tauMax; tau++)
            {
                double sum = 0, shifted = 0;
                for (int j = 0; j < window; j++)
                {
                    double d = x[j] - x[j + tau];
                    sum += d * d;
                    shifted += (double)x[j + tau] * x[j + tau];
                }

                // Normalized so that 0 is perfectly periodic and 1 is uncorrelated.
                double value = sum / Math.Max(1e-12, energy + shifted);
                difference[tau] = value;
                if (tau >= tauMin && tau < tauMax && value < bestValue)
                {
                    bestValue = value;
                    best = tau;
                }
            }

            if (best <= tauMin || best >= tauMax - 1)
            {
                // A minimum at the edge of the range is not a period.
                continue;
            }

            double offset = MathUtil.ParabolicOffset(difference[best - 1], difference[best], difference[best + 1]);
            hz[f] = sampleRate / (best + offset);
            confidence[f] = (float)MathUtil.Clamp01(1.0 - (2.0 * bestValue));
        }

        // Frames that are quiet or aperiodic have no pitch.
        var voiced = new bool[frames];
        var steady = new List<double>();
        for (int f = 0; f < frames; f++)
        {
            voiced[f] = hz[f] > 0 && confidence[f] > 0.5 && energies[f] > peakEnergy * 1e-4;
        }

        int from = frames / 5;
        int to = Math.Max(from + 1, frames - (frames / 5));
        for (int f = from; f < to; f++)
        {
            if (voiced[f])
            {
                steady.Add(hz[f]);
            }
        }

        if (steady.Count < 3)
        {
            return PitchTrajectory.Empty(expectedHz);
        }

        double reference = MathUtil.MedianInPlace(System.Runtime.InteropServices.CollectionsMarshal.AsSpan(steady));
        var cents = new float[frames];
        double confidenceSum = 0;
        int voicedCount = 0;
        for (int f = 0; f < frames; f++)
        {
            cents[f] = voiced[f] ? (float)(1200.0 * Math.Log2(hz[f] / reference)) : float.NaN;
            if (voiced[f])
            {
                confidenceSum += confidence[f];
                voicedCount++;
            }
        }

        double frameSeconds = (double)hop / sampleRate;
        (double attackOffset, double decaySeconds) = AttackOffset(cents, frameSeconds);
        (double rate, double depth, double vibratoConfidence, double drift) = Vibrato(cents, from, to, frameSeconds);
        return new PitchTrajectory
        {
            FrameSeconds = frameSeconds,
            Cents = cents,
            Confidence = confidence,
            ReferenceHz = reference,
            AttackOffsetCents = attackOffset,
            PitchDecaySeconds = decaySeconds,
            VibratoRateHz = rate,
            VibratoDepthCents = depth,
            VibratoConfidence = vibratoConfidence,
            DriftCentsPerSecond = drift,
            OverallConfidence = voicedCount > 0 ? confidenceSum / voicedCount : 0.0,
        };
    }

    /// <summary>
    /// Mean absolute pitch difference between two notes over the frames in which both have a pitch, 50 cents counting
    /// as 1. Includes a constant tuning difference. Returns 0 when either note has no usable pitch.
    /// </summary>
    /// <param name="target">Pitch of the target.</param>
    /// <param name="candidate">Pitch of the candidate, tracked with the same frame interval.</param>
    public static double Distance(PitchTrajectory target, PitchTrajectory candidate)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(candidate);
        if (!(target.ReferenceHz > 0) || !(candidate.ReferenceHz > 0))
        {
            return 0.0;
        }

        double tuning = 1200.0 * Math.Log2(candidate.ReferenceHz / target.ReferenceHz);
        int frames = Math.Min(target.Cents.Length, candidate.Cents.Length);
        double sum = 0, weights = 0;
        for (int f = 0; f < frames; f++)
        {
            if (float.IsNaN(target.Cents[f]) || float.IsNaN(candidate.Cents[f]))
            {
                continue;
            }

            double w = Math.Min(target.Confidence[f], candidate.Confidence[f]);
            sum += w * Math.Min(2.0, Math.Abs(candidate.Cents[f] + tuning - target.Cents[f]) / 50.0);
            weights += w;
        }

        return weights > 0 ? sum / weights : 0.0;
    }

    private static (double OffsetCents, double DecaySeconds) AttackOffset(float[] cents, double frameSeconds)
    {
        int head = Math.Max(2, (int)Math.Round(0.03 / frameSeconds));
        double sum = 0;
        int count = 0;
        for (int f = 0; f < Math.Min(head, cents.Length); f++)
        {
            if (!float.IsNaN(cents[f]))
            {
                sum += cents[f];
                count++;
            }
        }

        if (count == 0)
        {
            return (0, 0);
        }

        double offset = sum / count;
        if (Math.Abs(offset) < 5.0)
        {
            return (offset, 0);
        }

        // The decay ends at the first run of three frames within 5 cents of the reference.
        int run = 0;
        for (int f = 0; f < cents.Length; f++)
        {
            run = !float.IsNaN(cents[f]) && Math.Abs(cents[f]) < 5.0 ? run + 1 : 0;
            if (run == 3)
            {
                return (offset, (f - 2) * frameSeconds);
            }
        }

        return (offset, cents.Length * frameSeconds);
    }

    private static (double RateHz, double DepthCents, double Confidence, double Drift) Vibrato(float[] cents, int from, int to, double frameSeconds)
    {
        var times = new List<double>();
        var values = new List<double>();
        for (int f = from; f < to; f++)
        {
            if (!float.IsNaN(cents[f]))
            {
                times.Add(f * frameSeconds);
                values.Add(cents[f]);
            }
        }

        int n = values.Count;
        if (n < 8)
        {
            return (0, 0, 0, 0);
        }

        double st = 0, sv = 0, stt = 0, stv = 0;
        for (int i = 0; i < n; i++)
        {
            st += times[i];
            sv += values[i];
            stt += times[i] * times[i];
            stv += times[i] * values[i];
        }

        double variance = (n * stt) - (st * st);
        double slope = variance > 1e-12 ? ((n * stv) - (st * sv)) / variance : 0.0;
        double intercept = (sv - (slope * st)) / n;
        double residualPower = 0;
        for (int i = 0; i < n; i++)
        {
            values[i] -= intercept + (slope * times[i]);
            residualPower += values[i] * values[i];
        }

        residualPower /= n;
        double span = times[^1] - times[0];
        if (residualPower < 0.25 || span < 0.25)
        {
            return (0, 0, 0, slope);
        }

        // Scan 2..12 Hz for the sinusoid that explains most of the detrended movement.
        double bestPower = 0, bestRate = 0;
        double lowest = Math.Max(2.0, 1.5 / span);
        for (double rate = lowest; rate <= 12.0; rate += 0.1)
        {
            double re = 0, im = 0;
            for (int i = 0; i < n; i++)
            {
                double phase = 2.0 * Math.PI * rate * times[i];
                re += values[i] * Math.Cos(phase);
                im += values[i] * Math.Sin(phase);
            }

            double power = ((re * re) + (im * im)) / ((double)n * n);
            if (power > bestPower)
            {
                bestPower = power;
                bestRate = rate;
            }
        }

        // A sinusoid of amplitude A has a mean square of A^2 / 2 and a projection power of A^2 / 4.
        double amplitude = 2.0 * Math.Sqrt(bestPower);
        double explained = MathUtil.Clamp01(2.0 * bestPower / residualPower);
        return explained > 0.3 ? (bestRate, amplitude, explained, slope) : (0, 0, explained, slope);
    }
}

/// <summary>Slow periodic amplitude variation, as produced by two partials that are slightly detuned against each other.</summary>
/// <param name="RateHz">Beat rate in hertz; 0 when no beating was found.</param>
/// <param name="DepthDb">Peak level deviation in dB.</param>
/// <param name="Confidence">0..1, how much of the level movement the beat explains.</param>
public sealed record BeatAnalysis(double RateHz, double DepthDb, double Confidence)
{
    /// <summary>No beating.</summary>
    public static BeatAnalysis None { get; } = new(0, 0, 0);

    /// <summary>True for an audible, clearly periodic beat.</summary>
    public bool IsPresent => RateHz > 0 && DepthDb > 0.5 && Confidence > 0.4;
}

/// <summary>Finds beating in a level curve. Stateless and thread-safe.</summary>
public static class BeatDetector
{
    /// <summary>Analyses the steady part of a level curve.</summary>
    /// <param name="levelsDb">Levels in dB at a constant interval.</param>
    /// <param name="stepSeconds">Time between two levels.</param>
    /// <param name="fromSeconds">Start of the span to analyse.</param>
    /// <param name="toSeconds">End of the span to analyse.</param>
    public static BeatAnalysis Detect(ReadOnlySpan<float> levelsDb, double stepSeconds, double fromSeconds, double toSeconds)
    {
        int from = Math.Max(0, (int)(fromSeconds / stepSeconds));
        int to = Math.Min(levelsDb.Length, (int)(toSeconds / stepSeconds));
        int n = to - from;
        double span = n * stepSeconds;
        if (n < 16 || span < 0.3)
        {
            return BeatAnalysis.None;
        }

        // Remove the decay with a quadratic trend so that only the periodic part is left.
        var x = new double[n];
        var y = new double[n];
        for (int i = 0; i < n; i++)
        {
            x[i] = ((2.0 * i) / (n - 1)) - 1.0;
            y[i] = Math.Max(levelsDb[from + i], -90f);
        }

        double s0 = n, s2 = 0, s4 = 0, t0 = 0, t1 = 0, t2 = 0;
        for (int i = 0; i < n; i++)
        {
            double xx = x[i] * x[i];
            s2 += xx;
            s4 += xx * xx;
            t0 += y[i];
            t1 += x[i] * y[i];
            t2 += xx * y[i];
        }

        // The abscissa is symmetric, so odd moments vanish and the normal equations decouple.
        double b = t1 / s2;
        double determinant = (s0 * s4) - (s2 * s2);
        double a = ((t0 * s4) - (t2 * s2)) / determinant;
        double c = ((s0 * t2) - (s2 * t0)) / determinant;
        double residualPower = 0;
        for (int i = 0; i < n; i++)
        {
            y[i] -= a + (b * x[i]) + (c * x[i] * x[i]);
            residualPower += y[i] * y[i];
        }

        residualPower /= n;
        if (residualPower < 0.02)
        {
            return BeatAnalysis.None;
        }

        double bestPower = 0, bestRate = 0;
        double lowest = Math.Max(0.5, 1.5 / span);
        for (double rate = lowest; rate <= 20.0; rate += 0.05)
        {
            double re = 0, im = 0;
            for (int i = 0; i < n; i++)
            {
                double phase = 2.0 * Math.PI * rate * i * stepSeconds;
                re += y[i] * Math.Cos(phase);
                im += y[i] * Math.Sin(phase);
            }

            double power = ((re * re) + (im * im)) / ((double)n * n);
            if (power > bestPower)
            {
                bestPower = power;
                bestRate = rate;
            }
        }

        double confidence = MathUtil.Clamp01(2.0 * bestPower / residualPower);
        return confidence > 0.3 ? new BeatAnalysis(bestRate, 2.0 * Math.Sqrt(bestPower), confidence) : new BeatAnalysis(0, 0, confidence);
    }
}
