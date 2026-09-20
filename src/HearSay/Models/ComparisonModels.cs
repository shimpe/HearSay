namespace HearSay.Models;

/// <summary>Names of the time segments a note is divided into.</summary>
public static class TimeSegments
{
    /// <summary>From the onset until the envelope reaches its peak.</summary>
    public const string Attack = "Attack";

    /// <summary>From the peak until the sustain level is reached; for notes without sustain, everything after the attack.</summary>
    public const string Decay = "Decay";

    /// <summary>The sustain plateau.</summary>
    public const string Sustain = "Sustain";

    /// <summary>From the start of the release to the end of the note.</summary>
    public const string Release = "Release";

    /// <summary>All segment names in chronological order.</summary>
    public static IReadOnlyList<string> All { get; } = [Attack, Decay, Sustain, Release];
}

/// <summary>
/// Distance scores. Every distance uses <c>0 = perfect match</c> and is scaled so that values around 1 mean
/// "grossly different"; distances are not clamped. <see cref="Similarity"/> is the only value where 1 is perfect.
/// </summary>
/// <param name="Overall">Weighted mean of the component distances, using <see cref="Comparison.ScoreWeights"/>.</param>
/// <param name="Spectral">Multi-resolution STFT distance: mean of spectral convergence and log-magnitude distance / 20 dB.</param>
/// <param name="Harmonic">Salience-weighted mean harmonic level difference / 20 dB, blended with the HNR difference.</param>
/// <param name="Envelope">Mean envelope-curve difference / 20 dB, blended with normalized stage-time differences.</param>
/// <param name="Perceptual">Mean absolute log-Mel difference / 20 dB.</param>
/// <param name="Loudness">Absolute loudness difference / 20 dB.</param>
/// <param name="Similarity">Convenience similarity in 0..1: <c>exp(-3 * Overall)</c>; 1 is a perfect match.</param>
public sealed record SimilarityScores(
    double Overall,
    double Spectral,
    double Harmonic,
    double Envelope,
    double Perceptual,
    double Loudness,
    double Similarity);

/// <summary>Comparison of one envelope stage. Values are null when the stage could not be measured reliably.</summary>
/// <param name="Target">Value measured on the target.</param>
/// <param name="Candidate">Value measured on the candidate.</param>
/// <param name="Difference">Candidate minus target; null when either side is missing.</param>
/// <param name="Unit">Unit of the values ("ms" or "dB").</param>
/// <param name="Confidence">Confidence 0..1 of the difference: the lower of the two measurement confidences.</param>
public sealed record EnvelopeStageComparison(double? Target, double? Candidate, double? Difference, string Unit, double Confidence);

/// <summary>Comparison of the amplitude envelopes.</summary>
/// <param name="Attack">Attack time (10%..90% rise time / 0.8), ms.</param>
/// <param name="Decay">Decay time, ms.</param>
/// <param name="Sustain">Sustain level relative to the envelope peak, dB.</param>
/// <param name="Release">Release time (time to fall 20 dB below the sustain level), ms.</param>
/// <param name="CurveDistanceDb">Mean absolute difference between the peak-normalized dB envelopes, aligned at the onset.</param>
/// <param name="TargetHasSustain">True when a sustain plateau was found in the target.</param>
/// <param name="CandidateHasSustain">True when a sustain plateau was found in the candidate.</param>
/// <param name="TargetDurationMs">Duration of the trimmed target note.</param>
/// <param name="CandidateDurationMs">Duration of the trimmed candidate note.</param>
public sealed record EnvelopeComparison(
    EnvelopeStageComparison Attack,
    EnvelopeStageComparison Decay,
    EnvelopeStageComparison Sustain,
    EnvelopeStageComparison Release,
    double CurveDistanceDb,
    bool TargetHasSustain,
    bool CandidateHasSustain,
    double TargetDurationMs,
    double CandidateDurationMs);

/// <summary>Comparison of one harmonic. Levels are relative to the total harmonic power, so they describe timbre, not loudness.</summary>
/// <param name="HarmonicNumber">Harmonic number; 1 is the fundamental.</param>
/// <param name="FrequencyHz">Nominal frequency of the harmonic in the target.</param>
/// <param name="TargetLevelDb">Relative level in the target, clamped at the harmonic floor.</param>
/// <param name="CandidateLevelDb">Relative level in the candidate, clamped at the harmonic floor.</param>
/// <param name="DifferenceDb">Candidate minus target.</param>
/// <param name="TargetAbsoluteDb">Absolute level in the analysed target signal.</param>
/// <param name="CandidateAbsoluteDb">Absolute level in the analysed candidate signal.</param>
public sealed record HarmonicComparison(
    int HarmonicNumber,
    double FrequencyHz,
    double TargetLevelDb,
    double CandidateLevelDb,
    double DifferenceDb,
    double TargetAbsoluteDb,
    double CandidateAbsoluteDb);

/// <summary>Energy difference in one frequency band. Energies are relative to the total power of each signal unless absolute loudness mode is active.</summary>
/// <param name="Name">Name of the band.</param>
/// <param name="StartHz">Lower edge in Hz.</param>
/// <param name="EndHz">Upper edge in Hz.</param>
/// <param name="TargetEnergyDb">Band energy of the target in dB.</param>
/// <param name="CandidateEnergyDb">Band energy of the candidate in dB.</param>
/// <param name="DifferenceDb">Candidate minus target.</param>
public sealed record FrequencyBandDifference(
    string Name,
    double StartHz,
    double EndHz,
    double TargetEnergyDb,
    double CandidateEnergyDb,
    double DifferenceDb);

/// <summary>Spectral comparison restricted to one time segment of the note. Each signal is segmented by its own envelope.</summary>
/// <param name="Segment">Segment name, see <see cref="TimeSegments"/>.</param>
/// <param name="TargetStartMs">Start of the segment in the target.</param>
/// <param name="TargetEndMs">End of the segment in the target.</param>
/// <param name="CandidateStartMs">Start of the segment in the candidate.</param>
/// <param name="CandidateEndMs">End of the segment in the candidate.</param>
/// <param name="Bands">Band differences within the segment.</param>
/// <param name="TargetCentroidHz">Spectral centroid of the target within the segment.</param>
/// <param name="CandidateCentroidHz">Spectral centroid of the candidate within the segment.</param>
/// <param name="CentroidDifferenceHz">Candidate minus target.</param>
public sealed record TimeSegmentComparison(
    string Segment,
    double TargetStartMs,
    double TargetEndMs,
    double CandidateStartMs,
    double CandidateEndMs,
    IReadOnlyList<FrequencyBandDifference> Bands,
    double TargetCentroidHz,
    double CandidateCentroidHz,
    double CentroidDifferenceHz);

/// <summary>STFT distance at one resolution.</summary>
/// <param name="FftSize">FFT size.</param>
/// <param name="HopSize">Hop size in samples.</param>
/// <param name="SpectralConvergence">Frobenius norm of the magnitude difference over the norm of the target (0 = identical).</param>
/// <param name="LogMagnitudeDistanceDb">Mean absolute log-magnitude difference in dB within the compared dynamic range.</param>
public sealed record StftResolutionResult(int FftSize, int HopSize, double SpectralConvergence, double LogMagnitudeDistanceDb);

/// <summary>Spectral distance at one moment of the note.</summary>
/// <param name="TimeMs">Centre time of the interval in ms from the onset.</param>
/// <param name="LogMagnitudeDistanceDb">Mean absolute log-magnitude difference in dB in the interval.</param>
public sealed record TimeProfilePoint(double TimeMs, double LogMagnitudeDistanceDb);

/// <summary>Multi-resolution STFT comparison.</summary>
/// <param name="Resolutions">Result per resolution.</param>
/// <param name="SpectralConvergence">Mean spectral convergence over the resolutions.</param>
/// <param name="LogMagnitudeDistanceDb">Mean log-magnitude distance over the resolutions, dB.</param>
/// <param name="TimeProfile">Log-magnitude distance through time at the diagnostic resolution.</param>
public sealed record SpectralComparison(
    IReadOnlyList<StftResolutionResult> Resolutions,
    double SpectralConvergence,
    double LogMagnitudeDistanceDb,
    IReadOnlyList<TimeProfilePoint> TimeProfile);

/// <summary>Time-averaged energy difference in a contiguous range of Mel bands.</summary>
/// <param name="StartHz">Lower edge in Hz.</param>
/// <param name="EndHz">Upper edge in Hz.</param>
/// <param name="MeanDifferenceDb">Mean candidate-minus-target difference over the range.</param>
/// <param name="PeakDifferenceDb">Largest-magnitude difference within the range (signed).</param>
public sealed record MelBandDifference(double StartHz, double EndHz, double MeanDifferenceDb, double PeakDifferenceDb);

/// <summary>Perceptual (log-Mel) comparison.</summary>
/// <param name="DistanceDb">Mean absolute log-Mel difference over time and bands, dB.</param>
/// <param name="ExcessBands">Frequency ranges where the candidate has too much energy.</param>
/// <param name="DeficientBands">Frequency ranges where the candidate has too little energy.</param>
public sealed record MelComparison(
    double DistanceDb,
    IReadOnlyList<MelBandDifference> ExcessBands,
    IReadOnlyList<MelBandDifference> DeficientBands);

/// <summary>Target and candidate value of one scalar descriptor.</summary>
/// <param name="FeatureId">Stable feature id.</param>
/// <param name="Unit">Unit of the values.</param>
/// <param name="Target">Value of the target.</param>
/// <param name="Candidate">Value of the candidate.</param>
/// <param name="Difference">Candidate minus target.</param>
public sealed record DescriptorComparison(string FeatureId, string Unit, double Target, double Candidate, double Difference);

/// <summary>Direction of a measured difference, always describing the candidate relative to the target.</summary>
public enum DifferenceDirection
{
    /// <summary>A level or position is lower than in the target.</summary>
    TooLow,

    /// <summary>A level or position is higher than in the target.</summary>
    TooHigh,

    /// <summary>A duration is shorter than in the target.</summary>
    TooShort,

    /// <summary>A duration is longer than in the target.</summary>
    TooLong,

    /// <summary>An energy or component is weaker than in the target.</summary>
    TooWeak,

    /// <summary>An energy or component is stronger than in the target.</summary>
    TooStrong,

    /// <summary>The feature differs structurally; no signed magnitude applies.</summary>
    Different,
}

/// <summary>One actionable, machine-readable difference between candidate and target.</summary>
/// <param name="Feature">Stable feature id, see <see cref="Features.ToneFeatureIds"/>.</param>
/// <param name="Direction">Direction of the difference, candidate relative to target.</param>
/// <param name="Magnitude">Absolute size of the difference in <paramref name="Unit"/>.</param>
/// <param name="Unit">Unit of <paramref name="Magnitude"/>.</param>
/// <param name="Importance">Continuous relevance 0..1 combining magnitude, perceptual weight, persistence and confidence. Differences are sorted by it.</param>
/// <param name="Confidence">Reliability 0..1 of the underlying measurement.</param>
/// <param name="SignedDelta">Signed difference, candidate minus target, in <paramref name="Unit"/>.</param>
/// <param name="FrequencyStartHz">Lower edge of the affected frequency range, when applicable.</param>
/// <param name="FrequencyEndHz">Upper edge of the affected frequency range, when applicable.</param>
/// <param name="TimeRegion">Affected time segment (see <see cref="TimeSegments"/>); null means the whole note.</param>
public sealed record ToneDifference(
    string Feature,
    DifferenceDirection Direction,
    double Magnitude,
    string Unit,
    double Importance,
    double Confidence,
    double SignedDelta,
    double? FrequencyStartHz = null,
    double? FrequencyEndHz = null,
    string? TimeRegion = null);

/// <summary>Generic direction in which a related synth parameter category would have to move.</summary>
public enum ParameterAdjustmentDirection
{
    /// <summary>Make the sound brighter.</summary>
    IncreaseBrightness,

    /// <summary>Make the sound darker.</summary>
    DecreaseBrightness,

    /// <summary>Raise a level or amount.</summary>
    Increase,

    /// <summary>Lower a level or amount.</summary>
    Decrease,

    /// <summary>Make a time longer.</summary>
    Lengthen,

    /// <summary>Make a time shorter.</summary>
    Shorten,

    /// <summary>No generic direction can be given; the relation has to be explored.</summary>
    Investigate,
}

/// <summary>
/// Generic prior knowledge about which kinds of synth parameters usually relate to a measured difference. A hint is
/// never proof that a specific parameter is responsible; empirically observed effects for the actual synth
/// (see the Optimization namespace) always take priority.
/// </summary>
/// <param name="Feature">Feature id of the difference the hint is about.</param>
/// <param name="PotentialParameterCategories">Parameter categories that commonly influence the feature.</param>
/// <param name="SuggestedDirection">Generic direction of the adjustment.</param>
/// <param name="Confidence">Confidence 0..1; deliberately capped low because the mapping is generic.</param>
/// <param name="TimeRegion">Time segment the hint applies to; null means the whole note.</param>
public sealed record ParameterHint(
    string Feature,
    IReadOnlyList<string> PotentialParameterCategories,
    ParameterAdjustmentDirection SuggestedDirection,
    double Confidence,
    string? TimeRegion = null);

/// <summary>Codes of the warnings that can appear in <see cref="AnalysisQuality"/>.</summary>
public static class AnalysisWarningCodes
{
    /// <summary>The input has no samples or an invalid sample rate.</summary>
    public const string EmptyInput = "input.empty";

    /// <summary>The input is silent.</summary>
    public const string Silent = "input.silent";

    /// <summary>The note is too short to analyse.</summary>
    public const string TooShort = "input.too_short";

    /// <summary>NaN or infinite samples were replaced by zero.</summary>
    public const string InvalidSamples = "input.invalid_samples";

    /// <summary>The input contains clipped samples; harmonic measurements may be distorted.</summary>
    public const string Clipping = "input.clipping";

    /// <summary>The input was resampled to the analysis sample rate.</summary>
    public const string Resampled = "input.resampled";

    /// <summary>The fundamental was estimated with low confidence.</summary>
    public const string UncertainFundamental = "f0.uncertain";

    /// <summary>The candidate pitch deviates noticeably from the target pitch.</summary>
    public const string PitchMismatch = "f0.mismatch";

    /// <summary>Only one of the two notes has a sustain stage; sustain and release cannot be compared.</summary>
    public const string EnvelopeShapeMismatch = "envelope.shape_mismatch";
}

/// <summary>A warning about the quality of the input or of a measurement.</summary>
/// <param name="Code">Stable code, see <see cref="AnalysisWarningCodes"/>.</param>
/// <param name="Source">"target" or "candidate".</param>
/// <param name="Message">Diagnostic text; decisions should be based on <paramref name="Code"/>.</param>
public sealed record AnalysisWarning(string Code, string Source, string Message);

/// <summary>Quality and reliability information about a comparison.</summary>
/// <param name="IsUsable">False when the candidate could not be analysed; scores then hold the configured penalty.</param>
/// <param name="OverallConfidence">Overall confidence 0..1 in the measurements.</param>
/// <param name="FundamentalHz">Fundamental the analysis used, in Hz.</param>
/// <param name="FundamentalConfidence">Confidence 0..1 in <paramref name="FundamentalHz"/>; 1 when it was supplied by the caller.</param>
/// <param name="AlignmentOffsetMs">Refinement shift applied to the candidate after onset alignment (positive: the candidate was late).</param>
/// <param name="TargetOnsetMs">Onset position in the original target recording.</param>
/// <param name="CandidateOnsetMs">Onset position in the original candidate recording.</param>
/// <param name="UnreliableFeatures">Feature ids whose delta could not be measured and is reported as 0.</param>
/// <param name="Warnings">Warnings raised during the analysis.</param>
public sealed record AnalysisQuality(
    bool IsUsable,
    double OverallConfidence,
    double FundamentalHz,
    double FundamentalConfidence,
    double AlignmentOffsetMs,
    double TargetOnsetMs,
    double CandidateOnsetMs,
    IReadOnlyList<string> UnreliableFeatures,
    IReadOnlyList<AnalysisWarning> Warnings);

/// <summary>
/// Complete, JSON-serializable result of comparing a candidate with a target. All differences use
/// <c>delta = candidate - target</c>; all distances use <c>0 = perfect</c>.
/// </summary>
/// <param name="Scores">Distance scores.</param>
/// <param name="Envelope">Envelope comparison.</param>
/// <param name="Harmonics">Per-harmonic comparison.</param>
/// <param name="Spectral">Multi-resolution STFT comparison.</param>
/// <param name="Mel">Perceptual log-Mel comparison.</param>
/// <param name="Descriptors">Scalar descriptors of both signals.</param>
/// <param name="FrequencyBands">Whole-note energy difference per configured frequency band.</param>
/// <param name="TimeSegments">Spectral comparison per time segment.</param>
/// <param name="Differences">Actionable differences, sorted by descending importance.</param>
/// <param name="ParameterHints">Generic, low-confidence hints about related synth parameter categories.</param>
/// <param name="DifferenceVector">Strongly typed difference vector.</param>
/// <param name="FeatureVector">Dense raw difference vector in schema order.</param>
/// <param name="NormalizedDifferenceVector">Dense normalized difference vector in schema order.</param>
/// <param name="Quality">Quality information.</param>
public sealed record ToneComparisonResult(
    SimilarityScores Scores,
    EnvelopeComparison Envelope,
    IReadOnlyList<HarmonicComparison> Harmonics,
    SpectralComparison Spectral,
    MelComparison Mel,
    IReadOnlyList<DescriptorComparison> Descriptors,
    IReadOnlyList<FrequencyBandDifference> FrequencyBands,
    IReadOnlyList<TimeSegmentComparison> TimeSegments,
    IReadOnlyList<ToneDifference> Differences,
    IReadOnlyList<ParameterHint> ParameterHints,
    ToneDifferenceVector DifferenceVector,
    FeatureVector FeatureVector,
    NormalizedFeatureVector NormalizedDifferenceVector,
    AnalysisQuality Quality);
