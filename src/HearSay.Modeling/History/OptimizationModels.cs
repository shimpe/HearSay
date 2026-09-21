using HearSay.Models;
using HearSay.Modeling.Parameters;

namespace HearSay.Modeling.History;

/// <summary>Intent of an optimization step.</summary>
public enum OptimizationStepType
{
    /// <summary>A small, controlled change made to learn what a parameter does.</summary>
    Exploration,

    /// <summary>A change that is expected to reduce a known difference.</summary>
    Exploitation,

    /// <summary>A re-evaluation that checks an earlier result or prediction.</summary>
    Validation,
}

/// <summary>A parameter change together with its intent.</summary>
/// <param name="Type">Intent of the step.</param>
/// <param name="Before">State before the step.</param>
/// <param name="After">State after the step.</param>
/// <param name="PrimaryParameterId">The parameter the step is about, when there is a single one.</param>
public sealed record OptimizationStep(
    OptimizationStepType Type,
    SynthParameterState Before,
    SynthParameterState After,
    string? PrimaryParameterId);

/// <summary>One evaluated candidate of an optimization run.</summary>
/// <param name="Iteration">Sequence number within the run, starting at 0.</param>
/// <param name="Parameters">Parameter values that produced the candidate.</param>
/// <param name="Scores">Distance scores of the comparison.</param>
/// <param name="DifferenceVector">Typed difference vector (candidate minus target).</param>
/// <param name="Features">Dense raw difference vector in schema order (candidate minus target).</param>
/// <param name="Timestamp">Time of the evaluation.</param>
/// <param name="OverallLoss">Loss an optimizer minimizes; equal to <see cref="SimilarityScores.Overall"/>.</param>
/// <param name="StepType">Intent of the step that led to this candidate.</param>
/// <param name="MeasurementConfidence">Confidence 0..1 of the comparison; low-quality measurements weigh less when learning.</param>
/// <param name="CandidateId">Caller-defined id of the candidate.</param>
/// <param name="RenderDuration">Time the synth needed to render the candidate.</param>
/// <param name="AnalysisDuration">Time the comparison took.</param>
public sealed record OptimizationObservation(
    long Iteration,
    SynthParameterState Parameters,
    SimilarityScores Scores,
    ToneDifferenceVector DifferenceVector,
    FeatureVector Features,
    DateTimeOffset Timestamp,
    double OverallLoss,
    OptimizationStepType StepType = OptimizationStepType.Exploitation,
    double MeasurementConfidence = 1.0,
    string? CandidateId = null,
    TimeSpan? RenderDuration = null,
    TimeSpan? AnalysisDuration = null);

/// <summary>Thresholds for progress, stagnation and oscillation diagnostics.</summary>
public sealed record ProgressOptions
{
    /// <summary>Number of most recent iterations that are inspected for stagnation.</summary>
    public int StagnationWindow { get; init; } = 20;

    /// <summary>Relative improvement of the best loss over the window below which the run counts as stagnating.</summary>
    public double MinimumRelativeImprovement { get; init; } = 0.01;

    /// <summary>Number of most recent iterations that are inspected for oscillation.</summary>
    public int OscillationWindow { get; init; } = 8;

    /// <summary>Direction reversals within the window from which a series counts as oscillating.</summary>
    public int MinimumReversals { get; init; } = 3;

    /// <summary>Normalized parameter changes smaller than this are ignored when counting reversals.</summary>
    public double MinimumParameterStep { get; init; } = 0.005;

    /// <summary>Normalized feature errors smaller than this count as converged and are not reported as oscillating.</summary>
    public double MinimumFeatureError { get; init; } = 0.1;
}

/// <summary>Progress of an optimization run.</summary>
/// <param name="Iterations">Number of observations.</param>
/// <param name="CurrentLoss">Loss of the latest observation.</param>
/// <param name="BestLoss">Lowest loss so far.</param>
/// <param name="BestIteration">Iteration that achieved <paramref name="BestLoss"/>.</param>
/// <param name="ImprovementFromStart">First loss minus best loss.</param>
/// <param name="ImprovementSincePrevious">Previous loss minus current loss (negative: the latest step made things worse).</param>
/// <param name="RecentImprovementRate">Improvement of the best loss per iteration over the stagnation window.</param>
/// <param name="IterationsSinceBest">Iterations since the best loss was found.</param>
/// <param name="IsStagnating">True when the best loss barely improved over the stagnation window.</param>
public sealed record OptimizationProgress(
    int Iterations,
    double CurrentLoss,
    double BestLoss,
    long BestIteration,
    double ImprovementFromStart,
    double ImprovementSincePrevious,
    double RecentImprovementRate,
    int IterationsSinceBest,
    bool IsStagnating);

/// <summary>Oscillation diagnostic for one parameter or feature.</summary>
/// <param name="Id">Parameter id or feature id.</param>
/// <param name="IsParameter">True for a parameter series (normalized values), false for a feature-error series (normalized deltas).</param>
/// <param name="Reversals">Parameters: reversals of the step direction. Features: sign changes of the error.</param>
/// <param name="MeanAmplitude">Parameters: mean absolute step. Features: mean absolute error.</param>
/// <param name="NetChange">Absolute difference between the first and last value in the window.</param>
/// <param name="IsOscillating">True when the series keeps reversing without net progress.</param>
public sealed record OscillationReport(string Id, bool IsParameter, int Reversals, double MeanAmplitude, double NetChange, bool IsOscillating);

/// <summary>Change of one parameter between two observations.</summary>
/// <param name="ParameterId">Parameter id.</param>
/// <param name="Before">Value before, native units.</param>
/// <param name="After">Value after, native units.</param>
/// <param name="NormalizedDelta">Change in normalized (0..1) units.</param>
public sealed record ParameterChange(string ParameterId, double Before, double After, double NormalizedDelta);

/// <summary>Change of one feature delta between two observations.</summary>
/// <param name="FeatureId">Feature id.</param>
/// <param name="Before">Feature delta before.</param>
/// <param name="After">Feature delta after.</param>
/// <param name="Delta">After minus before, in raw feature units.</param>
public sealed record FeatureChange(string FeatureId, double Before, double After, double Delta);

/// <summary>
/// Observed statistical relation between a parameter and a feature over a run. This is a correlation, not proof of
/// causation: parameters that were changed together are indistinguishable here. Use probes or the local effect model
/// for causal estimates.
/// </summary>
/// <param name="ParameterId">Parameter id.</param>
/// <param name="FeatureId">Feature id.</param>
/// <param name="Correlation">Pearson correlation (-1..+1) between the normalized parameter value and the feature delta.</param>
/// <param name="Confidence">Statistical confidence 0..1 that the correlation is not zero (Fisher transformation).</param>
/// <param name="ObservationCount">Number of observations used.</param>
public sealed record ParameterFeatureRelationship(string ParameterId, string FeatureId, double Correlation, double Confidence, int ObservationCount);
