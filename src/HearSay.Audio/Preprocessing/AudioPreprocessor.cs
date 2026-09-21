using HearSay.Audio;
using HearSay.Dsp;
using HearSay.Models;

namespace HearSay.Preprocessing;

/// <summary>Cleans and trims a recording so that the analyzers see a comparable, well-formed note.</summary>
public interface IAudioPreprocessor
{
    /// <summary>Runs the preprocessing pipeline on one note.</summary>
    /// <param name="note">Input recording.</param>
    /// <param name="options">Pipeline configuration.</param>
    /// <param name="analysisSampleRate">Sample rate the output must have; the input is resampled when it differs.</param>
    /// <param name="referenceRmsDb">
    /// When set, the output is scaled so that its active RMS level equals this value (timbre comparison).
    /// When null, the level of the input is preserved (absolute comparison).
    /// </param>
    PreprocessedAudio Process(AudioNote note, PreprocessingOptions options, int analysisSampleRate, double? referenceRmsDb);
}

/// <summary>
/// Default preprocessing pipeline: sanitize invalid samples, detect clipping, remove DC, resample,
/// detect the onset, trim silence, measure loudness and optionally gain-normalize.
/// Stateless and thread-safe.
/// </summary>
public sealed class AudioPreprocessor : IAudioPreprocessor
{
    private const double EnvelopeWindowMs = 4.0;
    private const double EnvelopeHopMs = 1.0;

    /// <inheritdoc />
    public PreprocessedAudio Process(AudioNote note, PreprocessingOptions options, int analysisSampleRate, double? referenceRmsDb)
    {
        ArgumentNullException.ThrowIfNull(note);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(analysisSampleRate);

        var issues = new List<string>();
        if (note.SampleRate <= 0 || note.Samples.IsEmpty)
        {
            issues.Add(AnalysisWarningCodes.EmptyInput);
            return new PreprocessedAudio([], analysisSampleRate, issues) { IsUsable = false, ActiveRmsDb = MathUtil.FloorDb, PeakDb = MathUtil.FloorDb };
        }

        float[] samples = note.Samples.ToArray();

        // 1. Sanitize and detect clipping.
        int invalid = 0, clipped = 0;
        float clipLevel = (float)options.ClippingLevel;
        double sum = 0;
        for (int i = 0; i < samples.Length; i++)
        {
            float s = samples[i];
            if (!float.IsFinite(s))
            {
                samples[i] = 0f;
                invalid++;
                continue;
            }

            if (Math.Abs(s) >= clipLevel)
            {
                clipped++;
            }

            sum += s;
        }

        double clippedFraction = (double)clipped / samples.Length;
        if (invalid > 0)
        {
            issues.Add(AnalysisWarningCodes.InvalidSamples);
        }

        if (clippedFraction > options.ClippingWarningFraction)
        {
            issues.Add(AnalysisWarningCodes.Clipping);
        }

        bool usable = (double)invalid / samples.Length <= options.MaxInvalidSampleFraction;

        // 2. DC removal.
        double dc = 0;
        if (options.RemoveDcOffset)
        {
            dc = sum / samples.Length;
            float dcF = (float)dc;
            for (int i = 0; i < samples.Length; i++)
            {
                samples[i] -= dcF;
            }
        }

        // 3. Resample.
        bool resampled = false;
        if (note.SampleRate != analysisSampleRate)
        {
            samples = Resampler.Resample(samples, note.SampleRate, analysisSampleRate);
            resampled = true;
        }

        // 4. Onset detection and trimming.
        int sampleRate = analysisSampleRate;
        int hop = Math.Max(1, (int)Math.Round(EnvelopeHopMs * sampleRate / 1000.0));
        int window = Math.Max(2, (int)Math.Round(EnvelopeWindowMs * sampleRate / 1000.0));
        double[] meanSquares = FrameMeanSquares(samples, window, hop);

        double peakMs = 0;
        foreach (double ms in meanSquares)
        {
            peakMs = Math.Max(peakMs, ms);
        }

        float peakSample = 0f;
        foreach (float s in samples)
        {
            peakSample = Math.Max(peakSample, Math.Abs(s));
        }

        double peakDb = MathUtil.AmplitudeToDb(peakSample);
        if (peakDb < options.MinimumPeakDb || peakMs <= 0)
        {
            issues.Add(AnalysisWarningCodes.Silent);
            return new PreprocessedAudio([], sampleRate, issues)
            {
                IsUsable = false,
                ActiveRmsDb = MathUtil.FloorDb,
                PeakDb = peakDb,
                DcOffset = dc,
                InvalidSampleCount = invalid,
                ClippedFraction = clippedFraction,
                WasResampled = resampled,
            };
        }

        int start = 0, end = samples.Length;
        if (options.TrimSilence)
        {
            double threshold = peakMs * MathUtil.DbToPower(OnsetThresholdDb(meanSquares, peakMs, options));
            int firstFrame = 0;
            while (firstFrame < meanSquares.Length - 1 && meanSquares[firstFrame] < threshold)
            {
                firstFrame++;
            }

            // The tail may have its own, lower threshold: a decaying note is audible long after it has fallen 40 dB.
            double tailThreshold = options.TailThresholdDb is double tailDb
                ? peakMs * MathUtil.DbToPower(Math.Max(tailDb, OnsetThresholdDb(meanSquares, peakMs, options) + (tailDb - options.OnsetThresholdDb)))
                : threshold;
            int lastFrame = meanSquares.Length - 1;
            while (lastFrame > firstFrame && meanSquares[lastFrame] < tailThreshold)
            {
                lastFrame--;
            }

            int preRoll = (int)Math.Round(options.PreRollMs * sampleRate / 1000.0);
            start = Math.Max(0, (firstFrame * hop) - preRoll);
            end = Math.Min(samples.Length, (lastFrame + 1) * hop);
        }

        int length = Math.Max(0, end - start);
        float[] trimmed = start == 0 && end == samples.Length ? samples : samples.AsSpan(start, length).ToArray();
        if (1000.0 * trimmed.Length / sampleRate < options.MinimumDurationMs)
        {
            issues.Add(AnalysisWarningCodes.TooShort);
            usable = false;
        }

        // 5. Loudness of the active part of the note.
        double activeThreshold = peakMs * 0.01;
        double activeSum = 0;
        int activeCount = 0;
        foreach (double ms in meanSquares)
        {
            if (ms >= activeThreshold)
            {
                activeSum += ms;
                activeCount++;
            }
        }

        double activeRmsDb = MathUtil.PowerToDb(activeCount > 0 ? activeSum / activeCount : 0.0);

        // 6. Optional gain normalization.
        double gainDb = 0;
        if (referenceRmsDb is double reference && usable)
        {
            gainDb = reference - activeRmsDb;
            float gain = (float)MathUtil.DbToAmplitude(gainDb);
            for (int i = 0; i < trimmed.Length; i++)
            {
                trimmed[i] *= gain;
            }
        }

        return new PreprocessedAudio(trimmed, sampleRate, issues)
        {
            IsUsable = usable,
            OnsetOffsetMs = 1000.0 * start / sampleRate,
            ActiveRmsDb = activeRmsDb,
            PeakDb = peakDb,
            AppliedGainDb = gainDb,
            DcOffset = dc,
            InvalidSampleCount = invalid,
            ClippedFraction = clippedFraction,
            WasResampled = resampled,
        };
    }

    /// <summary>Mean-square level of overlapping frames, computed with a running prefix sum.</summary>
    internal static double[] FrameMeanSquares(ReadOnlySpan<float> samples, int window, int hop)
    {
        int frameCount = Math.Max(1, ((samples.Length - 1) / hop) + 1);
        var prefix = new double[samples.Length + 1];
        for (int i = 0; i < samples.Length; i++)
        {
            prefix[i + 1] = prefix[i] + ((double)samples[i] * samples[i]);
        }

        var result = new double[frameCount];
        for (int f = 0; f < frameCount; f++)
        {
            // Backward-looking frames: a frame never sees audio that lies after it, so the detected onset of a
            // note does not depend on how much silence precedes it.
            int b = Math.Min(samples.Length, (f + 1) * hop);
            int a = Math.Max(0, b - window);
            result[f] = b > a ? (prefix[b] - prefix[a]) / window : 0.0;
        }

        return result;
    }

    private static double OnsetThresholdDb(double[] meanSquares, double peakMs, PreprocessingOptions options)
    {
        // Estimate the noise floor from the quietest tenth of the frames. It is only trusted when it sits well
        // below the note itself; a recording without any silence would otherwise report the note as its own floor.
        var sorted = (double[])meanSquares.Clone();
        Array.Sort(sorted);
        double floor = sorted[(int)(0.1 * (sorted.Length - 1))];
        double floorDb = floor > 0 ? 10.0 * Math.Log10(floor / peakMs) : double.NegativeInfinity;
        double threshold = options.OnsetThresholdDb;
        if (floorDb > threshold - 6.0 && floorDb < -20.0)
        {
            threshold = Math.Min(floorDb + options.NoiseFloorMarginDb, -6.0);
        }

        return threshold;
    }
}
