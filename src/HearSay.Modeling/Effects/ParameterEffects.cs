using HearSay.Dsp;
using HearSay.Models;
using HearSay.Modeling.History;
using HearSay.Modeling.Parameters;

namespace HearSay.Modeling.Effects;

/// <summary>Observed change of one feature.</summary>
/// <param name="Feature">Feature id.</param>
/// <param name="Delta">Change of the feature delta (after minus before), in raw feature units.</param>
public sealed record FeatureEffect(string Feature, double Delta);

/// <summary>
/// What happened to the features when a parameter changed between two observations. When several parameters changed
/// at once, each gets its own record with a reduced <see cref="Confidence"/>: no single parameter is given full credit
/// for an effect that several parameters may have caused.
/// </summary>
/// <param name="ParameterId">Parameter that changed.</param>
/// <param name="ParameterDelta">Change in normalized (0..1) units; for unordered parameters the raw value change.</param>
/// <param name="Effects">Change of every feature.</param>
/// <param name="Confidence">Confidence 0..1 that the effects are caused by this parameter.</param>
/// <param name="NormalizedValueBefore">Normalized value of the parameter before the change; locates the observation in the parameter range.</param>
/// <param name="ChangedParameterCount">Number of parameters that changed in the same step (1 = clean, isolated experiment).</param>
/// <param name="FromValue">Native value before the change.</param>
/// <param name="ToValue">Native value after the change.</param>
/// <param name="IsDiscrete">True for unordered parameters, whose effects are per value pair and never interpolated.</param>
public sealed record ParameterEffectObservation(
    string ParameterId,
    double ParameterDelta,
    IReadOnlyList<FeatureEffect> Effects,
    double Confidence,
    double NormalizedValueBefore = 0.0,
    int ChangedParameterCount = 1,
    double FromValue = 0.0,
    double ToValue = 0.0,
    bool IsDiscrete = false);

/// <summary>Extracts parameter-effect observations from pairs of optimization observations.</summary>
public static class ParameterEffectExtractor
{
    /// <summary>Normalized step at which the step-size factor of the confidence reaches one half.</summary>
    public const double HalfConfidenceStep = 0.005;

    /// <summary>Effect observations for every consecutive pair in a history.</summary>
    /// <param name="history">Optimization history.</param>
    /// <param name="space">Parameter definitions.</param>
    public static IReadOnlyList<ParameterEffectObservation> FromHistory(OptimizationHistory history, SynthParameterSpace space)
    {
        ArgumentNullException.ThrowIfNull(history);
        IReadOnlyList<OptimizationObservation> all = history.Observations;
        var result = new List<ParameterEffectObservation>();
        for (int i = 1; i < all.Count; i++)
        {
            result.AddRange(FromPair(all[i - 1], all[i], space));
        }

        return result;
    }

    /// <summary>
    /// Effect observations for one before/after pair. Confidence combines isolation (the share of the total parameter
    /// movement that belongs to this parameter, squared), step size (tiny steps drown in measurement noise) and the
    /// measurement confidence of both observations.
    /// </summary>
    /// <param name="before">Observation before the change.</param>
    /// <param name="after">Observation after the change.</param>
    /// <param name="space">Parameter definitions.</param>
    public static IReadOnlyList<ParameterEffectObservation> FromPair(OptimizationObservation before, OptimizationObservation after, SynthParameterSpace space)
    {
        ArgumentNullException.ThrowIfNull(before);
        ArgumentNullException.ThrowIfNull(after);
        ArgumentNullException.ThrowIfNull(space);

        var changed = new List<(SynthParameter Parameter, double From, double To, double NormalizedDelta)>();
        double totalMovement = 0;
        foreach (SynthParameter parameter in space.Parameters)
        {
            double from = before.Parameters.Values.TryGetValue(parameter.Id, out double a) ? a : parameter.Value;
            double to = after.Parameters.Values.TryGetValue(parameter.Id, out double b) ? b : parameter.Value;
            if (from == to)
            {
                continue;
            }

            // A switch flip counts as a full-range move when judging how isolated the other changes are.
            double normalizedDelta = parameter.IsOrdered ? parameter.Normalize(to) - parameter.Normalize(from) : 1.0;
            changed.Add((parameter, from, to, normalizedDelta));
            totalMovement += Math.Abs(normalizedDelta);
        }

        var result = new List<ParameterEffectObservation>(changed.Count);
        if (changed.Count == 0 || totalMovement <= 0)
        {
            return result;
        }

        ReadOnlySpan<double> x = before.Features.Values.Span, y = after.Features.Values.Span;
        int count = Math.Min(x.Length, y.Length);
        var effects = new FeatureEffect[count];
        for (int i = 0; i < count; i++)
        {
            effects[i] = new FeatureEffect(after.Features.FeatureIds[i], y[i] - x[i]);
        }

        double measurement = Math.Min(before.MeasurementConfidence, after.MeasurementConfidence);
        foreach ((SynthParameter parameter, double from, double to, double normalizedDelta) in changed)
        {
            double isolation = Math.Abs(normalizedDelta) / totalMovement;
            double step = Math.Abs(normalizedDelta);
            double stepFactor = parameter.IsOrdered ? step / (step + HalfConfidenceStep) : 1.0;
            double confidence = MathUtil.Clamp01(isolation * isolation * stepFactor * measurement);
            result.Add(new ParameterEffectObservation(
                parameter.Id,
                parameter.IsOrdered ? normalizedDelta : to - from,
                effects,
                confidence,
                parameter.Normalize(from),
                changed.Count,
                from,
                to,
                !parameter.IsOrdered));
        }

        return result;
    }
}
