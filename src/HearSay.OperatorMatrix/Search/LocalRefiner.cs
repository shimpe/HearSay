using HearSay.Audio;
using HearSay.Comparison;
using HearSay.Modeling.Effects;
using HearSay.Modeling.Guidance;
using HearSay.Modeling.History;
using HearSay.Modeling.Parameters;
using HearSay.Modeling.Sensitivity;
using HearSay.Models;
using HearSay.Objective;
using HearSay.OperatorMatrix.Basins;
using HearSay.OperatorMatrix.Canonicalization;
using HearSay.OperatorMatrix.Parameters;
using HearSay.Synthesis.Patches;
using HearSay.Synthesis.Profiles;
using HearSay.Synthesis.Rendering;

namespace HearSay.OperatorMatrix.Search;

/// <summary>
/// Everything learned about parameter effects, kept apart per structural regime. A Jacobian estimated in one regime is
/// never used in another: what an amount does depends on which operators, waveforms, modes, ratios and edges surround
/// it.
/// </summary>
public sealed class RegimeModelStore
{
    private readonly Dictionary<string, (LocalLinearEffectModel Model, OptimizationHistory History, SynthParameterSpace Space)> _regimes = new(StringComparer.Ordinal);

    /// <summary>Regimes with observations.</summary>
    public IReadOnlyCollection<string> Regimes => _regimes.Keys;

    /// <summary>Number of observations of a regime.</summary>
    /// <param name="regime">Regime key.</param>
    public int ObservationCount(OperatorMatrixRegimeKey regime) =>
        _regimes.TryGetValue(Key(regime, null), out var entry) ? entry.History.Count : 0;

    internal (LocalLinearEffectModel Model, OptimizationHistory History) Get(OperatorMatrixRegimeKey regime, SynthParameterSpace space, FeatureVectorSchema schema)
    {
        // The parameter set is part of the key: a model is a regression over exactly these axes.
        string key = Key(regime, space);
        if (!_regimes.TryGetValue(key, out var entry))
        {
            entry = (new LocalLinearEffectModel(space, schema), new OptimizationHistory(), space);
            _regimes[key] = entry;
            _regimes[Key(regime, null)] = entry;
        }

        return (entry.Model, entry.History);
    }

    private static string Key(OperatorMatrixRegimeKey regime, SynthParameterSpace? space) =>
        space is null ? regime.Value : regime.Value + "#" + string.Join(",", space.Parameters.Select(static p => p.Id));
}

/// <summary>
/// Final polish of a basin with the local machinery of HearSay.Modeling: it probes the smooth parameters, learns a
/// regime-specific Jacobian of the diagnostic feature vector, and follows trust-region guidance steps for as long as
/// the audio objective confirms them. Structural parameters never take part.
/// </summary>
public sealed class LocalRefiner
{
    private readonly PatchEvaluator _evaluator;
    private readonly SynthesisCapabilities _capabilities;
    private readonly ToneComparer _comparer = new();
    private readonly PreparedTarget _target;
    private readonly OptimizationGuidance _guidance = new();
    private readonly RegimeModelStore _store;
    private readonly int _maxParameters;

    /// <summary>Creates a refiner.</summary>
    /// <param name="evaluator">Evaluator of the search.</param>
    /// <param name="capabilities">Limits of the synthesis profile.</param>
    /// <param name="store">Regime-specific models, shared by all basins of a search.</param>
    /// <param name="maxParameters">Largest number of parameters refined together.</param>
    public LocalRefiner(PatchEvaluator evaluator, SynthesisCapabilities capabilities, RegimeModelStore store, int maxParameters = 12)
    {
        ArgumentNullException.ThrowIfNull(evaluator);
        ArgumentNullException.ThrowIfNull(capabilities);
        ArgumentNullException.ThrowIfNull(store);
        _evaluator = evaluator;
        _capabilities = capabilities;
        _store = store;
        _maxParameters = maxParameters;
        PreprocessedTargetNote = new AudioNote(evaluator.Target.Analysis.Audio.Samples, evaluator.Target.Analysis.Audio.SampleRate, evaluator.Target.Analysis.FundamentalHz);
        _target = _comparer.PrepareTarget(PreprocessedTargetNote, new ToneComparisonOptions { LoudnessMode = LoudnessMode.TimbreOnly });
    }

    /// <summary>The preprocessed target as a note; the diagnostic comparison of the final report uses the same one.</summary>
    public AudioNote PreprocessedTargetNote { get; }

    /// <summary>Diagnostic comparison of a patch with the target. Costs no budget: it is meant for reports.</summary>
    /// <param name="patch">Patch to compare.</param>
    public ToneComparisonResult Compare(OperatorMatrixPatch patch)
    {
        RenderedAudio audio = _evaluator.Render(patch);
        return _comparer.Compare(_target, audio.ToAudioNote(_target.FundamentalHz));
    }

    /// <summary>Refines a basin.</summary>
    /// <param name="basin">Basin to refine; its best evaluation must be at <paramref name="level"/>.</param>
    /// <param name="evaluations">Renders this call may spend.</param>
    /// <param name="level">Evaluation level of the accept test.</param>
    /// <returns>Number of accepted guidance steps.</returns>
    public int Refine(SearchBasin basin, int evaluations, EvaluationLevel level)
    {
        ArgumentNullException.ThrowIfNull(basin);
        OperatorMatrixPatch patch = basin.Best.Patch;
        PatchParameterSchema schema = PatchParameterSchema.Build(patch, _capabilities, null, _evaluator.TimeLimits);
        var parameters = new List<PatchParameter>();
        foreach (PatchParameter p in schema.ActiveSmooth(patch))
        {
            // Only smooth parameters with a direct, local effect on the sound are eligible.
            if (p.Id.EndsWith(".amount", StringComparison.Ordinal) || p.Id.EndsWith(".output.level", StringComparison.Ordinal))
            {
                parameters.Add(p);
            }
        }

        foreach (PatchParameter p in schema.ActiveSmooth(patch))
        {
            if (parameters.Count < _maxParameters && p.Id.Contains(".env.p", StringComparison.Ordinal) && p.Id.EndsWith(".time", StringComparison.Ordinal) && !p.Id.StartsWith("pitch.", StringComparison.Ordinal))
            {
                parameters.Add(p);
            }
        }

        if (parameters.Count > _maxParameters)
        {
            parameters.RemoveRange(_maxParameters, parameters.Count - _maxParameters);
        }

        if (parameters.Count == 0 || evaluations < parameters.Count + 2)
        {
            return 0;
        }

        var set = new ActiveParameterSet(parameters);
        double[] position = set.ToVector(patch);
        var definitions = new List<SynthParameter>(parameters.Count);
        for (int i = 0; i < parameters.Count; i++)
        {
            definitions.Add(new SynthParameter(parameters[i].Id, parameters[i].Id, position[i], 0.0, 1.0, SynthParameterKind.Continuous, 0.005, 0.15));
        }

        var space = new SynthParameterSpace(definitions);
        OperatorMatrixRegimeKey regime = PatchCanonicalizer.CanonicalKey(patch);
        (LocalLinearEffectModel model, OptimizationHistory history) = _store.Get(regime, space, _target.Schema);

        int spent = 0;
        int accepted = 0;
        long iteration = history.Count;

        (PatchEvaluation Evaluation, ToneComparisonResult Comparison)? Measure(double[] at, OptimizationStepType type)
        {
            if (spent >= evaluations || _evaluator.Remaining == 0)
            {
                return null;
            }

            OperatorMatrixPatch candidate = set.Apply(patch, at);
            (PatchEvaluation Evaluation, RenderedAudio Audio)? measured = _evaluator.EvaluateWithAudio(candidate, level);
            if (measured is null)
            {
                return null;
            }

            // Loss and diagnostic vector come from the same render.
            spent++;
            PatchEvaluation evaluation = measured.Value.Evaluation;
            ToneComparisonResult comparison = _comparer.Compare(_target, measured.Value.Audio.ToAudioNote(_target.FundamentalHz));
            var observation = new OptimizationObservation(
                iteration++,
                State(parameters, at),
                comparison.Scores,
                comparison.DifferenceVector,
                comparison.FeatureVector,
                DateTimeOffset.UnixEpoch,
                evaluation.Loss,
                type);
            history.Add(observation);
            model.Observe(observation);
            return (evaluation, comparison);
        }

        var current = Measure(position, OptimizationStepType.Exploitation);
        if (current is null)
        {
            return 0;
        }

        // Probe every parameter once so that the regime model has a slope for each axis.
        for (int i = 0; i < parameters.Count; i++)
        {
            double[] probe = (double[])position.Clone();
            probe[i] = position[i] + (position[i] > 0.9 ? -0.04 : 0.04);
            var result = Measure(probe, OptimizationStepType.Exploration);
            if (result is null)
            {
                break;
            }

            if (result.Value.Evaluation.Loss < current.Value.Evaluation.Loss)
            {
                position = probe;
                current = result;
                basin.Offer(result.Value.Evaluation);
            }
        }

        double trust = 0.12;
        int rejected = 0;
        while (spent < evaluations && rejected < 4 && trust > 0.004)
        {
            SynthParameterState state = State(parameters, position);
            ParameterFeatureJacobian jacobian = model.EstimateJacobian(state);
            GuidanceResult guidance = _guidance.Suggest(
                current.Value.Comparison.FeatureVector,
                state,
                jacobian,
                space,
                _target.Schema,
                new GuidanceOptions { MaximumNormalizedStep = trust, UseGenericPriors = false });
            if (guidance.Suggestions.Count == 0)
            {
                break;
            }

            double[] next = (double[])position.Clone();
            foreach (ParameterAdjustmentSuggestion suggestion in guidance.Suggestions)
            {
                int index = parameters.FindIndex(p => p.Id == suggestion.ParameterId);
                if (index >= 0)
                {
                    next[index] = Math.Clamp(suggestion.SuggestedValue, 0.0, 1.0);
                }
            }

            var result = Measure(next, OptimizationStepType.Exploitation);
            if (result is null)
            {
                break;
            }

            // The diagnostic vector proposes, the audio objective decides.
            if (result.Value.Evaluation.Loss < current.Value.Evaluation.Loss)
            {
                position = next;
                current = result;
                basin.Offer(result.Value.Evaluation);
                accepted++;
                rejected = 0;
                trust = Math.Min(0.2, trust * 1.3);
            }
            else
            {
                rejected++;
                trust *= 0.5;
            }
        }

        basin.Evaluations += spent;
        return accepted;
    }

    private static SynthParameterState State(List<PatchParameter> parameters, double[] position)
    {
        var values = new Dictionary<string, double>(parameters.Count, StringComparer.Ordinal);
        for (int i = 0; i < parameters.Count; i++)
        {
            values[parameters[i].Id] = position[i];
        }

        return new SynthParameterState(values);
    }
}
