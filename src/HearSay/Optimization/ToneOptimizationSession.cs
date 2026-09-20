using System.Diagnostics;
using System.Reflection;
using HearSay.Audio;
using HearSay.Comparison;
using HearSay.Models;
using HearSay.Optimization.Effects;
using HearSay.Optimization.Guidance;
using HearSay.Optimization.History;
using HearSay.Optimization.Parameters;
using HearSay.Optimization.Sensitivity;

namespace HearSay.Optimization;

/// <summary>Settings of a <see cref="ToneOptimizationSession"/>.</summary>
public sealed record ToneOptimizationSessionOptions
{
    /// <summary>Settings of the learned parameter-effect model.</summary>
    public EffectModelOptions EffectModel { get; init; } = new();

    /// <summary>Settings of the adjustment guidance.</summary>
    public GuidanceOptions Guidance { get; init; } = new();

    /// <summary>Thresholds for stagnation and oscillation detection.</summary>
    public ProgressOptions Progress { get; init; } = new();
}

/// <summary>
/// Serializable state of a session. The analysed target (spectrograms and so on) is deliberately not part of it: it is
/// large and can be recomputed exactly from the target audio with <see cref="Options"/>.
/// </summary>
/// <param name="FormatVersion">Version of this state format.</param>
/// <param name="LibraryVersion">Version of the library that wrote the state.</param>
/// <param name="TargetFingerprint">Hash of the target audio the run belongs to.</param>
/// <param name="SampleRate">Analysis sample rate in Hz.</param>
/// <param name="FundamentalHz">Fundamental used by the analysis.</param>
/// <param name="Options">Comparison configuration, including metric weights.</param>
/// <param name="Schema">Feature schema, including normalization scales.</param>
/// <param name="Parameters">Parameter definitions.</param>
/// <param name="SessionOptions">Session settings.</param>
/// <param name="Observations">All observations so far.</param>
public sealed record ToneOptimizationSessionState(
    int FormatVersion,
    string LibraryVersion,
    string TargetFingerprint,
    int SampleRate,
    double FundamentalHz,
    ToneComparisonOptions Options,
    FeatureVectorSchema Schema,
    IReadOnlyList<SynthParameter> Parameters,
    ToneOptimizationSessionOptions SessionOptions,
    IReadOnlyList<OptimizationObservation> Observations);

/// <summary>
/// Measurement and bookkeeping side of a closed-loop sound-matching run. The session does not drive a synthesizer and
/// is not an optimizer: the caller renders candidates and decides on the next parameters. The session analyses each
/// candidate, records it, keeps the parameter-effect model up to date and answers questions about progress,
/// sensitivities and promising next steps. <see cref="Evaluate"/> may be called from several threads.
/// </summary>
public sealed class ToneOptimizationSession
{
    private const int StateFormatVersion = 1;

    private readonly IToneComparer comparer;
    private readonly TimeProvider timeProvider;
    private readonly OptimizationHistoryAnalyzer historyAnalyzer = new();
    private readonly OptimizationGuidance guidance = new();
    private readonly NextExperimentAdvisor advisor = new();
    private long nextIteration;

    /// <summary>Creates a session.</summary>
    /// <param name="target">Prepared target; analysed once and reused for every candidate.</param>
    /// <param name="parameters">Definitions of the synth parameters that are being optimized.</param>
    /// <param name="options">Session settings; null uses the defaults.</param>
    /// <param name="comparer">Comparer to use; null creates a default one.</param>
    /// <param name="timeProvider">Clock used for observation timestamps; null uses the system clock.</param>
    public ToneOptimizationSession(
        PreparedTarget target,
        IEnumerable<SynthParameter> parameters,
        ToneOptimizationSessionOptions? options = null,
        IToneComparer? comparer = null,
        TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(target);
        Target = target;
        ParameterSpace = new SynthParameterSpace(parameters);
        Options = options ?? new ToneOptimizationSessionOptions();
        this.comparer = comparer ?? new ToneComparer();
        this.timeProvider = timeProvider ?? TimeProvider.System;
        History = new OptimizationHistory();
        EffectModel = new LocalLinearEffectModel(ParameterSpace, target.Schema, Options.EffectModel);
    }

    /// <summary>The prepared target all candidates are compared with.</summary>
    public PreparedTarget Target { get; }

    /// <summary>Definitions of the optimized parameters.</summary>
    public SynthParameterSpace ParameterSpace { get; }

    /// <summary>Session settings.</summary>
    public ToneOptimizationSessionOptions Options { get; }

    /// <summary>All observations of the run.</summary>
    public OptimizationHistory History { get; }

    /// <summary>Parameter-effect model learned from the observations of this session.</summary>
    public LocalLinearEffectModel EffectModel { get; }

    /// <summary>Version of the library, stored with exported state for reproducibility.</summary>
    public static string LibraryVersion { get; } =
        typeof(ToneOptimizationSession).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
        ?? typeof(ToneOptimizationSession).Assembly.GetName().Version?.ToString()
        ?? "unknown";

    /// <summary>Analyses a rendered candidate, records the observation and updates the effect model.</summary>
    /// <param name="parameters">Parameter values the candidate was rendered with.</param>
    /// <param name="renderedCandidate">Rendered audio.</param>
    /// <param name="stepType">Intent of the step that led to this candidate.</param>
    /// <param name="candidateId">Optional caller-defined id.</param>
    /// <param name="renderDuration">Optional time the render took.</param>
    public ToneComparisonResult Evaluate(
        SynthParameterState parameters,
        AudioNote renderedCandidate,
        OptimizationStepType stepType = OptimizationStepType.Exploitation,
        string? candidateId = null,
        TimeSpan? renderDuration = null)
    {
        ArgumentNullException.ThrowIfNull(parameters);
        long started = Stopwatch.GetTimestamp();
        ToneComparisonResult result = comparer.Compare(Target, renderedCandidate);
        TimeSpan analysisDuration = Stopwatch.GetElapsedTime(started);

        var observation = new OptimizationObservation(
            Interlocked.Increment(ref nextIteration) - 1,
            parameters,
            result.Scores,
            result.DifferenceVector,
            result.FeatureVector,
            timeProvider.GetUtcNow(),
            result.Scores.Overall,
            stepType,
            result.Quality.IsUsable ? result.Quality.OverallConfidence : 0.0,
            candidateId,
            renderDuration,
            analysisDuration);
        History.Add(observation);
        if (result.Quality.IsUsable)
        {
            EffectModel.Observe(observation);
        }

        return result;
    }

    /// <summary>Progress of the run, including stagnation; null before the first evaluation.</summary>
    public OptimizationProgress? GetProgress() => historyAnalyzer.GetProgress(History, Options.Progress);

    /// <summary>Parameters and features that keep reversing without net progress.</summary>
    public IReadOnlyList<OscillationReport> DetectOscillations() =>
        historyAnalyzer.DetectOscillations(History, ParameterSpace, Target.Schema, Options.Progress);

    /// <summary>Observed parameter-feature correlations over the whole run (not proof of causation).</summary>
    /// <param name="minimumAbsoluteCorrelation">Weaker relations are omitted.</param>
    public IReadOnlyList<ParameterFeatureRelationship> GetRelationships(double minimumAbsoluteCorrelation = 0.3) =>
        historyAnalyzer.GetRelationships(History, ParameterSpace, minimumAbsoluteCorrelation);

    /// <summary>Approximate Jacobian around a state; null uses the best state so far.</summary>
    /// <param name="around">State of interest.</param>
    public ParameterFeatureJacobian EstimateJacobian(SynthParameterState? around = null) =>
        EffectModel.EstimateJacobian(around ?? ReferenceObservation()?.Parameters ?? SynthParameterState.FromDefaults(ParameterSpace.Parameters));

    /// <summary>Suggested parameter adjustments starting from an observation; null uses the best observation so far.</summary>
    /// <param name="from">Observation to improve on.</param>
    /// <param name="options">Guidance settings for this call, for example a smaller maximum step; null uses the session settings.</param>
    public GuidanceResult SuggestAdjustments(OptimizationObservation? from = null, GuidanceOptions? options = null)
    {
        OptimizationObservation? reference = from ?? ReferenceObservation();
        if (reference is null)
        {
            return new GuidanceResult([], 0.0, 0.0);
        }

        ParameterFeatureJacobian jacobian = EffectModel.EstimateJacobian(reference.Parameters);
        return guidance.Suggest(reference.Features, reference.Parameters, jacobian, ParameterSpace, Target.Schema, options ?? Options.Guidance);
    }

    /// <summary>Proposed next experiments: exploration where knowledge is missing, followed by exploitation steps.</summary>
    /// <param name="from">Observation to start from; null uses the best observation so far.</param>
    public IReadOnlyList<ExperimentSuggestion> SuggestExperiments(OptimizationObservation? from = null)
    {
        OptimizationObservation? reference = from ?? ReferenceObservation();
        if (reference is null)
        {
            return [];
        }

        ParameterFeatureJacobian jacobian = EffectModel.EstimateJacobian(reference.Parameters);
        GuidanceResult suggestions = guidance.Suggest(reference.Features, reference.Parameters, jacobian, ParameterSpace, Target.Schema, Options.Guidance);
        return advisor.Advise(reference.Features, reference.Parameters, jacobian, ParameterSpace, Target.Schema, History, suggestions);
    }

    /// <summary>
    /// Serializes the run (observations, parameter definitions and all configuration needed to reproduce and interpret
    /// it) to JSON. The analysed target is not included; pass the target again to <see cref="ImportState(string, PreparedTarget, IToneComparer?, bool)"/>.
    /// </summary>
    public string ExportState() => HearSayJson.Serialize(new ToneOptimizationSessionState(
        StateFormatVersion,
        LibraryVersion,
        Target.Fingerprint,
        Target.SampleRate,
        Target.FundamentalHz,
        Target.Options,
        Target.Schema,
        ParameterSpace.Parameters,
        Options,
        History.Observations));

    /// <summary>Restores a session from exported state and an already prepared target.</summary>
    /// <param name="json">State produced by <see cref="ExportState"/>.</param>
    /// <param name="target">The same target the run was started with.</param>
    /// <param name="comparer">Comparer to use; null creates a default one.</param>
    /// <param name="verifyTarget">Throw when the target audio differs from the one the state was exported with.</param>
    /// <exception cref="InvalidOperationException">The target or its feature schema does not match the stored run.</exception>
    public static ToneOptimizationSession ImportState(string json, PreparedTarget target, IToneComparer? comparer = null, bool verifyTarget = true)
    {
        ArgumentNullException.ThrowIfNull(target);
        ToneOptimizationSessionState state = HearSayJson.Deserialize<ToneOptimizationSessionState>(json);
        if (state.FormatVersion != StateFormatVersion)
        {
            throw new InvalidOperationException($"Unsupported session state version {state.FormatVersion}.");
        }

        if (verifyTarget && state.TargetFingerprint != target.Fingerprint)
        {
            throw new InvalidOperationException("The target audio differs from the target this run was recorded with.");
        }

        if (state.Schema.Features.Count != target.Schema.Features.Count)
        {
            throw new InvalidOperationException("The feature schema of the target differs from the stored run; prepare the target with the stored options.");
        }

        var session = new ToneOptimizationSession(target, state.Parameters, state.SessionOptions, comparer);
        foreach (OptimizationObservation observation in state.Observations)
        {
            session.History.Add(observation);
            if (observation.MeasurementConfidence > 0)
            {
                session.EffectModel.Observe(observation);
            }

            session.nextIteration = Math.Max(session.nextIteration, observation.Iteration + 1);
        }

        return session;
    }

    /// <summary>
    /// Restores a session from exported state and the raw target audio. The target is re-analysed with the stored
    /// comparison options, which reproduces the original prepared target exactly.
    /// </summary>
    /// <param name="json">State produced by <see cref="ExportState"/>.</param>
    /// <param name="targetAudio">The target audio of the run.</param>
    /// <param name="comparer">Comparer to use; null creates a default one.</param>
    public static ToneOptimizationSession ImportState(string json, AudioNote targetAudio, IToneComparer? comparer = null)
    {
        ToneOptimizationSessionState state = HearSayJson.Deserialize<ToneOptimizationSessionState>(json);
        comparer ??= new ToneComparer();
        return ImportState(json, comparer.PrepareTarget(targetAudio, state.Options), comparer);
    }

    private OptimizationObservation? ReferenceObservation() => History.Best ?? History.Latest;
}
