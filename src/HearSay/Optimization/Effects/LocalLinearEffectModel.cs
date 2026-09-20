using HearSay.Dsp;
using HearSay.Models;
using HearSay.Optimization.History;
using HearSay.Optimization.Parameters;
using HearSay.Optimization.Prediction;
using HearSay.Optimization.Sensitivity;

namespace HearSay.Optimization.Effects;

/// <summary>A model that predicts what a parameter change will do to the features, learned from earlier experiments.</summary>
public interface IParameterEffectModel
{
    /// <summary>Predicts the effect of changing one parameter.</summary>
    /// <param name="currentState">State the change starts from.</param>
    /// <param name="parameterId">Parameter to change.</param>
    /// <param name="proposedDelta">Proposed change in native units.</param>
    ParameterEffectPrediction Predict(SynthParameterState currentState, string parameterId, double proposedDelta);
}

/// <summary>Settings of <see cref="LocalLinearEffectModel"/>.</summary>
public sealed record EffectModelOptions
{
    /// <summary>
    /// Width of the Gaussian locality kernel in normalized parameter distance. Observations this far from the state
    /// of interest weigh about 60% of an observation at that state; at twice the distance about 14%. The kernel has
    /// to be narrow: distant observations have a lot of leverage on a slope, so a wide kernel lets them dominate.
    /// When there are too few observations nearby, the kernel is widened to reach the nearest (parameter count + 2)
    /// observations, and every confidence is multiplied by <c>configured / used</c> bandwidth.
    /// </summary>
    public double LocalityBandwidth { get; init; } = 0.15;

    /// <summary>Ridge regularization relative to the mean parameter variation; keeps confounded or sparse data from producing wild slopes.</summary>
    public double RidgeFactor { get; init; } = 1e-3;

    /// <summary>Unique normalized parameter variation (as a step size) at which the information part of the confidence reaches one half.</summary>
    public double MinimumInformativeStep { get; init; } = 0.02;

    /// <summary>Weight multiplier for observations whose unordered parameters (switches, selectors) differ from the state of interest.</summary>
    public double DiscreteMismatchWeight { get; init; } = 0.1;

    /// <summary>Only the most recent observations up to this count are used.</summary>
    public int MaximumObservations { get; init; } = 2000;
}

/// <summary>
/// Locally weighted ridge regression of the feature deltas on the normalized ordered parameters.
/// <para>
/// Locality: observations close to the state of interest weigh more than distant ones, so the model follows the
/// non-linear response of a synth instead of averaging it away. Confounding: when several parameters moved together,
/// the regression can only attribute what is unique to each parameter; the confidence of an entry is derived from
/// that unique variation, so jointly changed parameters get little credit. Regularization: the ridge term shrinks
/// slopes that the data does not support. Unordered parameters are never differentiated; observations taken in a
/// different switch or selector setting are down-weighted instead.
/// </para>
/// Thread-safe.
/// </summary>
public sealed class LocalLinearEffectModel : IParameterEffectModel
{
    private const double ReferenceStep = 0.1;

    private readonly Lock gate = new();
    private readonly List<OptimizationObservation> observations = [];
    private readonly SynthParameterSpace space;
    private readonly FeatureVectorSchema schema;
    private readonly EffectModelOptions options;
    private readonly SynthParameter[] ordered;
    private readonly SynthParameter[] unordered;

    /// <summary>Creates an empty model.</summary>
    /// <param name="space">Parameter definitions.</param>
    /// <param name="schema">Schema of the feature vectors in the observations.</param>
    /// <param name="options">Settings; null uses the defaults.</param>
    public LocalLinearEffectModel(SynthParameterSpace space, FeatureVectorSchema schema, EffectModelOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(space);
        ArgumentNullException.ThrowIfNull(schema);
        this.space = space;
        this.schema = schema;
        this.options = options ?? new EffectModelOptions();
        ordered = [.. space.Parameters.Where(p => p.IsOrdered)];
        unordered = [.. space.Parameters.Where(p => !p.IsOrdered)];
    }

    /// <summary>Number of observations the model has seen.</summary>
    public int ObservationCount
    {
        get
        {
            lock (gate)
            {
                return observations.Count;
            }
        }
    }

    /// <summary>Adds one observation. Observations whose feature vector does not match the schema are ignored.</summary>
    /// <param name="observation">Evaluated candidate.</param>
    public void Observe(OptimizationObservation observation)
    {
        ArgumentNullException.ThrowIfNull(observation);
        if (observation.Features.Values.Length != schema.Features.Count)
        {
            return;
        }

        lock (gate)
        {
            observations.Add(observation);
            if (observations.Count > options.MaximumObservations)
            {
                observations.RemoveAt(0);
            }
        }
    }

    /// <summary>Adds every observation of a history.</summary>
    /// <param name="history">Optimization history.</param>
    public void ObserveAll(OptimizationHistory history)
    {
        ArgumentNullException.ThrowIfNull(history);
        foreach (OptimizationObservation observation in history.Observations)
        {
            Observe(observation);
        }
    }

    /// <summary>Estimates the approximate Jacobian around a state.</summary>
    /// <param name="state">State of interest, normally the current or best state.</param>
    public ParameterFeatureJacobian EstimateJacobian(SynthParameterState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        OptimizationObservation[] data;
        lock (gate)
        {
            data = [.. observations];
        }

        int pCount = ordered.Length, fCount = schema.Features.Count, n = data.Length;
        var parameterIds = new string[pCount];
        for (int j = 0; j < pCount; j++)
        {
            parameterIds[j] = ordered[j].Id;
        }

        var featureIds = new string[fCount];
        for (int f = 0; f < fCount; f++)
        {
            featureIds[f] = schema.Features[f].Id;
        }

        var values = new double[fCount, pCount];
        var confidence = new double[fCount, pCount];
        if (n < 2 || pCount == 0)
        {
            return new ParameterFeatureJacobian(parameterIds, featureIds, values, confidence, state, n);
        }

        // Locality weights.
        var centre = new double[pCount];
        for (int j = 0; j < pCount; j++)
        {
            centre[j] = SynthParameterSpace.NormalizedValue(state, ordered[j]);
        }

        var x = new double[n, pCount];
        var w = new double[n];
        double weightSum = 0, weightSquares = 0;
        var squaredDistances = new double[n];
        for (int i = 0; i < n; i++)
        {
            for (int j = 0; j < pCount; j++)
            {
                x[i, j] = SynthParameterSpace.NormalizedValue(data[i].Parameters, ordered[j]) - centre[j];
                squaredDistances[i] += x[i, j] * x[i, j];
            }
        }

        // Adaptive kernel: never narrower than the configured bandwidth, but wide enough to reach the handful of
        // observations needed to determine one slope per parameter. With dense data the model is strictly local;
        // with sparse data it falls back on the nearest knowledge it has, at a reduced confidence (see below).
        double baseBandwidth = Math.Max(1e-6, options.LocalityBandwidth);
        var sortedDistances = (double[])squaredDistances.Clone();
        Array.Sort(sortedDistances);
        double reach = Math.Sqrt(sortedDistances[Math.Min(n - 1, pCount + 1)]);
        double bandwidth = Math.Max(baseBandwidth, reach);
        double localityFactor = baseBandwidth / bandwidth;

        for (int i = 0; i < n; i++)
        {
            double distance = squaredDistances[i];
            double weight = Math.Exp(-0.5 * distance / (bandwidth * bandwidth)) * Math.Max(0.0, data[i].MeasurementConfidence);
            foreach (SynthParameter parameter in unordered)
            {
                double here = state.Values.TryGetValue(parameter.Id, out double a) ? a : parameter.Value;
                double there = data[i].Parameters.Values.TryGetValue(parameter.Id, out double b) ? b : parameter.Value;
                if (here != there)
                {
                    weight *= options.DiscreteMismatchWeight;
                }
            }

            w[i] = weight;
            weightSum += weight;
            weightSquares += weight * weight;
        }

        if (weightSum <= 1e-12)
        {
            return new ParameterFeatureJacobian(parameterIds, featureIds, values, confidence, state, n);
        }

        // Weighted centring.
        var meanX = new double[pCount];
        var meanY = new double[fCount];
        for (int i = 0; i < n; i++)
        {
            ReadOnlySpan<double> y = data[i].Features.Values.Span;
            for (int j = 0; j < pCount; j++)
            {
                meanX[j] += w[i] * x[i, j];
            }

            for (int f = 0; f < fCount; f++)
            {
                meanY[f] += w[i] * y[f];
            }
        }

        for (int j = 0; j < pCount; j++)
        {
            meanX[j] /= weightSum;
        }

        for (int f = 0; f < fCount; f++)
        {
            meanY[f] /= weightSum;
        }

        // Normal equations: (Xc' W Xc + lambda I) J' = Xc' W Yc.
        var gram = new double[pCount, pCount];
        var cross = new double[pCount, fCount];
        for (int i = 0; i < n; i++)
        {
            ReadOnlySpan<double> y = data[i].Features.Values.Span;
            for (int j = 0; j < pCount; j++)
            {
                double xj = (x[i, j] - meanX[j]) * w[i];
                if (xj == 0)
                {
                    continue;
                }

                for (int k = 0; k < pCount; k++)
                {
                    gram[j, k] += xj * (x[i, k] - meanX[k]);
                }

                for (int f = 0; f < fCount; f++)
                {
                    cross[j, f] += xj * (y[f] - meanY[f]);
                }
            }
        }

        // Parameters count as varied when their local variation is not negligible next to the most varied one.
        double trace = 0, largest = 0;
        for (int j = 0; j < pCount; j++)
        {
            trace += gram[j, j];
            largest = Math.Max(largest, gram[j, j]);
        }

        int varied = 0;
        for (int j = 0; j < pCount; j++)
        {
            if (gram[j, j] > 1e-4 * largest && gram[j, j] > 1e-12)
            {
                varied++;
            }
        }

        if (varied == 0)
        {
            return new ParameterFeatureJacobian(parameterIds, featureIds, values, confidence, state, n);
        }

        double lambda = (options.RidgeFactor * trace / varied) + 1e-12;
        for (int j = 0; j < pCount; j++)
        {
            gram[j, j] += lambda;
        }

        double[,]? inverse = LinearAlgebra.Invert(gram);
        if (inverse is null)
        {
            return new ParameterFeatureJacobian(parameterIds, featureIds, values, confidence, state, n);
        }

        for (int f = 0; f < fCount; f++)
        {
            for (int j = 0; j < pCount; j++)
            {
                double sum = 0;
                for (int k = 0; k < pCount; k++)
                {
                    sum += inverse[j, k] * cross[k, f];
                }

                values[f, j] = sum;
            }
        }

        // Residual variance per feature, for the noise part of the confidence.
        double effectiveCount = weightSum * weightSum / weightSquares;
        double freedom = effectiveCount - varied - 1.0;
        var residual = new double[fCount];
        if (freedom >= 1.0)
        {
            for (int i = 0; i < n; i++)
            {
                ReadOnlySpan<double> y = data[i].Features.Values.Span;
                for (int f = 0; f < fCount; f++)
                {
                    double predicted = meanY[f];
                    for (int j = 0; j < pCount; j++)
                    {
                        predicted += values[f, j] * (x[i, j] - meanX[j]);
                    }

                    double r = y[f] - predicted;
                    residual[f] += w[i] * r * r;
                }
            }
        }

        double informative = options.MinimumInformativeStep * options.MinimumInformativeStep;

        // At least two effective observations are needed for any slope. Whether they can separate the parameters is
        // not a matter of counting: it is captured per parameter by its unique variation below.
        double countFactor = MathUtil.Clamp01(effectiveCount - 1.0);
        for (int j = 0; j < pCount; j++)
        {
            // Variation of this parameter that is not shared with any other parameter.
            double unique = Math.Max(0.0, (1.0 / inverse[j, j]) - lambda);
            double information = unique / (unique + informative);
            for (int f = 0; f < fCount; f++)
            {
                double noise = 0.85;
                if (freedom >= 1.0)
                {
                    double standardError = Math.Sqrt(Math.Max(0.0, residual[f] / freedom * inverse[j, j]));
                    noise = 1.0 / (1.0 + (standardError * ReferenceStep / schema.Features[f].TypicalScale));
                }

                confidence[f, j] = MathUtil.Clamp01(information * countFactor * noise * localityFactor);
            }
        }

        return new ParameterFeatureJacobian(parameterIds, featureIds, values, confidence, state, n);
    }

    /// <inheritdoc />
    public ParameterEffectPrediction Predict(SynthParameterState currentState, string parameterId, double proposedDelta)
    {
        ArgumentNullException.ThrowIfNull(currentState);
        SynthParameter parameter = space.Find(parameterId) ?? throw new ArgumentException($"Unknown parameter '{parameterId}'.", nameof(parameterId));
        int fCount = schema.Features.Count;
        var featureIds = new string[fCount];
        for (int f = 0; f < fCount; f++)
        {
            featureIds[f] = schema.Features[f].Id;
        }

        double current = currentState.Values.TryGetValue(parameterId, out double v) ? v : parameter.Value;
        double proposed = parameter.Coerce(current + proposedDelta);
        return parameter.IsOrdered
            ? PredictOrdered(currentState, parameter, current, proposed, proposedDelta, featureIds)
            : PredictUnordered(currentState, parameter, current, proposed, proposedDelta, featureIds);
    }

    private ParameterEffectPrediction PredictOrdered(SynthParameterState state, SynthParameter parameter, double current, double proposed, double proposedDelta, string[] featureIds)
    {
        ParameterFeatureJacobian jacobian = EstimateJacobian(state);
        int column = Array.IndexOf(ordered, parameter);
        double step = parameter.Normalize(proposed) - parameter.Normalize(current);
        var predicted = new double[featureIds.Length];
        double confidenceSum = 0;
        for (int f = 0; f < predicted.Length; f++)
        {
            predicted[f] = jacobian.Values[f, column] * step;
            confidenceSum += jacobian.Confidence[f, column];
        }

        // A linear model is only local: trust shrinks as the step grows relative to the locality bandwidth.
        double locality = 1.0 / (1.0 + (Math.Abs(step) / Math.Max(1e-6, options.LocalityBandwidth)));
        double confidence = predicted.Length > 0 ? confidenceSum / predicted.Length * locality : 0.0;
        return new ParameterEffectPrediction(parameter.Id, proposedDelta, new FeatureVector(featureIds, predicted), MathUtil.Clamp01(confidence), jacobian.ObservationCount);
    }

    /// <summary>
    /// Unordered parameters: average the feature change seen in earlier transitions between the same two values where
    /// nothing else changed, weighting transitions that happened near the current state more.
    /// </summary>
    private ParameterEffectPrediction PredictUnordered(SynthParameterState state, SynthParameter parameter, double current, double proposed, double proposedDelta, string[] featureIds)
    {
        OptimizationObservation[] data;
        lock (gate)
        {
            data = [.. observations];
        }

        var predicted = new double[featureIds.Length];
        double weightSum = 0;
        double bandwidth = Math.Max(1e-6, options.LocalityBandwidth);
        for (int i = 1; i < data.Length; i++)
        {
            IReadOnlyList<ParameterEffectObservation> effects = ParameterEffectExtractor.FromPair(data[i - 1], data[i], space);
            if (effects.Count != 1 || effects[0].ParameterId != parameter.Id)
            {
                continue;
            }

            // The reverse transition carries the same information with the opposite sign.
            ParameterEffectObservation effect = effects[0];
            double sign;
            if (effect.FromValue == current && effect.ToValue == proposed)
            {
                sign = 1.0;
            }
            else if (effect.FromValue == proposed && effect.ToValue == current)
            {
                sign = -1.0;
            }
            else
            {
                continue;
            }

            double distance = 0;
            foreach (SynthParameter other in ordered)
            {
                double d = SynthParameterSpace.NormalizedValue(data[i].Parameters, other) - SynthParameterSpace.NormalizedValue(state, other);
                distance += d * d;
            }

            double weight = effect.Confidence * Math.Exp(-0.5 * distance / (bandwidth * bandwidth));
            weightSum += weight;
            for (int f = 0; f < predicted.Length && f < effect.Effects.Count; f++)
            {
                predicted[f] += weight * sign * effect.Effects[f].Delta;
            }
        }

        if (weightSum > 0)
        {
            for (int f = 0; f < predicted.Length; f++)
            {
                predicted[f] /= weightSum;
            }
        }

        double confidence = weightSum / (weightSum + 0.5);
        return new ParameterEffectPrediction(parameter.Id, proposedDelta, new FeatureVector(featureIds, predicted), MathUtil.Clamp01(confidence), weightSum);
    }
}
