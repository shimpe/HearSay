namespace HearSay.Preprocessing;

/// <summary>Result of preprocessing one signal. Immutable; the sample buffer must not be modified.</summary>
public sealed class PreprocessedAudio
{
    /// <summary>Creates a preprocessing result.</summary>
    /// <param name="samples">Cleaned, trimmed and optionally gain-normalized samples.</param>
    /// <param name="sampleRate">Sample rate of <paramref name="samples"/> in Hz.</param>
    /// <param name="issues">Warning codes raised while preprocessing (see <see cref="Models.AnalysisWarningCodes"/>).</param>
    public PreprocessedAudio(float[] samples, int sampleRate, IReadOnlyList<string> issues)
    {
        Samples = samples;
        SampleRate = sampleRate;
        Issues = issues;
    }

    /// <summary>Cleaned, trimmed and optionally gain-normalized samples.</summary>
    public float[] Samples { get; }

    /// <summary>Sample rate of <see cref="Samples"/> in Hz.</summary>
    public int SampleRate { get; }

    /// <summary>False when the input cannot be analysed (silent, too short or mostly invalid).</summary>
    public bool IsUsable { get; init; } = true;

    /// <summary>Position of the detected onset in the original recording, in milliseconds.</summary>
    public double OnsetOffsetMs { get; init; }

    /// <summary>Duration of <see cref="Samples"/> in milliseconds.</summary>
    public double DurationMs => SampleRate > 0 ? 1000.0 * Samples.Length / SampleRate : 0.0;

    /// <summary>
    /// Loudness of the original signal: RMS level in dBFS over the active part of the note (frames within 20 dB of
    /// the loudest frame). Measured before any gain normalization, so it is insensitive to note length and tails.
    /// </summary>
    public double ActiveRmsDb { get; init; }

    /// <summary>Peak sample level of the original signal in dBFS.</summary>
    public double PeakDb { get; init; }

    /// <summary>Gain, in dB, that was applied to <see cref="Samples"/> to reach the reference level; 0 when not normalized.</summary>
    public double AppliedGainDb { get; init; }

    /// <summary>DC offset that was removed.</summary>
    public double DcOffset { get; init; }

    /// <summary>Number of NaN or infinite samples that were replaced by zero.</summary>
    public int InvalidSampleCount { get; init; }

    /// <summary>Fraction of samples at or above the clipping level.</summary>
    public double ClippedFraction { get; init; }

    /// <summary>True when the signal was resampled to the analysis sample rate.</summary>
    public bool WasResampled { get; init; }

    /// <summary>Warning codes raised while preprocessing.</summary>
    public IReadOnlyList<string> Issues { get; }
}
