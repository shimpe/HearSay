using HearSay.Dsp;
using HearSay.Models;
using HearSay.Optimization.Parameters;

namespace HearSay.Optimization.History;

/// <summary>Derives progress, stagnation, oscillation, change and correlation diagnostics from a history.</summary>
public interface IOptimizationHistoryAnalyzer
{
    /// <summary>Summarizes the progress of the run. Returns null for an empty history.</summary>
    /// <param name="history">Optimization history.</param>
    /// <param name="options">Thresholds; null uses the defaults.</param>
    OptimizationProgress? GetProgress(OptimizationHistory history, ProgressOptions? options = null);

    /// <summary>Reports every parameter and feature that keeps reversing without net progress in the recent window.</summary>
    /// <param name="history">Optimization history.</param>
    /// <param name="space">Parameter definitions.</param>
    /// <param name="schema">Feature schema, used to normalize feature errors.</param>
    /// <param name="options">Thresholds; null uses the defaults.</param>
    IReadOnlyList<OscillationReport> DetectOscillations(OptimizationHistory history, SynthParameterSpace space, FeatureVectorSchema schema, ProgressOptions? options = null);

    /// <summary>Parameters that differ between two observations.</summary>
    /// <param name="before">Earlier observation.</param>
    /// <param name="after">Later observation.</param>
    /// <param name="space">Parameter definitions.</param>
    IReadOnlyList<ParameterChange> GetParameterChanges(OptimizationObservation before, OptimizationObservation after, SynthParameterSpace space);

    /// <summary>Feature deltas that differ between two observations.</summary>
    /// <param name="before">Earlier observation.</param>
    /// <param name="after">Later observation.</param>
    IReadOnlyList<FeatureChange> GetFeatureChanges(OptimizationObservation before, OptimizationObservation after);

    /// <summary>
    /// Correlation between every ordered parameter and every feature over the run, strongest first. Reported as an
    /// observed relation only; it does not establish causality.
    /// </summary>
    /// <param name="history">Optimization history.</param>
    /// <param name="space">Parameter definitions.</param>
    /// <param name="minimumAbsoluteCorrelation">Relations with a weaker correlation are omitted.</param>
    IReadOnlyList<ParameterFeatureRelationship> GetRelationships(OptimizationHistory history, SynthParameterSpace space, double minimumAbsoluteCorrelation = 0.0);
}

/// <summary>Default history analyzer. Stateless and thread-safe.</summary>
public sealed class OptimizationHistoryAnalyzer : IOptimizationHistoryAnalyzer
{
    /// <inheritdoc />
    public OptimizationProgress? GetProgress(OptimizationHistory history, ProgressOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(history);
        options ??= new ProgressOptions();
        IReadOnlyList<OptimizationObservation> all = history.Observations;
        if (all.Count == 0)
        {
            return null;
        }

        int bestIndex = 0;
        for (int i = 1; i < all.Count; i++)
        {
            if (all[i].OverallLoss < all[bestIndex].OverallLoss)
            {
                bestIndex = i;
            }
        }

        double current = all[^1].OverallLoss;
        double best = all[bestIndex].OverallLoss;
        double previous = all.Count > 1 ? all[^2].OverallLoss : current;

        // Best loss before the window versus best loss now.
        int window = Math.Max(1, options.StagnationWindow);
        bool stagnating = false;
        double rate = 0;
        if (all.Count >= window)
        {
            double bestBefore = double.PositiveInfinity;
            for (int i = 0; i <= all.Count - window; i++)
            {
                bestBefore = Math.Min(bestBefore, all[i].OverallLoss);
            }

            double gained = Math.Max(0.0, bestBefore - best);
            rate = gained / window;
            stagnating = gained < options.MinimumRelativeImprovement * Math.Max(bestBefore, 1e-12);
        }
        else if (all.Count > 1)
        {
            rate = Math.Max(0.0, all[0].OverallLoss - best) / (all.Count - 1);
        }

        return new OptimizationProgress(
            all.Count, current, best, all[bestIndex].Iteration, all[0].OverallLoss - best, previous - current, rate, all.Count - 1 - bestIndex, stagnating);
    }

    /// <inheritdoc />
    public IReadOnlyList<OscillationReport> DetectOscillations(OptimizationHistory history, SynthParameterSpace space, FeatureVectorSchema schema, ProgressOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(history);
        ArgumentNullException.ThrowIfNull(space);
        ArgumentNullException.ThrowIfNull(schema);
        options ??= new ProgressOptions();
        IReadOnlyList<OptimizationObservation> all = history.Observations;
        int count = Math.Min(all.Count, Math.Max(3, options.OscillationWindow));
        var reports = new List<OscillationReport>();
        if (count < 3)
        {
            return reports;
        }

        int offset = all.Count - count;
        var series = new double[count];

        foreach (SynthParameter parameter in space.Parameters)
        {
            if (!parameter.IsOrdered)
            {
                continue;
            }

            for (int i = 0; i < count; i++)
            {
                series[i] = SynthParameterSpace.NormalizedValue(all[offset + i].Parameters, parameter);
            }

            // Count reversals of the step direction, ignoring steps too small to matter.
            int reversals = 0, steps = 0;
            double amplitude = 0, lastStep = 0;
            for (int i = 1; i < count; i++)
            {
                double step = series[i] - series[i - 1];
                if (Math.Abs(step) < options.MinimumParameterStep)
                {
                    continue;
                }

                if (lastStep != 0 && Math.Sign(step) != Math.Sign(lastStep))
                {
                    reversals++;
                }

                lastStep = step;
                amplitude += Math.Abs(step);
                steps++;
            }

            if (steps == 0)
            {
                continue;
            }

            amplitude /= steps;
            double net = Math.Abs(series[count - 1] - series[0]);
            bool oscillating = reversals >= options.MinimumReversals && net < 1.5 * amplitude;
            if (oscillating)
            {
                reports.Add(new OscillationReport(parameter.Id, true, reversals, amplitude, net, true));
            }
        }

        for (int f = 0; f < schema.Features.Count; f++)
        {
            double scale = schema.Features[f].TypicalScale;
            bool complete = true;
            for (int i = 0; i < count; i++)
            {
                ReadOnlySpan<double> values = all[offset + i].Features.Values.Span;
                if (f >= values.Length)
                {
                    complete = false;
                    break;
                }

                series[i] = values[f] / scale;
            }

            if (!complete)
            {
                continue;
            }

            // An error that keeps changing sign without shrinking means the optimizer overshoots.
            int signChanges = 0;
            double early = 0, late = 0, mean = 0;
            for (int i = 0; i < count; i++)
            {
                double magnitude = Math.Abs(series[i]);
                mean += magnitude;
                if (i < count / 2)
                {
                    early += magnitude;
                }
                else
                {
                    late += magnitude;
                }

                if (i > 0 && series[i] * series[i - 1] < 0)
                {
                    signChanges++;
                }
            }

            mean /= count;
            early /= Math.Max(1, count / 2);
            late /= Math.Max(1, count - (count / 2));
            bool oscillating = signChanges >= options.MinimumReversals && mean >= options.MinimumFeatureError && late >= 0.7 * early;
            if (oscillating)
            {
                reports.Add(new OscillationReport(schema.Features[f].Id, false, signChanges, mean, Math.Abs(series[count - 1] - series[0]), true));
            }
        }

        return reports;
    }

    /// <inheritdoc />
    public IReadOnlyList<ParameterChange> GetParameterChanges(OptimizationObservation before, OptimizationObservation after, SynthParameterSpace space)
    {
        ArgumentNullException.ThrowIfNull(before);
        ArgumentNullException.ThrowIfNull(after);
        ArgumentNullException.ThrowIfNull(space);
        var changes = new List<ParameterChange>();
        foreach (SynthParameter parameter in space.Parameters)
        {
            double a = before.Parameters.Values.TryGetValue(parameter.Id, out double va) ? va : parameter.Value;
            double b = after.Parameters.Values.TryGetValue(parameter.Id, out double vb) ? vb : parameter.Value;
            if (a != b)
            {
                changes.Add(new ParameterChange(parameter.Id, a, b, parameter.Normalize(b) - parameter.Normalize(a)));
            }
        }

        return changes;
    }

    /// <inheritdoc />
    public IReadOnlyList<FeatureChange> GetFeatureChanges(OptimizationObservation before, OptimizationObservation after)
    {
        ArgumentNullException.ThrowIfNull(before);
        ArgumentNullException.ThrowIfNull(after);
        var changes = new List<FeatureChange>();
        ReadOnlySpan<double> a = before.Features.Values.Span, b = after.Features.Values.Span;
        int count = Math.Min(a.Length, b.Length);
        for (int i = 0; i < count; i++)
        {
            if (a[i] != b[i])
            {
                changes.Add(new FeatureChange(after.Features.FeatureIds[i], a[i], b[i], b[i] - a[i]));
            }
        }

        return changes;
    }

    /// <inheritdoc />
    public IReadOnlyList<ParameterFeatureRelationship> GetRelationships(OptimizationHistory history, SynthParameterSpace space, double minimumAbsoluteCorrelation = 0.0)
    {
        ArgumentNullException.ThrowIfNull(history);
        ArgumentNullException.ThrowIfNull(space);
        IReadOnlyList<OptimizationObservation> all = history.Observations;
        var result = new List<ParameterFeatureRelationship>();
        int n = all.Count;
        if (n < 4)
        {
            return result;
        }

        IReadOnlyList<string> featureIds = all[0].Features.FeatureIds;
        int featureCount = featureIds.Count;
        var x = new double[n];
        var y = new double[n];
        foreach (SynthParameter parameter in space.Parameters)
        {
            if (!parameter.IsOrdered)
            {
                continue;
            }

            for (int i = 0; i < n; i++)
            {
                x[i] = SynthParameterSpace.NormalizedValue(all[i].Parameters, parameter);
            }

            for (int f = 0; f < featureCount; f++)
            {
                bool complete = true;
                for (int i = 0; i < n; i++)
                {
                    ReadOnlyMemory<double> values = all[i].Features.Values;
                    if (values.Length != featureCount)
                    {
                        complete = false;
                        break;
                    }

                    y[i] = values.Span[f];
                }

                if (!complete)
                {
                    continue;
                }

                double? correlation = Pearson(x, y);
                if (correlation is not double r || Math.Abs(r) < minimumAbsoluteCorrelation)
                {
                    continue;
                }

                // Fisher transformation: z * sqrt(n - 3) is approximately standard normal under "no correlation".
                double clamped = Math.Clamp(r, -0.999999, 0.999999);
                double z = Math.Atanh(clamped) * Math.Sqrt(n - 3);
                double confidence = MathUtil.Clamp01(MathUtil.Erf(Math.Abs(z) / Math.Sqrt(2.0)));
                result.Add(new ParameterFeatureRelationship(parameter.Id, featureIds[f], r, confidence, n));
            }
        }

        result.Sort(static (a, b) =>
        {
            int order = (Math.Abs(b.Correlation) * b.Confidence).CompareTo(Math.Abs(a.Correlation) * a.Confidence);
            if (order != 0)
            {
                return order;
            }

            order = string.CompareOrdinal(a.ParameterId, b.ParameterId);
            return order != 0 ? order : string.CompareOrdinal(a.FeatureId, b.FeatureId);
        });
        return result;
    }

    private static double? Pearson(double[] x, double[] y)
    {
        int n = x.Length;
        double mx = 0, my = 0;
        for (int i = 0; i < n; i++)
        {
            mx += x[i];
            my += y[i];
        }

        mx /= n;
        my /= n;
        double sxx = 0, syy = 0, sxy = 0;
        for (int i = 0; i < n; i++)
        {
            double dx = x[i] - mx, dy = y[i] - my;
            sxx += dx * dx;
            syy += dy * dy;
            sxy += dx * dy;
        }

        return sxx > 1e-18 && syy > 1e-18 ? sxy / Math.Sqrt(sxx * syy) : null;
    }
}
