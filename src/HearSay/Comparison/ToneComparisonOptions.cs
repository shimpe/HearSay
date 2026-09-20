using HearSay.Envelope;
using HearSay.Harmonics;
using HearSay.Preprocessing;
using HearSay.Spectral;

namespace HearSay.Comparison;

/// <summary>How loudness is treated relative to timbre.</summary>
public enum LoudnessMode
{
    /// <summary>
    /// Default. Both signals are gain-matched before any timbre analysis, and the loudness difference is measured
    /// separately on the original signals. Loudness contributes to the overall score through its own weight.
    /// </summary>
    SeparateLoudness,

    /// <summary>
    /// Only timbre matters. Signals are gain-matched; the loudness difference is still measured and present in the
    /// difference vector, but it does not contribute to the score and is not listed as a difference.
    /// </summary>
    TimbreOnly,

    /// <summary>
    /// Absolute comparison. No gain matching: every spectral measurement includes the level difference, and band
    /// energies are absolute instead of relative to the total power.
    /// </summary>
    Absolute,
}

/// <summary>A named frequency band.</summary>
/// <param name="Name">Short name without spaces; becomes part of the feature id of the band.</param>
/// <param name="StartHz">Lower edge in Hz.</param>
/// <param name="EndHz">Upper edge in Hz; limited to the Nyquist frequency during analysis.</param>
public sealed record FrequencyBand(string Name, double StartHz, double EndHz);

/// <summary>Configuration of the frequency-band analysis.</summary>
public sealed record BandOptions
{
    /// <summary>Bands reported in <see cref="Models.ToneComparisonResult.FrequencyBands"/> and per time segment.</summary>
    public IReadOnlyList<FrequencyBand> Bands { get; init; } =
    [
        new("sub", 20, 80),
        new("bass", 80, 250),
        new("lowmid", 250, 800),
        new("mid", 800, 2500),
        new("uppermid", 2500, 6000),
        new("air", 6000, 20000),
    ];

    /// <summary>Boundary between the aggregate low and mid bands of the difference vector, in Hz.</summary>
    public double LowMidSplitHz { get; init; } = 300.0;

    /// <summary>Boundary between the aggregate mid and high bands of the difference vector, in Hz.</summary>
    public double MidHighSplitHz { get; init; } = 3000.0;

    /// <summary>Lowest relative band energy, in dB, that is represented; keeps empty bands comparable.</summary>
    public double FloorDb { get; init; } = -80.0;
}

/// <summary>Weights of the component distances in <see cref="Models.SimilarityScores.Overall"/>. They are normalized to sum to 1.</summary>
public sealed record ScoreWeights
{
    /// <summary>Weight of the multi-resolution STFT distance.</summary>
    public double Spectral { get; init; } = 0.30;

    /// <summary>Weight of the log-Mel distance.</summary>
    public double Perceptual { get; init; } = 0.25;

    /// <summary>Weight of the harmonic distance.</summary>
    public double Harmonic { get; init; } = 0.20;

    /// <summary>Weight of the envelope distance.</summary>
    public double Envelope { get; init; } = 0.20;

    /// <summary>Weight of the loudness distance; ignored in <see cref="LoudnessMode.TimbreOnly"/>.</summary>
    public double Loudness { get; init; } = 0.05;
}

/// <summary>Thresholds that decide which measurements become <see cref="Models.ToneDifference"/> entries.</summary>
public sealed record DifferenceOptions
{
    /// <summary>Smallest level difference, in dB, that is reported for loudness, sustain and band energies.</summary>
    public double MinimumLevelDifferenceDb { get; init; } = 1.0;

    /// <summary>Smallest per-harmonic level difference, in dB, that is reported.</summary>
    public double MinimumHarmonicDifferenceDb { get; init; } = 1.5;

    /// <summary>Smallest ratio between two times (larger over smaller) that is reported.</summary>
    public double MinimumTimeRatio { get; init; } = 1.15;

    /// <summary>Smallest absolute time difference, in ms, that is reported.</summary>
    public double MinimumTimeDifferenceMs { get; init; } = 3.0;

    /// <summary>Smallest relative difference of frequency descriptors (centroid, rolloff, bandwidth) that is reported.</summary>
    public double MinimumFrequencyRatio { get; init; } = 0.03;

    /// <summary>Smallest pitch difference, in cents, that is reported.</summary>
    public double MinimumPitchDifferenceCents { get; init; } = 3.0;

    /// <summary>Differences with a lower importance are dropped.</summary>
    public double MinimumImportance { get; init; } = 0.02;

    /// <summary>Maximum number of differences that are returned.</summary>
    public int MaximumDifferences { get; init; } = 50;

    /// <summary>Generate generic <see cref="Models.ParameterHint"/> entries for the most important differences.</summary>
    public bool IncludeParameterHints { get; init; } = true;
}

/// <summary>Configuration of the dense feature vectors.</summary>
public sealed record FeatureVectorOptions
{
    /// <summary>Number of per-harmonic entries. Fixed so that the vector layout does not depend on the note.</summary>
    public int HarmonicCount { get; init; } = 16;

    /// <summary>Normalized values are clipped to plus or minus this value; null disables clipping.</summary>
    public double? NormalizedClip { get; init; } = 10.0;

    /// <summary>Overrides of the typical scale per feature id.</summary>
    public IReadOnlyDictionary<string, double>? TypicalScaleOverrides { get; init; }
}

/// <summary>Complete configuration of a comparison. Immutable and JSON-serializable, so it can be stored with an optimization run.</summary>
public sealed record ToneComparisonOptions
{
    /// <summary>Fundamental of the note in Hz. Overrides the value on the notes; when neither is set, it is estimated from the target.</summary>
    public double? ExpectedFundamentalHz { get; init; }

    /// <summary>Sample rate the analysis runs at; null uses the sample rate of the target. Signals with another rate are resampled.</summary>
    public int? AnalysisSampleRate { get; init; }

    /// <summary>How loudness is treated relative to timbre.</summary>
    public LoudnessMode LoudnessMode { get; init; } = LoudnessMode.SeparateLoudness;

    /// <summary>Active RMS level, in dBFS, that both signals are gain-matched to unless <see cref="LoudnessMode.Absolute"/> is used.</summary>
    public double ReferenceRmsDb { get; init; } = -20.0;

    /// <summary>Overall distance reported for a candidate that cannot be analysed (silent, too short, invalid).</summary>
    public double UnusableCandidateDistance { get; init; } = 10.0;

    /// <summary>Preprocessing configuration.</summary>
    public PreprocessingOptions Preprocessing { get; init; } = new();

    /// <summary>Multi-resolution STFT configuration.</summary>
    public StftOptions Stft { get; init; } = new();

    /// <summary>Log-Mel configuration.</summary>
    public MelOptions Mel { get; init; } = new();

    /// <summary>Harmonic-analysis configuration.</summary>
    public HarmonicOptions Harmonic { get; init; } = new();

    /// <summary>Envelope-analysis configuration.</summary>
    public EnvelopeOptions Envelope { get; init; } = new();

    /// <summary>Frequency-band configuration.</summary>
    public BandOptions Bands { get; init; } = new();

    /// <summary>Weights of the overall score.</summary>
    public ScoreWeights Weights { get; init; } = new();

    /// <summary>Thresholds for actionable differences.</summary>
    public DifferenceOptions Differences { get; init; } = new();

    /// <summary>Feature-vector configuration.</summary>
    public FeatureVectorOptions FeatureVector { get; init; } = new();
}
