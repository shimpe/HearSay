using System.Diagnostics;
using HearSay.Analysis;
using HearSay.Objective;
using HearSay.OperatorMatrix.Basins;
using HearSay.OperatorMatrix.Canonicalization;
using HearSay.OperatorMatrix.Parameters;
using HearSay.OperatorMatrix.Priors;
using HearSay.Synthesis.Patches;
using HearSay.Synthesis.Profiles;
using HearSay.Synthesis.Rendering;

namespace HearSay.OperatorMatrix.Search;

/// <summary>Progress of a running search.</summary>
/// <param name="Stage">Current stage.</param>
/// <param name="EvaluationsUsed">Renders used so far.</param>
/// <param name="EvaluationBudget">Render budget.</param>
/// <param name="BestLoss">Best total loss so far, at the level of the current stage.</param>
public sealed record SearchProgress(string Stage, int EvaluationsUsed, int EvaluationBudget, double BestLoss);

/// <summary>Everything a search produced.</summary>
/// <param name="Candidates">Finalists, best first, evaluated at the detailed level.</param>
/// <param name="Analysis">Analysis of the target.</param>
/// <param name="Request">Note every candidate was rendered with.</param>
/// <param name="Summary">Course of the search.</param>
/// <param name="Models">Regime-specific local models learned during refinement.</param>
public sealed record SearchOutcome(
    IReadOnlyList<PatchCandidateResult> Candidates,
    TargetAnalysis Analysis,
    RenderRequest Request,
    SearchSummary Summary,
    RegimeModelStore Models);

/// <summary>
/// Runs a complete patch search: target analysis, structural hypotheses, screening, multi-basin continuous
/// optimization with stagnation handling and topology mutation, temporal stages, local refinement and final ranking.
/// </summary>
/// <remarks>
/// The coordinator is independent of the synthesis method. It receives a renderer, a profile and prior providers; it
/// never looks at waveforms, never assumes sine operators and never generates a structure itself other than through
/// providers and the capability-checked <see cref="TopologyMutator"/>. It also owns no DSP: all measuring is done by
/// the audio objective.
/// </remarks>
public sealed class OperatorMatrixPatchSearchCoordinator
{
    private readonly IOperatorMatrixRenderer _renderer;
    private readonly IReadOnlyList<ISynthesisPriorProvider> _providers;
    private readonly AudioObjective _objective = new();

    /// <summary>Creates a coordinator.</summary>
    /// <param name="renderer">Renderer of the synthesis backend that is searched.</param>
    /// <param name="providers">Sources of structural hypotheses.</param>
    public OperatorMatrixPatchSearchCoordinator(IOperatorMatrixRenderer renderer, IReadOnlyList<ISynthesisPriorProvider> providers)
    {
        ArgumentNullException.ThrowIfNull(renderer);
        ArgumentNullException.ThrowIfNull(providers);
        _renderer = renderer;
        _providers = providers;
    }

    /// <summary>Searches a patch for a target.</summary>
    /// <param name="target">Target audio.</param>
    /// <param name="options">Search options.</param>
    /// <param name="progress">Receives progress after every step of the search.</param>
    /// <param name="cancellationToken">Stops the search early; the best patches found so far are returned.</param>
    /// <exception cref="ArgumentException">The renderer cannot play everything the profile allows, or the target is unusable.</exception>
    public SearchOutcome Search(AudioTarget target, PatchMatchOptions? options = null, IProgress<SearchProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(target);
        options ??= new PatchMatchOptions();
        SynthesisProfile profile = options.SynthesisProfile;
        SynthesisCapabilities capabilities = profile.Capabilities;
        if (!capabilities.IsSubsetOf(_renderer.Capabilities))
        {
            throw new ArgumentException($"The renderer cannot play everything profile '{profile.Name}' allows; the search would leave the space the renderer represents.", nameof(options));
        }

        if (profile.RenderProfile == RenderProfile.BackendExact && _renderer.RenderProfile != RenderProfile.BackendExact)
        {
            throw new ArgumentException($"Profile '{profile.Name}' needs a backend-exact renderer.", nameof(options));
        }

        var stopwatch = Stopwatch.StartNew();
        var stages = new List<SearchStageSummary>();
        int seedCounter = 0;
        int NextSeed() => unchecked((options.RandomSeed * 7919) + (++seedCounter * 104729));
        var random = new Random(options.RandomSeed);

        // Stage A: target analysis.
        int maxSamples = (int)(options.MaxSeconds * target.SampleRate);
        if (target.Samples.Length > maxSamples)
        {
            target = target with { Samples = target.Samples[..maxSamples] };
        }

        ObjectiveOptions objectiveOptions = options.Objective with { Analysis = options.Objective.Analysis with { SampleRate = options.SampleRate } };
        PreparedObjectiveTarget prepared = _objective.Prepare(target, objectiveOptions);
        TargetAnalysis analysis = prepared.Analysis;
        if (!analysis.IsUsable)
        {
            throw new ArgumentException("The target cannot be analysed: " + string.Join(", ", analysis.Issues), nameof(target));
        }

        var request = new RenderRequest(
            analysis.FundamentalHz,
            target.MidiNote,
            options.Velocity,
            TimeSpan.FromSeconds(Math.Max(0.02, analysis.NoteOffSeconds)),
            TimeSpan.FromSeconds(Math.Max(0.0, analysis.DurationSeconds - analysis.NoteOffSeconds)),
            options.SampleRate);
        var evaluator = new PatchEvaluator(_renderer, _objective, prepared, request, options.EvaluationBudget, options.ComplexityWeight, options.Complexity, options.MaxParallelEvaluations);
        var optimizer = new BasinOptimizer(evaluator, capabilities, options);
        var mutator = new TopologyMutator(capabilities);
        bool tuningMatters = analysis.Inharmonicity > 0.08 || analysis.Beating.IsPresent;
        stages.Add(new SearchStageSummary("target analysis", 0, double.NaN, EvaluationLevel.Detailed, FormattableString.Invariant($"f0 {analysis.FundamentalHz:F2} Hz, {analysis.Partials.Count} partials, inharmonicity {analysis.Inharmonicity:F2}, note-off {analysis.NoteOffSeconds:F2} s")));

        // Stage B: structural hypotheses, screened at the coarse level.
        var hypotheses = new List<PatchHypothesis>();
        foreach (ISynthesisPriorProvider provider in _providers)
        {
            hypotheses.AddRange(provider.Generate(analysis, capabilities));
        }

        hypotheses.RemoveAll(h => !PatchCompatibilityValidator.Instance.Validate(h.Patch, capabilities).IsCompatible);
        if (hypotheses.Count > options.HypothesisCount)
        {
            hypotheses = [.. hypotheses.OrderByDescending(static h => h.PriorScore).Take(options.HypothesisCount)];
        }

        if (hypotheses.Count == 0)
        {
            throw new ArgumentException("No prior provider produced a hypothesis for this profile.", nameof(options));
        }

        int screeningBudget = (int)(options.EvaluationBudget * options.ScreeningShare);
        double[] scales = screeningBudget >= hypotheses.Count * 3 ? [1.0, 0.5, 2.0] : [1.0];
        var screened = new List<(PatchHypothesis Hypothesis, PatchEvaluation Evaluation)>();
        int usedBefore = evaluator.Used;
        foreach (double scale in scales)
        {
            var patches = hypotheses.Select(h => ScaleAmounts(h.Patch, scale, capabilities)).ToList();
            IReadOnlyList<PatchEvaluation> results = evaluator.Evaluate(patches, EvaluationLevel.Coarse);
            for (int i = 0; i < results.Count; i++)
            {
                screened.Add((hypotheses[i], results[i]));
            }
        }

        // The best scale of every hypothesis competes.
        var survivors = screened
            .GroupBy(static s => s.Hypothesis)
            .Select(static g => g.OrderBy(static s => s.Evaluation.Loss).First())
            .OrderBy(static s => s.Evaluation.Loss)
            .ThenBy(static s => s.Hypothesis.Origin, StringComparer.Ordinal)
            .Take(options.SurvivorCount)
            .ToList();
        stages.Add(new SearchStageSummary("hypothesis screening", evaluator.Used - usedBefore, survivors[0].Evaluation.Loss, EvaluationLevel.Coarse, $"{hypotheses.Count} hypotheses at {scales.Length} modulation scales, {survivors.Count} survive"));
        progress?.Report(new SearchProgress("hypothesis screening", evaluator.Used, evaluator.Budget, survivors[0].Evaluation.Loss));

        // Survivors: first normal-level evaluation, then a short continuous optimization.
        usedBefore = evaluator.Used;
        IReadOnlyList<PatchEvaluation> firstNormal = evaluator.Evaluate([.. survivors.Select(static s => s.Evaluation.Patch)], EvaluationLevel.Normal);
        var basins = new List<SearchBasin>();
        int nextBasinId = 1;
        for (int i = 0; i < firstNormal.Count; i++)
        {
            basins.Add(new SearchBasin(nextBasinId++, survivors[i].Hypothesis.Origin, firstNormal[i]));
        }

        if (basins.Count == 0)
        {
            throw new ArgumentException("The evaluation budget is too small to evaluate a single hypothesis.", nameof(options));
        }

        double initialAudioLoss = basins.Min(static b => b.Best.AudioLoss);
        int survivorBudget = (int)(options.EvaluationBudget * options.SurvivorShare);
        int perSurvivor = Math.Max(0, (survivorBudget - basins.Count) / basins.Count);
        foreach (SearchBasin basin in basins)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                break;
            }

            RunCycle(optimizer, basin, perSurvivor, tuningMatters, NextSeed, 1.0, includeDetail: false);
            basin.Fate = "survivor";
            progress?.Report(new SearchProgress("survivor optimization", evaluator.Used, evaluator.Budget, basins.Min(static b => b.Best.Loss)));
        }

        basins = Distinct(basins, options.BasinCount);
        stages.Add(new SearchStageSummary("survivor optimization", evaluator.Used - usedBefore, basins[0].Best.Loss, EvaluationLevel.Normal, $"{firstNormal.Count} survivors, {basins.Count} distinct basins kept"));

        // Deep optimization with successive halving: better basins earn more of the budget.
        usedBefore = evaluator.Used;
        var retired = new List<SearchBasin>();
        int deepBudget = Math.Min(evaluator.Remaining, (int)(options.EvaluationBudget * options.DeepShare));
        const int rounds = 3;
        int mutations = 0, adopted = 0;
        for (int round = 0; round < rounds && !cancellationToken.IsCancellationRequested; round++)
        {
            int keep = round == rounds - 1
                ? Math.Min(basins.Count, options.FinalistCount)
                : Math.Max(options.FinalistCount, (int)Math.Ceiling(options.BasinCount * (1.0 - (round / (double)rounds))));
            Retire(basins, retired, keep);
            int perBasin = deepBudget / rounds / Math.Max(1, basins.Count);
            for (int b = 0; b < basins.Count && !cancellationToken.IsCancellationRequested; b++)
            {
                SearchBasin basin = basins[b];
                basin.Fate = "deep optimization";
                double sigmaScale = basin.Stagnated ? 1.8 : 1.0;
                int cycleBudget = basin.Stagnated ? perBasin / 2 : perBasin;
                bool improved = RunCycle(optimizer, basin, cycleBudget, tuningMatters, NextSeed, sigmaScale, includeDetail: true);

                // Stagnation: try structural variants with the rest of this basin's share.
                if ((!improved || basin.Stagnated) && basin.Restarts < options.MaxRestartsPerBasin)
                {
                    basin.Restarts++;
                    int mutationBudget = Math.Max(0, perBasin - cycleBudget) + (improved ? 0 : perBasin / 4);
                    IReadOnlyList<TopologyMutation> variants = mutator.ProposeScreened(basin.Best.Patch, analysis, evaluator, random, 3, maxScreened: 80);
                    SearchBasin? bestChild = null;
                    foreach (TopologyMutation variant in variants)
                    {
                        mutations++;
                        PatchEvaluation? first = evaluator.Evaluate(variant.Patch, EvaluationLevel.Normal);
                        if (first is null)
                        {
                            break;
                        }

                        var child = new SearchBasin(nextBasinId++, basin.Origin + "; " + variant.Description, first) { Evaluations = 1 };
                        RunCycle(optimizer, child, Math.Max(0, (mutationBudget / Math.Max(1, variants.Count)) - 1), tuningMatters, NextSeed, 1.0, includeDetail: false);
                        if (bestChild is null || child.Best.Loss < bestChild.Best.Loss)
                        {
                            bestChild = child;
                        }
                    }

                    if (bestChild is not null && bestChild.Best.Loss < basin.Best.Loss)
                    {
                        // The variant wins: it takes the place of its parent.
                        adopted++;
                        bestChild.Restarts = basin.Restarts;
                        bestChild.Fate = "deep optimization";
                        basin.Fate = "replaced by mutation";
                        retired.Add(basin);
                        basins[b] = bestChild;
                    }
                }

                basins[b].Stagnated = !improved;
                progress?.Report(new SearchProgress($"deep optimization round {round + 1}", evaluator.Used, evaluator.Budget, basins.Min(static x => x.Best.Loss)));
            }

            List<SearchBasin> distinct = Distinct(basins, basins.Count);
            foreach (SearchBasin dropped in basins.Except(distinct))
            {
                dropped.Fate = "duplicate";
                retired.Add(dropped);
            }

            basins = distinct;
        }

        Retire(basins, retired, options.FinalistCount);
        stages.Add(new SearchStageSummary("deep optimization", evaluator.Used - usedBefore, basins[0].Best.Loss, EvaluationLevel.Normal, $"{rounds} rounds, {mutations} topology mutations tried, {adopted} adopted"));

        // Finalists: one long, uninterrupted run each. A covariance that has adapted to a basin is worth more than
        // several fresh starts, so the better a finalist ranks, the longer its run; unused budget flows on.
        usedBefore = evaluator.Used;
        int finalistBudget = (int)(evaluator.Remaining * options.FinalistShare);
        double weightSum = 0;
        for (int b = 0; b < basins.Count; b++)
        {
            weightSum += 1.0 / (b + 1);
        }

        for (int b = 0; b < basins.Count && !cancellationToken.IsCancellationRequested; b++)
        {
            basins[b].Fate = "finalist";
            int share = (int)(finalistBudget * (1.0 / (b + 1)) / weightSum);
            RunCycle(optimizer, basins[b], (int)(share * 0.6), tuningMatters, NextSeed, 0.7, includeDetail: true);

            // A structure that has been optimized this far shows clearly what it lacks. Its variants start out sounding
            // like it, so each of them only has to show that its extra freedom helps.
            IReadOnlyList<TopologyMutation> variants = mutator.ProposeScreened(basins[b].Best.Patch, analysis, evaluator, random, 4);
            SearchBasin? bestChild = null;
            foreach (TopologyMutation variant in variants)
            {
                mutations++;
                PatchEvaluation? first = evaluator.Evaluate(variant.Patch, EvaluationLevel.Normal);
                if (first is null)
                {
                    break;
                }

                var child = new SearchBasin(nextBasinId++, basins[b].Origin + "; " + variant.Description, first) { Evaluations = 1, Fate = "finalist variant, not adopted" };
                RunCycle(optimizer, child, Math.Max(0, (int)(share * 0.3 / variants.Count) - 1), tuningMatters, NextSeed, 0.5, includeDetail: true);
                retired.Add(child);
                if (bestChild is null || child.Best.Loss < bestChild.Best.Loss)
                {
                    bestChild = child;
                }
            }

            if (bestChild is not null && bestChild.Best.Loss < basins[b].Best.Loss && !basins.Exists(x => x != basins[b] && x.Regime == bestChild.Regime))
            {
                adopted++;
                retired.Remove(bestChild);
                basins[b].Fate = "replaced by mutation";
                retired.Add(basins[b]);
                bestChild.Fate = "finalist";
                basins[b] = bestChild;
            }

            RunCycle(optimizer, basins[b], (int)(share * 0.1), tuningMatters, NextSeed, 0.5, includeDetail: true);
            progress?.Report(new SearchProgress("finalist optimization", evaluator.Used, evaluator.Budget, basins.Min(static x => x.Best.Loss)));
        }

        Retire(basins, retired, options.FinalistCount);
        stages.Add(new SearchStageSummary("finalist optimization", evaluator.Used - usedBefore, basins[0].Best.Loss, EvaluationLevel.Normal, $"{basins.Count} finalists, each with a round of structural variants"));
        double finalNormalAudioLoss = basins.Min(static b => b.Best.AudioLoss);

        // Local refinement of the finalists at the detailed level, with regime-specific models.
        usedBefore = evaluator.Used;
        var store = new RegimeModelStore();
        var refiner = new LocalRefiner(evaluator, capabilities, store);
        int accepted = 0;
        foreach (SearchBasin basin in basins)
        {
            PatchEvaluation? detailed = evaluator.Evaluate(basin.Best.Patch, EvaluationLevel.Detailed);
            if (detailed is not null)
            {
                basin.Rebase(detailed);
                basin.Evaluations++;
            }
        }

        // Worst finalist first: whatever a finalist leaves unused flows on to a better one.
        for (int b = basins.Count - 1; b >= 0 && !cancellationToken.IsCancellationRequested; b--)
        {
            SearchBasin basin = basins[b];
            if (basin.Best.Level != EvaluationLevel.Detailed)
            {
                continue;
            }

            basin.Fate = "finalist";
            int share = evaluator.Remaining / (b + 1);
            accepted += refiner.Refine(basin, share / 2, EvaluationLevel.Detailed);

            // Whatever is left of the share goes into a last, narrow continuous polish.
            IReadOnlyList<ActiveParameterSet> polish = optimizer.SelectSubsets(basin.Best.Patch, ContinuousStage.Joint, tuningMatters);
            if (polish.Count == 0)
            {
                polish = optimizer.SelectSubsets(basin.Best.Patch, ContinuousStage.Spectral, tuningMatters);
            }

            if (polish.Count > 0)
            {
                optimizer.Optimize(basin, polish[0], Math.Min(share / 2, evaluator.Remaining), 0.05, NextSeed(), EvaluationLevel.Detailed);
            }

            progress?.Report(new SearchProgress("local refinement", evaluator.Used, evaluator.Budget, basins.Min(static x => x.Best.Loss)));
        }

        stages.Add(new SearchStageSummary("local refinement", evaluator.Used - usedBefore, basins.Where(static b => b.Best.Level == EvaluationLevel.Detailed).Select(static b => b.Best.Loss).DefaultIfEmpty(double.NaN).Min(), EvaluationLevel.Detailed, $"{accepted} guidance steps accepted, {store.Regimes.Count} regime models"));

        // Final ranking. Finalists that could not be evaluated at the detailed level rank after those that were.
        var ranked = basins
            .OrderBy(static b => b.Best.Level == EvaluationLevel.Detailed ? 0 : 1)
            .ThenBy(static b => b.Best.Loss)
            .ThenBy(static b => b.Id)
            .ToList();
        var candidates = ranked
            .Select(b => new PatchCandidateResult(PatchCanonicalizer.Canonicalize(b.Best.Patch), b.Best.Loss, b.Best.AudioLoss, b.Best.Complexity, b.Best.Terms, b.Regime, b.Origin))
            .ToList();

        var summary = new SearchSummary
        {
            Profile = profile.Name,
            RandomSeed = options.RandomSeed,
            EvaluationBudget = options.EvaluationBudget,
            EvaluationsUsed = evaluator.Used,
            CacheHits = evaluator.CacheHits,
            InitialAudioLoss = initialAudioLoss,
            FinalAudioLoss = finalNormalAudioLoss,
            HypothesisCount = hypotheses.Count,
            Stages = stages,
            Basins = [.. ranked.Concat(retired.OrderBy(static b => b.Id)).Select(static b => new BasinSummary(b.Id, b.Origin, b.Regime.Value, b.Best.Loss, b.Evaluations, b.Restarts, b.Fate))],
            Elapsed = stopwatch.Elapsed,
        };
        return new SearchOutcome(candidates, analysis, request, summary, store);
    }

    /// <summary>One optimization cycle over the continuous stages. Returns true when the basin improved.</summary>
    private static bool RunCycle(BasinOptimizer optimizer, SearchBasin basin, int budget, bool tuningMatters, Func<int> nextSeed, double sigmaScale, bool includeDetail)
    {
        if (budget <= 0)
        {
            return false;
        }

        // Small patches: everything that is smooth and audible at once.
        IReadOnlyList<ActiveParameterSet> joint = optimizer.SelectSubsets(basin.Best.Patch, ContinuousStage.Joint, tuningMatters);
        if (joint.Count == 1)
        {
            return optimizer.OptimizeWithRestarts(
                basin,
                patch => optimizer.SelectSubsets(patch, ContinuousStage.Joint, tuningMatters) is [var set] ? set : null,
                budget,
                0.2 * sigmaScale,
                nextSeed,
                EvaluationLevel.Normal,
                includeDetail ? 3 : 1);
        }

        bool improved = false;
        if (includeDetail)
        {
            // Larger patches: one connected operator group at a time, everything inside a group together. Two passes,
            // because groups still meet in the output mix.
            foreach (double pass in new[] { 0.6, 0.4 })
            {
                IReadOnlyList<ActiveParameterSet> groups = optimizer.SelectSubsets(basin.Best.Patch, ContinuousStage.Components, tuningMatters);
                int total = groups.Sum(static g => g.Count);
                for (int g = 0; g < groups.Count && total > 0; g++)
                {
                    int index = g;
                    improved |= optimizer.OptimizeWithRestarts(
                        basin,
                        patch => optimizer.SelectSubsets(patch, ContinuousStage.Components, tuningMatters) is { } current && index < current.Count ? current[index] : null,
                        (int)(budget * pass * groups[g].Count / total),
                        0.2 * sigmaScale,
                        nextSeed,
                        EvaluationLevel.Normal,
                        3);
                }
            }

            return improved;
        }

        // Short runs on many basins: a few low-dimensional stages give the most ranking information per render.
        (ContinuousStage Stage, double Share, double Sigma)[] plan =
            [(ContinuousStage.Spectral, 0.6, 0.18), (ContinuousStage.TemporalMacros, 0.4, 0.12)];
        foreach ((ContinuousStage stage, double share, double sigma) in plan)
        {
            // Subsets are chosen from the current best patch: macros are re-centred on it every time.
            IReadOnlyList<ActiveParameterSet> subsets = optimizer.SelectSubsets(basin.Best.Patch, stage, tuningMatters);
            if (subsets.Count == 0)
            {
                continue;
            }

            int perSubset = (int)(budget * share) / subsets.Count;
            for (int s = 0; s < subsets.Count; s++)
            {
                ActiveParameterSet subset = s == 0 ? subsets[0] : optimizer.SelectSubsets(basin.Best.Patch, stage, tuningMatters)[s];
                improved |= optimizer.Optimize(basin, subset, perSubset, sigma * sigmaScale, nextSeed());
            }
        }

        return improved;
    }

    /// <summary>Multiplies all modulation amounts, within the limits of the profile.</summary>
    private static OperatorMatrixPatch ScaleAmounts(OperatorMatrixPatch patch, double scale, SynthesisCapabilities capabilities)
    {
        if (scale == 1.0)
        {
            return patch;
        }

        double limit = Math.Min(12.0, capabilities.MaxModulationRadiansPerDestination / 2.0);
        var edges = patch.ModulationMatrix.Edges.Select(e => e with { Amount = Math.Clamp(e.Amount * scale, -limit, limit) }).ToList();
        return patch with { ModulationMatrix = new PhaseModulationMatrix(patch.OperatorCount, edges) };
    }

    private static void Retire(List<SearchBasin> basins, List<SearchBasin> retired, int keep)
    {
        basins.Sort(static (a, b) =>
        {
            int result = a.Best.Loss.CompareTo(b.Best.Loss);
            return result != 0 ? result : a.Id.CompareTo(b.Id);
        });
        while (basins.Count > Math.Max(1, keep))
        {
            SearchBasin last = basins[^1];
            last.Fate = last.Fate == "survivor" ? "survivor, not selected" : "retired after " + last.Fate;
            retired.Add(last);
            basins.RemoveAt(basins.Count - 1);
        }
    }

    /// <summary>
    /// Best basins first, without duplicates. Two basins are duplicates when they have the same canonical regime, or
    /// the same frequency structure together with practically the same loss terms: the same sound found twice.
    /// </summary>
    private static List<SearchBasin> Distinct(List<SearchBasin> basins, int count)
    {
        List<SearchBasin> ordered = [.. basins.OrderBy(static b => b.Best.Loss).ThenBy(static b => b.Id)];
        var result = new List<SearchBasin>();
        var families = new HashSet<string>(StringComparer.Ordinal);

        // First the best basin of every structural family, then the best of the rest. Early losses are deceptive: an
        // additive structure sounds roughly right at once, a deep stack only after its amounts have been found. Keeping
        // one of each kind protects structures that need more optimization before they show what they can do.
        foreach (bool familiesOnly in new[] { true, false })
        {
            foreach (SearchBasin basin in ordered)
            {
                if (result.Count >= count)
                {
                    break;
                }

                if (result.Contains(basin) || (familiesOnly && families.Contains(Family(basin.Best.Patch))))
                {
                    continue;
                }

                bool duplicate = false;
                foreach (SearchBasin kept in result)
                {
                    if (kept.Regime == basin.Regime || (SameFrequencies(kept.Best.Patch, basin.Best.Patch) && SameSound(kept.Best, basin.Best)))
                    {
                        duplicate = true;
                        break;
                    }
                }

                if (!duplicate)
                {
                    result.Add(basin);
                    families.Add(Family(basin.Best.Patch));
                }
            }
        }

        result.Sort(static (a, b) =>
        {
            int order = a.Best.Loss.CompareTo(b.Best.Loss);
            return order != 0 ? order : a.Id.CompareTo(b.Id);
        });
        return result;
    }

    /// <summary>Coarse structural family: how many operators are heard directly, how many only shape others, and whether there is feedback.</summary>
    private static string Family(OperatorMatrixPatch patch)
    {
        bool[] audible = patch.FindAudibleOperators();
        int heard = 0, shaping = 0;
        for (int i = 0; i < patch.OperatorCount; i++)
        {
            if (audible[i])
            {
                if (patch.OutputLevels[i] != 0.0)
                {
                    heard++;
                }
                else
                {
                    shaping++;
                }
            }
        }

        bool feedback = patch.ModulationMatrix.Edges.Any(e => e.Enabled && e.Amount != 0.0 && audible[e.SourceOperator] && audible[e.DestinationOperator] && e.SourceOperator <= e.DestinationOperator);
        return $"{heard}/{shaping}/{(feedback ? "fb" : "ff")}";
    }

    private static bool SameFrequencies(OperatorMatrixPatch a, OperatorMatrixPatch b)
    {
        static List<double> Frequencies(OperatorMatrixPatch patch)
        {
            bool[] audible = patch.FindAudibleOperators();
            var list = new List<double>();
            for (int i = 0; i < patch.OperatorCount; i++)
            {
                if (audible[i])
                {
                    OperatorFrequency f = patch.Operators[i].Frequency;
                    list.Add(f.Mode == OperatorFrequencyMode.Ratio ? f.Ratio : -f.FixedFrequencyHz);
                }
            }

            list.Sort();
            return list;
        }

        return Frequencies(a).SequenceEqual(Frequencies(b));
    }

    private static bool SameSound(PatchEvaluation a, PatchEvaluation b)
    {
        static double[] Vector(ObjectiveTerms t) => [t.Stft, t.Mel, t.Partial, t.Trajectory, t.Envelope, t.Ltas];
        double[] x = Vector(a.Terms), y = Vector(b.Terms);
        for (int i = 0; i < x.Length; i++)
        {
            if (double.IsNaN(x[i]) || double.IsNaN(y[i]))
            {
                continue;
            }

            if (Math.Abs(x[i] - y[i]) > 0.02 * Math.Max(1e-3, Math.Max(x[i], y[i])))
            {
                return false;
            }
        }

        return true;
    }
}
