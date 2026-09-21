using HearSay.Objective;
using HearSay.OperatorMatrix.Canonicalization;
using HearSay.OperatorMatrix.Optimization;
using HearSay.OperatorMatrix.Parameters;
using HearSay.OperatorMatrix.Search;
using HearSay.Synthesis.Patches;
using HearSay.Synthesis.Profiles;

namespace HearSay.OperatorMatrix.Basins;

/// <summary>
/// One structural hypothesis under optimization: a fixed regime (operators, modes, waveforms, edges, coarse
/// frequencies, output routing) whose continuous parameters are being improved. Basins are optimized independently;
/// a structural mutation creates a new basin instead of moving an existing one.
/// </summary>
public sealed class SearchBasin
{
    /// <summary>Creates a basin from its first evaluation.</summary>
    /// <param name="id">Basin number, unique within a search.</param>
    /// <param name="origin">Hypothesis the structure came from.</param>
    /// <param name="first">First evaluation.</param>
    public SearchBasin(int id, string origin, PatchEvaluation first)
    {
        ArgumentNullException.ThrowIfNull(first);
        Id = id;
        Origin = origin;
        Best = first;
        Start = first.Patch;
        Regime = PatchCanonicalizer.CanonicalKey(first.Patch);
    }

    /// <summary>Basin number.</summary>
    public int Id { get; }

    /// <summary>Hypothesis and mutations that led to this structure.</summary>
    public string Origin { get; }

    /// <summary>Canonical structural regime.</summary>
    public OperatorMatrixRegimeKey Regime { get; }

    /// <summary>Patch the basin started from; restarts return here to escape a local optimum of the continuous search.</summary>
    public OperatorMatrixPatch Start { get; }

    /// <summary>Best evaluation so far.</summary>
    public PatchEvaluation Best { get; private set; }

    /// <summary>Renders spent on this basin.</summary>
    public int Evaluations { get; internal set; }

    /// <summary>Times the basin stagnated and was restarted or mutated.</summary>
    public int Restarts { get; internal set; }

    /// <summary>True when the last optimization round ended without progress.</summary>
    public bool Stagnated { get; internal set; }

    /// <summary>How far the basin got; for reports.</summary>
    public string Fate { get; internal set; } = "screened";

    /// <summary>Takes over an evaluation when it is better than the best so far. Returns true when it was.</summary>
    /// <param name="evaluation">New evaluation of a patch of this regime.</param>
    public bool Offer(PatchEvaluation evaluation)
    {
        ArgumentNullException.ThrowIfNull(evaluation);
        if (evaluation.Loss < Best.Loss)
        {
            Best = evaluation;
            return true;
        }

        return false;
    }

    /// <summary>Replaces the best evaluation after a change of evaluation level, where losses are not comparable.</summary>
    /// <param name="evaluation">Evaluation of the best patch at the new level.</param>
    internal void Rebase(PatchEvaluation evaluation) => Best = evaluation;
}

/// <summary>Which group of smooth parameters a continuous optimization works on.</summary>
public enum ContinuousStage
{
    /// <summary>What shapes the spectrum: modulation amounts, output levels, fine tuning and detune.</summary>
    Spectral,

    /// <summary>Envelope macros: tied attack, decay and release parameters of operator groups, and the pitch model.</summary>
    TemporalMacros,

    /// <summary>Individual envelope points.</summary>
    TemporalDetail,

    /// <summary>
    /// Spectral and temporal parameters together. Modulation amounts and envelopes are strongly coupled (the index over
    /// time is their product), so small patches are optimized jointly; the stage has no subsets when the patch has more
    /// smooth parameters than may be active at once.
    /// </summary>
    Joint,
}

/// <summary>
/// Continuous optimization inside one basin. It picks a small active parameter subset for a stage, freezes the rest
/// and runs CMA-ES on it. It knows nothing about waveforms or synthesis methods: it sees parameters with ids, groups
/// and transforms.
/// </summary>
public sealed class BasinOptimizer
{
    private readonly PatchEvaluator _evaluator;
    private readonly SynthesisCapabilities _capabilities;
    private readonly PatchMatchOptions _options;

    /// <summary>Creates an optimizer.</summary>
    /// <param name="evaluator">Evaluator of the search.</param>
    /// <param name="capabilities">Limits of the synthesis profile.</param>
    /// <param name="options">Search options.</param>
    public BasinOptimizer(PatchEvaluator evaluator, SynthesisCapabilities capabilities, PatchMatchOptions options)
    {
        _evaluator = evaluator;
        _capabilities = capabilities;
        _options = options;
    }

    /// <summary>
    /// The active subsets of a stage for a patch, each at most <see cref="PatchMatchOptions.MaxActiveParameters"/>
    /// parameters. Everything not in a subset stays frozen while that subset is optimized.
    /// </summary>
    /// <param name="patch">Patch whose structure decides what is active.</param>
    /// <param name="stage">Stage.</param>
    /// <param name="tuningMatters">Include fine tuning and detune, for inharmonic or beating targets.</param>
    public IReadOnlyList<ActiveParameterSet> SelectSubsets(OperatorMatrixPatch patch, ContinuousStage stage, bool tuningMatters)
    {
        PatchParameterSchema schema = PatchParameterSchema.Build(patch, _capabilities, null, _evaluator.TimeLimits);
        IReadOnlyList<PatchParameter> active = schema.ActiveSmooth(patch);
        var chosen = new List<PatchParameter>();
        if (stage == ContinuousStage.Joint)
        {
            foreach (PatchParameter p in active)
            {
                if (IsSpectral(p.Id, tuningMatters) || IsEnvelopeShape(p.Id) || (p.Id.StartsWith("pitch.", StringComparison.Ordinal) && !p.Id.EndsWith(".p0.time", StringComparison.Ordinal)))
                {
                    chosen.Add(p);
                }
            }

            return chosen.Count > 0 && chosen.Count <= _options.MaxActiveParameters ? [new ActiveParameterSet(chosen)] : [];
        }

        switch (stage)
        {
            case ContinuousStage.Spectral:
                foreach (PatchParameter p in active)
                {
                    if (IsSpectral(p.Id, tuningMatters))
                    {
                        chosen.Add(p);
                    }
                }

                break;

            case ContinuousStage.TemporalMacros:
                chosen.AddRange(Macros(patch, active));
                foreach (PatchParameter p in active)
                {
                    if (p.Id.StartsWith("pitch.", StringComparison.Ordinal) && !p.Id.EndsWith(".p0.time", StringComparison.Ordinal))
                    {
                        chosen.Add(p);
                    }
                }

                break;

            default:
                foreach (PatchParameter p in active)
                {
                    if (IsEnvelopeShape(p.Id))
                    {
                        chosen.Add(p);
                    }
                }

                break;
        }

        var subsets = new List<ActiveParameterSet>();
        int max = Math.Max(1, _options.MaxActiveParameters);
        for (int from = 0; from < chosen.Count; from += max)
        {
            subsets.Add(new ActiveParameterSet(chosen.GetRange(from, Math.Min(max, chosen.Count - from))));
        }

        return subsets;
    }

    /// <summary>
    /// Spends a budget on one subset with restarts: whenever a run stagnates or collapses, the next one starts from the
    /// best patch with a wide step and twice the population. A larger population smooths over the local optima that a
    /// small one settles into.
    /// </summary>
    /// <param name="basin">Basin to improve.</param>
    /// <param name="select">Chooses the active subset for the current best patch; called before every run.</param>
    /// <param name="evaluations">Renders this call may spend.</param>
    /// <param name="sigma">Step size of the first run.</param>
    /// <param name="nextSeed">Source of run seeds.</param>
    /// <param name="level">Evaluation level.</param>
    /// <returns>True when the basin improved.</returns>
    public bool OptimizeWithRestarts(SearchBasin basin, Func<OperatorMatrixPatch, ActiveParameterSet?> select, int evaluations, double sigma, Func<int> nextSeed, EvaluationLevel level = EvaluationLevel.Normal)
    {
        ArgumentNullException.ThrowIfNull(basin);
        ArgumentNullException.ThrowIfNull(select);
        ArgumentNullException.ThrowIfNull(nextSeed);
        bool improved = false;
        int scale = 1;
        int run = 0;
        int left = evaluations;
        while (left > 0)
        {
            ActiveParameterSet? subset = select(basin.Best.Patch);
            if (subset is null)
            {
                break;
            }

            int before = basin.Evaluations;
            // Every second restart goes back to where the basin began. The landscape inside one structure has several
            // optima, and a run that starts at the best patch so far cannot leave the one it is in.
            OperatorMatrixPatch? startFrom = run % 2 == 1 ? basin.Start : null;
            improved |= Optimize(basin, subset, left, sigma, nextSeed(), level, scale, startFrom);
            run++;
            int spent = basin.Evaluations - before;
            if (spent == 0)
            {
                break;
            }

            left -= spent;
            scale = Math.Min(8, scale * 2);
            sigma = 0.3;
        }

        return improved;
    }

    /// <summary>Runs CMA-ES on one subset until the budget is spent, the distribution collapses or progress stops.</summary>
    /// <param name="basin">Basin to improve.</param>
    /// <param name="subset">Active parameters.</param>
    /// <param name="evaluations">Renders this call may spend.</param>
    /// <param name="sigma">Initial step size.</param>
    /// <param name="seed">Random seed of this run.</param>
    /// <param name="level">Evaluation level.</param>
    /// <param name="populationScale">Multiplier of the default population size; restarts use larger populations.</param>
    /// <param name="startFrom">Patch whose active values are the start point; the best patch when null. Frozen values always come from the best patch.</param>
    /// <returns>True when the basin improved by at least the configured relative amount.</returns>
    public bool Optimize(SearchBasin basin, ActiveParameterSet subset, int evaluations, double sigma, int seed, EvaluationLevel level = EvaluationLevel.Normal, int populationScale = 1, OperatorMatrixPatch? startFrom = null)
    {
        ArgumentNullException.ThrowIfNull(basin);
        ArgumentNullException.ThrowIfNull(subset);
        if (subset.Count == 0 || evaluations <= 0)
        {
            return false;
        }

        double startLoss = basin.Best.Loss;
        OperatorMatrixPatch frozen = basin.Best.Patch;
        int lambda = (4 + (int)Math.Floor(3.0 * Math.Log(subset.Count))) * Math.Max(1, populationScale);
        var cma = new CmaEs(subset.ToVector(startFrom ?? frozen), sigma, seed, lambda);
        int spent = 0;
        int sinceProgress = 0;
        double reference = double.PositiveInfinity;
        double runBest = double.PositiveInfinity;
        while (spent + cma.PopulationSize <= evaluations && _evaluator.Remaining >= cma.PopulationSize && !cma.HasConverged)
        {
            double[][] population = cma.Ask();
            var patches = new OperatorMatrixPatch[population.Length];
            for (int k = 0; k < population.Length; k++)
            {
                patches[k] = subset.Apply(frozen, population[k]);
            }

            int before = _evaluator.Used;
            IReadOnlyList<PatchEvaluation> results = _evaluator.Evaluate(patches, level);
            spent += _evaluator.Used - before;
            if (results.Count < population.Length)
            {
                break;
            }

            var losses = new double[population.Length];
            for (int k = 0; k < losses.Length; k++)
            {
                losses[k] = results[k].Loss;
                runBest = Math.Min(runBest, losses[k]);
                basin.Offer(results[k]);
            }

            cma.Tell(losses);
            // Progress is judged on this run alone: a restart that begins far from the best patch needs time to catch up.
            if (runBest < reference * (1.0 - _options.MinimumRelativeImprovement))
            {
                reference = runBest;
                sinceProgress = 0;
            }
            else if (++sinceProgress >= _options.StagnationGenerations)
            {
                break;
            }
        }

        basin.Evaluations += spent;
        return basin.Best.Loss < startLoss * (1.0 - _options.MinimumRelativeImprovement);
    }

    private static bool IsSpectral(string id, bool tuningMatters) =>
        id.EndsWith(".amount", StringComparison.Ordinal)
        || id.EndsWith(".output.level", StringComparison.Ordinal)
        || id.EndsWith(".frequency.fixedHz", StringComparison.Ordinal)
        || (tuningMatters && (id.EndsWith(".fineTune", StringComparison.Ordinal) || id.EndsWith(".detune", StringComparison.Ordinal)));

    private static bool IsEnvelopeShape(string id)
    {
        // Times of all segments and the levels of the inner points. The start, the peak and the end of an amplitude
        // envelope are fixed: the peak is redundant with output levels and modulation amounts.
        if (!id.Contains(".env.p", StringComparison.Ordinal) || id.StartsWith("pitch.", StringComparison.Ordinal))
        {
            return false;
        }

        if (id.EndsWith(".time", StringComparison.Ordinal))
        {
            return true;
        }

        return id.EndsWith(".p2.level", StringComparison.Ordinal) || id.EndsWith(".p3.level", StringComparison.Ordinal);
    }

    /// <summary>Ties the envelope parameters of all operators with the same role, point by point.</summary>
    private static List<PatchParameter> Macros(OperatorMatrixPatch patch, IReadOnlyList<PatchParameter> active)
    {
        var groups = new SortedDictionary<string, List<PatchParameter>>(StringComparer.Ordinal);
        foreach (PatchParameter p in active)
        {
            if (!p.Id.StartsWith("op", StringComparison.Ordinal) || !IsEnvelopeShape(p.Id))
            {
                continue;
            }

            int dot = p.Id.IndexOf('.', StringComparison.Ordinal);
            int op = int.Parse(p.Id.AsSpan(2, dot - 2), System.Globalization.CultureInfo.InvariantCulture) - 1;
            string role = patch.OutputLevels[op] != 0.0 ? "heard" : "shaping";
            string key = "macro." + role + p.Id[dot..];
            if (!groups.TryGetValue(key, out List<PatchParameter>? members))
            {
                members = [];
                groups[key] = members;
            }

            members.Add(p);
        }

        var macros = new List<PatchParameter>();
        foreach ((string key, List<PatchParameter> members) in groups)
        {
            macros.Add(ParameterTie.Create(key, members, patch));
        }

        return macros;
    }
}
