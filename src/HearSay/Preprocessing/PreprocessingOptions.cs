namespace HearSay.Preprocessing;

/// <summary>Configuration of the preprocessing pipeline that runs before any analysis.</summary>
public sealed record PreprocessingOptions
{
    /// <summary>Subtract the mean of the signal before analysis.</summary>
    public bool RemoveDcOffset { get; init; } = true;

    /// <summary>Remove silence before the onset and after the end of the note.</summary>
    public bool TrimSilence { get; init; } = true;

    /// <summary>
    /// Level, in dB relative to the envelope peak, above which the note is considered to have started
    /// (and below which it is considered to have ended). Default -40 dB.
    /// </summary>
    public double OnsetThresholdDb { get; init; } = -40.0;

    /// <summary>
    /// When the recording has an audible noise floor that reaches <see cref="OnsetThresholdDb"/>, the onset threshold
    /// is raised to this many dB above the estimated floor instead.
    /// </summary>
    public double NoiseFloorMarginDb { get; init; } = 8.0;

    /// <summary>Audio kept before the detected onset, in milliseconds.</summary>
    public double PreRollMs { get; init; } = 1.0;

    /// <summary>Shortest note, after trimming, that is still analysed. Shorter input is reported as unusable.</summary>
    public double MinimumDurationMs { get; init; } = 30.0;

    /// <summary>Peak level in dBFS below which the input is treated as silence and reported as unusable.</summary>
    public double MinimumPeakDb { get; init; } = -80.0;

    /// <summary>Fraction of NaN or infinite samples above which the input is reported as unusable.</summary>
    public double MaxInvalidSampleFraction { get; init; } = 0.01;

    /// <summary>Absolute sample value at or above which a sample counts as clipped.</summary>
    public double ClippingLevel { get; init; } = 0.999;

    /// <summary>Fraction of clipped samples above which a clipping warning is raised.</summary>
    public double ClippingWarningFraction { get; init; } = 0.001;

    /// <summary>
    /// After onset alignment, refine the alignment of the candidate by cross-correlating the amplitude envelopes.
    /// The refinement only affects time-frequency comparisons; envelope timing is always measured per signal.
    /// </summary>
    public bool RefineAlignment { get; init; } = true;

    /// <summary>Largest shift, in milliseconds, that alignment refinement may apply.</summary>
    public double MaxAlignmentShiftMs { get; init; } = 5.0;
}
