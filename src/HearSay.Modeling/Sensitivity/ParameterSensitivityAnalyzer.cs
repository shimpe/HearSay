using HearSay.Dsp;
using HearSay.Modeling.Effects;
using HearSay.Modeling.History;
using HearSay.Modeling.Parameters;

namespace HearSay.Modeling.Sensitivity;

/// <summary>
/// Local sensitivity of a feature to a parameter: an estimate of d(feature) / d(parameter), in raw feature units per
/// normalized parameter unit (the full parameter range is 1).
/// </summary>
/// <param name="ParameterId">Parameter id.</param>
/// <param name="Feature">Feature id.</param>
/// <param name="Sensitivity">Estimated slope.</param>
/// <param name="Confidence">Confidence 0..1, from sample count, consistency, isolation and step size of the samples.</param>
/// <param name="SampleCount">Number of finite differences that were averaged (after outlier rejection).</param>
public sealed record ParameterSensitivity(string ParameterId, string Feature, double Sensitivity, double Confidence, int SampleCount);

/// <summary>A planned symmetric perturbation of one parameter around its current value.</summary>
/// <param name="ParameterId">Parameter to probe.</param>
/// <param name="CenterValue">Current value, native units.</param>
/// <param name="NegativeValue">Lower probe value, native units (equals the centre when the parameter sits at its minimum).</param>
/// <param name="PositiveValue">Upper probe value, native units (equals the centre when the parameter sits at its maximum).</param>
public sealed record ParameterProbe(string ParameterId, double CenterValue, double NegativeValue, double PositiveValue);

/// <summary>Sensitivities measured by one probe.</summary>
/// <param name="ParameterId">Probed parameter.</param>
/// <param name="Sensitivities">Sensitivity of every feature to the parameter.</param>
public sealed record ParameterProbeResult(string ParameterId, IReadOnlyList<ParameterSensitivity> Sensitivities);

/// <summary>Settings of the finite-difference sensitivity estimate.</summary>
public sealed record SensitivityOptions
{
    /// <summary>Normalized parameter changes smaller than this are ignored: the quotient would amplify measurement noise.</summary>
    public double MinimumParameterDelta { get; init; } = 0.002;

    /// <summary>Samples further than this many scaled median absolute deviations from the median are rejected (needs 4 or more samples).</summary>
    public double OutlierThreshold { get; init; } = 3.5;

    /// <summary>
    /// Width of the locality kernel in normalized parameter units. Samples taken this far from the reference value
    /// weigh about 60% of a sample taken at the reference value. Null disables locality weighting.
    /// </summary>
    public double? LocalityBandwidth { get; init; } = 0.15;

    /// <summary>Effect observations below this confidence are ignored.</summary>
    public double MinimumObservationConfidence { get; init; } = 0.05;
}

/// <summary>Estimates local sensitivities from observed parameter effects and from probes.</summary>
public interface IParameterSensitivityAnalyzer
{
    /// <summary>
    /// Averages finite differences per parameter and feature. Isolated, confident and nearby observations weigh more;
    /// outliers are rejected; unordered parameters are skipped because a slope is meaningless for them.
    /// </summary>
    /// <param name="effects">Observed effects.</param>
    /// <param name="referenceState">State around which the estimate should be valid; enables locality weighting.</param>
    /// <param name="space">Parameter definitions; required together with <paramref name="referenceState"/>.</param>
    /// <param name="options">Settings; null uses the defaults.</param>
    IReadOnlyList<ParameterSensitivity> Estimate(
        IEnumerable<ParameterEffectObservation> effects,
        SynthParameterState? referenceState = null,
        SynthParameterSpace? space = null,
        SensitivityOptions? options = null);

    /// <summary>Plans a symmetric probe around the current value that respects bounds and recommended step sizes.</summary>
    /// <param name="parameter">Parameter to probe.</param>
    /// <param name="currentValue">Current value, native units.</param>
    /// <param name="normalizedStep">Probe half-width in normalized units; null uses the recommended minimum step or 0.02.</param>
    ParameterProbe PlanProbe(SynthParameter parameter, double currentValue, double? normalizedStep = null);

    /// <summary>Central-difference sensitivities from the two probe observations.</summary>
    /// <param name="probe">The probe that was executed.</param>
    /// <param name="parameter">Definition of the probed parameter.</param>
    /// <param name="negative">Observation at <see cref="ParameterProbe.NegativeValue"/>.</param>
    /// <param name="positive">Observation at <see cref="ParameterProbe.PositiveValue"/>.</param>
    /// <param name="center">Optional observation at the centre; when given, agreement of both one-sided slopes refines the confidence.</param>
    ParameterProbeResult AnalyzeProbe(
        ParameterProbe probe,
        SynthParameter parameter,
        OptimizationObservation negative,
        OptimizationObservation positive,
        OptimizationObservation? center = null);
}

/// <summary>Default sensitivity analyzer. Stateless and thread-safe.</summary>
public sealed class ParameterSensitivityAnalyzer : IParameterSensitivityAnalyzer
{
    /// <inheritdoc />
    public IReadOnlyList<ParameterSensitivity> Estimate(
        IEnumerable<ParameterEffectObservation> effects,
        SynthParameterState? referenceState = null,
        SynthParameterSpace? space = null,
        SensitivityOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(effects);
        options ??= new SensitivityOptions();

        // Group samples per parameter, preserving first-seen order for deterministic output.
        var order = new List<string>();
        var groups = new Dictionary<string, List<ParameterEffectObservation>>(StringComparer.Ordinal);
        foreach (ParameterEffectObservation effect in effects)
        {
            if (effect.IsDiscrete
                || Math.Abs(effect.ParameterDelta) < options.MinimumParameterDelta
                || effect.Confidence < options.MinimumObservationConfidence)
            {
                continue;
            }

            if (!groups.TryGetValue(effect.ParameterId, out List<ParameterEffectObservation>? list))
            {
                list = [];
                groups[effect.ParameterId] = list;
                order.Add(effect.ParameterId);
            }

            list.Add(effect);
        }

        var result = new List<ParameterSensitivity>();
        foreach (string parameterId in order)
        {
            List<ParameterEffectObservation> samples = groups[parameterId];
            double? reference = null;
            if (referenceState is not null && space?.Find(parameterId) is SynthParameter parameter)
            {
                reference = SynthParameterSpace.NormalizedValue(referenceState, parameter);
            }

            var weights = new double[samples.Count];
            for (int i = 0; i < weights.Length; i++)
            {
                double locality = 1.0;
                if (reference is double r && options.LocalityBandwidth is double bandwidth && bandwidth > 0)
                {
                    // Distance from the middle of the step to the point of interest.
                    double midpoint = samples[i].NormalizedValueBefore + (0.5 * samples[i].ParameterDelta);
                    double d = (midpoint - r) / bandwidth;
                    locality = Math.Exp(-0.5 * d * d);
                }

                weights[i] = samples[i].Confidence * locality;
            }

            int featureCount = samples[0].Effects.Count;
            var slopes = new double[samples.Count];
            var scratch = new double[samples.Count];
            for (int f = 0; f < featureCount; f++)
            {
                string feature = samples[0].Effects[f].Feature;
                for (int i = 0; i < samples.Count; i++)
                {
                    slopes[i] = f < samples[i].Effects.Count ? samples[i].Effects[f].Delta / samples[i].ParameterDelta : 0.0;
                }

                result.Add(Combine(parameterId, feature, slopes, weights, scratch, options.OutlierThreshold));
            }
        }

        return result;
    }

    /// <inheritdoc />
    public ParameterProbe PlanProbe(SynthParameter parameter, double currentValue, double? normalizedStep = null)
    {
        ArgumentNullException.ThrowIfNull(parameter);
        double center = parameter.Coerce(currentValue);
        if (!parameter.IsOrdered)
        {
            return new ParameterProbe(parameter.Id, center, center, center);
        }

        double step = normalizedStep is double s
            ? s * parameter.Range
            : parameter.RecommendedMinStep ?? (0.02 * parameter.Range);
        if (parameter.RecommendedMaxStep is double max)
        {
            step = Math.Min(step, max);
        }

        if (parameter.Kind == SynthParameterKind.Integer)
        {
            step = Math.Max(1.0, Math.Round(step));
        }

        return new ParameterProbe(parameter.Id, center, parameter.Coerce(center - step), parameter.Coerce(center + step));
    }

    /// <inheritdoc />
    public ParameterProbeResult AnalyzeProbe(
        ParameterProbe probe,
        SynthParameter parameter,
        OptimizationObservation negative,
        OptimizationObservation positive,
        OptimizationObservation? center = null)
    {
        ArgumentNullException.ThrowIfNull(probe);
        ArgumentNullException.ThrowIfNull(parameter);
        ArgumentNullException.ThrowIfNull(negative);
        ArgumentNullException.ThrowIfNull(positive);

        double span = parameter.Normalize(probe.PositiveValue) - parameter.Normalize(probe.NegativeValue);
        var sensitivities = new List<ParameterSensitivity>();
        if (!parameter.IsOrdered || Math.Abs(span) < 1e-9)
        {
            return new ParameterProbeResult(probe.ParameterId, sensitivities);
        }

        ReadOnlySpan<double> low = negative.Features.Values.Span, high = positive.Features.Values.Span;
        ReadOnlySpan<double> middle = center is null ? default : center.Features.Values.Span;
        double lowerSpan = parameter.Normalize(probe.CenterValue) - parameter.Normalize(probe.NegativeValue);
        double upperSpan = parameter.Normalize(probe.PositiveValue) - parameter.Normalize(probe.CenterValue);
        double measurement = Math.Min(negative.MeasurementConfidence, positive.MeasurementConfidence);
        double stepFactor = Math.Abs(span) / (Math.Abs(span) + ParameterEffectExtractor.HalfConfidenceStep);
        int count = Math.Min(low.Length, high.Length);

        for (int f = 0; f < count; f++)
        {
            double slope = (high[f] - low[f]) / span;
            double consistency = 0.8;
            int samples = 1;
            if (!middle.IsEmpty && f < middle.Length && lowerSpan > 1e-9 && upperSpan > 1e-9)
            {
                // Two one-sided slopes: their agreement tells how linear (and how noise-free) the response is.
                double below = (middle[f] - low[f]) / lowerSpan;
                double above = (high[f] - middle[f]) / upperSpan;
                double disagreement = Math.Abs(above - below) / (Math.Abs(above) + Math.Abs(below) + 1e-12);
                consistency = 1.0 - (0.6 * disagreement);
                samples = 2;
            }

            sensitivities.Add(new ParameterSensitivity(
                probe.ParameterId, positive.Features.FeatureIds[f], slope, MathUtil.Clamp01(consistency * stepFactor * measurement), samples));
        }

        return new ParameterProbeResult(probe.ParameterId, sensitivities);
    }

    private static ParameterSensitivity Combine(string parameterId, string feature, double[] slopes, double[] weights, double[] scratch, double outlierThreshold)
    {
        int n = slopes.Length;
        double lower = double.NegativeInfinity, upper = double.PositiveInfinity;
        if (n >= 4)
        {
            slopes.CopyTo(scratch, 0);
            double median = MathUtil.MedianInPlace(scratch);
            for (int i = 0; i < n; i++)
            {
                scratch[i] = Math.Abs(slopes[i] - median);
            }

            double mad = 1.4826 * MathUtil.MedianInPlace(scratch);
            if (mad > 0)
            {
                lower = median - (outlierThreshold * mad);
                upper = median + (outlierThreshold * mad);
            }
        }

        double weightSum = 0, weighted = 0, confidenceSum = 0;
        int used = 0;
        for (int i = 0; i < n; i++)
        {
            if (slopes[i] < lower || slopes[i] > upper || weights[i] <= 0)
            {
                continue;
            }

            weightSum += weights[i];
            weighted += weights[i] * slopes[i];
            confidenceSum += weights[i] * weights[i];
            used++;
        }

        if (used == 0 || weightSum <= 0)
        {
            return new ParameterSensitivity(parameterId, feature, 0.0, 0.0, 0);
        }

        double mean = weighted / weightSum;
        double variance = 0;
        for (int i = 0; i < n; i++)
        {
            if (slopes[i] >= lower && slopes[i] <= upper && weights[i] > 0)
            {
                variance += weights[i] * (slopes[i] - mean) * (slopes[i] - mean);
            }
        }

        double deviation = Math.Sqrt(variance / weightSum);

        // Confidence: quality of the samples, how many there are, and whether they agree with each other.
        double quality = confidenceSum / weightSum;
        double countFactor = used / (used + 1.0);
        double consistency = used >= 2 ? 1.0 / (1.0 + (deviation / (Math.Abs(mean) + 1e-9))) : 0.75;
        double confidence = MathUtil.Clamp01(quality * (0.5 + (0.5 * countFactor)) * consistency);
        return new ParameterSensitivity(parameterId, feature, mean, confidence, used);
    }
}
