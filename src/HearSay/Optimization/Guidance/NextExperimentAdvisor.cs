using System.Globalization;
using HearSay.Models;
using HearSay.Optimization.History;
using HearSay.Optimization.Parameters;
using HearSay.Optimization.Sensitivity;

namespace HearSay.Optimization.Guidance;

/// <summary>A proposed next experiment.</summary>
/// <param name="ParameterId">Parameter to change.</param>
/// <param name="SuggestedValue">Value to try, native units.</param>
/// <param name="StepType">Exploration (learn what the parameter does) or exploitation (reduce a known error).</param>
/// <param name="ExpectedInformationGain">
/// 0..1. For exploration: the error-weighted share of this parameter's effects that is still unknown. For
/// exploitation: the remaining uncertainty of the suggestion.
/// </param>
/// <param name="Reason">Diagnostic text only; decisions should use the machine-readable fields.</param>
public sealed record ExperimentSuggestion(
    string ParameterId,
    double SuggestedValue,
    OptimizationStepType StepType,
    double ExpectedInformationGain,
    string Reason);

/// <summary>
/// Proposes which parameter would be most informative to test next. Exploration candidates are ranked by how little
/// is known about a parameter on the features that are currently wrong; unordered parameters are proposed value by
/// value, because they cannot be probed with small steps. Stateless and thread-safe.
/// </summary>
public sealed class NextExperimentAdvisor
{
    /// <summary>Exploration suggestions with a lower information gain are omitted.</summary>
    public double MinimumInformationGain { get; init; } = 0.25;

    /// <summary>Normalized size of an exploratory step when the parameter does not recommend one.</summary>
    public double ExplorationStep { get; init; } = 0.05;

    /// <summary>Proposes experiments, most informative exploration first, followed by exploitation steps from guidance.</summary>
    /// <param name="error">Current raw difference vector in schema order.</param>
    /// <param name="currentState">Current parameter state.</param>
    /// <param name="jacobian">Local Jacobian around <paramref name="currentState"/>.</param>
    /// <param name="space">Parameter definitions.</param>
    /// <param name="schema">Feature schema.</param>
    /// <param name="history">Optimization history; used to find untried values of unordered parameters.</param>
    /// <param name="guidance">Optional guidance result whose suggestions are appended as exploitation steps.</param>
    public IReadOnlyList<ExperimentSuggestion> Advise(
        FeatureVector error,
        SynthParameterState currentState,
        ParameterFeatureJacobian jacobian,
        SynthParameterSpace space,
        FeatureVectorSchema schema,
        OptimizationHistory history,
        GuidanceResult? guidance = null)
    {
        ArgumentNullException.ThrowIfNull(error);
        ArgumentNullException.ThrowIfNull(currentState);
        ArgumentNullException.ThrowIfNull(jacobian);
        ArgumentNullException.ThrowIfNull(space);
        ArgumentNullException.ThrowIfNull(schema);
        ArgumentNullException.ThrowIfNull(history);

        int fCount = schema.Features.Count;
        var relevance = new double[fCount];
        double relevanceSum = 0;
        for (int f = 0; f < fCount && f < error.Values.Length; f++)
        {
            relevance[f] = Math.Abs(error.Values.Span[f] / schema.Features[f].TypicalScale);
            relevanceSum += relevance[f];
        }

        var result = new List<ExperimentSuggestion>();
        IReadOnlyList<OptimizationObservation> observations = history.Observations;
        foreach (SynthParameter parameter in space.Parameters)
        {
            double current = currentState.Values.TryGetValue(parameter.Id, out double v) ? v : parameter.Value;
            if (!parameter.IsOrdered)
            {
                AddUntriedValues(result, parameter, current, observations);
                continue;
            }

            int column = -1;
            for (int p = 0; p < jacobian.ParameterIds.Count; p++)
            {
                if (jacobian.ParameterIds[p] == parameter.Id)
                {
                    column = p;
                    break;
                }
            }

            // Share of the current error whose dependence on this parameter is still unknown.
            double unknown = 1.0;
            if (column >= 0 && fCount > 0)
            {
                double sum = 0;
                for (int f = 0; f < fCount; f++)
                {
                    double weight = relevanceSum > 0 ? relevance[f] / relevanceSum : 1.0 / fCount;
                    sum += weight * (1.0 - jacobian.Confidence[f, column]);
                }

                unknown = sum;
            }

            if (unknown < MinimumInformationGain)
            {
                continue;
            }

            double step = Math.Max(parameter.RecommendedMinStep ?? 0.0, ExplorationStep * parameter.Range);
            if (parameter.Kind == SynthParameterKind.Integer)
            {
                step = Math.Max(1.0, Math.Round(step));
            }

            // Start on the side with more room. Rendering a state twice teaches nothing, so skip probes that are
            // already in the history: try the other side, then wider steps.
            double direction = parameter.MaxValue - current >= current - parameter.MinValue ? 1.0 : -1.0;
            double? suggested = null;
            for (int widen = 0; widen < 3 && suggested is null; widen++)
            {
                foreach (double sign in (ReadOnlySpan<double>)[direction, -direction])
                {
                    double value = parameter.Coerce(current + (sign * step * (1 << widen)));
                    if (value != current && !WasEvaluated(observations, currentState, parameter, value, space))
                    {
                        suggested = value;
                        break;
                    }
                }
            }

            if (suggested is null)
            {
                continue;
            }

            result.Add(new ExperimentSuggestion(
                parameter.Id,
                suggested.Value,
                OptimizationStepType.Exploration,
                unknown,
                string.Create(CultureInfo.InvariantCulture, $"{unknown:P0} of the current error has an unknown dependence on '{parameter.Id}'.")));
        }

        result.Sort(static (a, b) =>
        {
            int order = b.ExpectedInformationGain.CompareTo(a.ExpectedInformationGain);
            return order != 0 ? order : string.CompareOrdinal(a.ParameterId, b.ParameterId);
        });

        if (guidance is not null)
        {
            foreach (ParameterAdjustmentSuggestion suggestion in guidance.Suggestions)
            {
                if (suggestion.Source == SuggestionSource.Empirical)
                {
                    result.Add(new ExperimentSuggestion(
                        suggestion.ParameterId,
                        suggestion.SuggestedValue,
                        OptimizationStepType.Exploitation,
                        1.0 - suggestion.Confidence,
                        string.Create(CultureInfo.InvariantCulture, $"Measured effects predict an error reduction (confidence {suggestion.Confidence:F2}).")));
                }
            }
        }

        return result;
    }

    /// <summary>True when the history already contains the current state with only this parameter set to the given value.</summary>
    private static bool WasEvaluated(
        IReadOnlyList<OptimizationObservation> observations,
        SynthParameterState currentState,
        SynthParameter probed,
        double value,
        SynthParameterSpace space)
    {
        foreach (OptimizationObservation observation in observations)
        {
            bool same = true;
            foreach (SynthParameter parameter in space.Parameters)
            {
                double expected = parameter.Id == probed.Id
                    ? value
                    : currentState.Values.TryGetValue(parameter.Id, out double c) ? c : parameter.Value;
                double actual = observation.Parameters.Values.TryGetValue(parameter.Id, out double a) ? a : parameter.Value;
                if (Math.Abs(expected - actual) > 1e-9 * Math.Max(1.0, parameter.Range))
                {
                    same = false;
                    break;
                }
            }

            if (same)
            {
                return true;
            }
        }

        return false;
    }

    private static void AddUntriedValues(List<ExperimentSuggestion> result, SynthParameter parameter, double current, IReadOnlyList<OptimizationObservation> observations)
    {
        var tried = new HashSet<double> { current };
        foreach (OptimizationObservation observation in observations)
        {
            if (observation.Parameters.Values.TryGetValue(parameter.Id, out double value))
            {
                tried.Add(value);
            }
        }

        int valueCount = (int)Math.Min(64, Math.Round(parameter.Range) + 1);
        for (int i = 0; i < valueCount; i++)
        {
            double value = parameter.MinValue + i;
            if (!tried.Contains(value))
            {
                result.Add(new ExperimentSuggestion(
                    parameter.Id,
                    value,
                    OptimizationStepType.Exploration,
                    0.5,
                    string.Create(CultureInfo.InvariantCulture, $"Value {value} of the unordered parameter '{parameter.Id}' has not been tried.")));
            }
        }
    }
}
