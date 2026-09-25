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
    public OperatorMatrixPatch Start { get; private set; }

    /// <summary>Optimizer runs of this basin by active subset, kept so that they can be resumed.</summary>
    internal Dictionary<string, ContinuousRun> Runs { get; } = new(StringComparer.Ordinal);

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
    internal void Rebase(PatchEvaluation evaluation)
    {
        Best = evaluation;
        Start = evaluation.Patch;
        Runs.Clear();
    }
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

    /// <summary>
    /// One subset per connected group of operators: a stack with everything that belongs to it, spectral and temporal.
    /// Groups that do not modulate each other only meet in the output mix, so they can be optimized in turns, while
    /// the strongly coupled parameters inside a group stay together.
    /// </summary>
    Components,
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

    /// <summary>Optimize key scaling (level and rate per octave); only sensible with targets at several pitches.</summary>
    public bool IncludeKeyScaling { get; init; }

    /// <summary>Optimize velocity sensitivity; only sensible with targets at several velocities.</summary>
    public bool IncludeVelocity { get; init; }

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

        // Segment times that a break position stands in for are left alone, and so is the output level of a patch
        // with a single output operator when only timbre is compared: loudness is normalized away, the level does nothing.
        var superseded = new HashSet<string>(StringComparer.Ordinal);
        foreach (PatchParameter p in active)
        {
            if (p.Id.EndsWith(".breakPosition", StringComparison.Ordinal))
            {
                string prefix = p.Id[..^".breakPosition".Length];
                superseded.Add(prefix + ".p2.time");
                superseded.Add(prefix + ".p3.time");
            }
        }

        if (_options.Objective.TimbreNormalized)
        {
            List<PatchParameter> outputs = [.. active.Where(static p => p.Id.EndsWith(".output.level", StringComparison.Ordinal))];
            if (outputs.Count == 1)
            {
                superseded.Add(outputs[0].Id);
            }
        }

        active = [.. active.Where(p => !superseded.Contains(p.Id))];
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

        if (stage == ContinuousStage.Components)
        {
            return ComponentSubsets(patch, active, tuningMatters);
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
    /// Spends a budget on one subset, resuming the run this basin already has for that subset. A run keeps its adapted
    /// covariance from call to call, which is worth far more than a fresh start. Only when a run stagnates or collapses
    /// does the next one begin: with twice the population and a wide step, alternately from the best patch and from
    /// where the basin began, because the landscape inside one structure has several optima and a run that starts at
    /// the best patch cannot leave the one it is in.
    /// </summary>
    /// <param name="basin">Basin to improve.</param>
    /// <param name="select">Chooses the active subset for the current best patch; called before every new run.</param>
    /// <param name="evaluations">Renders this call may spend.</param>
    /// <param name="sigma">Step size of the first run.</param>
    /// <param name="nextSeed">Source of run seeds.</param>
    /// <param name="level">Evaluation level.</param>
    /// <param name="initialPopulationScale">Population multiplier of the first run. Larger populations make finding the good optimum inside a structure reliable instead of a matter of the seed; short runs cannot afford them.</param>
    /// <returns>True when the basin improved.</returns>
    public bool OptimizeWithRestarts(SearchBasin basin, Func<OperatorMatrixPatch, ActiveParameterSet?> select, int evaluations, double sigma, Func<int> nextSeed, EvaluationLevel level = EvaluationLevel.Normal, int initialPopulationScale = 1)
    {
        ArgumentNullException.ThrowIfNull(basin);
        ArgumentNullException.ThrowIfNull(select);
        ArgumentNullException.ThrowIfNull(nextSeed);
        double startLoss = basin.Best.Loss;
        int left = evaluations;
        while (left > 0 && _evaluator.Remaining > 0)
        {
            ActiveParameterSet? subset = select(basin.Best.Patch);
            if (subset is null || subset.Count == 0)
            {
                break;
            }

            string signature = level + ":" + string.Join(",", subset.Parameters.Select(static p => p.Id));
            if (!basin.Runs.TryGetValue(signature, out ContinuousRun? run) || run.Finished)
            {
                int index = run is null ? 0 : run.Index + 1;
                int scale = Math.Min(8, Math.Max(1, initialPopulationScale) << Math.Min(3, index));
                OperatorMatrixPatch origin = index % 2 == 1 ? basin.Start : basin.Best.Patch;
                int lambda = (4 + (int)Math.Floor(3.0 * Math.Log(subset.Count))) * scale;
                run = new ContinuousRun(new CmaEs(subset.ToVector(origin), index == 0 ? sigma : 0.3, nextSeed(), lambda), index);
                basin.Runs[signature] = run;
            }

            int spent = Step(basin, subset, run, left, level);
            if (spent == 0)
            {
                // Not even one generation fits into what is left.
                break;
            }

            left -= spent;
        }

        return basin.Best.Loss < startLoss * (1.0 - _options.MinimumRelativeImprovement);
    }

    /// <summary>Runs CMA-ES once on one subset until the budget is spent, the distribution collapses or progress stops.</summary>
    /// <param name="basin">Basin to improve.</param>
    /// <param name="subset">Active parameters.</param>
    /// <param name="evaluations">Renders this call may spend.</param>
    /// <param name="sigma">Initial step size.</param>
    /// <param name="seed">Random seed of this run.</param>
    /// <param name="level">Evaluation level.</param>
    /// <param name="populationScale">Multiplier of the default population size.</param>
    /// <returns>True when the basin improved by at least the configured relative amount.</returns>
    public bool Optimize(SearchBasin basin, ActiveParameterSet subset, int evaluations, double sigma, int seed, EvaluationLevel level = EvaluationLevel.Normal, int populationScale = 1)
    {
        ArgumentNullException.ThrowIfNull(basin);
        ArgumentNullException.ThrowIfNull(subset);
        if (subset.Count == 0 || evaluations <= 0)
        {
            return false;
        }

        double startLoss = basin.Best.Loss;
        int lambda = (4 + (int)Math.Floor(3.0 * Math.Log(subset.Count))) * Math.Max(1, populationScale);
        var run = new ContinuousRun(new CmaEs(subset.ToVector(basin.Best.Patch), sigma, seed, lambda), 0);
        int left = evaluations;
        while (left > 0 && !run.Finished)
        {
            int spent = Step(basin, subset, run, left, level);
            if (spent == 0)
            {
                break;
            }

            left -= spent;
        }

        return basin.Best.Loss < startLoss * (1.0 - _options.MinimumRelativeImprovement);
    }

    /// <summary>Advances a run by as many generations as the budget allows. Returns the renders spent.</summary>
    private int Step(SearchBasin basin, ActiveParameterSet subset, ContinuousRun run, int evaluations, EvaluationLevel level)
    {
        CmaEs cma = run.Cma;
        int spent = 0;
        while (spent + cma.PopulationSize <= evaluations && _evaluator.Remaining >= cma.PopulationSize)
        {
            if (cma.HasConverged)
            {
                run.Finished = true;
                break;
            }

            // Frozen values come from the best patch of the moment, so a resumed run sees what other subsets achieved.
            OperatorMatrixPatch frozen = basin.Best.Patch;
            double[][] population = cma.Ask();
            var patches = new OperatorMatrixPatch[population.Length];
            for (int k = 0; k < population.Length; k++)
            {
                patches[k] = subset.Apply(frozen, population[k]);
            }

            int before = _evaluator.Used;
            IReadOnlyList<PatchEvaluation> results = _evaluator.Evaluate(patches, level);
            spent += Math.Max(1, _evaluator.Used - before);
            if (results.Count < population.Length)
            {
                run.Finished = true;
                break;
            }

            var losses = new double[population.Length];
            for (int k = 0; k < losses.Length; k++)
            {
                losses[k] = results[k].Loss;
                run.Best = Math.Min(run.Best, losses[k]);
                basin.Offer(results[k]);
            }

            cma.Tell(losses);

            // Progress is judged on this run alone: a restart that begins far from the best patch needs time to catch up.
            if (run.Best < run.Reference * (1.0 - _options.MinimumRelativeImprovement))
            {
                run.Reference = run.Best;
                run.SinceProgress = 0;
            }
            else if (++run.SinceProgress >= _options.StagnationGenerations)
            {
                run.Finished = true;
                break;
            }
        }

        basin.Evaluations += spent;
        return spent;
    }

    /// <summary>Groups the smooth parameters by connected component of the modulation graph.</summary>
    private List<ActiveParameterSet> ComponentSubsets(OperatorMatrixPatch patch, IReadOnlyList<PatchParameter> active, bool tuningMatters)
    {
        int n = patch.OperatorCount;
        var component = new int[n];
        for (int i = 0; i < n; i++)
        {
            component[i] = i;
        }

        int Find(int i)
        {
            while (component[i] != i)
            {
                component[i] = component[component[i]];
                i = component[i];
            }

            return i;
        }

        bool[] audible = patch.FindAudibleOperators();
        foreach (ModulationEdge edge in patch.ModulationMatrix.Edges)
        {
            if (edge.Enabled && edge.Amount != 0.0 && audible[edge.SourceOperator] && audible[edge.DestinationOperator])
            {
                component[Find(edge.SourceOperator)] = Find(edge.DestinationOperator);
            }
        }

        var groups = new SortedDictionary<int, List<PatchParameter>>();
        var pitch = new List<PatchParameter>();
        foreach (PatchParameter p in active)
        {
            if (p.Id.StartsWith("pitch.", StringComparison.Ordinal))
            {
                if (!p.Id.EndsWith(".p0.time", StringComparison.Ordinal))
                {
                    pitch.Add(p);
                }

                continue;
            }

            if (!IsSpectral(p.Id, tuningMatters) && !IsEnvelopeShape(p.Id))
            {
                continue;
            }

            // "op3.…" belongs to operator 3, "edge.2>1.…" to the group of its destination.
            int op = p.Id.StartsWith("op", StringComparison.Ordinal)
                ? int.Parse(p.Id.AsSpan(2, p.Id.IndexOf('.', StringComparison.Ordinal) - 2), System.Globalization.CultureInfo.InvariantCulture) - 1
                : int.Parse(p.Id.AsSpan(p.Id.IndexOf('>', StringComparison.Ordinal) + 1, p.Id.IndexOf('.', 5) - p.Id.IndexOf('>', StringComparison.Ordinal) - 1), System.Globalization.CultureInfo.InvariantCulture) - 1;
            int root = Find(op);
            if (!groups.TryGetValue(root, out List<PatchParameter>? members))
            {
                members = [];
                groups[root] = members;
            }

            members.Add(p);
        }

        var subsets = new List<ActiveParameterSet>();
        int max = Math.Max(1, _options.MaxActiveParameters);
        bool first = true;
        foreach (List<PatchParameter> members in groups.Values)
        {
            if (first)
            {
                members.AddRange(pitch);
                first = false;
            }

            // A group that is still too large is split into what shapes its spectrum and what shapes it in time.
            if (members.Count > max)
            {
                List<PatchParameter> spectral = [.. members.Where(p => IsSpectral(p.Id, tuningMatters))];
                List<PatchParameter> temporal = [.. members.Where(p => !IsSpectral(p.Id, tuningMatters))];
                foreach (List<PatchParameter> part in new[] { spectral, temporal })
                {
                    for (int from = 0; from < part.Count; from += max)
                    {
                        subsets.Add(new ActiveParameterSet(part.GetRange(from, Math.Min(max, part.Count - from))));
                    }
                }
            }
            else if (members.Count > 0)
            {
                subsets.Add(new ActiveParameterSet(members));
            }
        }

        return subsets;
    }

    /// <summary>
    /// Parameters that shape the spectrum, the duty cycle of a pulse and the response of filtered noise among them. Key
    /// scaling and velocity sensitivity are only included when the target set has several pitches or velocities: on a
    /// single note they cannot be told apart from level and envelope.
    /// </summary>
    private bool IsSpectral(string id, bool tuningMatters) =>
        id.EndsWith(".amount", StringComparison.Ordinal)
        || id.EndsWith(".output.level", StringComparison.Ordinal)
        || id.EndsWith(".frequency.fixedHz", StringComparison.Ordinal)
        || id.EndsWith(".shape", StringComparison.Ordinal)
        || id.EndsWith(".filter", StringComparison.Ordinal)
        || (tuningMatters && (id.EndsWith(".fineTune", StringComparison.Ordinal) || id.EndsWith(".detune", StringComparison.Ordinal)))
        || (IncludeKeyScaling && id.Contains(".keyScaling.", StringComparison.Ordinal))
        || (IncludeVelocity && id.EndsWith(".velocity", StringComparison.Ordinal));

    private static bool IsEnvelopeShape(string id)
    {
        // Times of all segments and the levels of the inner points. The start, the peak and the end of an amplitude
        // envelope are fixed: the peak is redundant with output levels and modulation amounts.
        if (id.StartsWith("pitch.", StringComparison.Ordinal) || !(id.Contains(".env.p", StringComparison.Ordinal) || id.EndsWith(".env.breakPosition", StringComparison.Ordinal)))
        {
            return false;
        }

        if (id.EndsWith(".time", StringComparison.Ordinal) || id.EndsWith(".breakPosition", StringComparison.Ordinal))
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

/// <summary>A CMA-ES run that a basin keeps between calls, so that optimization resumes instead of starting over.</summary>
internal sealed class ContinuousRun(CmaEs cma, int index)
{
    public CmaEs Cma { get; } = cma;

    /// <summary>Zero for the first run on a subset, counting up with every restart.</summary>
    public int Index { get; } = index;

    public double Best { get; set; } = double.PositiveInfinity;

    public double Reference { get; set; } = double.PositiveInfinity;

    public int SinceProgress { get; set; }

    public bool Finished { get; set; }
}
