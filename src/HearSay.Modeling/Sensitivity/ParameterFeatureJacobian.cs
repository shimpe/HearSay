using System.Text.Json.Serialization;
using HearSay.Dsp;
using HearSay.Models;
using HearSay.Modeling.Parameters;
using HearSay.Modeling.Prediction;

namespace HearSay.Modeling.Sensitivity;

/// <summary>
/// Approximate local Jacobian of the feature deltas with respect to the ordered synth parameters.
/// <c>Values[f, p]</c> estimates d(feature f) / d(parameter p) in raw feature units per normalized parameter unit
/// (full range = 1). The matrix is only valid near <see cref="ReferenceState"/>: synth parameters are not linear over
/// their range. <c>Confidence[f, p]</c> (0..1) says how much each entry can be trusted; an entry of 0 with confidence
/// 0 means "unknown", not "no effect".
/// </summary>
public sealed class ParameterFeatureJacobian
{
    /// <summary>Creates a Jacobian.</summary>
    /// <param name="parameterIds">Column labels.</param>
    /// <param name="featureIds">Row labels.</param>
    /// <param name="values">Slopes, features by parameters.</param>
    /// <param name="confidence">Confidence per entry, features by parameters.</param>
    /// <param name="referenceState">State around which the Jacobian was estimated.</param>
    /// <param name="observationCount">Number of observations that contributed.</param>
    [JsonConstructor]
    public ParameterFeatureJacobian(
        IReadOnlyList<string> parameterIds,
        IReadOnlyList<string> featureIds,
        double[,] values,
        double[,] confidence,
        SynthParameterState? referenceState = null,
        int observationCount = 0)
    {
        ArgumentNullException.ThrowIfNull(parameterIds);
        ArgumentNullException.ThrowIfNull(featureIds);
        ArgumentNullException.ThrowIfNull(values);
        ArgumentNullException.ThrowIfNull(confidence);
        if (values.GetLength(0) != featureIds.Count || values.GetLength(1) != parameterIds.Count
            || confidence.GetLength(0) != featureIds.Count || confidence.GetLength(1) != parameterIds.Count)
        {
            throw new ArgumentException("Matrix dimensions must be features by parameters.");
        }

        ParameterIds = parameterIds;
        FeatureIds = featureIds;
        Values = values;
        Confidence = confidence;
        ReferenceState = referenceState;
        ObservationCount = observationCount;
    }

    /// <summary>Column labels: ids of the ordered parameters.</summary>
    public IReadOnlyList<string> ParameterIds { get; }

    /// <summary>Row labels: feature ids in schema order.</summary>
    public IReadOnlyList<string> FeatureIds { get; }

    /// <summary>Slopes, indexed [feature, parameter].</summary>
    public double[,] Values { get; }

    /// <summary>Confidence 0..1 per entry, indexed [feature, parameter].</summary>
    public double[,] Confidence { get; }

    /// <summary>State around which the Jacobian was estimated.</summary>
    public SynthParameterState? ReferenceState { get; }

    /// <summary>Number of observations that contributed.</summary>
    public int ObservationCount { get; }

    /// <summary>Builds a Jacobian from a list of sensitivities; missing entries are 0 with confidence 0.</summary>
    /// <param name="sensitivities">Sensitivities, for example from <see cref="IParameterSensitivityAnalyzer.Estimate"/>.</param>
    /// <param name="parameterIds">Column labels.</param>
    /// <param name="featureIds">Row labels.</param>
    /// <param name="referenceState">State the sensitivities are valid around.</param>
    public static ParameterFeatureJacobian FromSensitivities(
        IEnumerable<ParameterSensitivity> sensitivities,
        IReadOnlyList<string> parameterIds,
        IReadOnlyList<string> featureIds,
        SynthParameterState? referenceState = null)
    {
        ArgumentNullException.ThrowIfNull(sensitivities);
        ArgumentNullException.ThrowIfNull(parameterIds);
        ArgumentNullException.ThrowIfNull(featureIds);
        var parameterIndex = new Dictionary<string, int>(StringComparer.Ordinal);
        var featureIndex = new Dictionary<string, int>(StringComparer.Ordinal);
        for (int i = 0; i < parameterIds.Count; i++)
        {
            parameterIndex[parameterIds[i]] = i;
        }

        for (int i = 0; i < featureIds.Count; i++)
        {
            featureIndex[featureIds[i]] = i;
        }

        var values = new double[featureIds.Count, parameterIds.Count];
        var confidence = new double[featureIds.Count, parameterIds.Count];
        int samples = 0;
        foreach (ParameterSensitivity s in sensitivities)
        {
            if (parameterIndex.TryGetValue(s.ParameterId, out int p) && featureIndex.TryGetValue(s.Feature, out int f))
            {
                values[f, p] = s.Sensitivity;
                confidence[f, p] = s.Confidence;
                samples = Math.Max(samples, s.SampleCount);
            }
        }

        return new ParameterFeatureJacobian(parameterIds, featureIds, values, confidence, referenceState, samples);
    }

    /// <summary>Returns one entry as a <see cref="ParameterSensitivity"/>, or null when either id is unknown.</summary>
    /// <param name="parameterId">Parameter id.</param>
    /// <param name="featureId">Feature id.</param>
    public ParameterSensitivity? GetSensitivity(string parameterId, string featureId)
    {
        int p = IndexOf(ParameterIds, parameterId), f = IndexOf(FeatureIds, featureId);
        return p < 0 || f < 0 ? null : new ParameterSensitivity(parameterId, featureId, Values[f, p], Confidence[f, p], ObservationCount);
    }

    /// <summary>
    /// First-order prediction of how the feature deltas change for a proposed state:
    /// <c>predictedFeatureDelta = J * (proposed - reference)</c>, with parameter differences in normalized units.
    /// The confidence is the contribution-weighted mean entry confidence, reduced for large steps because the linear
    /// model is only local.
    /// </summary>
    /// <param name="currentState">State the step starts from.</param>
    /// <param name="proposedState">State to evaluate.</param>
    /// <param name="space">Parameter definitions.</param>
    public ParameterChangePrediction Predict(SynthParameterState currentState, SynthParameterState proposedState, SynthParameterSpace space)
    {
        ArgumentNullException.ThrowIfNull(currentState);
        ArgumentNullException.ThrowIfNull(proposedState);
        ArgumentNullException.ThrowIfNull(space);
        var deltas = new double[ParameterIds.Count];
        double stepLength = 0;
        for (int p = 0; p < deltas.Length; p++)
        {
            if (space.Find(ParameterIds[p]) is SynthParameter parameter)
            {
                deltas[p] = SynthParameterSpace.NormalizedValue(proposedState, parameter) - SynthParameterSpace.NormalizedValue(currentState, parameter);
                stepLength += deltas[p] * deltas[p];
            }
        }

        var predicted = new double[FeatureIds.Count];
        double contributionSum = 0, confidenceSum = 0;
        for (int f = 0; f < predicted.Length; f++)
        {
            for (int p = 0; p < deltas.Length; p++)
            {
                double contribution = Values[f, p] * deltas[p];
                predicted[f] += contribution;

                // Every moved parameter counts, also where the slope is zero: an unknown entry must lower the confidence.
                double weight = Math.Abs(deltas[p]);
                contributionSum += weight;
                confidenceSum += weight * Confidence[f, p];
            }
        }

        double confidence = contributionSum > 0 ? confidenceSum / contributionSum : 0.0;
        double locality = 1.0 / (1.0 + (Math.Sqrt(stepLength) / 0.25));
        return new ParameterChangePrediction(proposedState, new FeatureVector(FeatureIds, predicted), MathUtil.Clamp01(confidence * locality));
    }

    private static int IndexOf(IReadOnlyList<string> ids, string id)
    {
        for (int i = 0; i < ids.Count; i++)
        {
            if (ids[i] == id)
            {
                return i;
            }
        }

        return -1;
    }
}
