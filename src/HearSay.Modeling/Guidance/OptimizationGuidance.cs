using HearSay.Comparison;
using HearSay.Dsp;
using HearSay.Models;
using HearSay.Modeling.Parameters;
using HearSay.Modeling.Sensitivity;

namespace HearSay.Modeling.Guidance;

/// <summary>Where the knowledge behind a suggestion comes from.</summary>
public enum SuggestionSource
{
    /// <summary>Effects that were measured on this synth during the run. Always preferred.</summary>
    Empirical,

    /// <summary>Generic prior knowledge about synthesizers; low confidence, only used for parameters nothing is known about yet.</summary>
    GenericPrior,
}

/// <summary>A suggested change of one parameter. This is advice for an optimizer, not an optimizer.</summary>
/// <param name="ParameterId">Parameter to change.</param>
/// <param name="CurrentValue">Current value, native units.</param>
/// <param name="SuggestedDelta">Suggested change, native units.</param>
/// <param name="SuggestedValue">Value after the change, within bounds and rounded for integer parameters.</param>
/// <param name="Confidence">Confidence 0..1 that the change reduces the error.</param>
/// <param name="ExpectedAffectedFeatures">Features expected to change the most, strongest first.</param>
/// <param name="Source">Whether the suggestion rests on measured effects or on generic priors.</param>
public sealed record ParameterAdjustmentSuggestion(
    string ParameterId,
    double CurrentValue,
    double SuggestedDelta,
    double SuggestedValue,
    double Confidence,
    IReadOnlyList<string> ExpectedAffectedFeatures,
    SuggestionSource Source = SuggestionSource.Empirical);

/// <summary>Result of a guidance request.</summary>
/// <param name="Suggestions">Suggested changes, to be applied together; empirical suggestions first, by descending confidence.</param>
/// <param name="CurrentErrorNorm">Length of the weighted normalized error vector now.</param>
/// <param name="PredictedErrorNorm">Length predicted by the local linear model after applying all empirical suggestions.</param>
public sealed record GuidanceResult(
    IReadOnlyList<ParameterAdjustmentSuggestion> Suggestions,
    double CurrentErrorNorm,
    double PredictedErrorNorm);

/// <summary>Settings of <see cref="OptimizationGuidance"/>.</summary>
public sealed record GuidanceOptions
{
    /// <summary>Largest suggested step in normalized units, unless the parameter recommends a smaller maximum.</summary>
    public double MaximumNormalizedStep { get; init; } = 0.15;

    /// <summary>Levenberg-Marquardt style damping. Larger values give smaller, more cautious steps.</summary>
    public double Damping { get; init; } = 0.05;

    /// <summary>Parameters whose best Jacobian confidence is below this value are not used for empirical suggestions.</summary>
    public double MinimumConfidence { get; init; } = 0.15;

    /// <summary>Steps smaller than this (normalized) are not reported, unless the parameter recommends another minimum.</summary>
    public double MinimumNormalizedStep { get; init; } = 0.002;

    /// <summary>Allow prior-based suggestions for parameters that have categories but no empirical knowledge yet.</summary>
    public bool UseGenericPriors { get; init; } = true;

    /// <summary>Normalized size of prior-based steps; they are exploratory by nature.</summary>
    public double PriorStep { get; init; } = 0.05;

    /// <summary>
    /// Optional per-feature weights of the error vector (<c>e_weighted = W * e</c>). Listed features replace their
    /// schema default weight (<see cref="FeatureDefinition.DefaultWeight"/>); a weight of 0 ignores a feature.
    /// </summary>
    public IReadOnlyList<OptimizationFeatureWeight>? FeatureWeights { get; init; }
}

/// <summary>
/// Turns the current error vector and the learned local Jacobian into parameter adjustment suggestions.
/// <para>
/// The empirical part solves a damped, confidence-weighted least-squares problem in normalized feature space:
/// minimize <c>|W (e + J dp)|^2 + mu |dp|^2</c>, where every Jacobian entry is first shrunk by its confidence, so
/// uncertain relations contribute little. Steps are limited by a trust region and by the parameter bounds. It never
/// assumes that a particular parameter fixes a particular difference: only effects measured on this synth count.
/// Generic priors are used solely for parameters nothing has been learned about, and are marked as such.
/// </para>
/// Stateless and thread-safe.
/// </summary>
public sealed class OptimizationGuidance
{
    /// <summary>Suggests parameter adjustments.</summary>
    /// <param name="error">Current raw difference vector (candidate minus target) in schema order.</param>
    /// <param name="currentState">State that produced <paramref name="error"/>.</param>
    /// <param name="jacobian">Local Jacobian estimated around <paramref name="currentState"/>.</param>
    /// <param name="space">Parameter definitions with bounds and step recommendations.</param>
    /// <param name="schema">Schema of the error vector.</param>
    /// <param name="options">Settings; null uses the defaults.</param>
    public GuidanceResult Suggest(
        FeatureVector error,
        SynthParameterState currentState,
        ParameterFeatureJacobian jacobian,
        SynthParameterSpace space,
        FeatureVectorSchema schema,
        GuidanceOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(error);
        ArgumentNullException.ThrowIfNull(currentState);
        ArgumentNullException.ThrowIfNull(jacobian);
        ArgumentNullException.ThrowIfNull(space);
        ArgumentNullException.ThrowIfNull(schema);
        options ??= new GuidanceOptions();

        int fCount = schema.Features.Count;
        if (error.Values.Length != fCount || jacobian.FeatureIds.Count != fCount)
        {
            throw new ArgumentException("Error vector, Jacobian and schema must describe the same features.");
        }

        // Weighted, normalized error.
        var weights = new double[fCount];
        for (int f = 0; f < fCount; f++)
        {
            weights[f] = schema.Features[f].DefaultWeight;
        }

        if (options.FeatureWeights is not null)
        {
            foreach (OptimizationFeatureWeight weight in options.FeatureWeights)
            {
                int index = schema.IndexOf(weight.FeatureId);
                if (index >= 0)
                {
                    weights[index] = weight.Weight;
                }
            }
        }

        var e = new double[fCount];
        double currentNorm = 0;
        for (int f = 0; f < fCount; f++)
        {
            e[f] = weights[f] * error.Values.Span[f] / schema.Features[f].TypicalScale;
            currentNorm += e[f] * e[f];
        }

        currentNorm = Math.Sqrt(currentNorm);

        // Parameters with enough empirical support.
        var usable = new List<int>();
        var known = new HashSet<string>(StringComparer.Ordinal);
        for (int p = 0; p < jacobian.ParameterIds.Count; p++)
        {
            double best = 0;
            for (int f = 0; f < fCount; f++)
            {
                best = Math.Max(best, jacobian.Confidence[f, p]);
            }

            if (best >= options.MinimumConfidence && space.Find(jacobian.ParameterIds[p]) is { IsOrdered: true })
            {
                usable.Add(p);
                known.Add(jacobian.ParameterIds[p]);
            }
        }

        var suggestions = new List<ParameterAdjustmentSuggestion>();
        double predictedNorm = currentNorm;
        if (usable.Count > 0 && currentNorm > 0)
        {
            predictedNorm = SolveEmpirical(e, weights, currentState, jacobian, space, schema, options, usable, suggestions);
        }

        if (options.UseGenericPriors)
        {
            AddPriorSuggestions(e, currentState, space, schema, options, known, suggestions);
        }

        suggestions.Sort(static (a, b) =>
        {
            int order = a.Source.CompareTo(b.Source);
            if (order != 0)
            {
                return order;
            }

            order = b.Confidence.CompareTo(a.Confidence);
            return order != 0 ? order : string.CompareOrdinal(a.ParameterId, b.ParameterId);
        });
        return new GuidanceResult(suggestions, currentNorm, predictedNorm);
    }

    private static double SolveEmpirical(
        double[] e,
        double[] weights,
        SynthParameterState state,
        ParameterFeatureJacobian jacobian,
        SynthParameterSpace space,
        FeatureVectorSchema schema,
        GuidanceOptions options,
        List<int> usable,
        List<ParameterAdjustmentSuggestion> suggestions)
    {
        int fCount = e.Length, m = usable.Count;

        // Normalized Jacobian (weighted like the error); 'shrunk' additionally scales entries by their confidence.
        var full = new double[fCount, m];
        var shrunk = new double[fCount, m];
        for (int c = 0; c < m; c++)
        {
            int p = usable[c];
            for (int f = 0; f < fCount; f++)
            {
                full[f, c] = weights[f] * jacobian.Values[f, p] / schema.Features[f].TypicalScale;
                shrunk[f, c] = full[f, c] * jacobian.Confidence[f, p];
            }
        }

        var normal = new double[m, m];
        var gradient = new double[m];
        for (int a = 0; a < m; a++)
        {
            for (int f = 0; f < fCount; f++)
            {
                gradient[a] += shrunk[f, a] * e[f];
                for (int b = 0; b < m; b++)
                {
                    normal[a, b] += shrunk[f, a] * shrunk[f, b];
                }
            }
        }

        double trace = 0;
        for (int a = 0; a < m; a++)
        {
            trace += normal[a, a];
        }

        double damping = (options.Damping * Math.Max(trace / m, 1e-12)) + 1e-12;
        for (int a = 0; a < m; a++)
        {
            normal[a, a] += damping;
        }

        double[,]? inverse = LinearAlgebra.Invert(normal);
        if (inverse is null)
        {
            return Norm(e);
        }

        var step = new double[m];
        for (int a = 0; a < m; a++)
        {
            for (int b = 0; b < m; b++)
            {
                step[a] -= inverse[a, b] * gradient[b];
            }
        }

        // Trust region: scale the whole step so that no parameter exceeds its maximum step, then respect bounds.
        double shrink = 1.0;
        var parameters = new SynthParameter[m];
        for (int a = 0; a < m; a++)
        {
            parameters[a] = space.Find(jacobian.ParameterIds[usable[a]])!;
            double limit = options.MaximumNormalizedStep;
            if (parameters[a].RecommendedMaxStep is double max && parameters[a].Range > 0)
            {
                limit = Math.Min(limit, max / parameters[a].Range);
            }

            if (Math.Abs(step[a]) > limit)
            {
                shrink = Math.Min(shrink, limit / Math.Abs(step[a]));
            }
        }

        var applied = new double[m];
        for (int a = 0; a < m; a++)
        {
            SynthParameter parameter = parameters[a];
            double current = state.Values.TryGetValue(parameter.Id, out double v) ? v : parameter.Value;
            double normalized = parameter.Normalize(current);
            double suggestedValue = parameter.Denormalize(normalized + (step[a] * shrink));
            applied[a] = parameter.Normalize(suggestedValue) - normalized;

            double minimum = options.MinimumNormalizedStep;
            if (parameter.RecommendedMinStep is double min && parameter.Range > 0)
            {
                minimum = Math.Max(minimum, 0.5 * min / parameter.Range);
            }

            if (Math.Abs(applied[a]) < minimum)
            {
                applied[a] = 0;
                continue;
            }

            // Confidence: entry confidences weighted by how much each feature drives this step.
            double driveSum = 0, confidenceSum = 0;
            var impact = new List<(string Feature, double Size)>();
            for (int f = 0; f < fCount; f++)
            {
                double drive = Math.Abs(full[f, a] * e[f]);
                driveSum += drive;
                confidenceSum += drive * jacobian.Confidence[f, usable[a]];
                double size = Math.Abs(full[f, a] * applied[a]);
                if (size >= 0.05)
                {
                    impact.Add((schema.Features[f].Id, size));
                }
            }

            impact.Sort(static (x, y) => y.Size.CompareTo(x.Size));
            var affected = new List<string>();
            for (int i = 0; i < Math.Min(6, impact.Count); i++)
            {
                affected.Add(impact[i].Feature);
            }

            suggestions.Add(new ParameterAdjustmentSuggestion(
                parameter.Id,
                current,
                suggestedValue - current,
                suggestedValue,
                MathUtil.Clamp01(driveSum > 0 ? confidenceSum / driveSum : 0.0),
                affected));
        }

        var predicted = new double[fCount];
        for (int f = 0; f < fCount; f++)
        {
            predicted[f] = e[f];
            for (int a = 0; a < m; a++)
            {
                predicted[f] += full[f, a] * applied[a];
            }
        }

        return Norm(predicted);
    }

    /// <summary>
    /// Prior-based suggestions: for a parameter without empirical knowledge, add up what its categories generically do
    /// to the features that are currently wrong. The result is an exploratory step in the more promising direction.
    /// </summary>
    private static void AddPriorSuggestions(
        double[] e,
        SynthParameterState state,
        SynthParameterSpace space,
        FeatureVectorSchema schema,
        GuidanceOptions options,
        HashSet<string> known,
        List<ParameterAdjustmentSuggestion> suggestions)
    {
        foreach (SynthParameter parameter in space.Parameters)
        {
            if (!parameter.IsOrdered || known.Contains(parameter.Id) || parameter.Categories is not { Count: > 0 } categories)
            {
                continue;
            }

            double score = 0;
            var affected = new List<string>();
            foreach (GenericParameterRelation relation in GenericSynthPriors.Relations)
            {
                if (relation.Sign == 0 || !categories.Contains(relation.Category))
                {
                    continue;
                }

                int index = schema.IndexOf(relation.FeatureId);
                if (index < 0 || Math.Abs(e[index]) < 0.25)
                {
                    continue;
                }

                // Raising the parameter moves the feature by 'sign'; we want to move it against the error.
                score -= relation.Sign * relation.Strength * e[index];
                affected.Add(relation.FeatureId);
            }

            if (Math.Abs(score) < 0.25)
            {
                continue;
            }

            double current = state.Values.TryGetValue(parameter.Id, out double v) ? v : parameter.Value;
            double suggested = parameter.Denormalize(parameter.Normalize(current) + (Math.Sign(score) * options.PriorStep));
            if (suggested == current)
            {
                continue;
            }

            suggestions.Add(new ParameterAdjustmentSuggestion(
                parameter.Id, current, suggested - current, suggested, Math.Min(0.3, 0.1 + (0.05 * Math.Abs(score))), affected, SuggestionSource.GenericPrior));
        }
    }

    private static double Norm(double[] vector)
    {
        double sum = 0;
        foreach (double v in vector)
        {
            sum += v * v;
        }

        return Math.Sqrt(sum);
    }
}
